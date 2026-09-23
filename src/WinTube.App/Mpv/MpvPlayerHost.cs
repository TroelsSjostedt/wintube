using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTube.App.Mpv;

/// Owns one libmpv instance end to end: the D3D11 device, the composition swap chain attached to
/// a child SwapChainPanel, the render-API/ANGLE pipeline, and the property/event plumbing. Built
/// from the proven recipe in SpikePage/task-0b (render API, not child-HWND).
///
/// Lifecycle: construct, add to the visual tree, call Load (may be called again for a new video
/// without recreating the pipeline), Dispose when the page is done with it. Load before the
/// control has laid out is deferred to Loaded, since the swap chain needs a real pixel size.
///
/// Threading: UI thread owns device/swap-chain creation, mpv create/init and the public surface.
/// A dedicated event thread drains mpv_wait_event and marshals everything user-visible through
/// the DispatcherQueue captured at construction. A dedicated render thread owns every EGL/GL call
/// and every use of the immediate D3D11 context. Dispose only flags and detaches on the UI thread;
/// the joins and mpv_terminate_destroy run on a teardown thread. No background thread ever
/// touches XAML directly; events raised after Dispose are dropped.
public sealed class MpvPlayerHost : Grid, IDisposable
{
    private const ulong PosUserdata = 1, DurUserdata = 2, PauseUserdata = 3, EofUserdata = 4;

    public event Action? Opened;
    public event Action<double>? PositionChanged;
    public event Action? EndReached;
    public event Action<string>? Errored;

    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }

    public double Volume
    {
        get => volume;
        set
        {
            volume = value;
            if (Live) MpvNative.SetPropertyDouble(mpv, "volume", value * 100);
        }
    }

    public double Speed
    {
        get => speed;
        set
        {
            speed = value;
            if (Live) MpvNative.SetPropertyDouble(mpv, "speed", value);
        }
    }

    // Dispose hands the handle to the teardown thread, so the UI must stop using it the moment
    // `disposed` is set, not when `mpv` is finally zeroed over there.
    private bool Live => !disposed && mpv != IntPtr.Zero;

    private readonly SwapChainPanel panel;
    private readonly DispatcherQueue dispatcher;
    private readonly AutoResetEvent wake = new(false);
    private Thread? eventThread;
    private Thread? renderThread;
    private volatile bool disposed;
    private bool loadedIntoTree;
    private PendingLoad? pendingLoad;
    private double volume = 1.0, speed = 1.0;
    private bool eofReached; // edge-detects eof-reached false->true; event thread only

    // Load->FileLoaded timing, logged on open so the stall-timer window can be tuned from real
    // data instead of a guess; reset on every DoLoad, read from the event thread only.
    private DateTimeOffset loadStartedAt;
    private string loadKind = "";

    // Latest requested pixel size / DPI scale; the render thread applies it.
    private readonly object sizeGate = new();
    private int wantW, wantH;
    private float wantScale;

    private IntPtr device, deviceContext, swapChain, mpv;

    // Render-thread-owned state.
    private Egl? egl;
    private IntPtr eglDeviceHandle, eglDisplay, eglConfig, eglContext, eglSurface, texture;
    private int curW, curH;
    private IntPtr renderCtx;

    // Delegates handed to native code must stay rooted for the host's lifetime.
    private readonly MpvNative.GetProcAddressFn getProcAddress;
    private readonly MpvNative.UpdateFn onUpdate;

    private readonly record struct PendingLoad(string UrlOrPath, double StartAtSeconds, string UserAgent, string? AudioLanguage);

    public MpvPlayerHost()
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        getProcAddress = (_, name) => egl!.GetProcAddress(name);
        onUpdate = _ => wake.Set();

        panel = new SwapChainPanel();
        Children.Add(panel);
        panel.SizeChanged += (_, _) => PushSize();
        panel.CompositionScaleChanged += (_, _) => PushSize();
        Loaded += OnLoaded;
    }

    public void Load(string urlOrPath, double startAtSeconds, string userAgent, string? audioLanguage)
    {
        if (disposed) return;
        var request = new PendingLoad(urlOrPath, startAtSeconds, userAgent, audioLanguage);
        if (!loadedIntoTree)
        {
            pendingLoad = request; // applied from OnLoaded once the panel has a real size
            return;
        }
        StartAndLoad(request);
    }

    public void Play() { if (Live) MpvNative.Command(mpv, "set", "pause", "no"); }
    public void Pause() { if (Live) MpvNative.Command(mpv, "set", "pause", "yes"); }
    public void TogglePause() { if (Live) MpvNative.Command(mpv, "cycle", "pause"); }

    public void SeekTo(double seconds)
    {
        if (!Live) return;
        MpvNative.Command(mpv, "seek", seconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
    }

    // MARK: startup

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        loadedIntoTree = true;
        if (pendingLoad is not { } request || mpv != IntPtr.Zero) return;
        pendingLoad = null;
        StartAndLoad(request);
    }

    /// Brings the pipeline up if it isn't already, then loads — without letting a native failure
    /// (missing ANGLE DLL, device/mpv init failure, a rejected loadfile) escape a XAML event
    /// handler and kill the process. A failure while bringing the pipeline up is fully torn down
    /// (TeardownFailedStart) so the next Load() gets a clean EnsureStarted rather than working off
    /// a half-built pipeline (mpv non-zero but never initialized, etc); a failure from an
    /// already-healthy pipeline (e.g. one bad loadfile) just surfaces Errored and leaves the
    /// pipeline running for a retry.
    private void StartAndLoad(PendingLoad request)
    {
        if (mpv == IntPtr.Zero)
        {
            try
            {
                EnsureStarted();
            }
            catch (Exception ex)
            {
                TeardownFailedStart();
                RaiseErrored($"start: {ex.Message}");
                return;
            }
        }
        try
        {
            DoLoad(request);
        }
        catch (Exception ex)
        {
            RaiseErrored($"load: {ex.Message}");
        }
    }

    private void RaiseErrored(string message)
    {
        if (disposed) return;
        Errored?.Invoke(message);
    }

    /// Cleans up whatever EnsureStarted managed to create before it threw, back to "nothing built
    /// yet". EnsureStarted only starts the event/render threads after CreateMpv succeeds, so a
    /// failure here never has a thread to join first.
    private void TeardownFailedStart()
    {
        DetachSwapChain();
        TeardownNativeState();
    }

    private void EnsureStarted()
    {
        var (w, h, scale) = PixelSize();
        (wantW, wantH, wantScale) = (w, h, scale);
        curW = w; curH = h;
        CreateDeviceAndSwapChain(w, h, scale);
        CreateMpv();
        eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-event" };
        eventThread.Start();
        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv-render" };
        renderThread.Start();
    }

    /// user-agent/alang are set fresh before every load (not just the first), per-file, so a
    /// second Load() on a reused host picks up a different video's values.
    private void DoLoad(PendingLoad request)
    {
        MpvNative.SetOption(mpv, "user-agent", request.UserAgent);
        MpvNative.SetOption(mpv, "alang", request.AudioLanguage ?? "");
        loadStartedAt = DateTimeOffset.UtcNow;
        loadKind = ClassifyKind(request.UrlOrPath);
        // Per-load start= avoids seeking after open; pause=no clears a prior load's paused state.
        var options = FormattableString.Invariant($"start={request.StartAtSeconds:0.###},pause=no");
        Com.Check(MpvNative.Command(mpv, "loadfile", request.UrlOrPath, "replace", "0", options), "loadfile");
    }

    /// Purely for the open-duration log line below: PlayerPage hands either a local manifest
    /// path (adaptive) or a raw http(s) URL (muxed fallback) — distinguishing them is what makes
    /// the logged durations comparable across runs.
    private static string ClassifyKind(string urlOrPath) =>
        urlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        urlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? "direct" : "manifest";

    private void CreateMpv()
    {
        mpv = MpvNative.mpv_create();
        if (mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");
        MpvNative.SetOption(mpv, "vo", "libmpv"); // required for the render API
        // Both load targets (HLS manifest, muxed MP4) are lavf's. Without this, a manifest lavf
        // rejects (DRM, 403) falls through to mpv's other demuxers: the playlist demuxer turns it
        // into a playlist of raw segment URLs, and a failed rewind during one of those probes
        // closes a never-opened disc demuxer on a null priv — libmpv AV on the "opener" thread.
        MpvNative.SetOption(mpv, "demuxer", "lavf");
        MpvNative.SetOption(mpv, "hwdec", "auto-safe");
        MpvNative.SetOption(mpv, "keep-open", "yes"); // EndReached fires but the last frame stays
        MpvNative.SetOption(mpv, "video-timing-offset", "0");
        // A genuinely dead stream now fails through Errored well inside PlayerPage's stall
        // watchdog, instead of the watchdog being the only thing that ever notices.
        MpvNative.SetOption(mpv, "network-timeout", "20");
        MpvNative.SetOption(mpv, "terminal", "no");
        Com.Check(MpvNative.mpv_initialize(mpv), "mpv_initialize");
        MpvNative.SetPropertyDouble(mpv, "volume", volume * 100);
        MpvNative.SetPropertyDouble(mpv, "speed", speed);
        MpvNative.mpv_observe_property(mpv, PosUserdata, Utf8("time-pos"), MpvNative.FormatDouble);
        MpvNative.mpv_observe_property(mpv, DurUserdata, Utf8("duration"), MpvNative.FormatDouble);
        MpvNative.mpv_observe_property(mpv, PauseUserdata, Utf8("pause"), MpvNative.FormatFlag);
        // keep-open=yes means mpv sets stop_play=KEEP_PLAYING at end of file and never sends an
        // end-file/eof event; eof-reached is the only signal left, so EndReached is driven off its
        // false->true edge (HandlePropertyChange) instead of the end-file event.
        MpvNative.mpv_observe_property(mpv, EofUserdata, Utf8("eof-reached"), MpvNative.FormatFlag);
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    // MARK: resize

    /// Pixel size = ActualSize x XamlRoot.RasterizationScale, per the DPI-scaling decision for
    /// this task (the spike used the SwapChainPanel's own CompositionScale instead).
    private (int W, int H, float Scale) PixelSize()
    {
        var scale = (float)(XamlRoot?.RasterizationScale ?? 1.0);
        var w = Math.Max(1, (int)Math.Round(panel.ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Round(panel.ActualHeight * scale));
        return (w, h, scale);
    }

    private void PushSize()
    {
        var (w, h, scale) = PixelSize();
        lock (sizeGate) (wantW, wantH, wantScale) = (w, h, scale);
        wake.Set();
    }

    // MARK: UI thread setup

    private void CreateDeviceAndSwapChain(int w, int h, float scale)
    {
        Com.Check(D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */,
            IntPtr.Zero, 0, 7, out device, out _, out deviceContext), "D3D11CreateDevice");

        // The device is shared with ANGLE; make sure cross-thread access is safe.
        var mt = Com.QueryInterface(device, Com.IID_ID3D10Multithread);
        Com.Slot<Com.SetMultithreadProtectedFn>(mt, 5)(mt, 1);
        Com.Release(mt);

        var dxgiDevice = Com.QueryInterface(device, Com.IID_IDXGIDevice);
        Com.Check(Com.Slot<Com.GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out var adapter), "GetAdapter");
        var factoryIid = Com.IID_IDXGIFactory2;
        Com.Check(Com.Slot<Com.GetParentFn>(adapter, 6)(adapter, ref factoryIid, out var factory), "GetParent");

        var desc = new Com.DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)w, Height = (uint)h,
            Format = Com.DXGI_FORMAT_R8G8B8A8_UNORM,
            SampleCount = 1,
            BufferUsage = 0x20, // DXGI_USAGE_RENDER_TARGET_OUTPUT
            BufferCount = 2,
            Scaling = 0,        // DXGI_SCALING_STRETCH (only mode composition allows)
            SwapEffect = 3,     // DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL
            AlphaMode = 3,      // DXGI_ALPHA_MODE_IGNORE
        };
        Com.Check(Com.Slot<Com.CreateSwapChainForCompositionFn>(factory, 24)(factory, device, ref desc, IntPtr.Zero, out swapChain),
            "CreateSwapChainForComposition");
        Com.Release(factory);
        Com.Release(adapter);
        Com.Release(dxgiDevice);
        ApplyInverseScale(scale);

        var panelUnknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var panelNative = Com.QueryInterface(panelUnknown, Com.IID_ISwapChainPanelNative);
        Com.Check(Com.Slot<Com.SetSwapChainFn>(panelNative, 3)(panelNative, swapChain), "SetSwapChain");
        Com.Release(panelNative);
    }

    /// The panel shows swap-chain pixels 1:1 in DIPs; buffers are sized in physical pixels, so
    /// scale them back down by the DPI scale or they overflow the panel.
    private void ApplyInverseScale(float scale)
    {
        var sc2 = Com.QueryInterface(swapChain, Com.IID_IDXGISwapChain2);
        var m = new Com.DXGI_MATRIX_3X2_F { _11 = 1f / scale, _22 = 1f / scale };
        Com.Check(Com.Slot<Com.SetMatrixTransformFn>(sc2, 34)(sc2, ref m), "SetMatrixTransform");
        Com.Release(sc2);
    }

    // MARK: event thread

    private void EventLoop()
    {
        try
        {
            while (!disposed)
            {
                var evPtr = MpvNative.mpv_wait_event(mpv, 1.0);
                var ev = Marshal.PtrToStructure<MpvNative.Event>(evPtr);
                switch (ev.Id)
                {
                    case MpvNative.EventId.None: continue;
                    case MpvNative.EventId.Shutdown: return;
                    case MpvNative.EventId.FileLoaded:
                        Log(FormattableString.Invariant($"open: {(DateTimeOffset.UtcNow - loadStartedAt).TotalSeconds:F1}s {loadKind}"));
                        Post(() => Opened?.Invoke());
                        break;
                    case MpvNative.EventId.EndFile: HandleEndFile(ev); break;
                    case MpvNative.EventId.PropertyChange: HandlePropertyChange(ev); break;
                }
            }
        }
        catch (Exception ex)
        {
            Post(() => Errored?.Invoke($"event loop: {ex.Message}"));
        }
    }

    /// eof-reached (below) owns EndReached; end-file's eof reason never fires with keep-open=yes.
    /// This stays for load failures, which still end the file via this event.
    private void HandleEndFile(MpvNative.Event ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var end = Marshal.PtrToStructure<MpvNative.EventEndFile>(ev.Data);
        if (end.Reason == MpvNative.EndFileError)
        {
            var message = MpvNative.ErrorString(end.Error);
            Post(() => Errored?.Invoke(message));
        }
    }

    private void HandlePropertyChange(MpvNative.Event ev)
    {
        if (ev.Data == IntPtr.Zero) return;
        var prop = Marshal.PtrToStructure<MpvNative.EventProperty>(ev.Data);
        if (prop.Data == IntPtr.Zero) return;
        switch (ev.Userdata)
        {
            case PosUserdata when prop.Format == MpvNative.FormatDouble:
                var pos = Marshal.PtrToStructure<double>(prop.Data);
                Post(() => { Position = pos; PositionChanged?.Invoke(pos); });
                break;
            case DurUserdata when prop.Format == MpvNative.FormatDouble:
                var dur = Marshal.PtrToStructure<double>(prop.Data);
                Post(() => Duration = dur);
                break;
            case PauseUserdata when prop.Format == MpvNative.FormatFlag:
                var paused = Marshal.ReadInt32(prop.Data) != 0;
                Post(() => IsPaused = paused);
                break;
            case EofUserdata when prop.Format == MpvNative.FormatFlag:
                var eof = Marshal.ReadInt32(prop.Data) != 0;
                if (eof && !eofReached) { eofReached = true; Post(() => EndReached?.Invoke()); }
                else if (!eof) eofReached = false; // reset for the next Load's edge
                break;
        }
    }

    private void Post(Action action) => dispatcher.TryEnqueue(() =>
    {
        if (disposed) return;
        action();
    });

    // MARK: render thread — owns every EGL/GL call and every use of the immediate context.

    private void RenderLoop()
    {
        try
        {
            InitEgl();
            CreateTarget(curW, curH);

            var init = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(init, 0, Marshal.GetFunctionPointerForDelegate(getProcAddress));
            Marshal.WriteIntPtr(init, 8, IntPtr.Zero);
            var apiType = Marshal.StringToHGlobalAnsi("opengl");
            var createParams = MpvNative.Params((MpvNative.ParamApiType, apiType), (MpvNative.ParamOpenGlInitParams, init));
            Com.Check(MpvNative.mpv_render_context_create(out renderCtx, mpv, createParams), "mpv_render_context_create");
            Marshal.FreeHGlobal(createParams);
            Marshal.FreeHGlobal(apiType);
            Marshal.FreeHGlobal(init);
            MpvNative.mpv_render_context_set_update_callback(renderCtx, Marshal.GetFunctionPointerForDelegate(onUpdate), IntPtr.Zero);

            var fbo = Marshal.AllocHGlobal(16);
            var flipY = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(flipY, 0); // upright: an ANGLE pbuffer on a D3D texture, not the back buffer
            try
            {
                while (!disposed)
                {
                    wake.WaitOne(500);
                    if (disposed) break;

                    int w, h; float scale;
                    lock (sizeGate) (w, h, scale) = (wantW, wantH, wantScale);
                    var resized = w != curW || h != curH;
                    if (resized) Resize(w, h, scale);

                    var flags = MpvNative.mpv_render_context_update(renderCtx);
                    if ((flags & 1) == 0 && !resized) continue; // no MPV_RENDER_UPDATE_FRAME

                    Marshal.WriteInt32(fbo, 0, 0);
                    Marshal.WriteInt32(fbo, 4, curW);
                    Marshal.WriteInt32(fbo, 8, curH);
                    Marshal.WriteInt32(fbo, 12, 0);
                    var renderParams = MpvNative.Params((MpvNative.ParamOpenGlFbo, fbo), (MpvNative.ParamFlipY, flipY));
                    MpvNative.mpv_render_context_render(renderCtx, renderParams);
                    Marshal.FreeHGlobal(renderParams);
                    egl!.Flush();
                    Present();
                    MpvNative.mpv_render_context_report_swap(renderCtx);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(fbo);
                Marshal.FreeHGlobal(flipY);
            }
        }
        catch (Exception ex)
        {
            Post(() => Errored?.Invoke($"render: {ex.Message}"));
        }
        finally
        {
            if (renderCtx != IntPtr.Zero) MpvNative.mpv_render_context_free(renderCtx);
            renderCtx = IntPtr.Zero;
            DestroyTarget();
            if (egl is not null && eglDisplay != IntPtr.Zero)
            {
                egl.MakeCurrent(eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (eglContext != IntPtr.Zero) egl.DestroyContext(eglDisplay, eglContext);
                egl.Terminate(eglDisplay);
            }
            // ANGLE's Device11 holds its own AddRef on the D3D11 device for as long as the
            // EGLDevice lives; without this release, Dispose's Com.Release(device) never reaches
            // 0 and the device (plus everything it holds) leaks every construct/Load/Dispose cycle.
            if (egl is not null && eglDeviceHandle != IntPtr.Zero)
            {
                egl.ReleaseDeviceANGLE(eglDeviceHandle);
                eglDeviceHandle = IntPtr.Zero;
            }
        }
    }

    private void InitEgl()
    {
        egl = Egl.Load();
        eglDeviceHandle = egl.CreateDeviceANGLE(0x33A1 /* EGL_D3D11_DEVICE_ANGLE */, device, IntPtr.Zero);
        if (eglDeviceHandle == IntPtr.Zero) throw new InvalidOperationException($"eglCreateDeviceANGLE 0x{egl.GetError():x}");
        eglDisplay = egl.GetPlatformDisplayEXT(0x313F /* EGL_PLATFORM_DEVICE_EXT */, eglDeviceHandle, [Egl.NONE]);
        if (eglDisplay == IntPtr.Zero) throw new InvalidOperationException($"eglGetPlatformDisplayEXT 0x{egl.GetError():x}");
        if (egl.Initialize(eglDisplay, out _, out _) == 0)
            throw new InvalidOperationException($"eglInitialize 0x{egl.GetError():x}");

        int[] configAttribs =
        [
            0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, // RED/GREEN/BLUE/ALPHA
            0x3033, 0x0001,                             // SURFACE_TYPE = PBUFFER_BIT
            0x3040, 0x0040,                             // RENDERABLE_TYPE = OPENGL_ES3_BIT
            Egl.NONE,
        ];
        var configs = new IntPtr[1];
        if (egl.ChooseConfig(eglDisplay, configAttribs, configs, 1, out var count) == 0 || count < 1)
            throw new InvalidOperationException($"eglChooseConfig 0x{egl.GetError():x}");
        eglConfig = configs[0];
        eglContext = egl.CreateContext(eglDisplay, eglConfig, IntPtr.Zero, [0x3098 /* CLIENT_VERSION */, 3, Egl.NONE]);
        if (eglContext == IntPtr.Zero) throw new InvalidOperationException($"eglCreateContext 0x{egl.GetError():x}");
    }

    /// The EGL pbuffer wraps our own D3D11 texture (not the swap chain's back buffer), so ANGLE
    /// never holds a back-buffer reference and ResizeBuffers stays legal.
    private void CreateTarget(int w, int h)
    {
        var desc = new Com.D3D11_TEXTURE2D_DESC
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Com.DXGI_FORMAT_R8G8B8A8_UNORM, SampleCount = 1,
            BindFlags = 0x20 | 0x8, // RENDER_TARGET | SHADER_RESOURCE
        };
        Com.Check(Com.Slot<Com.CreateTexture2DFn>(device, 5)(device, ref desc, IntPtr.Zero, out texture), "CreateTexture2D");
        eglSurface = egl!.CreatePbufferFromClientBuffer(eglDisplay, 0x33A3 /* EGL_D3D_TEXTURE_ANGLE */, texture, eglConfig,
            [0x3057, w, 0x3056, h, Egl.NONE]);
        if (eglSurface == IntPtr.Zero) throw new InvalidOperationException($"eglCreatePbufferFromClientBuffer 0x{egl.GetError():x}");
        if (egl.MakeCurrent(eglDisplay, eglSurface, eglSurface, eglContext) == 0)
            throw new InvalidOperationException($"eglMakeCurrent 0x{egl.GetError():x}");
    }

    private void DestroyTarget()
    {
        if (egl is not null && eglSurface != IntPtr.Zero)
        {
            egl.MakeCurrent(eglDisplay, IntPtr.Zero, IntPtr.Zero, eglContext);
            egl.DestroySurface(eglDisplay, eglSurface);
        }
        eglSurface = IntPtr.Zero;
        if (texture != IntPtr.Zero) Com.Release(texture);
        texture = IntPtr.Zero;
    }

    private void Resize(int w, int h, float scale)
    {
        DestroyTarget();
        Com.Check(Com.Slot<Com.ResizeBuffersFn>(swapChain, 13)(swapChain, 0, (uint)w, (uint)h, 0, 0), "ResizeBuffers");
        ApplyInverseScale(scale);
        CreateTarget(w, h);
        (curW, curH) = (w, h);
    }

    /// Present(0,0), not Present(1,0): mpv_render_context_render already blocks until the frame's
    /// target time, so a vsync wait on top of it double-waits (measured: 39 vs 4 VO drops over 54s
    /// of 1080p60). Composition swap chains never tear, so interval 0 is safe.
    private void Present()
    {
        var texIid = Com.IID_ID3D11Texture2D;
        Com.Check(Com.Slot<Com.GetBufferFn>(swapChain, 9)(swapChain, 0, ref texIid, out var back), "GetBuffer");
        Com.Slot<Com.CopyResourceFn>(deviceContext, 47)(deviceContext, back, texture);
        Com.Release(back);
        Com.Slot<Com.PresentFn>(swapChain, 8)(swapChain, 0, 0);
    }

    // MARK: dispose

    /// Returns at once. The thread joins (up to 3 s each) plus mpv_terminate_destroy, which waits
    /// on mpv's own demuxer/network shutdown with no bound, used to run here on the UI thread —
    /// anything past 5 s there is a Windows "not responding" hang. They now run on a teardown
    /// thread; nothing it touches is shared with a newer host.
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Loaded -= OnLoaded;

        if (eventThread is null && renderThread is null) return; // never started, or a failed start already tore down

        DetachSwapChain();
        new Thread(TeardownThreads) { IsBackground = true, Name = "mpv-teardown" }.Start();
    }

    private void TeardownThreads()
    {
        try
        {
            if (mpv != IntPtr.Zero) MpvNative.mpv_wakeup(mpv);
            var eventJoined = eventThread is null || eventThread.Join(3000);

            wake.Set();
            var renderJoined = renderThread is null || renderThread.Join(3000); // frees the render context on its own thread, per the recipe

            if (!eventJoined || !renderJoined)
            {
                // A wedged thread may still be inside mpv_wait_event / Present / mpv_render_context_render
                // — still touching the mpv handle, the immediate D3D11 context, or the swap chain.
                // Destroying/releasing any of them now would race that thread and crash the process.
                // Leak instead: a wedged native thread was never coming back regardless, and a leak beats
                // a use-after-free crash.
                Log($"dispose: thread join timed out (event joined={eventJoined}, render joined={renderJoined}) — leaking native handles to avoid a use-after-free");
                return;
            }

            TeardownNativeState();
        }
        catch (Exception ex)
        {
            Log($"dispose: teardown failed: {ex.Message}"); // an escaping exception here would end the process
        }
    }

    /// UI thread only. Detach, or the panel keeps its own reference and shows the stale last frame.
    /// Presenting into a swap chain no panel shows is legal, so the render thread may still be
    /// mid-frame when this runs.
    private void DetachSwapChain()
    {
        if (swapChain == IntPtr.Zero) return;
        var panelNative = Com.QueryInterface(((WinRT.IWinRTObject)panel).NativeObject.ThisPtr, Com.IID_ISwapChainPanelNative);
        Com.Slot<Com.SetSwapChainFn>(panelNative, 3)(panelNative, IntPtr.Zero);
        Com.Release(panelNative);
    }

    /// mpv_terminate_destroy, release the D3D11 objects. Only safe to call once nothing else (in
    /// particular the render thread) can still be touching them — i.e. after both threads have been
    /// confirmed joined, or before either was ever started. Touches no XAML, so any thread may run it.
    private void TeardownNativeState()
    {
        if (mpv != IntPtr.Zero) MpvNative.mpv_terminate_destroy(mpv);
        mpv = IntPtr.Zero;

        if (swapChain != IntPtr.Zero) Com.Release(swapChain);
        if (deviceContext != IntPtr.Zero) Com.Release(deviceContext);
        if (device != IntPtr.Zero)
        {
            var refCount = Com.Release(device);
            Log($"device release refcount={refCount}");
        }
        swapChain = deviceContext = device = IntPtr.Zero;
    }

    // MARK: logging — best-effort, append-only (WatchProgressSync's own pattern): a leaked-on-
    // purpose pipeline or a half-built one that got torn down should leave a trail, not vanish.
    private static void Log(string message) =>
        WinTube.Core.Sync.WatchProgressSync.LogTo(WinTube.App.Session.DataDirectory, $"MpvPlayerHost: {message}");

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
}

/// Raw COM vtable calls — enough of DXGI/D3D11/ISwapChainPanelNative without pulling in a D3D
/// wrapper package. Slot numbers are vtable indices including IUnknown's 3.
internal static class Com
{
    public static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid IID_IDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid IID_IDXGISwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");
    public static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid IID_ID3D10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    // WinUI 3 flavour (microsoft.ui.xaml.media.dxinterop.h), not the UWP one.
    public static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    public const int DXGI_FORMAT_R8G8B8A8_UNORM = 28;

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_SWAP_CHAIN_DESC1
    {
        public uint Width, Height;
        public int Format;
        public int Stereo;
        public uint SampleCount, SampleQuality;
        public uint BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_TEXTURE2D_DESC
    {
        public uint Width, Height, MipLevels, ArraySize;
        public int Format;
        public uint SampleCount, SampleQuality;
        public int Usage;
        public uint BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DXGI_MATRIX_3X2_F
    {
        public float _11, _12, _21, _22, _31, _32;
    }

    public delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr ppv);
    public delegate uint ReleaseFn(IntPtr self);
    public delegate int SetSwapChainFn(IntPtr self, IntPtr swapChain);
    public delegate int SetMultithreadProtectedFn(IntPtr self, int protect);
    public delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
    public delegate int GetParentFn(IntPtr self, ref Guid iid, out IntPtr parent);
    public delegate int CreateSwapChainForCompositionFn(IntPtr self, IntPtr device, ref DXGI_SWAP_CHAIN_DESC1 desc,
        IntPtr restrictToOutput, out IntPtr swapChain);
    public delegate int PresentFn(IntPtr self, uint syncInterval, uint flags);
    public delegate int GetBufferFn(IntPtr self, uint buffer, ref Guid iid, out IntPtr surface);
    public delegate int ResizeBuffersFn(IntPtr self, uint count, uint w, uint h, int format, uint flags);
    public delegate int SetMatrixTransformFn(IntPtr self, ref DXGI_MATRIX_3X2_F matrix);
    public delegate int CreateTexture2DFn(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture);
    public delegate void CopyResourceFn(IntPtr self, IntPtr dst, IntPtr src);

    public static T Slot<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    public static IntPtr QueryInterface(IntPtr obj, Guid iid)
    {
        Check(Slot<QueryInterfaceFn>(obj, 0)(obj, ref iid, out var result), $"QueryInterface {iid}");
        return result;
    }

    public static uint Release(IntPtr obj) => Slot<ReleaseFn>(obj, 2)(obj);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:x8}");
    }
}

/// ANGLE loaded dynamically: Avalonia.Angle.Windows.Natives' single-DLL build (av_libglesv2.dll)
/// exports the EGL entry points as EGL_*; GL entry points come from EGL_GetProcAddress. The
/// PackageReference in the csproj is what puts the DLL beside the exe (Debug and publish alike).
internal sealed class Egl
{
    public const int NONE = 0x3038;
    private const string DllName = "av_libglesv2.dll";

    private readonly IntPtr lib;
    private readonly GetProcAddressFn getProcAddress;
    public readonly CreateDeviceANGLEFn CreateDeviceANGLE;
    public readonly ReleaseDeviceANGLEFn ReleaseDeviceANGLE;
    public readonly GetPlatformDisplayEXTFn GetPlatformDisplayEXT;
    public readonly InitializeFn Initialize;
    public readonly ChooseConfigFn ChooseConfig;
    public readonly CreateContextFn CreateContext;
    public readonly CreatePbufferFromClientBufferFn CreatePbufferFromClientBuffer;
    public readonly MakeCurrentFn MakeCurrent;
    public readonly DestroySurfaceFn DestroySurface;
    public readonly DestroyContextFn DestroyContext;
    public readonly TerminateFn Terminate;
    public readonly GetErrorFn GetError;
    private readonly GlFlushFn glFlush;

    public delegate IntPtr GetProcAddressFn([MarshalAs(UnmanagedType.LPStr)] string name);
    public delegate IntPtr CreateDeviceANGLEFn(int deviceType, IntPtr nativeDevice, IntPtr attribs);
    public delegate int ReleaseDeviceANGLEFn(IntPtr device);
    public delegate IntPtr GetPlatformDisplayEXTFn(int platform, IntPtr nativeDisplay, int[] attribs);
    public delegate int InitializeFn(IntPtr display, out int major, out int minor);
    public delegate int ChooseConfigFn(IntPtr display, int[] attribs, [Out] IntPtr[] configs, int size, out int count);
    public delegate IntPtr CreateContextFn(IntPtr display, IntPtr config, IntPtr share, int[] attribs);
    public delegate IntPtr CreatePbufferFromClientBufferFn(IntPtr display, int bufType, IntPtr buffer, IntPtr config, int[] attribs);
    public delegate int MakeCurrentFn(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    public delegate int DestroySurfaceFn(IntPtr display, IntPtr surface);
    public delegate int DestroyContextFn(IntPtr display, IntPtr context);
    public delegate int TerminateFn(IntPtr display);
    public delegate int GetErrorFn();
    public delegate void GlFlushFn();

    private Egl(IntPtr lib)
    {
        this.lib = lib;
        getProcAddress = Export<GetProcAddressFn>("EGL_GetProcAddress");
        CreateDeviceANGLE = Export<CreateDeviceANGLEFn>("EGL_CreateDeviceANGLE");
        ReleaseDeviceANGLE = Export<ReleaseDeviceANGLEFn>("EGL_ReleaseDeviceANGLE");
        GetPlatformDisplayEXT = Export<GetPlatformDisplayEXTFn>("EGL_GetPlatformDisplayEXT");
        Initialize = Export<InitializeFn>("EGL_Initialize");
        ChooseConfig = Export<ChooseConfigFn>("EGL_ChooseConfig");
        CreateContext = Export<CreateContextFn>("EGL_CreateContext");
        CreatePbufferFromClientBuffer = Export<CreatePbufferFromClientBufferFn>("EGL_CreatePbufferFromClientBuffer");
        MakeCurrent = Export<MakeCurrentFn>("EGL_MakeCurrent");
        DestroySurface = Export<DestroySurfaceFn>("EGL_DestroySurface");
        DestroyContext = Export<DestroyContextFn>("EGL_DestroyContext");
        Terminate = Export<TerminateFn>("EGL_Terminate");
        GetError = Export<GetErrorFn>("EGL_GetError");
        glFlush = Marshal.GetDelegateForFunctionPointer<GlFlushFn>(GetProcAddress("glFlush"));
    }

    public static Egl Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DllName);
        if (!File.Exists(path)) throw new FileNotFoundException($"{DllName} not found beside the exe; check the Avalonia.Angle.Windows.Natives package reference", path);
        return new Egl(NativeLibrary.Load(path));
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    public IntPtr GetProcAddress(string name) => getProcAddress(name);
    public void Flush() => glFlush();
}
