using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinTube.App.Mpv;
using WinTube.Core.InnerTube;
using WinTube.Core.Links;
using WinTube.Core.Models;
using WinTube.Core.Player;
using WinTube.Core.SponsorBlock;

namespace WinTube.App.Views;

/// Full-window playback: resolves a stream for the navigated VideoItem, plays it with resume
/// and periodic progress recording, and retries once up the client ladder if playback never
/// starts. Ports the tvOS player contract onto MpvPlayerHost, the libmpv-backed control that
/// replaced MediaPlayerElement; the source-building logic fetches YouTube's own HLS master
/// playlist and hands mpv a single-variant manifest filtered out of it.
public sealed partial class PlayerPage : Page
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    private VideoItem? video;
    private TimeSpan? startAt;
    private TimeSpan flyoutPosition;

    /// The live mpv instance, or null between OnNavigatedTo and the first PlayAsync, or while
    /// torn down. Constructed fresh per PlayAsync attempt — a ladder retry never reuses a host
    /// whose start failed, it disposes it and builds a new one.
    private MpvPlayerHost? Player;
    public double CurrentPositionSeconds => Player!.Position;

    private DispatcherQueueTimer? progressTimer;
    private DispatcherQueueTimer? stallTimer;
    private ClientKind? lastClient;
    private string? lastUserAgent;
    private string? lastAudioLanguage;
    private bool retried;
    private bool hasPlayed;
    private bool handledFailure;
    private bool leftPage;
    /// M4: armed whenever ReloadKeepingPosition starts a reload stall watchdog (quality, subtitle
    /// or mute-driven subtitle switch), and cleared the moment ANY Opened lands (host.Opened
    /// fires from a reload the same way it does from a fresh load). Kept separate from
    /// hasPlayed, which stays true for the rest of the video once the first attempt has ever
    /// opened and would otherwise never gate a later reload's own watchdog.
    private bool reloadArmed;
    /// M5: set by ReloadKeepingPosition when the host was paused right before the reload.
    /// DoLoad always issues pause=no, so re-pausing has to wait for the reload's own Opened —
    /// issuing Pause() immediately after Load() would race mpv's command queue against that
    /// same pause=no. Consumed (and cleared) the moment that Opened lands.
    private bool pauseAfterReload;

    // MARK: quality picker state
    private string? hlsMaster;
    private IReadOnlyList<HlsVariant> hlsVariants = [];
    private IReadOnlyList<HlsQuality> hlsQualities = [];
    private HlsQuality? activeQuality;
    /// hand-edited simulatedBandwidthMbps override, read once per page (a fresh PlayerPage per
    /// video) — settings reads are file I/O and Auto resolves, teardown and every flyout
    /// opening all need it. simulatedMbpsRead distinguishes "not read yet" from "read, absent".
    private double? simulatedMbps;
    private bool simulatedMbpsRead;

    // MARK: stall downshift state (all per video, reset in OnNavigatedTo)
    /// Level 2 of the adaptive Auto policy: counts genuine stalls and decides when Auto steps down
    /// one rung. Lives on the UI thread with everything else (BufferingStarted is already posted
    /// there). Deliberately NOT reset by a ladder retry re-entering PlayAsync - same video, so the
    /// stall history and the downshift budget carry over.
    private readonly DownshiftPolicy downshiftPolicy = new();
    /// Mirror of the stalls the policy has been given, pruned by the policy's own window rule, only
    /// so the downshift log line can say "2 stalls in 38s" (the policy keeps its list private).
    private readonly Queue<DateTimeOffset> recordedStalls = new();
    /// Per-video cap on what Auto may resolve to, set by a granted downshift and consumed by
    /// ResolveQuality's Auto branch next to the screen height. Null until the first downshift.
    private int? autoCeilingHeight;
    /// When the latest file open (first load or any reload) landed; buffering edges shortly after
    /// are the open's own, not a bandwidth signal.
    private DateTimeOffset? openedAt;
    /// When the user (or a sponsor skip) last asked mpv to seek; a seek into an uncached range
    /// raises paused-for-cache without the connection being slow.
    private DateTimeOffset? lastSeekAt;
    /// The session-wide choice, as on youtube.com: null is Auto; a height pins the nearest
    /// rung at or below it on every video until the app closes. Deliberately not persisted.
    private static int? preferredHeight;
    /// Seeded from the resolved resume position before the picker becomes clickable, so a
    /// reload (quality, subtitle or mute-driven) during the loading ring (before the first
    /// Opened lands and hasPlayed flips true) reloads at the real resume point instead of
    /// Player.Position's still-0 value. Only read by ReloadKeepingPosition while !hasPlayed — once real positions are
    /// flowing, Player.Position is authoritative and this goes stale on purpose.
    private double lastKnownPosition;

    /// What the current attempt last handed Player.Load (the manifest path, or the muxed URL).
    /// A subtitle switch reloads onto this same source; null until an attempt's Load has been
    /// issued, so a pick during the resolve phase just stores state and the first Load carries it.
    private string? loadTarget;

    // MARK: subtitle picker state
    private IReadOnlyList<CaptionTrack> captionTracks = [];
    private SubtitleChoice? activeSubtitle;
    private SubtitleMode subtitleMode = SubtitleMode.Off;
    /// The subtitle URL every Player.Load passes (activeSubtitle?.Url) — any reload (quality,
    /// subtitle or mute) reloads the file, and mpv drops external subtitles with it, so it must be re-attached each time.
    private string? lastSubtitleUrl;
    /// Whether the volume was at zero the last time the mute automation looked. The slider fires
    /// ValueChanged on every notch of a drag; only a change of this flag is a mute edge, so
    /// sliding 40 -> 20 -> 0 -> 0 acts exactly once, at the first 0.
    private bool wasMuted;
    /// Menu entries paired with the choice each one applies (null = Off), so the checked mark can
    /// follow activeSubtitle whoever changed it.
    private readonly List<(RadioMenuFlyoutItem Item, SubtitleChoice? Choice)> subtitleItems = [];

    // MARK: audio track picker state
    /// The language of the dub the viewer picked from the audio menu, for THIS video only (reset in
    /// OnNavigatedTo, deliberately not persisted). Every reload and ladder retry opens a fresh file
    /// on which mpv re-picks via alang, so AudioTracksChanged re-applies this after each open.
    /// Null until a pick, and stays null when the picked track carries no language to match on.
    private string? chosenAudioLanguage;
    /// Menu entries paired with the mpv track id each one selects, so the checked mark can follow
    /// the host's SelectedAudioId whoever changed it (a pick, or the re-apply after a reload).
    private readonly List<(RadioMenuFlyoutItem Item, int TrackId)> audioItems = [];

    private IReadOnlyList<SponsorSegment> sponsorSegments = [];
    private readonly HashSet<string> sponsorSkipped = [];
    private DispatcherQueueTimer? toastTimer;
    /// Duration RenderSponsorMarkers last drew against — UpdateTransport compares against this
    /// on every PositionChanged tick so a rebuild only happens once duration actually changes
    /// (goes from 0 to known, or a quality reload briefly resets it), not on every tick.
    private double markersDuration = -1;
    /// The segment the skip button currently offers, or null while none is upcoming. Set by
    /// UpdateSkipButtonVisibility, read (and cleared) by OnSkipBlockClick.
    private SponsorSegment? skipCandidate;
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush SponsorMarkerBrush =
        new(Windows.UI.Color.FromArgb(179, 0xE6, 0xC2, 0x1F));   // ~70% opacity caution yellow
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush BufferedRangeBrush =
        new(Windows.UI.Color.FromArgb(0xCC, 0x4C, 0xAF, 0x50));  // green, clearly distinct from track and markers
    /// Cached result of GetThumbInset() — set once the live Thumb part has a real ActualWidth.
    private double? thumbInset;

    // MARK: description
    /// The latest resolved stream's videoDetails.shortDescription — set fresh by every PlayAsync
    /// (including a ladder retry's), reset to null in OnNavigatedTo. Null/whitespace means no
    /// description panel is offered at all (TitleChevron stays collapsed, the title isn't
    /// clickable).
    private string? description;
    private bool HasDescription => !string.IsNullOrWhiteSpace(description);

    // MARK: comments
    private readonly ObservableCollection<CommentRowViewModel> commentRows = [];
    private List<CommentItem> topLevelComments = [];
    private string? topLevelContinuation;
    private bool commentsLoaded;
    private CommentItem? repliesParent;
    private List<CommentItem> repliesComments = [];
    private string? repliesContinuation;
    private CancellationTokenSource? commentsCts;
    private bool commentsPaging;
    private double topLevelScrollOffset;

    // MARK: transport bar state
    private DispatcherQueueTimer? volumeSaveTimer;
    private double pendingVolume;
    private DispatcherQueueTimer? transportHideTimer;
    private bool seekBarHeld;
    private bool transportPointerOverBar;
    private RadioMenuFlyoutItem? speedOneItem;
    private static readonly double[] SpeedOptions = [0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2];
    private bool isFullScreen;
    /// M3: the session's own chosen speed, independent of any one MpvPlayerHost instance —
    /// CreatePlayerHost builds a fresh host (speed defaults to 1x) on every ladder retry, so
    /// PlayAsync re-applies this after each Load rather than trusting the new host to already
    /// match whatever the (unchanged) SpeedButton/menu are still showing.
    private double chosenSpeed = 1.0;

    public PlayerPage()
    {
        InitializeComponent();
        BuildSpeedMenu();
        // The Slider consumes its own PointerPressed/Released for thumb dragging and marks them
        // handled, so plain XAML event attributes would never see them — handledEventsToo gets
        // seekBarHeld tracking a drag (or a plain click, which still presses then releases).
        SeekBar.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekBarPointerPressed), true);
        SeekBar.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekBarPointerReleased), true);
        SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekBarPointerReleased), true);
        // Same reason for the hover bubble: the Slider handles PointerMoved itself during a drag,
        // and the bubble has to keep following the pointer then.
        SeekBar.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnSeekBarPointerMoved), true);
        SeekBar.AddHandler(PointerMovedEvent, new PointerEventHandler(OnSeekBarPointerMoved), true);
        SeekBar.AddHandler(PointerExitedEvent, new PointerEventHandler(OnSeekBarPointerLeft), true);
        SeekBar.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekBarPointerLeft), true);
        VideoGrid.AddHandler(PointerMovedEvent, new PointerEventHandler(OnVideoGridPointerMoved), true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var request = (PlayerRequest)e.Parameter;
        video = request.Video;
        startAt = request.StartAt;
        TitleText.Text = video.Title;

        var hasChannel = video.ChannelId is not null && video.Author.Length > 0;
        AuthorLink.Text = hasChannel ? video.Author : "";
        AuthorLink.Visibility = hasChannel ? Visibility.Visible : Visibility.Collapsed;

        // Player is fresh per visit, so mpv's own speed is already 1 — the button/menu just
        // need to catch up to that.
        SpeedButton.Content = "1×";
        if (speedOneItem is not null) speedOneItem.IsChecked = true;
        chosenSpeed = 1.0;
        chosenAudioLanguage = null;
        ClearAudioMenu();

        leftPage = false;
        retried = false;
        simulatedMbpsRead = false;
        downshiftPolicy.Reset();
        recordedStalls.Clear();
        autoCeilingHeight = null;
        openedAt = null;
        lastSeekAt = null;
        ClearSponsorMarkers();
        ResetDescription();
        _ = StartAsync(after: null);

        ResetComments();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        leftPage = true;
        // Leaving the page while fullscreen must restore the window — nothing else undoes the
        // presenter flip once the player content is gone.
        if (isFullScreen)
        {
            isFullScreen = false;
            App.Window!.SetPlayerFullScreen(false);
        }
        ReportProgressOnce();
        App.Session.ProgressSync?.FlushNow();
        TearDownPlayer();

        toastTimer?.Stop();
        sponsorSegments = [];
        sponsorSkipped.Clear();
        ClearSponsorMarkers();

        commentsCts?.Cancel();
        commentsCts?.Dispose();
        commentsCts = null;
    }

    // MARK: resolve + play

    private async Task StartAsync(ClientKind? after)
    {
        ShowLoading();
        try
        {
            var stream = await App.Session.Streams.ResolveAsync(video!.Id, after);
            if (leftPage) return;
            lastClient = stream.Client;
            await PlayAsync(stream);
        }
        catch (Exception ex)
        {
            if (leftPage) return;
            ShowError(ex.Message);
        }
    }

    private async Task PlayAsync(ResolvedStream stream)
    {
        // M1: reset here, before any of the early manifest-step failures below can call
        // RetryOrFailAsync — resetting it only after those steps (as this used to) left a
        // second attempt's own early failure hitting RetryOrFailAsync's `handledFailure`
        // early-return, which skipped TearDownPlayer/ShowError and left the spinner forever.
        handledFailure = false;
        // No load issued yet for this attempt — keeps ApplySubtitleChoice below from reloading a
        // previous attempt's source.
        loadTarget = null;
        // A pending reload window belongs to the host this attempt replaces; the failed attempt's
        // re-pause must not carry over to a fresh host's first Opened.
        reloadArmed = false;
        pauseAfterReload = false;

        // Resolved and seeded into lastKnownPosition before BuildQualityMenu can make the picker
        // clickable below — a reload (quality, subtitle or mute) during the loading ring, before this attempt's own
        // Opened has landed, must reload at the real resume point, not 0. startAt is NOT cleared
        // here — a ladder retry re-enters PlayAsync before the video has ever played, and must
        // still resume at the original link timestamp, not recorded progress. It's cleared once,
        // in Opened's first-play branch below.
        var resume = startAt?.TotalSeconds ?? App.Session.Progress.ResumePosition(video!.Id) ?? 0;
        lastKnownPosition = resume;

        string target;
        if (stream.IsAdaptive)
        {
            string master;
            try
            {
                using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(stream.Url.ToString()));
                manifestRequest.Headers.TryAddWithoutValidation("User-Agent", stream.UserAgent);
                var manifestResponse = await App.Http.SendAsync(manifestRequest);
                manifestResponse.EnsureSuccessStatusCode();
                master = await manifestResponse.Content.ReadAsStringAsync();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                if (leftPage) return;
                await RetryOrFailAsync($"Manifest fetch failed: {e.Message}");
                return;
            }
            if (leftPage) return;

            hlsMaster = master;
            hlsVariants = HlsVariantParser.Parse(master);
            hlsQualities = HlsVariantParser.QualityLevels(hlsVariants);
            if (hlsQualities.Count == 0)
            {
                if (leftPage) return;
                await RetryOrFailAsync("Manifest had no variants.");
                return;
            }

            try
            {
                // preferredHeight comes from the quality picker (BuildQualityMenu, below);
                // null means Auto.
                target = WriteManifestForHeight(preferredHeight);
            }
            catch (InvalidManifestVariantException e)
            {
                if (leftPage) return;
                await RetryOrFailAsync(e.Message);
                return;
            }
            catch (IOException e)
            {
                if (leftPage) return;
                await RetryOrFailAsync($"Manifest write failed: {e.Message}");
                return;
            }
            BuildQualityMenu();
        }
        else
        {
            // The muxed fallback has exactly one quality; the picker just says so.
            hlsMaster = null;
            hlsVariants = [];
            hlsQualities = [];
            activeQuality = null;
            target = stream.Url.ToString();
            QualityLabel.Text = "360p";
            QualityLabelBar.Text = "360p";
            QualityButton.IsEnabled = false;
            QualityButton.Visibility = Visibility.Visible;
            QualityButtonBar.IsEnabled = false;
            QualityFlyout.Items.Clear();
            QualityFlyoutBar.Items.Clear();
            UpdateBarControlsVisibility();
        }

        hasPlayed = false;
        lastUserAgent = stream.UserAgent;
        lastAudioLanguage = stream.OriginalAudioLanguage;
        // A ladder retry re-enters PlayAsync with a different ResolvedStream (ANDROID carries
        // its own videoDetails too) — just take whatever the latest attempt reports.
        SetDescription(stream.Description);

        // The persisted preference auto-applies before the first Load, so the first load already
        // carries the subtitle (no reload). persist stays false: re-saving the matched track's
        // language here would narrow a "en" preference to "en-GB" the first time that track won.
        // A ladder retry re-enters here and re-applies against its own captions.
        captionTracks = stream.Captions;
        var preferred = SubtitleSelection.Choose(captionTracks, App.Session.PlayerSettings.LoadSubtitleLanguage());
        ApplySubtitleChoice(preferred, preferred is null ? SubtitleMode.Off : SubtitleMode.Manual);

        // The slider's ValueChanged below can't be trusted to report the mute state of this load:
        // it is suppressed when the value equals what the slider already held (a fresh 0-default
        // slider loading a saved 0), and a ladder retry re-applies the preference above — which
        // resets subtitleMode — while the slider still sits at 0 and fires nothing. So the saved
        // volume is checked explicitly here, before the first Load, and wasMuted is seeded to it
        // so the slider event that does fire (or not) is judged against the same baseline. Nothing
        // is loaded yet, so a muted Off attaches through the first Load with no reload.
        var volume = App.Session.PlayerSettings.LoadVolume();
        wasMuted = volume <= 0;
        ApplyMuteState(wasMuted);
        BuildSubtitleMenu();

        CreatePlayerHost();
        Player!.Volume = volume;
        // Fires OnVolumeSliderChanged, which re-applies the same volume and re-saves it —
        // harmless, and the simplest way to keep the slider and the host in sync on every load.
        var volumePercent = Player.Volume * 100;
        VolumeSlider.Value = volumePercent;
        // Explicit, not just relying on the ValueChanged side effect above — Slider suppresses
        // the event when the new value equals whatever it already held (e.g. a fresh 0-default
        // slider loading a 0 persisted volume), which would otherwise leave the icon stale.
        UpdateVolumeIcon(volumePercent);
        // M3: a fresh MpvPlayerHost always starts at speed 1x — re-apply the session's chosen
        // speed so a ladder retry's new host matches what SpeedButton/the menu are still showing.
        Player.Speed = chosenSpeed;
        loadTarget = target;
        Player.Load(target, resume, stream.UserAgent, stream.OriginalAudioLanguage, lastSubtitleUrl);

        // Backstop watchdog, not the primary failure signal — Errored (mpv's network-timeout
        // fires it for a genuinely dead stream) is. This only catches a hang Errored never
        // reports, so the window is wide: mpv has been observed taking 10-25s to reach
        // FileLoaded on a slow-but-healthy manifest, and a watchdog that beats that tears down
        // an open that was about to succeed and burns a ladder retry on nothing.
        stallTimer = dispatcher.CreateTimer();
        stallTimer.Interval = TimeSpan.FromSeconds(40);
        stallTimer.IsRepeating = false;
        stallTimer.Tick += (_, _) => { if (!hasPlayed) _ = RetryOrFailAsync("Playback did not start."); };
        stallTimer.Start();
    }

    /// Pure lookup, no side effects — lets SetPreferredHeight check whether a preference change
    /// actually lands on a different rung before touching activeQuality/disk/mpv.
    private HlsQuality ResolveQuality(int? height) =>
        ResolveQuality(height, height is null ? EffectiveRateBytesPerSecond() : null);

    /// The rate only ever reaches the Auto branch: a manual pin ignores it entirely.
    private HlsQuality ResolveQuality(int? height, double? autoRateBytesPerSecond) =>
        height is { } wanted
            ? (hlsQualities.FirstOrDefault(q => q.Height <= wanted) ?? hlsQualities[^1])
            : HlsVariantParser.AutoQuality(hlsQualities, AutoMaxHeight(), autoRateBytesPerSecond)!;

    /// The height cap for Auto: the screen, and - once a stall downshift has fired for this video -
    /// the downshifted rung too. Both apply, the lower wins; the rate cap then applies on top inside
    /// AutoQuality, so the result is the lowest of all three. A ceiling below every rung (a retry
    /// client with a different ladder) falls through to AutoQuality's lowest-rung fallback.
    private int AutoMaxHeight() => Math.Min(ScreenHeight(), autoCeilingHeight ?? int.MaxValue);

    /// The hand-edited simulated bandwidth (Mbit/s), cached for the life of the page.
    private double? SimulatedMbps()
    {
        if (!simulatedMbpsRead)
        {
            simulatedMbps = App.Session.PlayerSettings.LoadSimulatedBandwidthMbps();
            simulatedMbpsRead = true;
        }
        return simulatedMbps;
    }

    /// What Auto budgets against, in bytes per second: the simulated override when set, else the
    /// live host rate, else the last session's persisted measurement; null when none of them has
    /// anything (Auto then falls back to the screen-only pick).
    private double? EffectiveRateBytesPerSecond() =>
        SimulatedMbps() is { } mbps
            ? mbps * 125_000
            : Player?.DownloadRateBytesPerSecond ?? App.Session.PlayerSettings.LoadMeasuredBandwidthBps();

    private static string FormatMbps(double bytesPerSecond) =>
        (bytesPerSecond * 8 / 1e6).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    /// mpv reads manifests from disk happily; one scratch file per page, overwritten per load.
    /// M7: the filtered manifest's kept variant is checked before it ever reaches disk — a
    /// relative path or non-https URI there is a manifest bug (or worse) worth failing loudly
    /// on rather than silently handing mpv something it may resolve unexpectedly.
    private string WriteManifestForHeight(int? height)
    {
        double? autoRate = height is null ? EffectiveRateBytesPerSecond() : null;
        var quality = ResolveQuality(height, autoRate);
        activeQuality = quality;
        if (height is null)
        {
            var rateText = autoRate is { } r
                ? $"{FormatMbps(r)}Mbps{(SimulatedMbps() is not null ? "(simulated)" : "")}"
                : "n/a";
            WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory,
                $"auto: rate={rateText} screen={ScreenHeight()} → {quality.Height}p");
        }
        var filtered = HlsVariantParser.FilterToBandwidth(hlsMaster!, quality.Bandwidth);
        if (!HlsVariantParser.KeptVariantUriIsAbsoluteHttps(filtered))
            throw new InvalidManifestVariantException();
        var path = Path.Combine(Session.DataDirectory, "current.m3u8");
        File.WriteAllText(path, filtered);
        return path;
    }

    private int ScreenHeight()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(App.MainWindowId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
        return area.OuterBounds.Height;
    }

    /// One mpv instance per playback attempt: a ladder retry's TearDownPlayer disposes the
    /// failed host before StartAsync loops back into PlayAsync, so this never reuses a host
    /// that failed to start. Events are wired per instance and guarded by a `host == Player`
    /// check, so a stale host's already-queued callback can never touch state after a fresher
    /// host has replaced it.
    private void CreatePlayerHost()
    {
        if (Player is { } stale)
        {
            PlayerSlot.Children.Remove(stale);
            stale.Dispose();
        }

        // A fresh host has no tracks until its own FileLoaded; don't offer the previous one's.
        ClearAudioMenu();
        // Cache sizing is read once per host and applied when mpv is created (first Load), so a
        // settings.json edit takes effect from the next video.
        var settings = App.Session.PlayerSettings;
        var host = new MpvPlayerHost
        {
            CacheReadaheadSeconds = settings.CacheReadaheadSeconds(),
            CacheForwardMegabytes = settings.CacheForwardMegabytes(),
            CacheBackMegabytes = settings.CacheBackMegabytes(),
        };
        // A fresh host has cached nothing; don't leave the previous one's ranges drawn.
        BufferedMarkers.Children.Clear();
        host.BufferedRangesChanged += () => { if (!leftPage && host == Player) RenderBufferedRanges(); };
        host.Opened += () =>
        {
            if (leftPage || host != Player) return;
            openedAt = DateTimeOffset.UtcNow;
            HideOverlays();
            WakeTransport();
            // The video surface is the page's only always-present, always-in-tree focus owner —
            // without this, nothing holds keyboard focus after a fresh load (PlayerSlot is
            // IsTabStop so this succeeds).
            PlayerSlot.Focus(FocusState.Programmatic);
            // DoLoad always issues pause=no, so a fresh open is always playing — set the icon
            // directly rather than waiting on the first "pause" property-change event, which may
            // not have arrived yet.
            PlayPauseIcon.Glyph = "";
            // M4: stops both this attempt's own stall watchdog AND a ReloadKeepingPosition one
            // (quality, subtitle or mute trigger) — whichever is currently running is whatever
            // this Opened belongs to.
            reloadArmed = false;
            stallTimer?.Stop();
            if (!hasPlayed)
            {
                // Applied once — a ladder retry re-entering Opened after this must resume from
                // recorded progress, not re-seek back to the original link timestamp.
                startAt = null;
                hasPlayed = true;
                App.Session.History.Record(video!);
                StartProgressTimer();
                _ = LoadSponsorSegmentsAsync();
            }
            // M5: DoLoad's pause=no has already landed by the time Opened fires, so re-pausing
            // here (rather than right after Load()) can't race mpv's own command queue.
            if (pauseAfterReload)
            {
                pauseAfterReload = false;
                host.Pause();
            }
        };
        host.AudioTracksChanged += () => { if (!leftPage && host == Player) OnAudioTracksChanged(host); };
        host.PositionChanged += _ => { if (host == Player) { OnPlayerPosition(); UpdateTransport(); UpdateSkipButtonVisibility(); } };
        // time-pos stops ticking the instant playback pauses, so UpdateTransport (driven off
        // PositionChanged) can't be trusted to refresh the play/pause icon — mpv's own "pause"
        // property change is the live signal instead.
        host.PausedChanged += paused => { if (host == Player) PlayPauseIcon.Glyph = paused ? "" : ""; };
        host.EndReached += () => { if (host == Player) ReportProgressOnce(); };
        host.BufferingStarted += () => { if (!leftPage && host == Player) OnBufferingStarted(); };
        host.Errored += message => { if (!leftPage && host == Player) _ = RetryOrFailAsync(message); };

        Player = host;
        PlayerSlot.Children.Insert(0, host);
    }

    private async Task RetryOrFailAsync(string reason)
    {
        // Every ladder step-down is logged — this line is how the silent 360p-fallback era
        // was finally caught; keep it.
        WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory,
            $"player retry: client={lastClient} reason={reason}");
        if (leftPage || handledFailure) return;
        handledFailure = true;
        TearDownPlayer();

        if (retried)
        {
            ShowError(reason);
            return;
        }

        retried = true;
        await StartAsync(after: lastClient);
    }

    private void TogglePlayPause() => Player?.TogglePause();

    /// The one place PlayerPage asks mpv to seek: stamps lastSeekAt so the stall filter can discount
    /// the paused-for-cache a seek into an uncached range raises. Every seek the page issues goes
    /// through here - seek-bar release, the +-10 s accelerators, description timestamp links, the
    /// sponsor auto-skip and the skip button.
    private void SeekPlayer(MpvPlayerHost player, double seconds)
    {
        lastSeekAt = DateTimeOffset.UtcNow;
        player.SeekTo(seconds);
    }

    /// Level 2 of adaptive Auto. A buffering edge only counts as a genuine stall when none of our
    /// own doing explains it; filters, in order:
    ///  - reloadArmed: a quality/subtitle/mute reload is in flight, the edge belongs to its load;
    ///  - !hasPlayed: the first open (and a ladder retry's) has not landed yet;
    ///  - PostOpenStallGraceSeconds after the latest Opened: an open's own cache fill;
    ///  - PostOpenStallGraceSeconds after the latest user/sponsor seek (same constant, see tuning).
    /// And only an adaptive stream under Auto can act on one: a pinned height (preferredHeight set)
    /// or the muxed fallback (no ladder) never reaches RecordStall.
    private void OnBufferingStarted()
    {
        if (reloadArmed || !hasPlayed) return;
        if (preferredHeight is not null || hlsMaster is null) return;
        var now = DateTimeOffset.UtcNow;
        if (InStallGrace(now, openedAt) || InStallGrace(now, lastSeekAt)) return;

        var window = TimeSpan.FromSeconds(AdaptiveAutoTuning.StallWindowSeconds);
        while (recordedStalls.Count > 0 && now - recordedStalls.Peek() >= window) recordedStalls.Dequeue();
        recordedStalls.Enqueue(now);

        switch (downshiftPolicy.RecordStall(now))
        {
            case DownshiftDecision.None:
                break;
            case DownshiftDecision.Downshift:
                DownshiftOneRung(now);
                break;
            case DownshiftDecision.SuppressedCooldown:
                LogDownshift("downshift: suppressed (cooldown)");
                break;
            case DownshiftDecision.SuppressedCap:
                LogDownshift("downshift: suppressed (cap)");
                break;
        }
    }

    private static bool InStallGrace(DateTimeOffset now, DateTimeOffset? since) =>
        since is { } t && (now - t).TotalSeconds < AdaptiveAutoTuning.PostOpenStallGraceSeconds;

    private static void LogDownshift(string line) =>
        WinTube.Core.Sync.WatchProgressSync.LogTo(Session.DataDirectory, line);

    /// Steps Auto down to the rung below the one playing, for the rest of this video: records the
    /// ceiling, then reloads through the same mechanic a quality pick uses (ReloadAtCurrentQuality
    /// re-resolves Auto - now capped - rewrites the manifest, reloads at position and refreshes the
    /// "Auto - Np" label from activeQuality).
    private void DownshiftOneRung(DateTimeOffset now)
    {
        if (activeQuality is not { } current) return;
        var index = -1;
        for (var i = 0; i < hlsQualities.Count; i++)
            if (hlsQualities[i].Height == current.Height) { index = i; break; }
        if (index < 0 || index + 1 >= hlsQualities.Count)
        {
            LogDownshift("downshift: suppressed (floor)");
            return;
        }

        var target = hlsQualities[index + 1];
        var stalls = recordedStalls.Count;
        var seconds = (int)Math.Round((now - recordedStalls.Peek()).TotalSeconds);
        autoCeilingHeight = target.Height;
        ReloadAtCurrentQuality();
        // The rung actually loaded: normally the target, lower if the live rate cap bites harder.
        LogDownshift($"downshift: {stalls} stalls in {seconds}s → {(activeQuality ?? target).Height}p");
    }

    // MARK: transport bar

    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
        WakeTransport();
    }

    /// Player.PositionChanged-driven — refreshes the time label and (unless the user is
    /// mid-drag) the seek bar's position every time mpv reports a new time-pos.
    private void UpdateTransport()
    {
        if (Player is not { } p) return;
        // PlayPauseIcon is NOT set here — time-pos (this method's trigger, PositionChanged) stops
        // ticking the instant playback pauses, so the icon would freeze. MpvPlayerHost.PausedChanged
        // (wired in CreatePlayerHost) drives it live instead.
        TimeLabel.Text = $"{Fmt(p.Position, p.Duration)} / {Fmt(p.Duration, p.Duration)}";
        if (!seekBarHeld)
        {
            SeekBar.Maximum = p.Duration;
            SeekBar.Value = p.Position;
        }
        // Duration is 0 until mpv reports it, then holds steady — comparing against the value
        // markers were last drawn for catches "duration just became known" without rebuilding
        // the overlay on every tick.
        if (Math.Abs(p.Duration - markersDuration) > 0.01)
        {
            markersDuration = p.Duration;
            RenderSponsorMarkers();
            RenderBufferedRanges();
        }
    }

    /// h:mm:ss once the video runs an hour or more, else m:ss — the DURATION decides the format,
    /// applied to both numbers so "1:02:03 / 1:30:00" and "0:45 / 3:20" line up.
    private static string Fmt(double seconds, double durationSeconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return durationSeconds >= 3600
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{(int)span.TotalMinutes}:{span.Seconds:D2}";
    }

    private void OnSeekBarPointerMoved(object sender, PointerRoutedEventArgs e) =>
        ShowSeekHover(e.GetCurrentPoint(SeekBar).Position.X);

    private void OnSeekBarPointerLeft(object sender, PointerRoutedEventArgs e) =>
        SeekHoverBubble.Visibility = Visibility.Collapsed;

    /// Time under the pointer, as a bubble above the seek bar. Inverts the shared overlay mapping
    /// (TryGetTrackMapping): x(t) = inset + t / duration * span  =>  t = (x - inset) / span *
    /// duration, clamped to [0, duration] so the 9 px of track either side of the thumb centre
    /// range read as the first/last second. Hidden while the mapping is unusable (duration unknown).
    /// Called on every move, so it also re-shows the bubble after a drag's capture-lost hid it with
    /// the pointer still over the bar. x is relative to SeekBar, which shares SeekHoverLayer's origin.
    private void ShowSeekHover(double x)
    {
        if (!TryGetTrackMapping(out var duration, out var inset, out var span))
        {
            SeekHoverBubble.Visibility = Visibility.Collapsed;
            return;
        }

        var time = Math.Clamp((x - inset) / span, 0, 1) * duration;
        SeekHoverText.Text = Fmt(time, duration);
        SeekHoverBubble.Visibility = Visibility.Visible;
        SeekHoverBubble.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = SeekHoverBubble.DesiredSize;

        // Centred on the pointer, kept inside the bar's own width.
        var left = Math.Clamp(x - size.Width / 2, 0, Math.Max(0, SeekBar.ActualWidth - size.Width));
        Canvas.SetLeft(SeekHoverBubble, left);
        Canvas.SetTop(SeekHoverBubble, -(size.Height + 4));
    }

    private void OnSeekBarPointerPressed(object sender, PointerRoutedEventArgs e) => seekBarHeld = true;

    private void OnSeekBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (Player is { } seekPlayer) SeekPlayer(seekPlayer, SeekBar.Value);
        seekBarHeld = false;
        WakeTransport();
    }

    /// Debounced volume persistence: the transport slider fires ValueChanged per notch of a
    /// drag, so the write waits half a second after the last change. Saving here (rather than
    /// only on page exit) also survives the window being closed mid-playback.
    private void OnVolumeSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var newVolume = e.NewValue / 100;
        if (Player is { } p) p.Volume = newVolume;
        pendingVolume = newVolume;
        UpdateVolumeIcon(e.NewValue);
        volumeSaveTimer ??= CreateVolumeSaveTimer();
        volumeSaveTimer.Stop();
        volumeSaveTimer.Start();

        var nowMuted = e.NewValue <= 0;
        if (nowMuted == wasMuted) return;
        wasMuted = nowMuted;
        ApplyMuteState(nowMuted);
    }

    /// The mute automation: asks the pure state machine what a mute edge does to the current
    /// subtitle mode and carries it out. Off + mute attaches the best track as AutoMute;
    /// AutoMute + unmute detaches it; Manual never moves. Neither transition persists — the saved
    /// preference is the user's, and the automation must not rewrite it.
    ///
    /// Reload cost: with a load live, attaching or detaching changes the URL and so reloads at
    /// position (ReloadKeepingPosition) — a brief rebuffer, accepted since mute is deliberate.
    /// A Manual track is already attached, so mute/unmute on it touches nothing at all.
    private void ApplyMuteState(bool nowMuted)
    {
        var next = SubtitleMuteMachine.OnMuteChanged(subtitleMode, nowMuted);
        if (next == subtitleMode) return;
        if (next == SubtitleMode.AutoMute)
        {
            // A video with nothing to attach stays Off — there is nothing for unmute to retire.
            if (BestMuteChoice() is { } best) ApplySubtitleChoice(best, SubtitleMode.AutoMute);
        }
        else
        {
            ApplySubtitleChoice(null, next);
        }
    }

    /// The track the mute automation attaches: the saved language preference through the usual
    /// chain, else — no preference, or none that matches this video — the video's default track
    /// (the first manual one, falling back to the first of any kind), as a plain Track choice.
    private SubtitleChoice? BestMuteChoice()
    {
        if (SubtitleSelection.Choose(captionTracks, App.Session.PlayerSettings.LoadSubtitleLanguage()) is { } preferred)
            return preferred;
        var fallback = captionTracks.FirstOrDefault(t => !t.IsAutoGenerated) ?? captionTracks.FirstOrDefault();
        return fallback is null ? null : new SubtitleChoice(fallback.VttUrl, fallback.Language, fallback.Name, false);
    }

    /// percent is 0..100, matching VolumeSlider's own range — mute, then three roughly-even
    /// bands, the standard Segoe Fluent glyph set for a volume control.
    private void UpdateVolumeIcon(double percent) => VolumeIcon.Glyph = percent switch
    {
        <= 0 => "",
        < 33 => "",
        < 66 => "",
        _ => "",
    };

    private DispatcherQueueTimer CreateVolumeSaveTimer()
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(500);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => App.Session.PlayerSettings.SaveVolume(pendingVolume);
        return timer;
    }

    private void BuildSpeedMenu()
    {
        foreach (var value in SpeedOptions)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{value:0.##}×", GroupName = "speed", IsChecked = value == 1 };
            item.Click += (_, _) =>
            {
                chosenSpeed = value;
                if (Player is { } p) p.Speed = value;
                SpeedButton.Content = $"{value:0.##}×";
            };
            if (value == 1) speedOneItem = item;
            SpeedFlyout.Items.Add(item);
        }
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e)
    {
        isFullScreen = !isFullScreen;
        ApplyFullScreen();
    }

    /// Same ButtonBase/RangeBase ancestor guard as OnPlayerTapped, so a double-click on an
    /// actual control (e.g. double-clicking the play button) never toggles fullscreen. The
    /// gesture already fired a single Tapped first, which flipped play/pause — that flip is
    /// undone here rather than reworked with a click-delay timer, so a double-click always
    /// leaves playback exactly as it was and only changes fullscreen.
    private void OnPlayerDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (MouseBackGuard.SuppressTap()) return;
        if (Player is null) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.Primitives.RangeBase) return;
        }
        TogglePlayPause();
        isFullScreen = !isFullScreen;
        ApplyFullScreen();
        PlayerSlot.Focus(FocusState.Programmatic);
    }

    /// Pushes isFullScreen out to the window presenter/shell chrome, the title row (video-only
    /// in fullscreen), and the button's own glyph. The single place both the button and the
    /// double-click gesture funnel through.
    private void ApplyFullScreen()
    {
        App.Window!.SetPlayerFullScreen(isFullScreen);
        TitleRow.Visibility = isFullScreen ? Visibility.Collapsed : Visibility.Visible;
        // I1: TitleRow (and the QualityButton/CommentsButton it carries) just went away, or came
        // back — the transport bar's own copies mirror that.
        UpdateBarControlsVisibility();
        FullScreenIcon.Glyph = isFullScreen ? "" : "";
        WakeTransport();
    }

    /// I1: decides whether the transport bar's Quality/Comments controls show at all — they
    /// exist only so those two stay reachable while fullscreen has collapsed TitleRow, so they
    /// track isFullScreen (and, for Quality, whatever the title row's own button is currently
    /// showing — QualityButton.Visibility is Visible once a stream has resolved, Collapsed
    /// before that).
    private void UpdateBarControlsVisibility()
    {
        QualityButtonBar.Visibility = isFullScreen && QualityButton.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
        CommentsButtonBar.Visibility = isFullScreen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTransportEntered(object sender, PointerRoutedEventArgs e) => transportPointerOverBar = true;

    private void OnTransportExited(object sender, PointerRoutedEventArgs e) => transportPointerOverBar = false;

    /// Mirrors OnCommentsPanelTapped: a tap on the bar's own background must never bubble to
    /// OnPlayerTapped and toggle playback.
    private void OnTransportBarTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private void OnVideoGridPointerMoved(object sender, PointerRoutedEventArgs e) => WakeTransport();

    /// Shows the bar and the cursor, and restarts the 3 s auto-hide countdown. Called on Opened,
    /// on any pointer movement over the video, and on the keys that act on playback.
    private void WakeTransport()
    {
        SetTransportVisible(true);
        transportHideTimer ??= CreateTransportHideTimer();
        transportHideTimer.Stop();
        transportHideTimer.Start();
    }

    private DispatcherQueueTimer CreateTransportHideTimer()
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(3);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            // Never hides while there's nothing to show controls for, while paused, while the
            // pointer is resting on the bar itself, or while a flyout it owns is open.
            if (Player is null || Player.IsPaused || transportPointerOverBar ||
                VolumeFlyout.IsOpen || SpeedFlyout.IsOpen || QualityFlyoutBar.IsOpen ||
                SubtitleFlyout.IsOpen || AudioFlyout.IsOpen) return;
            SetTransportVisible(false);
        };
        return timer;
    }

    private void SetTransportVisible(bool visible)
    {
        TransportBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) SeekHoverBubble.Visibility = Visibility.Collapsed;
        ProtectedCursor = visible ? InputSystemCursor.Create(InputSystemCursorShape.Arrow) : null;
    }

    // MARK: progress

    private void StartProgressTimer()
    {
        if (progressTimer is not null) return;
        progressTimer = dispatcher.CreateTimer();
        progressTimer.Interval = TimeSpan.FromSeconds(5);
        progressTimer.IsRepeating = true;
        progressTimer.Tick += (_, _) => ReportProgressOnce();
        progressTimer.Start();
    }

    private void ReportProgressOnce()
    {
        if (video is null || Player is not { } p || p.Duration <= 0) return;
        App.Session.Progress.Report(video.Id, p.Position, p.Duration);
    }

    // MARK: SponsorBlock

    /// Fired after playback has started, so a slow SponsorBlock server can never delay the
    /// first frame. Failures yield an empty list inside the service; nothing to catch here
    /// beyond the page-left guard.
    private async Task LoadSponsorSegmentsAsync()
    {
        var segments = await App.Session.SponsorBlock.FetchSegmentsAsync(video!.Id);
        if (leftPage || segments.Count == 0) return;
        sponsorSegments = segments;
        RenderSponsorMarkers();
    }

    /// Rebuilds the yellow segment rectangles over SeekBar from scratch — called on segments
    /// loading, duration becoming known (from UpdateTransport) and SeekBar resizing. Cheap
    /// enough (a handful of segments) to just clear and redraw rather than diff.
    ///
    /// A WinUI Slider's track fills the whole control, but the THUMB CENTER only ever travels
    /// [inset, ActualWidth-inset], inset = half the thumb's width — the same rule mpv's own
    /// position feeds SeekBar.Value through, so a marker drawn at bare fraction*ActualWidth
    /// (no inset) sits measurably left of where that time actually lands on the visible track.
    /// GetThumbInset() reads the live Thumb part so this tracks the real template/theme instead
    /// of a hardcoded guess.
    private void RenderSponsorMarkers()
    {
        SponsorMarkers.Children.Clear();
        if (!TryGetTrackMapping(out var duration, out var inset, out var span)) return;

        foreach (var segment in sponsorSegments)
        {
            var (left, width) = OverlayBar(segment.Start, segment.Start + segment.Duration, duration, inset, span);
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = 4, Fill = SponsorMarkerBrush };
            Canvas.SetLeft(rect, left);
            SponsorMarkers.Children.Add(rect);
        }
    }

    /// Draws the cached ranges as light-grey bars under the sponsor markers. Same mapping as
    /// RenderSponsorMarkers (TryGetTrackMapping), so a range edge lands exactly where the thumb
    /// would be at that time. Called on BufferedRangesChanged, duration becoming known and resize;
    /// a handful of ranges, so clear-and-redraw.
    private void RenderBufferedRanges()
    {
        BufferedMarkers.Children.Clear();
        if (Player is not { } p || !TryGetTrackMapping(out var duration, out var inset, out var span)) return;

        foreach (var (start, end) in p.BufferedRanges)
        {
            var from = Math.Clamp(start, 0, duration);
            var to = Math.Clamp(end, 0, duration);
            if (to <= from) continue;
            var (left, width) = OverlayBar(from, to, duration, inset, span);
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = width, Height = 4, Fill = BufferedRangeBrush };
            Canvas.SetLeft(rect, left);
            BufferedMarkers.Children.Add(rect);
        }
    }

    /// Left/width of one overlay bar for the time span [from, to]. Interior edges use the exact
    /// thumb mapping, but the Slider's own fill runs to the CONTROL's edges (the thumb inset only
    /// limits where the thumb centre travels), so an edge within 0.5 s of the start/end is pulled out
    /// to x=0 / the full ActualWidth, or a ~inset-wide stub of bare slider fill shows beside the bar.
    private (double Left, double Width) OverlayBar(double from, double to, double duration, double inset, double span)
    {
        const double edgeSeconds = 0.5;
        var left = from <= edgeSeconds ? 0 : inset + from / duration * span;
        var right = to >= duration - edgeSeconds ? SeekBar.ActualWidth : inset + to / duration * span;
        return (left, Math.Max(1, right - left));
    }

    /// The shared overlay mapping: x(t) = inset + t / duration * span, with span = trackWidth -
    /// 2 * inset (see RenderSponsorMarkers for why the inset). False while the seek bar has no
    /// usable width or duration is unknown, in which case there is nothing to draw.
    private bool TryGetTrackMapping(out double duration, out double inset, out double span)
    {
        duration = markersDuration;
        inset = GetThumbInset();
        span = SeekBar.ActualWidth - 2 * inset;
        return span > 0 && duration > 0;
    }

    /// Half the width of SeekBar's own Thumb part, found once via VisualTreeHelper and cached —
    /// the Thumb only has a real ActualWidth after the control's template has been applied and
    /// measured, which is already true by the time RenderSponsorMarkers runs (segments load
    /// post-Opened; the transport bar, and SeekBar within it, is on screen well before that).
    /// Falls back to 9 (half of WinUI's SliderHorizontalThumbWidth=18, confirmed in the
    /// Microsoft.WindowsAppSDK 1.7 generic.xaml this project references) for the rare call that
    /// somehow lands before the template exists, so markers are never left unrendered.
    private double GetThumbInset()
    {
        if (thumbInset is { } cached) return cached;
        if (FindDescendant<Microsoft.UI.Xaml.Controls.Primitives.Thumb>(SeekBar) is { ActualWidth: > 0 } thumb)
        {
            thumbInset = thumb.ActualWidth / 2;
            return thumbInset.Value;
        }
        return 9;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private void ClearSponsorMarkers()
    {
        SponsorMarkers.Children.Clear();
        BufferedMarkers.Children.Clear();
        markersDuration = -1;
        skipCandidate = null;
        SkipBlockButton.Visibility = Visibility.Collapsed;
    }

    private void OnSeekBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderSponsorMarkers();
        RenderBufferedRanges();
    }

    /// Shows the skip-ahead button from 10s before a segment's start through its end. Also stays
    /// visible INSIDE a segment: sponsorSkipped's skip-once rule means auto-skip (OnPlayerPosition)
    /// won't fire again for a segment the user has already rewound back into, so the button is the
    /// only way to jump past it a second time. When several segments match (overlap/adjacency),
    /// the one with the furthest End wins — that's the single seek that clears all of them. Runs
    /// alongside OnPlayerPosition off the same PositionChanged tick.
    private void UpdateSkipButtonVisibility()
    {
        if (Player is not { } p)
        {
            skipCandidate = null;
            SkipBlockButton.Visibility = Visibility.Collapsed;
            return;
        }
        var time = p.Position;
        skipCandidate = sponsorSegments
            .Where(s => time >= s.Start - 10 && time < s.End)
            .OrderByDescending(s => s.End)
            .FirstOrDefault();
        SkipBlockButton.Visibility = skipCandidate is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnSkipBlockClick(object sender, RoutedEventArgs e)
    {
        if (skipCandidate is not { } segment || Player is not { } p) return;
        // Same clamp-to-duration as the auto-skip path (OnPlayerPosition).
        var duration = p.Duration;
        var target = duration > 0 ? Math.Min(segment.End, duration) : segment.End;
        SeekPlayer(p, target);
        // Mirrors OnPlayerPosition's bookkeeping so a rewind back into this segment plays it
        // normally instead of being silently re-skipped.
        sponsorSkipped.Add(segment.Id);
        ShowSkipToast($"Skipped {segment.Category.DisplayName()} · {segment.Duration:F0}s");
        skipCandidate = null;
        SkipBlockButton.Visibility = Visibility.Collapsed;
        WakeTransport();
    }

    /// Runs on every mpv position update instead of a 250 ms poll — PositionChanged fires at
    /// least that often during normal playback and also on every seek, so a resume landing
    /// mid-sponsor or the user scrubbing into one is still caught.
    private void OnPlayerPosition()
    {
        if (leftPage || Player is not { } p || p.IsPaused) return;
        var time = p.Position;
        if (SponsorSegment.NextToSkip(sponsorSegments, time, sponsorSkipped) is not { } segment)
            return;

        sponsorSkipped.Add(segment.Id);
        // A segment running to the end has nothing to seek to — clamping to the duration
        // parks playback at the last frame, which is what "the video is over" looks like.
        var duration = p.Duration;
        var target = duration > 0 ? Math.Min(segment.End, duration) : segment.End;
        SeekPlayer(p, target);
        ShowSkipToast($"Skipped {segment.Category.DisplayName()} · {segment.Duration:F0}s");
    }

    /// A newer skip replaces the toast and owns its timer, so back-to-back skips don't have
    /// the first skip's timer hide the second skip's message. The hide-toast Tick handler is
    /// subscribed only the first time the timer is created.
    private void ShowSkipToast(string message)
    {
        SkipToastText.Text = message;
        SkipToast.Visibility = Visibility.Visible;
        toastTimer?.Stop();
        if (toastTimer is null)
        {
            toastTimer = dispatcher.CreateTimer();
            toastTimer.Interval = TimeSpan.FromSeconds(3);
            toastTimer.IsRepeating = false;
            toastTimer.Tick += (_, _) => SkipToast.Visibility = Visibility.Collapsed;
        }
        toastTimer.Start();
    }

    // MARK: teardown + UI state

    private void TearDownPlayer()
    {
        // A volume change still waiting out its debounce is flushed now — leaving the page
        // must not lose the last adjustment.
        if (volumeSaveTimer is { IsRunning: true })
        {
            volumeSaveTimer.Stop();
            App.Session.PlayerSettings.SaveVolume(pendingVolume);
        }
        transportHideTimer?.Stop();
        SetTransportVisible(false);

        progressTimer?.Stop();
        progressTimer = null;
        stallTimer?.Stop();
        stallTimer = null;

        if (Player is { } p)
        {
            // The next session's first Auto pick is seeded from this one's last rate. A simulated
            // bandwidth is a test override, so the real measurement is neither used nor stored then.
            // Only after the video has actually played: a failed open leaves a one-sample rate that
            // would overwrite a good earlier measurement.
            if (hasPlayed && SimulatedMbps() is null && p.DownloadRateBytesPerSecond is { } rate)
                App.Session.PlayerSettings.SaveMeasuredBandwidthBps(rate);
            PlayerSlot.Children.Remove(p);
            p.Dispose();
        }
        Player = null;
    }

    private void ShowLoading()
    {
        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        transportHideTimer?.Stop();
        SetTransportVisible(false);
    }

    private void HideOverlays()
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed;
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e) => Frame.GoBack();

    // MARK: quality picker

    /// Builds the picker from the filtered manifest's rungs: Auto plus one entry per height.
    /// I1: populates BOTH the title row's QualityFlyout and the transport bar's QualityFlyoutBar
    /// through the same PopulateQualityFlyout routine, so the fullscreen-only bar copy (RULING:
    /// visible only while isFullScreen, since TitleRow — and the flyout's usual home — is
    /// Collapsed there) never drifts from the title row's.
    private void BuildQualityMenu()
    {
        if (hlsQualities.Count == 0) return;

        QualityButton.IsEnabled = true;
        QualityButton.Visibility = Visibility.Visible;
        QualityButtonBar.IsEnabled = true;
        UpdateQualityLabel(); // also (re)populates both flyouts
        UpdateBarControlsVisibility();
    }

    /// Shared menu-building path for QualityFlyout (title row) and QualityFlyoutBar (transport
    /// bar, I1) — same items, same click handler, rebuilt together on every label update so a
    /// pick made through one flyout is reflected as checked in the other the next time it opens
    /// (the two are independent RadioMenuFlyoutItem groups since they're separate MenuFlyouts,
    /// so nothing else keeps them in sync).
    private void PopulateQualityFlyout(MenuFlyout flyout)
    {
        flyout.Items.Clear();
        var auto = new RadioMenuFlyoutItem { Text = "Auto", GroupName = "quality", IsChecked = preferredHeight is null };
        auto.Click += (_, _) => SetPreferredHeight(null);
        flyout.Items.Add(auto);
        foreach (var quality in hlsQualities)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = quality.Label,
                GroupName = "quality",
                // Checked against the RESOLVED rung (activeQuality), not the raw preference: a
                // preference carried over from a taller video (e.g. 1080 preferred, this ladder
                // tops at 360) must still land on the rung that's actually playing, not on
                // nothing. Auto owns the checked mark whenever there's no manual preference.
                IsChecked = preferredHeight is not null && quality.Height == activeQuality?.Height,
            };
            var height = quality.Height;
            item.Click += (_, _) => SetPreferredHeight(height);
            flyout.Items.Add(item);
        }
        AppendRateFooter(flyout);
    }

    /// Footer text for the quality flyouts, or null when there is no rate to show.
    private string? RateFooterText() =>
        SimulatedMbps() is { } mbps
            ? $"Simulated: {FormatMbps(mbps * 125_000)} Mbit/s"
            : (Player?.DownloadRateBytesPerSecond ?? App.Session.PlayerSettings.LoadMeasuredBandwidthBps()) is { } rate
                ? $"Measured: {FormatMbps(rate)} Mbit/s"
                : null;

    private const string RateFooterTag = "rate-footer";

    /// A separator plus a disabled (greyed, unclickable, unchecked) text line under the picker's
    /// items, so the rate Auto is acting on is visible. Both carry RateFooterTag so a refresh can
    /// find and replace them. Never added to an empty flyout (the muxed fallback's single rung).
    private void AppendRateFooter(MenuFlyout flyout)
    {
        if (flyout.Items.Count == 0 || RateFooterText() is not { } text) return;
        flyout.Items.Add(new MenuFlyoutSeparator { Tag = RateFooterTag });
        flyout.Items.Add(new MenuFlyoutItem { Text = text, IsEnabled = false, Tag = RateFooterTag });
    }

    /// The live rate moves while the flyout is closed, so each opening rebuilds just the footer.
    private void OnQualityFlyoutOpening(object sender, object e)
    {
        if (sender is not MenuFlyout flyout) return;
        for (var i = flyout.Items.Count - 1; i >= 0; i--)
            if (flyout.Items[i].Tag is RateFooterTag) flyout.Items.RemoveAt(i);
        AppendRateFooter(flyout);
    }

    /// Mode (Auto vs a manual pin) and playback are independent, so the stored choice always
    /// changes and the label/checked item always follow it — the ONLY thing that's conditional
    /// is whether mpv needs to reload. Pinning the exact rung Auto is already showing (or Auto
    /// resolving back to a rung that was pinned) must still flip the label and pin/unpin the
    /// mode, just without touching mpv, since the manifest on disk wouldn't change. Re-clicking
    /// the identical stored preference is the only true no-op (nothing changed at all). Non-
    /// adaptive streams and a switch landing mid-teardown/retry (Player null) store only — the
    /// next PlayAsync's WriteManifestForHeight picks the preference up.
    private void SetPreferredHeight(int? height)
    {
        if (height == preferredHeight) return;
        preferredHeight = height;
        if (hlsMaster is null || Player is null) return;
        if (ResolveQuality(height).Height == activeQuality?.Height)
        {
            UpdateQualityLabel();
            return;
        }
        ReloadAtCurrentQuality();
    }

    /// Reloads the already-started host onto the new rung's single-variant manifest, resuming
    /// from the position it was just showing (see ReloadKeepingPosition).
    ///
    /// M2: WriteManifestForHeight touches disk (and now, M7, validates the kept variant's URI)
    /// on every call, not just the first — an IOException/UnauthorizedAccessException or an
    /// InvalidManifestVariantException from a menu click is routed through RetryOrFailAsync
    /// like any other failed attempt, instead of escaping the click handler and killing the
    /// process.
    private void ReloadAtCurrentQuality()
    {
        if (hlsMaster is null || Player is null) return;

        string path;
        try
        {
            path = WriteManifestForHeight(preferredHeight);
        }
        catch (InvalidManifestVariantException e)
        {
            _ = RetryOrFailAsync(e.Message);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = RetryOrFailAsync($"Manifest write failed: {e.Message}");
            return;
        }

        ReloadKeepingPosition(path, "Quality switch did not complete.");
        UpdateQualityLabel();
    }

    /// The shared reload mechanic behind the quality, subtitle and mute-driven subtitle reloads: mpv's own
    /// "loadfile ... replace" on the existing MpvPlayerHost, not a fresh host — Load() only builds
    /// native state when nothing has started yet, so this is cheap and keeps the render pipeline
    /// (and its cold-start race, see MpvPlayerHost.OnRenderReady) out of a routine switch. Always
    /// re-attaches lastSubtitleUrl: mpv drops an external subtitle with the file it was added to.
    /// Before the first Opened of this attempt lands, Player.Position is still 0 (mpv hasn't
    /// seeked yet) — hasPlayed gates between that and the seeded lastKnownPosition, so a switch
    /// hit during the loading ring reloads at the real resume point instead of the start.
    ///
    /// M4: restarts the stall watchdog, stopping the prior one first — which also covers a switch
    /// clicked mid-open, since the original attempt's own timer never gets a chance to fire once
    /// this one replaces it. M5: captures IsPaused before the reload and hands it to host.Opened
    /// (see CreatePlayerHost) to re-apply once the new file is actually open, since DoLoad always
    /// issues pause=no and re-pausing here would race that.
    private void ReloadKeepingPosition(string target, string stallReason)
    {
        if (Player is not { } player) return;
        var position = hasPlayed ? player.Position : lastKnownPosition;
        // A second reload landing inside the first's open window sees IsPaused false (the first
        // load's pause=no already went out), so a re-pause still pending from that first reload
        // is carried forward instead of being overwritten with false.
        var repause = player.IsPaused || (reloadArmed && pauseAfterReload);

        stallTimer?.Stop();
        reloadArmed = true;
        stallTimer = dispatcher.CreateTimer();
        stallTimer.Interval = TimeSpan.FromSeconds(40);
        stallTimer.IsRepeating = false;
        stallTimer.Tick += (_, _) => { if (reloadArmed) _ = RetryOrFailAsync(stallReason); };
        stallTimer.Start();

        pauseAfterReload = repause;
        loadTarget = target;
        player.Load(target, position, lastUserAgent!, lastAudioLanguage, lastSubtitleUrl);
    }

    // MARK: subtitle picker

    /// Rebuilds the CC menu for the resolved captions: Off, one entry per track (YouTube's own
    /// Name, which already carries "(auto-generated)" for ASR), and "Translate to English" only
    /// when the video has no English track of its own. The button shows only when there is
    /// anything to pick. Checked marks come from activeSubtitle.
    private void BuildSubtitleMenu()
    {
        SubtitleFlyout.Items.Clear();
        subtitleItems.Clear();
        SubtitleButton.Visibility = captionTracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (captionTracks.Count == 0) return;

        AddSubtitleItem("Off", null);
        foreach (var track in captionTracks)
            AddSubtitleItem(track.Name, new SubtitleChoice(track.VttUrl, track.Language, track.Name, false));

        var hasEnglish = captionTracks.Any(t =>
            t.Language == "en" || t.Language.StartsWith("en-", StringComparison.Ordinal));
        // With no English track, Choose(.., "en") is exactly the translation of the default track.
        if (!hasEnglish && SubtitleSelection.Choose(captionTracks, "en") is { } translation)
            AddSubtitleItem("Translate to English", translation);

        UpdateSubtitleChecks();
    }

    private void AddSubtitleItem(string text, SubtitleChoice? choice)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = "subtitle" };
        item.Click += (_, _) => OnSubtitlePicked(choice);
        SubtitleFlyout.Items.Add(item);
        subtitleItems.Add((item, choice));
    }

    /// A menu pick is the only caller that persists: Off stores "no preference", a track stores its
    /// language. Re-picking what is already attached (same URL, same mode) reloads nothing but
    /// still persists (spec: a menu pick always stores the preference, so Off-when-already-Off
    /// still clears it). Only the menu items' Click handlers reach this method.
    private void OnSubtitlePicked(SubtitleChoice? choice)
    {
        var mode = SubtitleMuteMachine.OnManualPick(pickedOff: choice is null);
        if (choice?.Url == lastSubtitleUrl && mode == subtitleMode)
        {
            UpdateSubtitleChecks(); // a click re-checks the radio item either way; keep it truthful
            App.Session.PlayerSettings.SaveSubtitleLanguage(choice?.Language);
            return;
        }
        ApplySubtitleChoice(choice, mode, persist: true);
    }

    /// Single entry point for changing what is attached (Task 6's mute automation calls it too).
    /// Stores the state, follows it in the menu, optionally persists the language preference, and —
    /// only if the attached URL actually changed while a load is live — reloads at the current
    /// position through ReloadKeepingPosition. Persistence is explicit rather than derived
    /// from mode: a user's Off must clear the preference, while the auto-apply on load and the
    /// mute automation's transitions must leave it alone.
    private void ApplySubtitleChoice(SubtitleChoice? choice, SubtitleMode mode, bool persist = false)
    {
        var urlChanged = choice?.Url != lastSubtitleUrl;
        activeSubtitle = choice;
        subtitleMode = mode;
        lastSubtitleUrl = choice?.Url;
        UpdateSubtitleChecks();
        if (persist) App.Session.PlayerSettings.SaveSubtitleLanguage(choice?.Language);
        if (urlChanged && Player is not null && loadTarget is { } target)
            ReloadKeepingPosition(target, "Subtitle switch did not complete.");
    }

    private void UpdateSubtitleChecks()
    {
        foreach (var (item, choice) in subtitleItems)
            item.IsChecked = choice?.Url == activeSubtitle?.Url;
    }

    // MARK: audio track picker

    /// Runs after every FileLoaded (first load, quality/subtitle/mute reload, ladder retry): the
    /// tracks can differ per quality rung, so the menu is rebuilt each time. If the viewer already
    /// picked a dub on this video and mpv's alang pick on the new file landed elsewhere, the pick
    /// is re-applied — a live `aid` switch, no further reload.
    private void OnAudioTracksChanged(MpvPlayerHost host)
    {
        ReapplyChosenAudio(host);
        BuildAudioMenu(host);
    }

    private void ReapplyChosenAudio(MpvPlayerHost host)
    {
        if (chosenAudioLanguage is not { } wanted) return;
        var tracks = host.AudioTracks;
        // mpv's own pick already carries the language: leave it, even when a second track shares it.
        if (tracks.FirstOrDefault(t => t.Id == host.SelectedAudioId)?.Language == wanted) return;
        var match = tracks.FirstOrDefault(t => t.Language == wanted)
            ?? tracks.FirstOrDefault(t => t.Language is { } l && PrimarySubtag(l) == PrimarySubtag(wanted));
        if (match is not null && match.Id != host.SelectedAudioId) host.SelectAudioTrack(match.Id);
    }

    private static string PrimarySubtag(string language)
    {
        var dash = language.IndexOf('-');
        return dash < 0 ? language : language[..dash];
    }

    /// One radio entry per audio track, the button shown only when there is a choice to make (a
    /// muxed single-track fallback, or an adaptive rung with one rendition, hides it). The label
    /// is the track's Title, else its Language, else "Track N" by position.
    private void BuildAudioMenu(MpvPlayerHost host)
    {
        ClearAudioMenu();
        var tracks = host.AudioTracks;
        if (tracks.Count <= 1) return;

        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var label = !string.IsNullOrWhiteSpace(track.Title) ? track.Title
                : !string.IsNullOrWhiteSpace(track.Language) ? track.Language
                : $"Track {i + 1}";
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "audiotrack" };
            item.Click += (_, _) => OnAudioPicked(track);
            AudioFlyout.Items.Add(item);
            audioItems.Add((item, track.Id));
        }
        AudioButton.Visibility = Visibility.Visible;
        UpdateAudioChecks();
    }

    private void ClearAudioMenu()
    {
        AudioFlyout.Items.Clear();
        audioItems.Clear();
        AudioButton.Visibility = Visibility.Collapsed;
    }

    private void OnAudioPicked(AudioTrackInfo track)
    {
        chosenAudioLanguage = track.Language;
        Player?.SelectAudioTrack(track.Id);
        UpdateAudioChecks();
    }

    /// Follows the host's SelectedAudioId (what mpv picked via alang until a pick is made); with no
    /// track flagged selected, the first entry shows as checked.
    private void UpdateAudioChecks()
    {
        var selected = Player?.SelectedAudioId ?? (audioItems.Count > 0 ? audioItems[0].TrackId : (int?)null);
        foreach (var (item, id) in audioItems)
            item.IsChecked = id == selected;
    }

    private void UpdateQualityLabel()
    {
        if (activeQuality is not { } quality) return;
        var text = preferredHeight is null ? $"Auto · {quality.Height}p" : quality.Label;
        QualityLabel.Text = text;
        QualityLabelBar.Text = text;
        PopulateQualityFlyout(QualityFlyout);
        PopulateQualityFlyout(QualityFlyoutBar);
    }

    /// Clicking the video surface toggles play/pause. Tapped bubbles up from the transport
    /// controls too, and their invisible root layout spans the full frame — so instead
    /// of fencing off the controls as a region, only a tap that actually landed on an
    /// interactive control (a button, a slider) is left alone.
    ///
    /// A mouse-back press raises Tapped here too (WinUI: the side buttons raise Tapped but never
    /// Click) — without the MouseBackGuard check, pressing mouse-back over the player would both
    /// navigate back and toggle play/pause. See MainWindow's PointerPressed handler.
    private void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (MouseBackGuard.SuppressTap()) return;
        if (Player is null) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.Primitives.RangeBase) return;
        }
        TogglePlayPause();
        // Reclaims focus for the video surface every tap — a tap that happens to land while some
        // other control (e.g. a just-closed flyout's remnant) holds focus would otherwise leave
        // keyboard shortcuts dead until the next click on an actual Control.
        PlayerSlot.Focus(FocusState.Programmatic);
    }

    // MARK: channel link

    private void OnAuthorEntered(object sender, PointerRoutedEventArgs e) =>
        AuthorLink.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

    private void OnAuthorExited(object sender, PointerRoutedEventArgs e) =>
        AuthorLink.TextDecorations = Windows.UI.Text.TextDecorations.None;

    private void OnAuthorTapped(object sender, TappedRoutedEventArgs e)
    {
        if (MouseBackGuard.SuppressTap()) return;
        if (video?.ChannelId is not { } channelId) return;
        Frame.Navigate(typeof(ChannelPage), new ChannelRequest(channelId, video.Author));
    }

    // MARK: copy link

    private void OnCopyLink(SplitButton sender, SplitButtonClickEventArgs args) =>
        CopyLink(YouTubeLink.For(video!.Id));

    /// The label carries the live position captured when the flyout opens, so what the item
    /// says is exactly what a click copies.
    private void OnCopyFlyoutOpening(object sender, object e)
    {
        flyoutPosition = Player is { } p ? TimeSpan.FromSeconds(p.Position) : TimeSpan.Zero;
        CopyLinkAtItem.Text = $"Copy link at {YouTubeLink.Format(flyoutPosition)}";
    }

    private void OnCopyLinkAt(object sender, RoutedEventArgs e) =>
        CopyLink(YouTubeLink.For(video!.Id, flyoutPosition));

    private void CopyLink(string url)
    {
        var package = new DataPackage();
        package.SetText(url);
        Clipboard.SetContent(package);
        ShowSkipToast("Link copied");
    }

    // MARK: description

    /// A fresh video always starts with no description offered at all — TitleChevron collapsed,
    /// the panel closed and empty — until this video's own PlayAsync (SetDescription) reports
    /// what the resolved stream carried.
    private void ResetDescription()
    {
        description = null;
        TitleChevron.Visibility = Visibility.Collapsed;
        DescriptionPanel.Visibility = Visibility.Collapsed;
        DescriptionBody.Blocks.Clear();
    }

    private void SetDescription(string? text)
    {
        description = text;
        TitleChevron.Visibility = HasDescription ? Visibility.Visible : Visibility.Collapsed;
        // A ladder retry that lands on a client with no description (or none at all) must not
        // leave a stale panel open and clickable for content that's no longer there.
        if (!HasDescription && DescriptionPanel.Visibility == Visibility.Visible)
            DescriptionPanel.Visibility = Visibility.Collapsed;
        BuildDescriptionContent();
    }

    /// Turns the parsed runs into one Paragraph's Inlines — a plain Run for Text, a Hyperlink
    /// for Timestamp (seeks the live player) and Url (hands off to the shell/browser). Rebuilt
    /// from scratch on every SetDescription; a video's description is small enough that this is
    /// far simpler than diffing inline collections.
    private void BuildDescriptionContent()
    {
        var paragraph = new Paragraph();
        foreach (var run in DescriptionText.Parse(description))
            paragraph.Inlines.Add(BuildDescriptionInline(run));
        DescriptionBody.Blocks.Clear();
        DescriptionBody.Blocks.Add(paragraph);
    }

    private Inline BuildDescriptionInline(DescriptionRun run)
    {
        switch (run.Kind)
        {
            case DescriptionRunKind.Timestamp:
            {
                var seconds = run.Seconds!.Value;
                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = run.Text });
                link.Click += (_, _) => { if (Player is { } seekPlayer) SeekPlayer(seekPlayer, seconds); };
                return link;
            }
            case DescriptionRunKind.Url:
            {
                var url = run.Url!;
                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = run.Text });
                link.Click += async (_, _) =>
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                        await Windows.System.Launcher.LaunchUriAsync(uri);
                };
                return link;
            }
            default:
                return new Run { Text = run.Text };
        }
    }

    private void OnTitleEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!HasDescription) return;
        TitleText.TextDecorations = Windows.UI.Text.TextDecorations.Underline;
    }

    private void OnTitleExited(object sender, PointerRoutedEventArgs e) =>
        TitleText.TextDecorations = Windows.UI.Text.TextDecorations.None;

    private void OnTitleTapped(object sender, TappedRoutedEventArgs e)
    {
        if (MouseBackGuard.SuppressTap()) return;
        if (!HasDescription) return;
        ToggleDescriptionPanel();
    }

    private void OnToggleDescription(object sender, RoutedEventArgs e) => ToggleDescriptionPanel();

    /// Mutual exclusion with comments (I1-style): the two panels share the same slot visually,
    /// so showing one always hides the other first.
    private void ToggleDescriptionPanel()
    {
        if (DescriptionPanel.Visibility == Visibility.Visible)
        {
            DescriptionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            CommentsPanel.Visibility = Visibility.Collapsed;
            SetCommentsChecked(false);
        }
        DescriptionPanel.Visibility = Visibility.Visible;
    }

    /// Mirrors OnCommentsPanelTapped: a tap anywhere inside the panel must never bubble to
    /// OnPlayerTapped and toggle playback.
    private void OnDescriptionPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    // MARK: comments
    //
    // Unauthenticated reads (no Bearer, no 401-retry) — called directly on App.Session.Comments
    // rather than through Session.RunAsync. Every path here is fully try/caught: a comments
    // failure must never surface as, or be confused with, a playback failure.

    /// A fresh video always starts with the panel closed and every cache cleared, so a prior
    /// video's comments never flash before the new load replaces them.
    private void ResetComments()
    {
        commentsCts?.Cancel();
        commentsCts?.Dispose();
        commentsCts = null;
        commentsLoaded = false;
        commentRows.Clear();
        topLevelComments = [];
        topLevelContinuation = null;
        repliesParent = null;
        repliesComments = [];
        repliesContinuation = null;
        PanelTitle.Text = "COMMENTS";
        RepliesBackButton.Visibility = Visibility.Collapsed;
        CommentsList.Visibility = Visibility.Visible;
        CommentsStatus.Visibility = Visibility.Collapsed;
        CommentsRetry.Visibility = Visibility.Collapsed;
        CommentsPanel.Visibility = Visibility.Collapsed;
        SetCommentsChecked(false);
    }

    /// I1: shared by CommentsButton (title row) and CommentsButtonBar (transport bar, fullscreen
    /// only) — either one opens/closes the same panel, so both must show the same toggled state.
    private void OnToggleComments(object sender, RoutedEventArgs e)
    {
        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            CommentsPanel.Visibility = Visibility.Collapsed;
            SetCommentsChecked(false);
            return;
        }

        // Mutual exclusion with the description panel (I1-style): the two share the same slot.
        DescriptionPanel.Visibility = Visibility.Collapsed;
        CommentsPanel.Visibility = Visibility.Visible;
        SetCommentsChecked(true);
        if (commentsLoaded) return;
        commentsLoaded = true;
        _ = LoadTopLevelAsync();
    }

    private void SetCommentsChecked(bool value)
    {
        CommentsButton.IsChecked = value;
        CommentsButtonBar.IsChecked = value;
    }

    /// A tap that lands inside the panel (including empty space below the list) must never
    /// bubble to OnPlayerTapped and toggle playback.
    private void OnCommentsPanelTapped(object sender, TappedRoutedEventArgs e) => e.Handled = true;

    private async Task LoadTopLevelAsync()
    {
        ShowCommentsStatus("Loading…");
        commentsCts?.Cancel();
        var cts = new CancellationTokenSource();
        commentsCts = cts;
        try
        {
            var page = await App.Session.Comments.TopLevelAsync(video!.Id, cts.Token);
            if (leftPage || cts.IsCancellationRequested) return;
            topLevelComments = page.Comments.ToList();
            topLevelContinuation = page.Continuation;
            if (topLevelComments.Count == 0)
            {
                ShowCommentsStatus("No comments yet.");
                return;
            }
            HideCommentsStatus();
            RenderTopLevel();
        }
        catch (CommentsUnavailableException)
        {
            if (leftPage) return;
            ShowCommentsStatus("Comments aren't available for this video.");
        }
        catch (Exception)
        {
            if (leftPage || cts.IsCancellationRequested) return;
            ShowCommentsStatus("Comments couldn't be loaded.", showRetry: true);
        }
    }

    private void OnCommentsRetry(object sender, RoutedEventArgs e) => _ = LoadTopLevelAsync();

    /// A click can only ever start one paging/drill-down fetch at a time — commentsPaging is
    /// checked-and-set here (covering both Load More and drill-down) so a double-click never
    /// fires the same continuation twice and appends its page twice.
    private async void OnCommentClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CommentRowViewModel row || row.IsPinnedParent) return;
        if (commentsPaging) return;
        commentsPaging = true;
        try
        {
            if (row.IsLoadMore) await LoadMoreAsync();
            else if (row.Comment is { HasReplies: true } comment) await OpenRepliesAsync(comment);
        }
        catch (Exception)
        {
            // A load-more/replies fetch failing leaves the panel showing whatever it already
            // had — never lets a comments hiccup touch playback or crash the page.
        }
        finally
        {
            commentsPaging = false;
        }
    }

    private async Task LoadMoreAsync()
    {
        var ct = commentsCts?.Token ?? default;
        if (repliesParent is not null)
        {
            if (repliesContinuation is not { } token) return;
            var page = await App.Session.Comments.PageAsync(token, ct);
            if (leftPage) return;
            repliesComments = [.. repliesComments, .. page.Comments];
            repliesContinuation = page.Continuation;
            AppendCommentRows(page.Comments, repliesContinuation, "Show more replies");
        }
        else
        {
            if (topLevelContinuation is not { } token) return;
            var page = await App.Session.Comments.PageAsync(token, ct);
            if (leftPage) return;
            topLevelComments = [.. topLevelComments, .. page.Comments];
            topLevelContinuation = page.Continuation;
            AppendCommentRows(page.Comments, topLevelContinuation, "Load more");
        }
    }

    /// Appends a fetched page's rows in place — dropping the old trailing load-more row (if
    /// any) and adding a fresh one only while a continuation remains — rather than clearing and
    /// rebuilding, so the ListView's scroll position never jumps back to the top mid-page.
    private void AppendCommentRows(IReadOnlyList<CommentItem> newItems, string? continuation, string loadMoreText)
    {
        if (commentRows.Count > 0 && commentRows[^1].IsLoadMore)
            commentRows.RemoveAt(commentRows.Count - 1);
        foreach (var item in newItems) commentRows.Add(new CommentRowViewModel(item));
        if (continuation is not null) commentRows.Add(CommentRowViewModel.LoadMore(loadMoreText));
    }

    private async Task OpenRepliesAsync(CommentItem comment)
    {
        // Captured before the fetch so "back" can restore exactly where the top-level list was
        // scrolled to when the drill-down happened.
        topLevelScrollOffset = FindScrollViewer(CommentsList)?.VerticalOffset ?? 0;
        var page = await App.Session.Comments.PageAsync(comment.RepliesToken!, commentsCts?.Token ?? default);
        if (leftPage) return;
        repliesParent = comment;
        repliesComments = page.Comments.ToList();
        repliesContinuation = page.Continuation;
        PanelTitle.Text = "REPLIES";
        RepliesBackButton.Visibility = Visibility.Visible;
        RenderReplies();
    }

    private void OnRepliesBack(object sender, RoutedEventArgs e) => CloseReplies();

    /// Restores the cached top-level rows rather than re-fetching — matches tvOS and keeps
    /// "back" instant. The rebuild resets ListView scroll to the top, so the offset captured on
    /// the way into replies is restored after the fact, once layout has caught up with the new
    /// items (TryEnqueue runs after the current render pass).
    private void CloseReplies()
    {
        repliesParent = null;
        repliesComments = [];
        repliesContinuation = null;
        PanelTitle.Text = "COMMENTS";
        RepliesBackButton.Visibility = Visibility.Collapsed;
        RenderTopLevel();

        var offset = topLevelScrollOffset;
        dispatcher.TryEnqueue(() => FindScrollViewer(CommentsList)?.ChangeView(null, offset, null, true));
    }

    private void RenderTopLevel()
    {
        commentRows.Clear();
        foreach (var comment in topLevelComments) commentRows.Add(new CommentRowViewModel(comment));
        if (topLevelContinuation is not null)
            commentRows.Add(CommentRowViewModel.LoadMore("Load more"));
    }

    private void RenderReplies()
    {
        commentRows.Clear();
        commentRows.Add(new CommentRowViewModel(repliesParent!, isPinnedParent: true));
        foreach (var reply in repliesComments) commentRows.Add(new CommentRowViewModel(reply));
        if (repliesContinuation is not null)
            commentRows.Add(CommentRowViewModel.LoadMore("Show more replies"));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scrollViewer) return scrollViewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void ShowCommentsStatus(string message, bool showRetry = false)
    {
        CommentsList.Visibility = Visibility.Collapsed;
        CommentsStatus.Text = message;
        CommentsStatus.Visibility = Visibility.Visible;
        CommentsRetry.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideCommentsStatus()
    {
        CommentsStatus.Visibility = Visibility.Collapsed;
        CommentsRetry.Visibility = Visibility.Collapsed;
        CommentsList.Visibility = Visibility.Visible;
    }

    /// All four playback shortcuts (Esc/Space/Left/Right) are wired as KeyboardAccelerators
    /// (Page.KeyboardAccelerators in XAML) rather than through routed PreviewKeyDown/KeyDown: a
    /// routed KeyDown tunnels from whichever element currently holds focus, and that tunnel was
    /// observed to silently stop delivering events right after a double-click-triggered
    /// fullscreen toggle — confirmed live (for Esc first, then reproduced identically for Space
    /// and Left) that PlayerSlot legitimately held focus (FocusManager.GetFocusedElement agreed)
    /// and the key still never reached a routed KeyDown handler. A KeyboardAccelerator is scoped
    /// to the XamlRoot instead of a specific focused element, and reliably fired in the same
    /// broken state. Each accelerator is declared exactly once, statically in XAML — instantiated
    /// once per fresh PlayerPage (OnNavigatedTo always gets a new instance, NavigationCacheMode
    /// is the default Disabled) — so double-registration isn't reachable; nothing here adds or
    /// removes accelerators at runtime. There's no TextBox on this page to yield focus to either.

    /// Esc closes the description panel first if it's open, else walks the comments panel back
    /// one level at a time: out of a replies view first, then closes the panel entirely —
    /// matching tvOS.
    private void OnEscapeAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (DescriptionPanel.Visibility == Visibility.Visible)
        {
            DescriptionPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            if (repliesParent is not null) CloseReplies();
            else
            {
                CommentsPanel.Visibility = Visibility.Collapsed;
                SetCommentsChecked(false);
            }
            return;
        }

        if (isFullScreen)
        {
            isFullScreen = false;
            ApplyFullScreen();
        }
    }

    private void OnSpaceAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Player is null) return;
        args.Handled = true;
        TogglePlayPause();
        WakeTransport();
    }

    private void OnLeftAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args) => Seek(-10, args);

    private void OnRightAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender,
        Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args) => Seek(10, args);

    private void Seek(int deltaSeconds, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Player is not { } p) return;
        args.Handled = true;
        SeekPlayer(p, Math.Clamp(p.Position + deltaSeconds, 0, p.Duration));
        WakeTransport();
    }

    /// M7: distinguishes "the filtered manifest's kept variant URI isn't absolute https" from
    /// an actual disk I/O failure, so WriteManifestForHeight's two callers can each report the
    /// exact reason instead of it arriving wrapped as "Manifest write failed: ...".
    private sealed class InvalidManifestVariantException()
        : Exception("Manifest variant URI not absolute https.");
}

/// Mutable per-row state for one comments-panel entry: a normal comment/reply row, the pinned
/// parent shown atop a replies view, or a "load more" row. Immutable CommentItem carries the
/// data; this wraps it with the display strings and brushes the row's DataTemplate binds to —
/// same split as VideoCardViewModel/SubscriptionItemViewModel elsewhere in this file's siblings.
public sealed class CommentRowViewModel
{
    private static readonly Brush FallbackAvatarFill =
        (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];

    public CommentItem? Comment { get; }
    public bool IsPinnedParent { get; }
    public bool IsLoadMore { get; }
    public bool HasReplies => Comment?.HasReplies ?? false;

    public string TimeAndAuthorLine { get; } = "";
    public string Text { get; } = "";
    public string MetaLine { get; } = "";
    public string LoadMoreText { get; } = "";
    public Brush AvatarFill { get; } = FallbackAvatarFill;

    public Visibility CommentVisibility => IsLoadMore ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LoadMoreVisibility => IsLoadMore ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ChevronVisibility =>
        HasReplies && !IsPinnedParent ? Visibility.Visible : Visibility.Collapsed;
    public Thickness PinnedBorderThickness => IsPinnedParent ? new Thickness(0, 0, 0, 1) : default;

    public CommentRowViewModel(CommentItem comment, bool isPinnedParent = false)
    {
        Comment = comment;
        IsPinnedParent = isPinnedParent;
        TimeAndAuthorLine = $"{comment.Author} · {comment.PublishedTime}";
        Text = comment.Text;
        MetaLine = comment.ReplyCount.Length > 0
            ? $"👍 {comment.LikeCount} · {comment.ReplyCount} replies"
            : $"👍 {comment.LikeCount}";
        AvatarFill = comment.AvatarUrl is { } avatarUrl
            ? new ImageBrush { ImageSource = new BitmapImage(new Uri(avatarUrl)), Stretch = Stretch.UniformToFill }
            : FallbackAvatarFill;
    }

    private CommentRowViewModel(string loadMoreText)
    {
        IsLoadMore = true;
        LoadMoreText = loadMoreText;
    }

    public static CommentRowViewModel LoadMore(string text) => new(text);
}
