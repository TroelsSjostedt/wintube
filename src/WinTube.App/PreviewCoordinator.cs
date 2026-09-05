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

    /// A ListView container is about to be rebound to a different `Video` (or unloaded) —
    /// cold every id this host instance is still registered under, keyed by the OLD identity
    /// rather than `host.PreviewVideoId` (which already reflects the new one by the time
    /// OnVideoChanged runs). Without this, container recycling during a live preview leaves a
    /// stuck `hosts` entry and a gate permanently locked on an id no card can ever cold again.
    public void Detach(IPreviewHost host)
    {
        List<string>? stale = null;
        foreach (var (id, mapped) in hosts)
        {
            if (ReferenceEquals(mapped, host)) (stale ??= []).Add(id);
        }
        if (stale is null) return;
        foreach (var id in stale)
        {
            if (pendingId == id) { pendingId = null; dwellTimer?.Stop(); }
            StopIfTold(gate.Cold(id));
            hosts.Remove(id);
        }
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
            // Previews resolve straight to the ANDROID rung's muxed 360p MP4: the VISIONOS
            // HLS manifest routinely refuses to open in a bare MediaPlayer (SourceNotSupported
            // or a hang in Opening — PlayerPage survives that only via its retry ladder), and
            // 360p progressive is exactly the right weight for a 320 px tile anyway.
            var stream = await App.Session.Streams.ResolveAsync(
                id, after: WinTube.Core.InnerTube.ClientKind.VisionOs, cts.Token);
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
                // Wrapped exactly as PlayerPage wraps its source — a raw MediaSource set
                // directly as Source has been seen to hang in Opening.
                Source = new MediaPlaybackItem(source),
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
