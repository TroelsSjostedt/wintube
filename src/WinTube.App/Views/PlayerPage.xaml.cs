using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// replaced MediaPlayerElement; the source-building logic is exactly what SpikePage (Task 0)
/// proved works against YouTube's HLS.
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

    // MARK: quality picker state
    private string? hlsMaster;
    private IReadOnlyList<HlsVariant> hlsVariants = [];
    private IReadOnlyList<HlsQuality> hlsQualities = [];
    private HlsQuality? activeQuality;
    /// The session-wide choice, as on youtube.com: null is Auto; a height pins the nearest
    /// rung at or below it on every video until the app closes. Deliberately not persisted.
    private static int? preferredHeight;

    private IReadOnlyList<SponsorSegment> sponsorSegments = [];
    private readonly HashSet<string> sponsorSkipped = [];
    private DispatcherQueueTimer? toastTimer;

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

    public PlayerPage() => InitializeComponent();

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

        leftPage = false;
        retried = false;
        _ = StartAsync(after: null);

        ResetComments();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        leftPage = true;
        ReportProgressOnce();
        App.Session.ProgressSync?.FlushNow();
        TearDownPlayer();

        toastTimer?.Stop();
        sponsorSegments = [];
        sponsorSkipped.Clear();

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
                target = WriteManifestForHeight(preferredHeight);   // Task 8 fills the picker UI; the mechanism lands here
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
            QualityButton.IsEnabled = false;
            QualityButton.Visibility = Visibility.Visible;
        }

        hasPlayed = false;
        handledFailure = false;
        // startAt is NOT cleared here — a ladder retry re-enters PlayAsync before the video has
        // ever played, and must still resume at the original link timestamp, not recorded
        // progress. It's cleared once, in Opened's first-play branch below.
        var resume = startAt?.TotalSeconds ?? App.Session.Progress.ResumePosition(video!.Id) ?? 0;
        lastUserAgent = stream.UserAgent;
        lastAudioLanguage = stream.OriginalAudioLanguage;

        CreatePlayerHost();
        Player!.Volume = App.Session.PlayerSettings.LoadVolume();
        Player.Load(target, resume, stream.UserAgent, stream.OriginalAudioLanguage);

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

    /// mpv reads manifests from disk happily; one scratch file per page, overwritten per load.
    private string WriteManifestForHeight(int? height)
    {
        var quality = height is { } wanted
            ? (hlsQualities.FirstOrDefault(q => q.Height <= wanted) ?? hlsQualities[^1])
            : HlsVariantParser.AutoQuality(hlsQualities, ScreenHeight())!;
        activeQuality = quality;
        var path = Path.Combine(Session.DataDirectory, "current.m3u8");
        File.WriteAllText(path, HlsVariantParser.FilterToBandwidth(hlsMaster!, quality.Bandwidth));
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

        var host = new MpvPlayerHost();
        host.Opened += () =>
        {
            if (leftPage || host != Player) return;
            HideOverlays();
            if (!hasPlayed)
            {
                // Applied once — a ladder retry re-entering Opened after this must resume from
                // recorded progress, not re-seek back to the original link timestamp.
                startAt = null;
                hasPlayed = true;
                stallTimer?.Stop();
                App.Session.History.Record(video!);
                StartProgressTimer();
                _ = LoadSponsorSegmentsAsync();
            }
        };
        host.PositionChanged += _ => { if (host == Player) OnPlayerPosition(); };
        host.EndReached += () => { if (host == Player) ReportProgressOnce(); };
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
        p.SeekTo(target);
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
        progressTimer?.Stop();
        progressTimer = null;
        stallTimer?.Stop();
        stallTimer = null;

        if (Player is { } p)
        {
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
    private void BuildQualityMenu()
    {
        if (hlsQualities.Count == 0) return;

        QualityFlyout.Items.Clear();
        var auto = new RadioMenuFlyoutItem { Text = "Auto", GroupName = "quality", IsChecked = preferredHeight is null };
        auto.Click += (_, _) => SetPreferredHeight(null);
        QualityFlyout.Items.Add(auto);
        foreach (var quality in hlsQualities)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = quality.Label,
                GroupName = "quality",
                IsChecked = preferredHeight == quality.Height,
            };
            var height = quality.Height;
            item.Click += (_, _) => SetPreferredHeight(height);
            QualityFlyout.Items.Add(item);
        }

        QualityButton.IsEnabled = true;
        QualityButton.Visibility = Visibility.Visible;
        UpdateQualityLabel();
    }

    /// Stores the choice for the next video's WriteManifestForHeight call and relabels from
    /// whatever is already loaded — switching the running playback live is Task 8.
    private void SetPreferredHeight(int? height)
    {
        preferredHeight = height;
        UpdateQualityLabel();
    }

    private void UpdateQualityLabel()
    {
        if (activeQuality is not { } quality) return;
        QualityLabel.Text = preferredHeight is null ? $"Auto · {quality.Height}p" : quality.Label;
    }

    /// Clicking the video surface toggles play/pause. Tapped bubbles up from the transport
    /// controls too (Task 6), and their invisible root layout spans the full frame — so instead
    /// of fencing off the controls as a region, only a tap that actually landed on an
    /// interactive control (a button, a slider) is left alone.
    private void OnPlayerTapped(object sender, TappedRoutedEventArgs e)
    {
        if (Player is null) return;
        for (var element = e.OriginalSource as DependencyObject; element is not null;
             element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
        {
            if (element is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase
                or Microsoft.UI.Xaml.Controls.Primitives.RangeBase) return;
        }
        TogglePlayPause();
    }

    // MARK: channel link

    private void OnAuthorEntered(object sender, PointerRoutedEventArgs e) =>
        AuthorLink.TextDecorations = Windows.UI.Text.TextDecorations.Underline;

    private void OnAuthorExited(object sender, PointerRoutedEventArgs e) =>
        AuthorLink.TextDecorations = Windows.UI.Text.TextDecorations.None;

    private void OnAuthorTapped(object sender, TappedRoutedEventArgs e)
    {
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
        CommentsButton.IsChecked = false;
    }

    private void OnToggleComments(object sender, RoutedEventArgs e)
    {
        if (CommentsPanel.Visibility == Visibility.Visible)
        {
            CommentsPanel.Visibility = Visibility.Collapsed;
            CommentsButton.IsChecked = false;
            return;
        }

        CommentsPanel.Visibility = Visibility.Visible;
        CommentsButton.IsChecked = true;
        if (commentsLoaded) return;
        commentsLoaded = true;
        _ = LoadTopLevelAsync();
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

    /// Esc walks the panel back one level at a time: out of a replies view first, then closes
    /// the panel entirely — matching tvOS. Wired to both PreviewKeyDown (seen regardless of
    /// which child has focus) and KeyDown (belt-and-suspenders for focus landing on the page
    /// itself); marking the preview pass handled suppresses the later bubbling KeyDown.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || CommentsPanel.Visibility != Visibility.Visible) return;
        e.Handled = true;
        if (repliesParent is not null) CloseReplies();
        else
        {
            CommentsPanel.Visibility = Visibility.Collapsed;
            CommentsButton.IsChecked = false;
        }
    }
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
