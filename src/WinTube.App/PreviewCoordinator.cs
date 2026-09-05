using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using WinTube.Core.Player;

namespace WinTube.App;

/// Bridges card hover/focus to one silent in-place preview. PreviewGate decides; this owns
/// the dwell timer, the stream resolution, and the muted MediaPlayer's lifetime. Everything
/// runs on the UI thread. See docs/specs/2026-09-05-wintube-preview-design.html.
public sealed class PreviewCoordinator
{
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(700);

    private readonly PreviewGate gate = new(Dwell);
    private readonly Dictionary<string, IPreviewHost> hosts = [];
    private DispatcherQueueTimer? dwellTimer;
    private string? pendingId;
    private MediaPlayer? player;
    private string? playerId;
    private CancellationTokenSource? resolving;

    public void Warm(IPreviewHost host)
    {
        var id = host.PreviewVideoId;
        hosts[id] = host;
        StopIfTold(gate.Warm(id, DateTimeOffset.UtcNow));

        pendingId = id;
        dwellTimer ??= CreateDwellTimer();
        dwellTimer.Stop();
        dwellTimer.Start();
    }

    public void Cold(IPreviewHost host)
    {
        var id = host.PreviewVideoId;
        if (pendingId == id) { pendingId = null; dwellTimer?.Stop(); }
        StopIfTold(gate.Cold(id));
        hosts.Remove(id);
    }

    private DispatcherQueueTimer CreateDwellTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Dwell;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (pendingId is not { } id) return;
            // A pointer jiggle on the already-playing card restarts this timer; don't
            // re-resolve a preview that is already up.
            if (gate.IsActive(id)) return;
            if (gate.DwellElapsed(id, DateTimeOffset.UtcNow) is not { } start) return;
            _ = StartAsync(start);
        };
        return timer;
    }

    private async Task StartAsync(string id)
    {
        resolving?.Cancel();
        var cts = resolving = new CancellationTokenSource();
        try
        {
            var stream = await App.Session.Streams.ResolveAsync(id, after: null, cts.Token);
            // Preempted or gone cold while resolving — a stale stream must not start playing.
            if (cts.IsCancellationRequested || !gate.IsActive(id)) return;

            MediaSource source;
            if (stream.IsAdaptive)
            {
                var http = new Windows.Web.Http.HttpClient();
                http.DefaultRequestHeaders.TryAppendWithoutValidation("User-Agent", stream.UserAgent);
                var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(stream.Url.ToString()), http);
                if (cts.IsCancellationRequested || !gate.IsActive(id)) return;
                if (result.Status != AdaptiveMediaSourceCreationStatus.Success) return;   // silent
                source = MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
            }
            else
            {
                source = MediaSource.CreateFromUri(new Uri(stream.Url.ToString()));
            }

            StopPlayer();
            player = new MediaPlayer
            {
                AutoPlay = true,
                IsMuted = true,
                Source = source,
            };
            playerId = id;
            if (hosts.TryGetValue(id, out var host)) host.ShowPreview(player);
        }
        catch
        {
            // Silent by spec: a failed preview leaves the thumbnail standing.
        }
    }

    private void StopIfTold(string? id)
    {
        if (id is null) return;
        resolving?.Cancel();
        if (playerId == id && hosts.TryGetValue(id, out var host)) host.HidePreview();
        if (playerId == id) StopPlayer();
    }

    private void StopPlayer()
    {
        player?.Dispose();
        player = null;
        playerId = null;
    }
}

/// What a card offers the coordinator: identity, and the two visual transitions.
public interface IPreviewHost
{
    string PreviewVideoId { get; }
    void ShowPreview(MediaPlayer player);
    void HidePreview();
}
