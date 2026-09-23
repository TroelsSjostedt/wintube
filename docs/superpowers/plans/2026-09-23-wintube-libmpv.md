# WinTube libmpv Player Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Media Foundation playback in PlayerPage with libmpv, so YouTube's current TV HLS manifests (video-only TS + packed-audio renditions, VP9 up to 4K) actually play.

**Architecture:** A pinned `libmpv-2.dll` is fetched by a script and shipped in the app output. A thin hand-rolled P/Invoke layer (`MpvNative`) drives one mpv instance owned by a new `MpvPlayerHost` control, which renders into the XAML tree (render API into SwapChainPanel — or the child-HWND fallback if the stage-0 gate chose it) and exposes a small player interface. PlayerPage drops MediaPlayerElement/AdaptiveMediaSource entirely, gains a custom transport bar, its own fullscreen, and a manifest-level quality picker built on `HlsVariantParser`.

**Tech Stack:** WinUI 3 / .NET 8, libmpv (zhongfly mpv-winbuild x86_64), xUnit for Core tests.

**Spec:** `docs/specs/2026-09-23-wintube-libmpv-design.html` — read it first; every task below implements a numbered spec section.

## Global Constraints

- **STAGE-0 GATE:** Tasks 0a and 0b must pass before ANY later task is started. If 0a (playback) fails → stop, report, re-evaluate. If 0b (render-API embedding) fails → all later tasks use the child-HWND embedding the spike proved instead, interface unchanged.
- No NuGet mpv wrapper. P/Invoke only, <20 functions.
- `libs/mpv/` is gitignored; the DLL never enters git history.
- Hover previews, `StreamService` ladder, and the permanent `player retry:` log line in `RetryOrFailAsync` are untouched.
- Existing behavior contracts that must survive the rewrite: resume position (link `startAt` first, recorded progress otherwise, `startAt` cleared after first use), one-retry client ladder on failure/stall, volume persisted debounced 500 ms via `PlayerSettingsStore` (0–1 double), click-to-video toggles play/pause except on ButtonBase/RangeBase, SponsorBlock skip + toast, progress report every 5 s and once on leave, Esc walks comments back before anything else.
- Build check after every app task: `dotnet build src/WinTube.App -p:Platform=x64`. Core tests: `dotnet test tests/WinTube.Core.Tests`.
- Commit after every task (this is WinTube — committing freely is fine).
- App code comments follow the existing style: sparse, only for non-obvious constraints.

---

### Task 1: libmpv download script + packaging hook

**Files:**
- Create: `tools/get-libmpv.ps1`
- Modify: `.gitignore` (add `libs/`)
- Modify: `src/WinTube.App/WinTube.App.csproj`

**Interfaces:**
- Produces: `libs/mpv/libmpv-2.dll` on disk after running the script; the App build copies it to the output directory and **fails with a clear message when it is missing**.

- [ ] **Step 1: Pick the pinned release**

Find the newest release of `zhongfly/mpv-winbuild` and its `mpv-dev-x86_64-*.7z` asset (the "dev" archive is the one containing `libmpv-2.dll`):

```powershell
gh release view --repo zhongfly/mpv-winbuild --json tagName,assets
```

Download that asset once by hand, extract, confirm `libmpv-2.dll` exists, and compute its archive hash:

```powershell
Get-FileHash mpv-dev-x86_64-<version>.7z -Algorithm SHA256
```

Record the exact asset URL and hash — they go into the script as constants.

- [ ] **Step 2: Write `tools/get-libmpv.ps1`**

```powershell
# Fetches the pinned libmpv build WinTube links against and unpacks libmpv-2.dll
# into libs/mpv/ (gitignored). Upgrading mpv = change $Url and $Sha256 together.
$ErrorActionPreference = "Stop"

$Url = "<the exact asset URL from step 1>"
$Sha256 = "<the hash from step 1>"

$root = Split-Path $PSScriptRoot -Parent
$dest = Join-Path $root "libs/mpv"
$dll = Join-Path $dest "libmpv-2.dll"
$marker = Join-Path $dest "version.txt"

if ((Test-Path $dll) -and (Test-Path $marker) -and (Get-Content $marker) -eq $Sha256) {
    Write-Host "libmpv already present and pinned; nothing to do."
    exit 0
}

New-Item -ItemType Directory -Force $dest | Out-Null
$archive = Join-Path $dest "libmpv.7z"
Invoke-WebRequest $Url -OutFile $archive

$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $Sha256) { throw "libmpv archive hash mismatch: expected $Sha256, got $actual" }

# Windows has no built-in .7z extractor; require 7-Zip with a clear message.
$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if (-not $sevenZip) { throw "7z not found on PATH. Install with: winget install 7zip.7zip" }
& $sevenZip.Source e $archive -o"$dest" "*/libmpv-2.dll" -r -y | Out-Null

if (-not (Test-Path $dll)) { throw "libmpv-2.dll not found in archive" }
Set-Content $marker $Sha256
Remove-Item $archive
Write-Host "libmpv-2.dll unpacked to libs/mpv/"
```

Note for CI: `windows-latest` runners have `7z` preinstalled, so the guard only bites on dev machines.

- [ ] **Step 3: Gitignore and csproj wiring**

`.gitignore`: add a line `libs/`.

`WinTube.App.csproj`, new ItemGroup after the icon Content group:

```xml
<ItemGroup>
  <!-- Native playback engine, fetched by tools/get-libmpv.ps1 (pinned, gitignored). -->
  <Content Include="..\..\libs\mpv\libmpv-2.dll" Link="libmpv-2.dll" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
<Target Name="EnsureLibMpv" BeforeTargets="Build">
  <Error Condition="!Exists('..\..\libs\mpv\libmpv-2.dll')"
         Text="libmpv-2.dll missing - run tools/get-libmpv.ps1 first." />
</Target>
```

- [ ] **Step 4: Verify both paths**

Run: `pwsh tools/get-libmpv.ps1` → DLL lands in `libs/mpv/`. Run it again → "nothing to do". Temporarily rename `libs/mpv` → `dotnet build src/WinTube.App -p:Platform=x64` fails with the EnsureLibMpv message; rename back → build succeeds and `libmpv-2.dll` sits next to `WinTube.App.exe` in the output.

- [ ] **Step 5: Commit**

```bash
git add tools/get-libmpv.ps1 .gitignore src/WinTube.App/WinTube.App.csproj
git commit -m "feat: pinned libmpv download script and packaging hook"
```

---

### Task 0a: GATE — libmpv plays YouTube HLS (throwaway console spike)

Runs after Task 1 (needs the DLL). **This is a spike: the code is throwaway, the output is a yes/no plus findings.** Model it on `scratchpad\manifest-probe` (copy its repo `NuGet.config` trick — the work Azure feed 401s).

**Files:**
- Create: `<scratchpad>/mpv-spike/` console project referencing `src/WinTube.Core` (never committed)

**Interfaces:**
- Produces: a written PASS/FAIL verdict for: (a) VISIONOS HLS URL plays **with audio**, (b) a variant above 360p renders, (c) `seek` works. Plus the working mpv option set, which Task 3 copies.

- [ ] **Step 1: Console spike**

Resolve a stream exactly like the probe did (`StreamService.ResolveAsync` via a `Session`-less setup — reuse the manifest-probe pattern: `InnerTubeClient` + `VisitorDataStore` + `PostAsync("player", ClientKind.VisionOs, ...)` and read `streamingData.hlsManifestUrl`). Then drive mpv with its own window (no embedding yet — that is Task 0b's question):

```csharp
using System.Runtime.InteropServices;

const string Lib = "libmpv-2.dll";  // copy it next to the spike exe
[DllImport(Lib)] static extern IntPtr mpv_create();
[DllImport(Lib)] static extern int mpv_initialize(IntPtr h);
[DllImport(Lib)] static extern int mpv_set_option_string(IntPtr h, byte[] name, byte[] value);
[DllImport(Lib)] static extern int mpv_command(IntPtr h, IntPtr[] args);
[DllImport(Lib)] static extern IntPtr mpv_wait_event(IntPtr h, double timeout);

static byte[] B(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");
static int Cmd(IntPtr h, params string[] args)
{
    var ptrs = new IntPtr[args.Length + 1];
    for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToHGlobalAnsi(args[i]);
    try { return mpv_command(h, ptrs); }
    finally { foreach (var p in ptrs[..^1]) Marshal.FreeHGlobal(p); }
}

var h = mpv_create();
mpv_set_option_string(h, B("force-window"), B("yes"));
mpv_set_option_string(h, B("user-agent"), B(stream.UserAgent));
mpv_set_option_string(h, B("hwdec"), B("auto-safe"));
mpv_set_option_string(h, B("terminal"), B("yes"));       // mpv logs to the console
mpv_set_option_string(h, B("msg-level"), B("all=info"));
mpv_initialize(h);
Cmd(h, "loadfile", stream.Url.ToString());

// After ~15 s of playback, prove seek:
_ = Task.Run(async () => { await Task.Delay(15000); Cmd(h, "seek", "60", "absolute"); });
while (true) { mpv_wait_event(h, 1.0); }   // Ctrl+C to end; mpv's own log shows track/format info
```

- [ ] **Step 2: Judge against the gate**

In mpv's console log check: selected video track resolution (>360p — with VP9 it should pick the top variant), an audio track playing (hear it), and the seek landing. Also load a **filtered single-variant manifest from a local temp file** (write the master fetched with the stream user agent to `spike.m3u8`, keep one 1080p `#EXT-X-STREAM-INF` pair plus all `#EXT-X-MEDIA` lines, `Cmd(h, "loadfile", path)`) — this pre-proves Task 8's mechanism.

- [ ] **Step 3: Report**

PASS → continue to Task 0b. FAIL → **stop the plan**, report exactly what mpv logged; the stage is re-evaluated. Either way, note the option set that worked and delete nothing yet (0b reuses the spike).

---

### Task 0b: GATE — embedding: render API into SwapChainPanel, else child HWND

**Files:**
- Create: temporary `src/WinTube.App/Views/SpikePage.xaml(.cs)` (same trick as v1's Task 0; deleted in Task 10)
- Modify: `src/WinTube.App/MainWindow.xaml` (temporary nav item to reach SpikePage; also removed in Task 10)

**Interfaces:**
- Produces: THE EMBEDDING DECISION. Either "render API works — Task 3 implements it with these exact ANGLE/EGL steps" or "child HWND — Task 3 implements `--wid` with these exact steps". Written into the task ledger; every later task honors it.

- [ ] **Step 1: Attempt the render API path**

A SpikePage with a `SwapChainPanel` and a Play button. The mpv render API needs an OpenGL context the app owns; on Windows/XAML that means ANGLE (EGL on D3D11). Try in this order, cheapest first:

1. **ANGLE binaries:** get `libEGL.dll` + `libGLESv2.dll`. Candidate sources, try in order: (a) the `mpv-dev` archive from Task 1 (some winbuilds bundle ANGLE — check), (b) a pinned release of `google/angle` via a prebuilt distribution (e.g. the `ANGLE.WindowsStore` NuGet — old but GLES3-capable), (c) copy from a local Chromium/Edge installation just for the spike (not shippable, but answers feasibility).
2. **EGL surface from the SwapChainPanel:** ANGLE's `EGL_ANGLE_surface_d3d_texture_2d_share_handle` / `EGLNativeWindowType = ISwapChainPanelNative` flow: create EGL display with `eglGetPlatformDisplayEXT(EGL_PLATFORM_ANGLE_ANGLE, ...)`, window surface from a `PropertySet` holding the SwapChainPanel — the classic UWP/ANGLE interop, which WinUI 3's SwapChainPanel still supports via `ISwapChainPanelNative.SetSwapChain`.
3. **mpv render context:** `mpv_render_context_create` with `MPV_RENDER_API_TYPE_OPENGL` and a `get_proc_address` callback backed by `eglGetProcAddress`; `mpv_render_context_set_update_callback` → on callback, on a render thread: `eglMakeCurrent`, `mpv_render_context_render` with the default framebuffer + panel size, `eglSwapBuffers`.

Success = the Task 0a video visibly plays inside the SwapChainPanel with a XAML `TextBlock` overlaid on top, window resize follows. Time-box this: if it is not rendering after a focused day of work, that IS the answer — fall back.

- [ ] **Step 2: If render API fails — prove child HWND**

On SpikePage, create a bare Win32 child window over the panel's screen rect (`CreateWindowEx` with `WS_CHILD | WS_VISIBLE`, parent = the app's main HWND from `WindowNative.GetWindowHandle`), hand it to mpv via `mpv_set_option_string("wid", hwnd.ToString())` before `mpv_initialize`, reposition it on the panel's `SizeChanged`/`LayoutUpdated`. Verify: video plays in-place; check whether XAML siblings (a test Border) render above it — if not, note that overlays will need `Popup`-based hosting and verify a `Popup` does render above.

- [ ] **Step 3: Record the decision and commit the spike page**

Write the verdict + exact working recipe into the ledger/commit message. Commit SpikePage as-is (it is the reference implementation for Task 3):

```bash
git add src/WinTube.App/Views/SpikePage.xaml src/WinTube.App/Views/SpikePage.xaml.cs src/WinTube.App/MainWindow.xaml
git commit -m "spike: prove mpv embedding path (temporary page, removed at stage end)"
```

**CHECKPOINT: report the gate outcome to the user before continuing.**

---

### Task 2: MpvNative P/Invoke layer

**Files:**
- Create: `src/WinTube.App/Mpv/MpvNative.cs`

**Interfaces:**
- Produces: `static class MpvNative` with the exact imports below plus helpers `MpvNative.Command(IntPtr, params string[])`, `MpvNative.SetOption(IntPtr, string, string)`, `MpvNative.GetPropertyDouble/String`, `MpvNative.ObserveDouble(IntPtr, string, ulong userdata)`. Also `enum MpvEventId` and `struct MpvEvent`/`MpvEventProperty` matching client.h.

- [ ] **Step 1: Write the layer**

```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace WinTube.App.Mpv;

/// The <20 libmpv functions WinTube uses, straight from client.h/render.h. Strings cross
/// as UTF-8; mpv owns every pointer it returns from wait_event until the next call.
internal static partial class MpvNative
{
    private const string Lib = "libmpv-2";

    [LibraryImport(Lib)] internal static partial IntPtr mpv_create();
    [LibraryImport(Lib)] internal static partial int mpv_initialize(IntPtr handle);
    [LibraryImport(Lib)] internal static partial void mpv_terminate_destroy(IntPtr handle);
    [LibraryImport(Lib)] internal static partial int mpv_set_option_string(IntPtr handle, byte[] name, byte[] value);
    [LibraryImport(Lib)] internal static partial int mpv_command(IntPtr handle, IntPtr[] args);
    [LibraryImport(Lib)] internal static partial int mpv_get_property(IntPtr handle, byte[] name, int format, out double data);
    [LibraryImport(Lib)] internal static partial int mpv_set_property(IntPtr handle, byte[] name, int format, ref double data);
    [LibraryImport(Lib)] internal static partial int mpv_observe_property(IntPtr handle, ulong userdata, byte[] name, int format);
    [LibraryImport(Lib)] internal static partial IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [LibraryImport(Lib)] internal static partial void mpv_wakeup(IntPtr handle);
    [LibraryImport(Lib)] internal static partial IntPtr mpv_error_string(int error);
    // Render API (used only on the render-API embedding; harmless otherwise):
    [LibraryImport(Lib)] internal static partial int mpv_render_context_create(out IntPtr ctx, IntPtr handle, IntPtr[] params_);
    [LibraryImport(Lib)] internal static partial int mpv_render_context_render(IntPtr ctx, IntPtr[] params_);
    [LibraryImport(Lib)] internal static partial void mpv_render_context_set_update_callback(IntPtr ctx, IntPtr callback, IntPtr userdata);
    [LibraryImport(Lib)] internal static partial void mpv_render_context_free(IntPtr ctx);

    internal const int FormatDouble = 5;      // MPV_FORMAT_DOUBLE
    internal const int FormatFlag = 3;        // MPV_FORMAT_FLAG

    internal enum EventId
    {
        None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4,
        CommandReply = 5, StartFile = 6, EndFile = 7, FileLoaded = 8, PropertyChange = 22,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Event { public EventId Id; public int Error; public ulong Userdata; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventProperty { public IntPtr Name; public int Format; public IntPtr Data; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EventEndFile { public int Reason; public int Error; public long PlaylistEntryId; }
    internal const int EndFileEof = 0, EndFileError = 4;   // MPV_END_FILE_REASON_*

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    internal static void SetOption(IntPtr handle, string name, string value) =>
        mpv_set_option_string(handle, Utf8(name), Utf8(value));

    internal static int Command(IntPtr handle, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        for (var i = 0; i < args.Length; i++) ptrs[i] = StringToUtf8(args[i]);
        try { return mpv_command(handle, ptrs); }
        finally { for (var i = 0; i < args.Length; i++) Marshal.FreeHGlobal(ptrs[i]); }
    }

    internal static double? GetDouble(IntPtr handle, string name) =>
        mpv_get_property(handle, Utf8(name), FormatDouble, out var value) >= 0 ? value : null;

    internal static void SetDouble(IntPtr handle, string name, double value) =>
        mpv_set_property(handle, Utf8(name), FormatDouble, ref value);

    internal static void ObserveDouble(IntPtr handle, string name, ulong userdata) =>
        mpv_observe_property(handle, userdata, Utf8(name), FormatDouble);

    internal static string ErrorString(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";

    private static IntPtr StringToUtf8(string s)
    {
        var bytes = Utf8(s);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }
}
```

Adjust anything the spike learned differently (e.g. extra render-param structs for the render API, or none of them for `--wid`). If the child-HWND path won, also import nothing extra — `wid` is just an option.

- [ ] **Step 2: Build**

Run: `dotnet build src/WinTube.App -p:Platform=x64` → succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/WinTube.App/Mpv/MpvNative.cs
git commit -m "feat: thin libmpv P/Invoke layer"
```

---

### Task 3: MpvPlayerHost control

**Files:**
- Create: `src/WinTube.App/Mpv/MpvPlayerHost.cs` (code-only control; the panel is built in code)

**Interfaces:**
- Consumes: `MpvNative` (Task 2), the embedding recipe from Task 0b.
- Produces — this exact surface, which Tasks 5–9 program against:

```csharp
public sealed class MpvPlayerHost : Grid, IDisposable
{
    public event Action? Opened;                    // FileLoaded, on UI thread
    public event Action<double>? PositionChanged;   // observed time-pos (seconds), UI thread
    public event Action? EndReached;                // end-file reason eof, UI thread
    public event Action<string>? Errored;           // end-file reason error, UI thread
    public double Position { get; }                 // last observed time-pos, seconds
    public double Duration { get; }                 // last observed duration, seconds
    public bool IsPaused { get; }
    public void Load(string urlOrPath, double startAtSeconds, string userAgent, string? audioLanguage);
    public void Play(); public void Pause(); public void TogglePause();
    public void SeekTo(double seconds);
    public double Volume { get; set; }              // 0..1, mapped to mpv 0..100
    public double Speed { get; set; }               // 0.25..2
    public void Dispose();                          // stop event thread, terminate mpv
}
```

- [ ] **Step 1: Implement lifecycle + events**

One mpv handle created in the constructor: options `hwdec=auto-safe`, `keep-open=yes` (EndReached fires but the last frame stays), `video-timing-offset=0`, `terminal=no`, plus `alang` per Load. `Load` uses `MpvNative.Command(h, "loadfile", urlOrPath, "replace", "0", $"start={startAtSeconds}")` — per-load `start` avoids seeking after open; set `user-agent` before the load command. Observe `time-pos` (userdata 1), `duration` (2), `pause` (3, FormatFlag).

Event thread: a dedicated `Thread` looping `mpv_wait_event(h, 1.0)`, translating events and marshalling via `DispatcherQueue.TryEnqueue`. On `Dispose`: set a `volatile bool disposed`, `mpv_wakeup`, `Join` the thread, then `mpv_terminate_destroy`. Never touch the handle after. Events raised after dispose are dropped (`disposed` check inside the enqueue lambda).

`Position`/`Duration` cache the last observed values (plain doubles written on the UI thread only). `Volume`/`Speed` setters call `MpvNative.SetDouble(h, "volume", value * 100)` / `("speed", value)`; `TogglePause` cycles the `pause` flag via `MpvNative.Command(h, "cycle", "pause")`.

- [ ] **Step 2: Implement rendering per the Task 0b decision**

Render API: transplant SpikePage's working ANGLE/EGL + `mpv_render_context` code — SwapChainPanel child, render thread driven by the update callback, resize handling from `SizeChanged`. Child HWND: transplant SpikePage's `CreateWindowEx` + `wid` + reposition-on-layout code, positioning against this control's bounds (`TransformToVisual(null)` + `XamlRoot.RasterizationScale`).

Whichever ANGLE DLLs the spike settled on: add them to `tools/get-libmpv.ps1` (same pinned-URL + hash pattern) and to the csproj Content group from Task 1, in this task.

- [ ] **Step 3: Smoke-test via SpikePage**

Point SpikePage at `MpvPlayerHost` instead of its inline code: Load a resolved URL, confirm Opened/PositionChanged fire, pause/seek/volume/speed all work, navigating away and back does not leak (watch the process in Task Manager for runaway threads on repeated visits).

- [ ] **Step 4: Commit**

```bash
git add src/WinTube.App/Mpv/MpvPlayerHost.cs src/WinTube.App/Views/SpikePage.xaml.cs tools/get-libmpv.ps1 src/WinTube.App/WinTube.App.csproj
git commit -m "feat: MpvPlayerHost control owning the libmpv instance"
```

---

### Task 4: Core — single-variant manifest filter + Auto height choice (TDD)

**Files:**
- Modify: `src/WinTube.Core/Player/HlsVariantParser.cs`
- Test: `tests/WinTube.Core.Tests/HlsVariantParserTests.cs`

**Interfaces:**
- Produces: `HlsVariantParser.FilterToBandwidth(string masterPlaylist, uint bandwidth)` — keeps every non-STREAM-INF line (header, `#EXT-X-MEDIA` audio renditions) and exactly the one STREAM-INF/URI pair whose `BANDWIDTH=` equals `bandwidth`. And `HlsVariantParser.AutoQuality(IReadOnlyList<HlsQuality> qualities, int maxHeight)` — the highest quality with `Height <= maxHeight`, or the lowest offered when everything exceeds it; null for an empty list.

- [ ] **Step 1: Write the failing tests** (extend the existing test class, reusing its `Manifest` constant)

```csharp
[Fact]
public void FilterToBandwidth_KeepsOnlyThatPair_AndAllOtherLines()
{
    var filtered = HlsVariantParser.FilterToBandwidth(Manifest, 6321284);
    Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO", filtered);
    Assert.Contains("v1080-avc.m3u8", filtered);
    Assert.DoesNotContain("v1080-vp9.m3u8", filtered);
    Assert.DoesNotContain("v2160.m3u8", filtered);
    Assert.DoesNotContain("v360-hi.m3u8", filtered);
    // The audio-only STREAM-INF pair is dropped too: a STREAM-INF pair is kept iff its
    // bandwidth matches. Real YouTube audio travels on #EXT-X-MEDIA lines, which pass through.
    Assert.DoesNotContain("audio-only.m3u8", filtered);
    Assert.Single(HlsVariantParser.Parse(filtered));
}

[Fact]
public void AutoQuality_HighestAtOrBelowScreen()
{
    var levels = HlsVariantParser.QualityLevels(HlsVariantParser.Parse(Manifest)); // 2160, 1080, 360
    Assert.Equal(1080, HlsVariantParser.AutoQuality(levels, 1440)!.Height);
    Assert.Equal(2160, HlsVariantParser.AutoQuality(levels, 2160)!.Height);
    Assert.Equal(360, HlsVariantParser.AutoQuality(levels, 240)!.Height);   // everything too tall -> lowest
    Assert.Null(HlsVariantParser.AutoQuality([], 1080));
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/WinTube.Core.Tests --filter HlsVariantParserTests`
Expected: FAIL — the two methods do not exist.

- [ ] **Step 3: Implement**

`FilterToBandwidth` mirrors `FilterToAvc`'s pair-walking loop, with the keep condition changed to: the `BandwidthPattern` match equals the argument.

`AutoQuality`: `qualities.FirstOrDefault(q => q.Height <= maxHeight) ?? qualities.LastOrDefault()` (the list is already tallest-first).

- [ ] **Step 4: Run all Core tests**

Run: `dotnet test tests/WinTube.Core.Tests`
Expected: all green (203 + new).

- [ ] **Step 5: Commit**

```bash
git add src/WinTube.Core/Player/HlsVariantParser.cs tests/WinTube.Core.Tests/HlsVariantParserTests.cs
git commit -m "feat: single-variant manifest filter and Auto height choice"
```

---

### Task 5: PlayerPage on MpvPlayerHost (core swap)

The big one: MediaPlayerElement out, MpvPlayerHost in, every existing behavior contract preserved. The transport bar (Task 6), fullscreen (Task 7) and quality picker (Task 8) come later — after this task the video plays with NO on-screen controls beyond click-to-pause, and that is expected.

**Files:**
- Modify: `src/WinTube.App/Views/PlayerPage.xaml` — replace the `MediaPlayerElement` with `<mpv:MpvPlayerHost x:Name="Player" Tapped="OnPlayerTapped"/>` (`xmlns:mpv="using:WinTube.App.Mpv"`); everything else (toast, ring, error panel, comments panel, title bar) stays.
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: the exact `MpvPlayerHost` surface from Task 3, `HlsVariantParser.FilterToBandwidth`/`AutoQuality` (Task 4), `ResolvedStream(Uri Url, ClientKind Client, string UserAgent, bool IsAdaptive, string? OriginalAudioLanguage)`.
- Produces: `PlayerPage` fields later tasks touch: `MpvPlayerHost Player` (x:Name), `double CurrentPositionSeconds => Player.Position`, methods `TogglePlayPause()`, and the preserved `RetryOrFailAsync(string reason)`.

- [ ] **Step 1: Rewrite the playback pipeline in PlayerPage.xaml.cs**

Delete: `player` (MediaPlayer), `adaptiveSource`, `OnMediaOpened/OnMediaFailed/OnPlaybackStateChanged/OnVolumeChanged` MediaPlayer handlers, `SelectOriginalAudioTrack`, `FixVolumeButtonTooltip`, `FindDescendant`, the whole AdaptiveMediaSource branch of `PlayAsync`, and the `Windows.Media.*` / `Windows.Media.Streaming.Adaptive` usings.

New `PlayAsync(ResolvedStream stream)`:

```csharp
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
        target = WriteManifestForHeight(preferredHeight);   // Task 8 fills the picker UI; the mechanism lands here
    }
    else
    {
        hlsMaster = null;
        hlsVariants = [];
        hlsQualities = [];
        target = stream.Url.ToString();
    }

    hasPlayed = false;
    handledFailure = false;
    var resume = startAt?.TotalSeconds ?? App.Session.Progress.ResumePosition(video!.Id) ?? 0;
    startAt = null;
    Player.Volume = App.Session.PlayerSettings.LoadVolume();
    Player.Load(target, resume, stream.UserAgent, stream.OriginalAudioLanguage);

    stallTimer = dispatcher.CreateTimer();
    stallTimer.Interval = TimeSpan.FromSeconds(10);
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
```

New fields replacing the old picker state: `private string? hlsMaster; private HlsQuality? activeQuality;` (keep `hlsVariants`, `hlsQualities`, static `preferredHeight`). `App.MainWindowId`: expose the main `WindowId` from `App`/`MainWindow` if not already available (`AppWindow.Id`).

Host events, wired once in the constructor after `InitializeComponent()`:

```csharp
Player.Opened += () =>
{
    if (leftPage) return;
    HideOverlays();
    if (!hasPlayed)
    {
        hasPlayed = true;
        stallTimer?.Stop();
        App.Session.History.Record(video!);
        StartProgressTimer();
        _ = LoadSponsorSegmentsAsync();
    }
};
Player.PositionChanged += _ => OnPlayerPosition();
Player.EndReached += () => ReportProgressOnce();
Player.Errored += message => { if (!leftPage) _ = RetryOrFailAsync(message); };
```

`OnPlayerPosition()` runs the sponsor check inline (replaces the 250 ms `sponsorTimer` — property observation fires at least that often and also on every seek): move `OnSponsorTick`'s body here, using `Player.Position`/`Player.Duration` and `Player.SeekTo(target)`; delete `sponsorTimer`/`StartSponsorTimer`. `ReportProgressOnce` becomes `App.Session.Progress.Report(video.Id, Player.Position, Player.Duration)` guarded by `Duration > 0`. `OnPlayerTapped`'s ancestor walk stays exactly as-is and now calls `TogglePlayPause()`:

```csharp
private void TogglePlayPause() => Player.TogglePause();
```

Volume persistence: `MpvPlayerHost` gets no event for it yet — the transport bar (Task 6) drives volume, so persistence moves there; this task keeps `Player.Volume = ...LoadVolume()` on load and drops the old debounce members (`volumeSaveTimer`, `pendingVolume`, `CreateVolumeSaveTimer`) — Task 6 reintroduces the debounce at the slider.

`TearDownPlayer()` shrinks to: flush nothing (Task 6 owns the volume flush), stop `progressTimer`/`stallTimer`, `Player.Dispose()` — but the host lives in XAML and the page instance may be re-navigated. Decision: dispose in `OnNavigatedFrom` and create the host **in code** instead: give PlayerPage a `Grid x:Name="PlayerSlot"` in XAML, construct `new MpvPlayerHost()` per `OnNavigatedTo`, insert at `PlayerSlot.Children.Insert(0, ...)`, remove + dispose in teardown. This keeps one mpv instance per visit, mirroring today's per-visit MediaPlayer.

The ladder (`StartAsync`/`RetryOrFailAsync` with its permanent log line) is byte-for-byte untouched apart from `TearDownPlayer` internals.

- [ ] **Step 2: Build + Core tests**

Run: `dotnet build src/WinTube.App -p:Platform=x64` and `dotnet test tests/WinTube.Core.Tests` → green.

- [ ] **Step 3: Manual smoke (the stage's turning point)**

Launch the app, play a normal video: video+audio well above 360p, resume works (leave, re-enter), SponsorBlock toast fires on a sponsored video (e.g. an LTT video), progress recorded (check Continue watching), click-to-pause works, Shorts play, ladder untouched (log stays quiet). Screenshot-verify per app-lifecycle rules.

- [ ] **Step 4: Commit**

```bash
git add src/WinTube.App/Views/PlayerPage.xaml src/WinTube.App/Views/PlayerPage.xaml.cs src/WinTube.App/App.xaml.cs
git commit -m "feat: PlayerPage plays through libmpv"
```

---

### Task 6: Custom transport bar

**Files:**
- Modify: `src/WinTube.App/Views/PlayerPage.xaml` — new overlay grid inside the video grid, above SkipToast in z-order
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `Player` (MpvPlayerHost), `TogglePlayPause()`, `App.Session.PlayerSettings.LoadVolume()/SaveVolume(double)`.
- Produces: `TransportBar` (x:Name) and `void SetTransportVisible(bool)`; `FullScreenButton` exists but is wired in Task 7.

- [ ] **Step 1: XAML**

```xml
<Grid x:Name="TransportBar" VerticalAlignment="Bottom" Padding="16,8,16,12"
      Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
      PointerEntered="OnTransportEntered" PointerExited="OnTransportExited">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/><RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>
    <Slider x:Name="SeekBar" Grid.Row="0" Minimum="0" StepFrequency="1"
            ThumbToolTipValueConverter="{x:Null}"/>
    <Grid Grid.Row="1" ColumnDefinitions="Auto,Auto,Auto,*,Auto,Auto">
        <Button x:Name="PlayPauseButton" Grid.Column="0" Click="OnPlayPauseClick"
                Background="Transparent" BorderThickness="0">
            <FontIcon x:Name="PlayPauseIcon" Glyph="&#xE769;" FontSize="16"/>
        </Button>
        <Button Grid.Column="1" Background="Transparent" BorderThickness="0" Margin="4,0,0,0">
            <FontIcon Glyph="&#xE767;" FontSize="16"/>
            <Button.Flyout>
                <Flyout Placement="Top">
                    <Slider x:Name="VolumeSlider" Minimum="0" Maximum="100" Width="160"
                            Orientation="Horizontal" ValueChanged="OnVolumeSliderChanged"/>
                </Flyout>
            </Button.Flyout>
        </Button>
        <TextBlock x:Name="TimeLabel" Grid.Column="2" Margin="12,0,0,0" VerticalAlignment="Center"
                   FontSize="13" Text="0:00 / 0:00"/>
        <DropDownButton x:Name="SpeedButton" Grid.Column="4" Content="1×"
                        Background="Transparent" BorderThickness="0" Margin="0,0,4,0">
            <DropDownButton.Flyout><MenuFlyout x:Name="SpeedFlyout"/></DropDownButton.Flyout>
        </DropDownButton>
        <Button x:Name="FullScreenButton" Grid.Column="5" Click="OnFullScreenClick"
                Background="Transparent" BorderThickness="0">
            <FontIcon x:Name="FullScreenIcon" Glyph="&#xE740;" FontSize="16"/>
        </Button>
    </Grid>
</Grid>
```

Place it after `SkipToast`/before `LoadingRing` in the video grid; bump SkipToast's bottom margin so the toast clears the bar.

- [ ] **Step 2: Code-behind**

- **Play/pause:** `OnPlayPauseClick` → `TogglePlayPause()`. A `Player.PositionChanged`-driven `UpdateTransport()` sets `PlayPauseIcon.Glyph = Player.IsPaused ? "" : ""`, `TimeLabel.Text = $"{Fmt(Player.Position)} / {Fmt(Player.Duration)}"` (`Fmt`: `h:mm:ss` above an hour, else `m:ss`), and — unless `seekBarHeld` — `SeekBar.Maximum = Player.Duration; SeekBar.Value = Player.Position`.
- **Seek:** `seekBarHeld` set true on SeekBar `AddHandler(PointerPressedEvent, ..., handledEventsToo: true)`, and on `PointerReleased`/`PointerCaptureLost`: `Player.SeekTo(SeekBar.Value); seekBarHeld = false;`. Also handle a plain click (no drag): the released handler covers it since Value updated on press.
- **Volume:** `VolumeSlider.Value = LoadVolume() * 100` once per load; `OnVolumeSliderChanged` → `Player.Volume = e.NewValue / 100` plus the 500 ms debounced `SaveVolume(e.NewValue / 100)` (reintroduce `volumeSaveTimer`/`pendingVolume` exactly as they were, flushed in `TearDownPlayer` as before).
- **Speed:** fill `SpeedFlyout` once with `RadioMenuFlyoutItem`s for 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 (`GroupName="speed"`, 1 checked); click → `Player.Speed = value; SpeedButton.Content = $"{value:0.##}×"`. Speed resets to 1 on each new video (`Player` is fresh per visit; set the button back to "1×" in `OnNavigatedTo`).
- **Auto-hide:** a 3 s one-shot `DispatcherQueueTimer transportHideTimer`; `PointerMoved` on the video grid (AddHandler, handledEventsToo) shows the bar (`SetTransportVisible(true)`, restart timer); the Tick hides it and the cursor (`ProtectedCursor = null`) **unless** paused, the pointer is over the bar (`OnTransportEntered/Exited` flag), or a flyout is open (check `SpeedFlyout.IsOpen` / volume flyout `IsOpen`). `SetTransportVisible(true)` restores `ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow)`.
- **Keyboard:** extend `OnKeyDown` (before the comments block, not touching its Esc logic): Space → `TogglePlayPause(); e.Handled = true;` (skip when focus sits in a TextBox — none exist on this page today), Left/Right → `Player.SeekTo(Math.Max(0, Player.Position ∓ 10)); e.Handled = true;` — and any of these also wake the transport bar.
- **Click-to-pause guard:** the existing ButtonBase/RangeBase ancestor walk in `OnPlayerTapped` already exempts every control in the bar; verify a tap on the bar's empty background does NOT toggle (add `Tapped` → `e.Handled = true` on TransportBar, mirroring the comments panel).

- [ ] **Step 3: Manual check**

Bar shows on load and on mouse move, hides after 3 s with the cursor, never hides while paused/hovered/flyout-open; seek by click and by drag; volume survives restart; 2× audibly faster; space/arrows work; toast not covered.

- [ ] **Step 4: Commit**

```bash
git add src/WinTube.App/Views/PlayerPage.xaml src/WinTube.App/Views/PlayerPage.xaml.cs
git commit -m "feat: custom transport bar with speed picker and auto-hide"
```

---

### Task 7: Fullscreen

**Files:**
- Modify: `src/WinTube.App/MainWindow.xaml.cs` — add `SetPlayerFullScreen(bool)`
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs`
- Modify: `src/WinTube.App/Views/PlayerPage.xaml` — the title-bar row gets `x:Name="TitleRow"`

**Interfaces:**
- Consumes: `FullScreenButton`/`FullScreenIcon` from Task 6.
- Produces: `MainWindow.SetPlayerFullScreen(bool on)` — window presenter + NavigationView chrome; `PlayerPage.isFullScreen`.

- [ ] **Step 1: MainWindow**

```csharp
/// Fullscreen video: the window presenter flips and the shell chrome (pane, back arrow,
/// version footer) collapses so the player page is the only thing on screen.
public void SetPlayerFullScreen(bool on)
{
    AppWindow.SetPresenter(on ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
    NavView.IsPaneVisible = !on;
    NavView.IsBackButtonVisible = on ? NavigationViewBackButtonVisible.Collapsed
                                     : NavigationViewBackButtonVisible.Visible;
}
```

Adapt member names to MainWindow's actual fields (`NavView` per MainWindow.xaml; reuse however AppWindow is already reached for `SetIcon`). If leaving fullscreen restores a stale maximized/normal state oddly, capture the prior presenter kind instead of hardcoding Default.

- [ ] **Step 2: PlayerPage**

`OnFullScreenClick` toggles: `isFullScreen`, `((MainWindow)App.MainWindow).SetPlayerFullScreen(isFullScreen)` (use the app's actual main-window accessor), `TitleRow.Visibility` (the row with title/author/quality/comments/copy buttons — in fullscreen the video grid is everything), and `FullScreenIcon.Glyph` (`` enter / `` exit). Esc order in `OnKeyDown` becomes: replies → comments panel → fullscreen → nothing. Double-click on the video toggles fullscreen (`DoubleTapped` on the host, same ancestor guard as Tapped). **Leaving the page while fullscreen must restore the window** — call `SetPlayerFullScreen(false)` in `OnNavigatedFrom` when `isFullScreen`.

- [ ] **Step 3: Manual check**

Fullscreen in/out via button, double-click and Esc; comments panel opens IN fullscreen (the parked limitation is now fixed — verify explicitly); nav pane and title row come back intact; back-navigation while fullscreen restores the window.

- [ ] **Step 4: Commit**

```bash
git add src/WinTube.App/MainWindow.xaml.cs src/WinTube.App/Views/PlayerPage.xaml src/WinTube.App/Views/PlayerPage.xaml.cs
git commit -m "feat: player fullscreen with preserved overlays"
```

---

### Task 8: Quality picker on mpv

**Files:**
- Modify: `src/WinTube.App/Views/PlayerPage.xaml.cs`

**Interfaces:**
- Consumes: `hlsMaster`, `hlsQualities`, `activeQuality`, `WriteManifestForHeight(int?)` (Task 5), `QualityButton`/`QualityLabel`/`QualityFlyout` (existing XAML, unchanged).

- [ ] **Step 1: Rebuild the picker logic**

`BuildQualityMenu()` (called from `PlayAsync` when adaptive): same Auto + per-height `RadioMenuFlyoutItem` structure as before (reuse the existing method shell). `SetPreferredHeight(int? height)` now: `preferredHeight = height;` then **reload at position**:

```csharp
private void ReloadAtCurrentQuality()
{
    if (hlsMaster is null) return;
    var position = Player.Position;
    var path = WriteManifestForHeight(preferredHeight);
    Player.Load(path, position, lastUserAgent!, lastAudioLanguage);
    UpdateQualityLabel();
}
```

Cache `lastUserAgent`/`lastAudioLanguage` in `PlayAsync` for this. `UpdateQualityLabel()`: manual → `activeQuality.Label`; Auto → `$"Auto · {activeQuality.Height}p"`. Non-adaptive stays the grey "360p" button. Delete the dead `OnPlaybackBitrateChanged`/`ApplyPreferredHeight`/`CurrentAutoLabel` remnants if Task 5 left any.

- [ ] **Step 2: Manual check**

A 4K video offers 2160p/1440p (VP9 heights are real now); switching keeps position (within a segment); Auto on a 1440p monitor shows "Auto · 1440p"; the choice sticks across videos in the session; a Shorts/fallback video shows the disabled "360p".

- [ ] **Step 3: Commit**

```bash
git add src/WinTube.App/Views/PlayerPage.xaml.cs
git commit -m "feat: quality picker drives mpv via single-variant manifests"
```

---

### Task 9: CI + README

**Files:**
- Modify: `.github/workflows/release.yml` — insert before the Publish step:

```yaml
      - name: Fetch libmpv
        run: pwsh tools/get-libmpv.ps1
```

- Modify: `README.md` — build-from-source section gains one line: run `tools/get-libmpv.ps1` once before building (and its 7-Zip prerequisite).

**Interfaces:** none.

- [ ] **Step 1: Edit both files** as above (also add ANGLE DLLs to the publish output implicitly via the csproj Content entries from Task 3 — verify `dotnet publish src/WinTube.App -c Release -p:Platform=x64 -r win-x64 --self-contained -p:WindowsAppSDKSelfContained=true -o publish-check` locally contains `libmpv-2.dll` and any ANGLE DLLs; delete `publish-check` after).

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/release.yml README.md
git commit -m "chore: ship libmpv in CI releases"
```

---

### Task 10: Cleanup + full E2E gate

**Files:**
- Delete: `src/WinTube.App/Views/SpikePage.xaml(.cs)`, its MainWindow nav item, the scratchpad spike
- Modify: whatever the sweep below finds

**Interfaces:** none — this is the stage's exit gate.

- [ ] **Step 1: Sweep**

`grep -rn "MediaPlayer\|AdaptiveMediaSource\|Windows.Media" src/WinTube.App/Views/PlayerPage.xaml.cs` → only preview-related files elsewhere may still reference Windows.Media (previews stay on MF — `PreviewCoordinator` and friends are untouched). Remove SpikePage + nav item. `FilterToAvc` in HlsVariantParser: now unused by the app — delete it and its test if nothing references it (check `grep -rn "FilterToAvc" src tests`).

- [ ] **Step 2: Build + tests + manual E2E per spec §7**

All Core tests green. Manual: a known 4K video at 2160p with audio · seek · SponsorBlock skips fire · progress resumes · fullscreen + comments coexist · quality switch keeps position · previews still hover-play · Shorts play · volume/speed/transport behave · app close mid-playback saves volume and progress.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore: remove libmpv spike scaffolding; stage 10 complete"
```

**CHECKPOINT: hand to the user for their own verification pass before any release tag.**
