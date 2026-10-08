# WinTube Adaptive Auto Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Auto quality picks what the measured connection can carry at video start, and steps down one rung when playback stalls — visible in the quality menu and the decision log, simulatable via settings.json.

**Architecture:** `DemuxerCacheState` additionally parses mpv's `raw-input-rate`; `MpvPlayerHost` smooths it (EMA) and raises buffering edges from `paused-for-cache`. Pure Core logic decides everything: the rate-constrained pick (screen AND bandwidth), and a clock-injected downshift policy (2 stalls/60 s → one rung down, 90 s cooldown, max 2/video). PlayerPage wires both into the existing Auto resolve and `ReloadKeepingPosition`, persists the latest rate for the next session, shows the rate in the quality flyouts' footers, and logs every decision.

**Tech Stack:** WinUI 3 / .NET 8, libmpv properties (`raw-input-rate`, `paused-for-cache`), xUnit.

**Spec:** `docs/specs/2026-10-08-wintube-adaptive-auto-design.html` — read it first.

## Global Constraints

- All tunables are named constants in ONE place: new `src/WinTube.Core/Player/AdaptiveAutoTuning.cs` — `SafetyFactor = 0.7`, `StallWindowSeconds = 60`, `StallsToDownshift = 2`, `CooldownSeconds = 90`, `MaxDownshiftsPerVideo = 2`, `RateSmoothingHalfLifeSeconds = 5.0`.
- The rate can only pull Auto DOWN from the screen-based rung, never above it. No usable rate → today's behavior exactly (screen height only).
- `BANDWIDTH` is bits/s; `raw-input-rate` is bytes/s — the ×8 conversion is pinned by a test.
- settings.json keys: `measuredBandwidthBps` (persisted on teardown; skipped while simulating), `simulatedBandwidthMbps` (read-only override; replaces the measured rate everywhere). Both follow PlayerSettingsStore's hostile-file discipline (wrong-typed → default, never throw).
- A manual quality pin disables ALL automation (no downshifts, no rate-based resolve).
- Decision log lines (permanent, via the established `WatchProgressSync.LogTo` pattern): `auto: rate=42Mbps screen=1440 → 1440p` (also `rate=n/a`, `rate=5Mbps(simulated)`), `downshift: 2 stalls in 38s → 720p`, and one line when a downshift is suppressed (cooldown or cap) stating which.
- Reconcile against CURRENT sources — line numbers drift. Key members today: `HlsVariantParser.AutoQuality(qualities, maxHeight)`, `DemuxerCacheState.ParseRanges(string?)`, `MpvPlayerHost.PollBufferedRanges`/`ReadBufferedRanges` (1 s cadence on the event thread), `PlayerPage.WriteManifestForHeight(int?)` + `ResolveQuality` + `ReloadKeepingPosition(target, stallReason)` + `reloadArmed`, the two quality flyouts (`QualityFlyout`, `QualityFlyoutBar`) rebuilt via `PopulateQualityFlyout`.
- Build: `dotnet build src/WinTube.App -p:Platform=x64` (`-c Release` when the user's running app locks Debug). Tests: `dotnet test tests/WinTube.Core.Tests` (298 green today). Do NOT launch the app (user at machine; the E2E gate is theirs).
- Commit per task; footer exactly: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` (the user's attribution preference — use this line even if other guidance names a different model).

## Review Focus

Failure modes the spec implies but its text never names — each line's pinning test lives in the owning task:

1. **Rate ≤ 0 or NaN (simulated or measured)** → treated as "no usable rate", never a crash or an empty pick. (Task 3, Task 2.)
2. **Rate lower than every rung's bandwidth** → Auto picks the LOWEST rung, never none. (Task 3.)
3. **Buffering edges caused by a reload itself** (quality/subtitle/mute switches trigger `paused-for-cache` while reopening) **must not count as stalls** — else every switch walks toward a downshift spiral. (Task 4 policy accepts suppression windows; Task 7 feeds it: edges are ignored while `reloadArmed` and for 5 s after `Opened`.)
4. **Zero-rate samples while paused/idle must not decay the EMA** → a long pause must not downgrade the persisted rate or the next video's pick. (Task 2 smoother ignores zeros; pinned.)
5. **Empty qualities list with a rate present** → null, no NRE (the muxed/no-variant path). (Task 3.)

---

### Task 1: Core — parse raw-input-rate from demuxer-cache-state (TDD)

**Files:**
- Modify: `src/WinTube.Core/Player/DemuxerCacheState.cs`
- Test: `tests/WinTube.Core.Tests/DemuxerCacheStateTests.cs` (extend)

**Interfaces:**
- Produces: `public static (IReadOnlyList<(double Start, double End)> Ranges, double? RawInputBytesPerSecond) ParseState(string? json)` — same tolerance discipline as `ParseRanges` (absent key/garbage/wrong type → null rate, empty ranges, never throws). `ParseRanges` stays (delegating to `ParseState` internally is fine).

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void ParseState_ReadsRawInputRate()
{
    var (_, rate) = DemuxerCacheState.ParseState(
        """{"seekable-ranges":[{"start":0.0,"end":10.0}],"raw-input-rate":5242880}""");
    Assert.Equal(5242880, rate!.Value, 0);
}

[Fact]
public void ParseState_MissingOrWrongTypedRate_IsNull()
{
    Assert.Null(DemuxerCacheState.ParseState("""{"seekable-ranges":[]}""").RawInputBytesPerSecond);
    Assert.Null(DemuxerCacheState.ParseState("""{"raw-input-rate":"fast"}""").RawInputBytesPerSecond);
    Assert.Null(DemuxerCacheState.ParseState(null).RawInputBytesPerSecond);
    Assert.Null(DemuxerCacheState.ParseState("garbage").RawInputBytesPerSecond);
}
```

- [ ] **Step 2: Run to verify fail** — `dotnet test tests/WinTube.Core.Tests --filter DemuxerCacheState` → FAIL (no ParseState).
- [ ] **Step 3: Implement** — extract the existing tolerant walk into `ParseState`; rate read defensively (`TryGetDouble`, NaN/negative → null).
- [ ] **Step 4: Full suite green** (298 + 2).
- [ ] **Step 5: Commit** — `feat: parse the download rate from demuxer-cache-state`

---

### Task 2: Core — rate smoother + settings keys (TDD)

**Files:**
- Create: `src/WinTube.Core/Player/BandwidthSmoother.cs`, `src/WinTube.Core/Player/AdaptiveAutoTuning.cs`
- Modify: `src/WinTube.Core/Stores/PlayerSettingsStore.cs`
- Test: `tests/WinTube.Core.Tests/BandwidthSmootherTests.cs`, `PlayerSettingsStoreTests.cs` (extend)

**Interfaces:**
- Produces: `AdaptiveAutoTuning` static class with the six constants from Global Constraints. `public sealed class BandwidthSmoother { public void Sample(double bytesPerSecond, double elapsedSeconds); public double? BytesPerSecond { get; } }` — exponential moving average with half-life `RateSmoothingHalfLifeSeconds`; samples ≤ 0 or NaN are IGNORED (Review Focus 4); `BytesPerSecond` null until the first accepted sample. `PlayerSettingsStore`: `double? LoadMeasuredBandwidthBps()` / `SaveMeasuredBandwidthBps(double)` (clamped > 0; wrong-typed → null) and `double? LoadSimulatedBandwidthMbps()` (read-only; ≤ 0/wrong-typed → null).

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public void Smoother_ConvergesTowardSteadyRate()
{
    var s = new BandwidthSmoother();
    for (var i = 0; i < 20; i++) s.Sample(1_000_000, 1.0);
    Assert.Equal(1_000_000, s.BytesPerSecond!.Value, 0);
}

[Fact]
public void Smoother_IgnoresZeroAndNegativeAndNaN()
{
    var s = new BandwidthSmoother();
    s.Sample(1_000_000, 1.0);
    var before = s.BytesPerSecond;
    s.Sample(0, 1.0); s.Sample(-5, 1.0); s.Sample(double.NaN, 1.0);
    Assert.Equal(before, s.BytesPerSecond);   // a long pause must not decay the rate
}

[Fact]
public void Smoother_NullUntilFirstAcceptedSample()
{
    var s = new BandwidthSmoother();
    s.Sample(0, 1.0);
    Assert.Null(s.BytesPerSecond);
}

[Fact]
public void Store_MeasuredAndSimulatedBandwidthRoundTrip()
{
    var dir = Directory.CreateTempSubdirectory().FullName;
    var store = new PlayerSettingsStore(dir);
    Assert.Null(store.LoadMeasuredBandwidthBps());
    store.SaveMeasuredBandwidthBps(5_000_000);
    Assert.Equal(5_000_000, store.LoadMeasuredBandwidthBps()!.Value, 0);
    Assert.Null(store.LoadSimulatedBandwidthMbps());
    File.WriteAllText(Path.Combine(dir, "settings.json"),
        """{"measuredBandwidthBps":1,"simulatedBandwidthMbps":25}""");
    Assert.Equal(25, store.LoadSimulatedBandwidthMbps()!.Value, 0);
}

[Fact]
public void Store_WrongTypedBandwidthKeys_AreNull()
{
    var dir = Directory.CreateTempSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "settings.json"),
        """{"measuredBandwidthBps":{},"simulatedBandwidthMbps":"fast"}""");
    var store = new PlayerSettingsStore(dir);
    Assert.Null(store.LoadMeasuredBandwidthBps());
    Assert.Null(store.LoadSimulatedBandwidthMbps());
}
```

- [ ] **Step 2: Run to verify fail.**
- [ ] **Step 3: Implement** — EMA: `alpha = 1 - 2^(-elapsed/halfLife)`; store methods via the existing defensive `is JsonValue && TryGetValue` pattern (read-modify-write preserved).
- [ ] **Step 4: Full suite green.**
- [ ] **Step 5: Commit** — `feat: bandwidth smoothing and settings keys for adaptive Auto`

---

### Task 3: Core — rate-constrained Auto pick (TDD)

**Files:**
- Modify: `src/WinTube.Core/Player/HlsVariantParser.cs`
- Test: `tests/WinTube.Core.Tests/HlsVariantParserTests.cs` (extend)

**Interfaces:**
- Produces: `public static HlsQuality? AutoQuality(IReadOnlyList<HlsQuality> qualities, int maxHeight, double? rateBytesPerSecond)` — overload; highest rung with `Height <= maxHeight` AND (`rateBytesPerSecond` unusable OR `Bandwidth <= rateBytesPerSecond * 8 * AdaptiveAutoTuning.SafetyFactor`); if no rung satisfies both → the lowest rung overall (never null for a non-empty list); empty list → null; rate ≤ 0/NaN → treated as unusable (screen-only). The existing two-arg `AutoQuality` stays and delegates with `rate: null`.

- [ ] **Step 1: Failing tests** (reuse the test class's `Manifest`: rungs 2160@28.0M, 1080@6.3M, 360@0.8M bits/s)

```csharp
[Fact]
public void AutoQuality_RateCapsBelowScreenChoice()
{
    var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest)); // 2160,1080,360
    // 2 MB/s = 16 Mbit/s; ×0.7 → 11.2 Mbit/s: 2160p (28M) too rich, 1080p (6.3M) fits.
    Assert.Equal(1080, HlsVariantParser.AutoQuality(levels, 2160, 2_000_000)!.Height);
    // Plenty of rate → the screen rung wins unchanged.
    Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160, 100_000_000)!.Height);
    // The rate can never pull ABOVE the screen cap.
    Assert.Equal(1080, HlsVariantParser.AutoQuality(levels, 1080, 100_000_000)!.Height);
}

[Fact]
public void AutoQuality_StarvedRate_PicksLowestNotNothing()
{
    var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest));
    Assert.Equal(360, HlsVariantParser.AutoQuality(levels, 2160, 10_000)!.Height);
}

[Fact]
public void AutoQuality_UnusableRate_FallsBackToScreenOnly()
{
    var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest));
    Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160, null)!.Height);
    Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160, 0)!.Height);
    Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160, double.NaN)!.Height);
    Assert.Null(HlsVariantParser.AutoQuality([], 2160, 5_000_000));
}
```

- [ ] **Step 2: Run to verify fail.**
- [ ] **Step 3: Implement** (filter by both, else lowest; doc comment states the bits/bytes contract).
- [ ] **Step 4: Full suite green.**
- [ ] **Step 5: Commit** — `feat: rate-constrained Auto quality pick`

---

### Task 4: Core — downshift policy (TDD)

**Files:**
- Create: `src/WinTube.Core/Player/DownshiftPolicy.cs`
- Test: `tests/WinTube.Core.Tests/DownshiftPolicyTests.cs`

**Interfaces:**
- Produces: `public sealed class DownshiftPolicy { public DownshiftDecision RecordStall(DateTimeOffset now); public void Reset(); }` with `public enum DownshiftDecision { None, Downshift, SuppressedCooldown, SuppressedCap }`. Pure, clock passed in. Rules: `Downshift` when this stall is the `StallsToDownshift`-th within `StallWindowSeconds`, not within `CooldownSeconds` of the previous downshift, and fewer than `MaxDownshiftsPerVideo` downshifts so far; suppressed decisions name why; `Reset()` = new video. The CALLER filters reload-induced stalls (Review Focus 3) — the policy sees only genuine ones; its doc comment says so.

- [ ] **Step 1: Failing tests** — two stalls 10 s apart → second returns `Downshift`; two stalls 70 s apart → `None` (window); a stall 30 s after a downshift → `SuppressedCooldown`; third downshift attempt after two granted (outside cooldowns) → `SuppressedCap`; `Reset()` clears everything; one stall alone → `None`. Fixed `DateTimeOffset` arithmetic, no wall clock.

```csharp
[Fact]
public void SecondStallInsideWindow_Downshifts()
{
    var t0 = DateTimeOffset.UnixEpoch;
    var p = new DownshiftPolicy();
    Assert.Equal(DownshiftDecision.None, p.RecordStall(t0));
    Assert.Equal(DownshiftDecision.Downshift, p.RecordStall(t0.AddSeconds(10)));
}
```

(Write the remaining five in the same style — every rule above gets an assertion.)

- [ ] **Step 2: Run to verify fail.**
- [ ] **Step 3: Implement** (small stall-timestamp list pruned to the window, lastDownshiftAt, count).
- [ ] **Step 4: Full suite green.**
- [ ] **Step 5: Commit** — `feat: stall downshift policy`

---

### Task 5: MpvPlayerHost — smoothed rate + buffering edges

**Files:**
- Modify: `src/WinTube.App/Mpv/MpvPlayerHost.cs`

**Interfaces:**
- Consumes: `DemuxerCacheState.ParseState`, `BandwidthSmoother`.
- Produces: `public double? DownloadRateBytesPerSecond { get; }` (UI thread, updated from the 1 s cache poll through a `BandwidthSmoother`; elapsed = actual ms since the previous accepted poll) and `public event Action? BufferingStarted;` raised (UI thread) on each false→true edge of mpv's `paused-for-cache` (observe as a flag property alongside the existing observations; track the previous value on the event thread).

- [ ] **Step 1: Implement** — `ReadBufferedRanges` switches to `ParseState`, feeding the smoother on the event thread; the smoothed value posts to the UI thread only when it changes by >1% (avoid churn). `paused-for-cache` observed like `pause`/`eof-reached` with its own userdata; edge detection on the event thread, `Post(BufferingStarted)`.
- [ ] **Step 2: Build green** (`-c Release` if locked); Core suite untouched.
- [ ] **Step 3: Commit** — `feat: host exposes download rate and buffering edges`

---

### Task 6: PlayerPage — rate-informed Auto + visibility (level 1)

**Files:**
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs` (+ `.xaml` if the flyout footer needs named items)

**Interfaces:**
- Consumes: `AutoQuality(qualities, maxHeight, rate)`, `LoadMeasuredBandwidthBps`/`SaveMeasuredBandwidthBps`/`LoadSimulatedBandwidthMbps`, `Player.DownloadRateBytesPerSecond`.
- Produces: `double? EffectiveRateBytesPerSecond()` — simulated (Mbps × 125_000) when set, else the live host rate, else the persisted last-session rate; used by every Auto resolve. Task 7 consumes `activeQuality`-stepping via the existing `ResolveQuality`/`WriteManifestForHeight` path.

- [ ] **Step 1: Wire the pick** — `WriteManifestForHeight`'s Auto branch calls the three-arg `AutoQuality(qualities, ScreenHeight(), EffectiveRateBytesPerSecond())`. Log every Auto resolve: `auto: rate=42Mbps screen=1440 → 1440p` (`rate=n/a` when unusable; `rate=5Mbps(simulated)` when overridden; Mbps = bytes×8/1e6, one decimal).
- [ ] **Step 2: Persist** — in `TearDownPlayer`, when NOT simulating and `Player.DownloadRateBytesPerSecond` is usable: `SaveMeasuredBandwidthBps(rate)`.
- [ ] **Step 3: Flyout footer** — a disabled, muted `MenuFlyoutItem` (or TextBlock-styled item) appended at the bottom of BOTH quality flyouts by `PopulateQualityFlyout`: "Measured: 42 Mbit/s" / "Simulated: 5 Mbit/s" / absent when no rate; refreshed on every flyout rebuild and on `Opening` of each flyout.
- [ ] **Step 4: Build green; Core suite green.** Trace in the report: EffectiveRate precedence, the log line's three forms, no behavior change for manual pins.
- [ ] **Step 5: Commit** — `feat: Auto quality respects the measured connection rate`

---

### Task 7: PlayerPage — stall downshift (level 2)

**Files:**
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `DownshiftPolicy`/`DownshiftDecision`, `Player.BufferingStarted`, `ReloadKeepingPosition`, `reloadArmed`, `hasPlayed`, `activeQuality`, `hlsQualities`, `preferredHeight`.

- [ ] **Step 1: Wire the policy** — one `DownshiftPolicy` per video (`Reset()` in `OnNavigatedTo`; also on ladder retry re-entry if state carries — trace). Subscribe `BufferingStarted` per host (instance-guarded like the other events): ignore edges while `reloadArmed`, while `!hasPlayed`, and within 5 s after `Opened` (Review Focus 3 — a named constant `PostOpenStallGraceSeconds = 5` in `AdaptiveAutoTuning`); otherwise `RecordStall(DateTimeOffset.UtcNow)`. Act only when `preferredHeight is null` (Auto) and the stream is adaptive.
- [ ] **Step 2: Downshift** — on `Downshift`: target = the rung below `activeQuality` in `hlsQualities` (if already lowest → log suppressed-floor, do nothing); constrain subsequent Auto resolves for THIS video to ≤ the downshifted height (a per-video `autoCeilingHeight` field consumed inside `WriteManifestForHeight`'s Auto branch, reset per video); reload via the same path a quality pick uses. Log `downshift: 2 stalls in 38s → 720p`; on `SuppressedCooldown`/`SuppressedCap` log one line naming it (once per suppression occurrence).
- [ ] **Step 3: Build green; Core suite green.** Trace: the edge-filter windows, Auto-only gating, ceiling interaction with level 1's rate cap (both caps apply; lowest wins), label behavior ("Auto · 720p" follows activeQuality as today).
- [ ] **Step 4: Commit** — `feat: automatic downshift after repeated stalls`

---

### Task 8: E2E gate (user checkpoint)

**Files:** none.

- [ ] **Step 1: Sweep** — full suite + build green; `grep -n "0.7\|60\|90" src/WinTube.Core/Player/AdaptiveAutoTuning.cs` shows the constants live ONLY there (no magic numbers at call sites).
- [ ] **Step 2: Hand the user the checklist** (controller): `simulatedBandwidthMbps` at 4 / 25 / 200 → Auto lands on the expected rungs (label + `auto:` log lines); flyout footer shows "Simulated: …" then, with the key removed, "Measured: …" after ~10 s of playback; NetLimiter/clumsy cap → stalls trigger exactly one downshift per the rules, cooldown + cap respected, suppressed lines logged; manual pin stops everything; restart → first video's pick uses the persisted rate (log shows a rate before any measurement).
- [ ] **Step 3: Commit nothing** — stage closes after the user's pass + final review.
