# WinTube Preview Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A card the pointer rests on — or keyboard focus lands on — plays its video silently in place of the thumbnail after a 0.7 s dwell; leaving the card restores the thumbnail.

**Architecture:** A UI-free `PreviewGate` state machine in Core owns the rules (dwell, cancellation, one-at-a-time, preemption, stale-completion discard) with injected time. An App-side `PreviewCoordinator` singleton drives it with a real timer and `StreamService`, and hands a muted `MediaPlayer` to whichever card is active. Cards raise warm/cold from pointer AND keyboard-focus events and own a hidden `MediaPlayerElement` that fades in on first frame.

**Tech Stack:** Existing only. No new packages.

**Spec:** `docs/specs/2026-09-05-wintube-preview-design.html` (approved 2026-09-05). tvOS arbiter: the metube checkout's `YouTubeTV/Sources/UI/VideoPreview.swift` (re-clone https://github.com/claust/metube to the scratchpad if gone).

## Global Constraints

- Dwell 0.7 s from warm to resolution start; fade-in 0.2 s ease at first frame; thumbnail never flashes black while resolving.
- Muted always; NO watch-progress writes, NO history recording, NO SponsorBlock, no keep-awake; playback starts from the top; the remembered player volume (settings.json) is neither read nor written by previews.
- At most one active preview; a new warm preempts the previous preview immediately; a stale resolution completing after preemption is discarded before play.
- Failures are silent — the thumbnail stays; no error UI.
- Cold = pointer leaves, keyboard focus moves on, or the card unloads (page navigation).
- Both card types (VideoCard thumbnail area, ShortCard whole tile); all pages with cards. Subscriptions grid untouched. The real player untouched.
- Tests: `dotnet test tests/WinTube.Core.Tests` (183 green before this plan). App build: `dotnet build src/WinTube.App -p:Platform=x64`; MSB3027-only failure → `Get-Process WinTube.App | Stop-Process`, retry, touch nothing else.
- Branch `master`; commit per task with the given message.

## File Structure

```
src/WinTube.Core/Player/PreviewGate.cs        CREATE: dwell/one-at-a-time/preemption state machine
src/WinTube.App/PreviewCoordinator.cs         CREATE: timer + StreamService + MediaPlayer lifetime
src/WinTube.App/App.xaml.cs                   MODIFY: expose the coordinator singleton
src/WinTube.App/Controls/VideoCard.xaml(.cs)  MODIFY: preview surface + warm/cold sources
src/WinTube.App/Controls/ShortCard.xaml(.cs)  MODIFY: same
tests/WinTube.Core.Tests/PreviewGateTests.cs  CREATE
```

---

### Task 1: Core — PreviewGate

**Files:**
- Create: `src/WinTube.Core/Player/PreviewGate.cs`
- Test: `tests/WinTube.Core.Tests/PreviewGateTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (exact — Task 2 relies on these):
  `public sealed class PreviewGate(TimeSpan dwell)` with
  `string? Warm(string id, DateTimeOffset now)` (returns an id the caller must STOP, or null),
  `string? DwellElapsed(string id, DateTimeOffset now)` (returns the id to START resolving, or null),
  `string? Cold(string id)` (returns an id the caller must STOP, or null),
  `bool IsActive(string id)` (stale-completion check before play).

- [ ] **Step 1: Write the failing tests**

New `tests/WinTube.Core.Tests/PreviewGateTests.cs`:

```csharp
using WinTube.Core.Player;

namespace WinTube.Core.Tests;

public class PreviewGateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
    private static PreviewGate Gate() => new(TimeSpan.FromMilliseconds(700));

    [Fact]
    public void DwellElapsed_StartsTheWarmCard_AfterTheDwell()
    {
        var gate = Gate();
        Assert.Null(gate.Warm("a", T0));
        Assert.Equal("a", gate.DwellElapsed("a", T0.AddMilliseconds(700)));
        Assert.True(gate.IsActive("a"));
    }

    [Fact]
    public void DwellElapsed_DoesNothing_BeforeTheDwell_OrForACardNoLongerWarm()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(500)));   // too early
        gate.Cold("a");
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(700)));   // left already
        Assert.False(gate.IsActive("a"));
    }

    [Fact]
    public void Cold_StopsAnActivePreview_AndOnlyThat()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Null(gate.Cold("b"));            // someone else's cold is not ours
        Assert.Equal("a", gate.Cold("a"));      // ours stops
        Assert.Null(gate.Cold("a"));            // idempotent
        Assert.False(gate.IsActive("a"));
    }

    [Fact]
    public void Warm_PreemptsTheActivePreview_Immediately()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Equal("a", gate.Warm("b", T0.AddSeconds(5)));   // b warm -> stop a now
        Assert.False(gate.IsActive("a"));
        Assert.Equal("b", gate.DwellElapsed("b", T0.AddSeconds(5).AddMilliseconds(700)));
        Assert.True(gate.IsActive("b"));
    }

    [Fact]
    public void ReWarmingTheActiveCard_DoesNotStopIt()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        Assert.Null(gate.Warm("a", T0.AddSeconds(2)));   // pointer jiggle on the same card
        Assert.True(gate.IsActive("a"));
    }

    [Fact]
    public void StaleCompletion_IsNotActive()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.DwellElapsed("a", T0.AddMilliseconds(700));
        gate.Warm("b", T0.AddSeconds(1));      // preempts a
        Assert.False(gate.IsActive("a"));      // a's late resolution must be discarded
    }

    [Fact]
    public void AFreshWarm_RestartsTheDwellClock()
    {
        var gate = Gate();
        gate.Warm("a", T0);
        gate.Cold("a");
        gate.Warm("a", T0.AddMilliseconds(600));
        Assert.Null(gate.DwellElapsed("a", T0.AddMilliseconds(700)));   // only 100ms into the new dwell
        Assert.Equal("a", gate.DwellElapsed("a", T0.AddMilliseconds(1300)));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: compile FAILURE — `PreviewGate` not defined.

- [ ] **Step 3: Implement**

New `src/WinTube.Core/Player/PreviewGate.cs`:

```csharp
namespace WinTube.Core.Player;

/// The preview rules, UI-free: one active preview at a time, started only after the pointer
/// (or keyboard focus) has rested on a card for the dwell, preempted the moment another card
/// goes warm, and never resurrected by a stale stream resolution. The caller owns the actual
/// timer and player; this only decides. Not thread-safe — drive it from the UI thread.
public sealed class PreviewGate(TimeSpan dwell)
{
    private string? warmId;
    private DateTimeOffset warmSince;
    private string? activeId;

    /// A card went warm (pointer entered / focus landed). Returns an id whose preview must
    /// stop now — a new card preempts the previous preview immediately — or null.
    public string? Warm(string id, DateTimeOffset now)
    {
        if (id != warmId)
        {
            warmId = id;
            warmSince = now;
        }
        if (activeId is null || activeId == id) return null;
        var stopped = activeId;
        activeId = null;
        return stopped;
    }

    /// The caller's dwell timer fired for `id`. Returns the id to start resolving when the
    /// card is still warm and the dwell has truly elapsed; null otherwise.
    public string? DwellElapsed(string id, DateTimeOffset now)
    {
        if (id != warmId || now - warmSince < dwell) return null;
        activeId = id;
        return id;
    }

    /// A card went cold (pointer left / focus moved / card unloaded). Returns the id whose
    /// preview must stop, or null when nothing of that card's is running.
    public string? Cold(string id)
    {
        if (warmId == id) warmId = null;
        if (activeId != id) return null;
        activeId = null;
        return id;
    }

    /// Whether `id` still owns the active slot — checked after an async resolution completes,
    /// so a preview preempted mid-resolve is discarded instead of starting to play.
    public bool IsActive(string id) => activeId == id;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: PASS (183 + 7 new).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: PreviewGate - the preview lifetime rules"
```

---

### Task 2: App — coordinator and card surfaces

**Files:**
- Create: `src/WinTube.App/PreviewCoordinator.cs`
- Modify: `src/WinTube.App/App.xaml.cs`
- Modify: `src/WinTube.App/Controls/VideoCard.xaml`, `src/WinTube.App/Controls/VideoCard.xaml.cs`
- Modify: `src/WinTube.App/Controls/ShortCard.xaml`, `src/WinTube.App/Controls/ShortCard.xaml.cs`

**Interfaces:**
- Consumes: `PreviewGate` (Task 1), `App.Session.Streams.ResolveAsync(videoId, after: null)` → `ResolvedStream(Url, IsAdaptive, UserAgent, Client, OriginalAudioLanguage)` — see `PlayerPage.PlayAsync` for the exact source-building recipe to copy (AdaptiveMediaSource with UA header vs plain uri).
- Produces: `App.Previews` (`PreviewCoordinator`) with
  `void Warm(IPreviewHost host)`, `void Cold(IPreviewHost host)`;
  `public interface IPreviewHost { string PreviewVideoId { get; } void ShowPreview(MediaPlayer player); void HidePreview(); }` implemented by both cards.

- [ ] **Step 1: The coordinator**

New `src/WinTube.App/PreviewCoordinator.cs` (namespace `WinTube.App`):

```csharp
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
```

NOTE: check `StreamService.ResolveAsync`'s real signature for the CancellationToken
parameter name/position and adapt the call.

`App.xaml.cs`: add `public static PreviewCoordinator Previews { get; } = new();`
NOTE: the coordinator calls `DispatcherQueue.GetForCurrentThread()` lazily inside
`CreateDwellTimer` (first Warm), which runs on the UI thread — safe even though App's static
init happens before `Application.Start`'s dispatcher exists.

- [ ] **Step 2: VideoCard surface + warm/cold sources**

`VideoCard.xaml` — inside the thumbnail Grid (row 0), directly after the `Thumbnail` Image:

```xml
<MediaPlayerElement x:Name="PreviewSurface" Width="320" Height="180"
                    Stretch="UniformToFill" IsHitTestVisible="False" Opacity="0">
    <MediaPlayerElement.OpacityTransition><ScalarTransition Duration="0:0:0.2"/></MediaPlayerElement.OpacityTransition>
</MediaPlayerElement>
```

`VideoCard.xaml.cs`:

- Implement `IPreviewHost`: `PreviewVideoId => Video?.Id ?? ""`;
  `ShowPreview(MediaPlayer player)` stores the player, subscribes
  `player.PlaybackSession.PlaybackStateChanged`, and on the first `Playing` state (marshalled
  via `DispatcherQueue.TryEnqueue`) sets `PreviewSurface.SetMediaPlayer(player)` and
  `PreviewSurface.Opacity = 1` — the thumbnail stays until there is a frame to show;
  `HidePreview()` sets `Opacity = 0`, `PreviewSurface.SetMediaPlayer(null)`, unsubscribes.
- Warm sources: `PointerEntered` on the root Grid AND ListViewItem keyboard focus. In
  `Loaded`, walk up `VisualTreeHelper.GetParent` to the first `ListViewItem` ancestor (may be
  absent in odd hosts — null-safe) and subscribe its `GotFocus`/`LostFocus`; unsubscribe on
  `Unloaded`. All warm paths call `App.Previews.Warm(this)`; `PointerExited`, `LostFocus` and
  `Unloaded` call `App.Previews.Cold(this)`.
- GUARD: pointer-exit into the card's own context menu is fine (Cold just stops the preview);
  no special handling.
- The existing hover edge / click behavior is untouched. `IsHitTestVisible="False"` on the
  surface keeps taps, context menus and the author link working through it.

- [ ] **Step 3: ShortCard surface**

Same recipe on `ShortCard.xaml` — inside the rounded Border, over the ImageBrush background:
the Border gets a child `MediaPlayerElement x:Name="PreviewSurface" Width="150" Height="267"
Stretch="UniformToFill" IsHitTestVisible="False" Opacity="0"` with the same
OpacityTransition. `ShortCard.xaml.cs` implements `IPreviewHost` and the same warm/cold
sources (its root Grid already handles PointerEntered/Exited for the hover edge — extend
those handlers; keep the edge behavior).

- [ ] **Step 4: Build, run all tests, verify visually**

Run: `dotnet test tests/WinTube.Core.Tests` and `dotnet build src/WinTube.App -p:Platform=x64`.
Launch; rest the cursor on a card ≥1 s → the video fades in silently over the thumbnail;
sweep the mouse across a row quickly → nothing resolves; move to another card → the first
snaps back to its thumbnail, the second previews after its dwell; Tab/arrow onto a card →
same; open a previewed video → it resumes from real progress, at the remembered volume,
unaffected by the preview.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: silent in-place preview on hover and keyboard focus"
```
