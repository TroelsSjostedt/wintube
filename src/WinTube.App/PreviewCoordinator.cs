using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinTube.Core.Player;

namespace WinTube.App;

/// Bridges card hover/focus to one silent in-place preview. PreviewGate decides; this owns
/// the dwell timer, the stream resolution, and the muted MediaPlayer's lifetime. Everything
/// runs on the UI thread. See docs/specs/2026-09-05-wintube-preview-design.html.
public sealed class PreviewCoordinator
{
    private static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(700);

    private readonly PreviewGate gate = new(Dwell);
    private DispatcherQueueTimer? dwellTimer;

    // Tracked by HOST INSTANCE rather than by video id: two live cards can show the same
    // video (a duplicate row entry, the same clip in two shelves), and an id-keyed map would
    // let the second card's hover overwrite the first's registration — its later
    // PointerExited would then hide the WRONG card and dispose the still-playing card's
    // player. Instance tracking makes that collision structurally impossible: HidePreview is
    // always sent to the host that actually received ShowPreview, and a Cold/Detach from any
    // other instance is a no-op regardless of what id it reports.
    private string? pendingId;
    private IPreviewHost? pendingHost;
    private MediaPlayer? player;
    private string? playerId;
    private IPreviewHost? playerHost;
    private CancellationTokenSource? resolving;

    public void Warm(IPreviewHost host)
    {
        var id = host.PreviewVideoId;
        if (string.IsNullOrEmpty(id)) return;

        if (playerId == id && playerHost is { } owner && !ReferenceEquals(owner, host))
        {
            // Same video already previewing under a DIFFERENT card instance. The gate only
            // compares ids and would treat this as a no-op continuation, but a different
            // instance means this is a genuine preemption: hide the old card and let the new
            // one run its own dwell/resolve so it gets shown in turn.
            owner.HidePreview();
            StopPlayer();
            gate.Cold(id);
        }
        else
        {
            var stop = gate.Warm(id, DateTimeOffset.UtcNow);
            if (stop is not null && playerId == stop && playerHost is { } stale)
            {
                stale.HidePreview();
                StopPlayer();
            }
        }

        pendingId = id;
        pendingHost = host;
        dwellTimer ??= CreateDwellTimer();
        dwellTimer.Stop();
        dwellTimer.Start();
    }

    /// A card's hover/focus ended, its `Video` is about to be rebound to a different id (container
    /// recycling), or it's unloading — in every case, release whatever THIS INSTANCE currently
    /// occupies (the pending dwell and/or the active preview slot). Cold and the old Detach are the
    /// same lookup once tracking is keyed on the instance rather than on `host.PreviewVideoId`
    /// (which may already reflect a new value by the time this runs), so one method serves both.
    public void Cold(IPreviewHost host)
    {
        if (ReferenceEquals(pendingHost, host))
        {
            if (pendingId is { } id) gate.Cold(id);
            pendingId = null;
            pendingHost = null;
            dwellTimer?.Stop();
        }

        if (ReferenceEquals(playerHost, host))
        {
            if (playerId is { } id) gate.Cold(id);
            host.HidePreview();
            StopPlayer();
        }
    }

    /// Alias kept for call-site clarity at container-rebind/unload sites; identical to `Cold`
    /// now that tracking is instance-keyed rather than id-keyed.
    public void Detach(IPreviewHost host) => Cold(host);

    private DispatcherQueueTimer CreateDwellTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = Dwell;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (pendingId is not { } id || pendingHost is not { } host) return;
            // A pointer jiggle on the already-playing card restarts this timer; don't
            // re-resolve a preview that is already up.
            if (gate.IsActive(id)) return;
            if (gate.DwellElapsed(id, DateTimeOffset.UtcNow) is not { } start) return;
            _ = StartAsync(start, host);
        };
        return timer;
    }

    private async Task StartAsync(string id, IPreviewHost host)
    {
        resolving?.Cancel();
        resolving?.Dispose();
        var cts = resolving = new CancellationTokenSource();
        try
        {
            // Previews resolve straight to the ANDROID rung's muxed 360p MP4: the VISIONOS
            // HLS manifest routinely refuses to open in a bare MediaPlayer (SourceNotSupported
            // or a hang in Opening — PlayerPage survives that only via its retry ladder), and
            // 360p progressive is exactly the right weight for a 320 px tile anyway. VisionOs
            // always yields a progressive MP4, never an adaptive manifest, so there is no
            // adaptive-source branch to build here.
            var stream = await App.Session.Streams.ResolveAsync(
                id, after: WinTube.Core.InnerTube.ClientKind.VisionOs, cts.Token);
            // Preempted or gone cold while resolving — a stale stream must not start playing.
            if (cts.IsCancellationRequested || !gate.IsActive(id)) return;

            var source = MediaSource.CreateFromUri(new Uri(stream.Url.ToString()));

            StopPlayer();
            player = new MediaPlayer
            {
                AutoPlay = true,
                IsMuted = true,
            };
            // Must be set before Source: WinRT requires CommandManager.IsEnabled to change
            // while no command-manager activity is pending. A muted preview must never
            // capture the hardware play/pause keys.
            player.CommandManager.IsEnabled = false;
            player.Source = new MediaPlaybackItem(source);
            playerId = id;
            playerHost = host;
            host.ShowPreview(player);
        }
        catch
        {
            // Silent by spec: a failed preview leaves the thumbnail standing.
        }
    }

    private void StopPlayer()
    {
        resolving?.Cancel();
        resolving?.Dispose();
        resolving = null;
        player?.Dispose();
        player = null;
        playerId = null;
        playerHost = null;
    }
}

/// What a card offers the coordinator: identity, and the two visual transitions.
public interface IPreviewHost
{
    string PreviewVideoId { get; }
    void ShowPreview(MediaPlayer player);
    void HidePreview();
}
