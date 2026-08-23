using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using WinTube.Core.InnerTube;
using WinTube.Core.Models;
using WinTube.Core.Player;

namespace WinTube.App.Views;

/// Full-window playback: resolves a stream for the navigated VideoItem, plays it with resume
/// and periodic progress recording, and retries once up the client ladder if playback never
/// starts. Ports the tvOS player contract onto MediaPlayerElement; the source-building logic is
/// exactly what SpikePage (Task 0) proved works against YouTube's HLS.
public sealed partial class PlayerPage : Page
{
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();

    private VideoItem? video;
    private MediaPlayer? player;
    private DispatcherQueueTimer? progressTimer;
    private DispatcherQueueTimer? stallTimer;
    private ClientKind? lastClient;
    private string? originalAudioLanguage;
    private bool retried;
    private bool hasPlayed;
    private bool handledFailure;
    private bool leftPage;

    public PlayerPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        video = (VideoItem)e.Parameter;
        TitleText.Text = video.Title;
        leftPage = false;
        retried = false;
        _ = StartAsync(after: null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        leftPage = true;
        ReportProgressOnce();
        App.Session.ProgressSync?.FlushNow();
        TearDownPlayer();
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
        MediaSource source;
        if (stream.IsAdaptive)
        {
            var http = new Windows.Web.Http.HttpClient();
            http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", stream.UserAgent);
            var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(stream.Url.ToString()), http);
            if (leftPage) return;
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                await RetryOrFailAsync($"Adaptive stream failed: {result.Status}");
                return;
            }
            source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
        }
        else
        {
            source = MediaSource.CreateFromUri(new Uri(stream.Url.ToString()));
        }

        originalAudioLanguage = stream.OriginalAudioLanguage;
        hasPlayed = false;
        handledFailure = false;

        var playbackItem = new MediaPlaybackItem(source);
        var mediaPlayer = new MediaPlayer { AutoPlay = true };
        mediaPlayer.MediaOpened += OnMediaOpened;
        mediaPlayer.MediaFailed += OnMediaFailed;
        mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        mediaPlayer.Source = playbackItem;

        player = mediaPlayer;
        Player.SetMediaPlayer(mediaPlayer);

        stallTimer = dispatcher.CreateTimer();
        stallTimer.Interval = TimeSpan.FromSeconds(10);
        stallTimer.IsRepeating = false;
        stallTimer.Tick += (_, _) =>
        {
            if (hasPlayed) return;
            _ = RetryOrFailAsync("Playback did not start.");
        };
        stallTimer.Start();
    }

    private async Task RetryOrFailAsync(string reason)
    {
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

    // MARK: MediaPlayer event handlers — all fire on a background thread, marshal to the UI
    // thread before touching anything visual or any of this page's state.

    private void OnMediaOpened(MediaPlayer sender, object args) => dispatcher.TryEnqueue(() =>
    {
        if (leftPage || player != sender) return;

        if (App.Session.Progress.ResumePosition(video!.Id) is { } resume)
            sender.PlaybackSession.Position = TimeSpan.FromSeconds(resume);

        SelectOriginalAudioTrack(sender);
        HideOverlays();
    });

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        dispatcher.TryEnqueue(() =>
        {
            if (leftPage || player != sender) return;
            var message = string.IsNullOrEmpty(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
            _ = RetryOrFailAsync(message);
        });

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args) =>
        dispatcher.TryEnqueue(() =>
        {
            if (leftPage || player is not { } current || current.PlaybackSession != sender) return;
            if (sender.PlaybackState != MediaPlaybackState.Playing) return;

            stallTimer?.Stop();
            HideOverlays();
            if (!hasPlayed)
            {
                hasPlayed = true;
                App.Session.History.Record(video!);
                StartProgressTimer();
            }
        });

    /// Walks the track list YouTube's dubs leave with no default and selects the video's own
    /// audio track — otherwise alphabetical order wins.
    private void SelectOriginalAudioTrack(MediaPlayer mediaPlayer)
    {
        if (originalAudioLanguage is not { } language) return;
        if (mediaPlayer.Source is not MediaPlaybackItem item) return;

        var tracks = item.AudioTracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Language.StartsWith(language, StringComparison.OrdinalIgnoreCase))
            {
                tracks.SelectedIndex = i;
                return;
            }
        }
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
        if (video is null || player is not { } p) return;
        var session = p.PlaybackSession;
        App.Session.Progress.Report(video.Id, session.Position.TotalSeconds, session.NaturalDuration.TotalSeconds);
    }

    // MARK: teardown + UI state

    private void TearDownPlayer()
    {
        progressTimer?.Stop();
        progressTimer = null;
        stallTimer?.Stop();
        stallTimer = null;

        if (player is { } p)
        {
            p.MediaOpened -= OnMediaOpened;
            p.MediaFailed -= OnMediaFailed;
            p.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            Player.SetMediaPlayer(null);
            p.Dispose();
        }
        player = null;
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
}
