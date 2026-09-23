using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinTube.App.Views;

/// TEMPORARY (libmpv stage, Task 0b; deleted in Task 10). Embedding-gate spike and the
/// reference implementation for Task 3: libmpv's OpenGL render API drawn through ANGLE
/// (EGL on D3D11) into a composition swap chain owned by a XAML SwapChainPanel.
///
/// Recipe, in order:
///  UI thread:     D3D11CreateDevice (BGRA) → IDXGIDevice.GetAdapter → GetParent(IDXGIFactory2)
///                 → CreateSwapChainForComposition (R8G8B8A8, flip-sequential, 2 buffers)
///                 → ISwapChainPanelNative.SetSwapChain; mpv_create + options (vo=libmpv) +
///                 mpv_initialize; start the render thread.
///  Render thread: eglCreateDeviceANGLE(D3D11 device) → eglGetPlatformDisplayEXT(DEVICE_EXT)
///                 → eglInitialize → config/context (ES3) → an offscreen D3D11 texture wrapped as
///                 an EGL pbuffer (EGL_D3D_TEXTURE_ANGLE) → eglMakeCurrent →
///                 mpv_render_context_create(opengl, get_proc_address=eglGetProcAddress) →
///                 loop: wait for mpv's update callback → mpv_render_context_render(fbo 0) →
///                 glFlush → CopyResource(back buffer ← texture) → Present(1,0) → report_swap.
///  Resize:        SizeChanged/CompositionScaleChanged record the pixel size; the render thread
///                 recreates texture + pbuffer, ResizeBuffers, and sets the inverse-DPI matrix.
public sealed partial class SpikePage : Page
{
    private const string VideoId = "LXb3EKWsInQ";

    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer statusTimer;
    private MpvRenderSession? session;

    public SpikePage()
    {
        InitializeComponent();
        statusTimer = dispatcher.CreateTimer();
        statusTimer.Interval = TimeSpan.FromMilliseconds(500);
        statusTimer.Tick += (_, _) => Status.Text = session?.Describe() ?? "idle";
        Unloaded += (_, _) => StopSession();
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (session is not null) return;
        try
        {
            Status.Text = "resolving…";
            var stream = await App.Session.Streams.ResolveAsync(VideoId, null);
            SpikeLog.Write($"resolved client={stream.Client} url={stream.Url}");
            var (w, h) = PixelSize();
            session = new MpvRenderSession(Panel, w, h, Panel.CompositionScaleX, Panel.CompositionScaleY);
            session.Start(stream.Url.ToString(), stream.UserAgent);
            statusTimer.Start();
        }
        catch (Exception ex)
        {
            SpikeLog.Write($"play failed: {ex}");
            Status.Text = $"failed: {ex.Message}";
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopSession();

    private void StopSession()
    {
        statusTimer.Stop();
        session?.Dispose();
        session = null;
        Status.Text = "stopped";
    }

    private (int W, int H) PixelSize() => (
        Math.Max(1, (int)Math.Round(Panel.ActualWidth * Panel.CompositionScaleX)),
        Math.Max(1, (int)Math.Round(Panel.ActualHeight * Panel.CompositionScaleY)));

    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs e) => PushSize();

    private void OnPanelScaleChanged(SwapChainPanel sender, object args) => PushSize();

    private void PushSize()
    {
        var (w, h) = PixelSize();
        session?.RequestResize(w, h, Panel.CompositionScaleX, Panel.CompositionScaleY);
    }
}

internal static class SpikeLog
{
    private static readonly object Gate = new();
    public static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "spike.log");

    public static void Write(string line)
    {
        lock (Gate) File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {line}\n");
    }
}

/// Owns one mpv instance, the D3D11 device + composition swap chain, and the render thread.
internal sealed class MpvRenderSession : IDisposable
{
    private readonly SwapChainPanel panel;
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile bool quit;

    // Latest requested pixel size / DPI scale; the render thread applies it.
    private readonly object sizeGate = new();
    private int wantW, wantH;
    private float wantScaleX, wantScaleY;

    private IntPtr device, context, swapChain, mpv;
    private string url = "", userAgent = "";

    // Render-thread state
    private Egl? egl;
    private IntPtr eglDisplay, eglConfig, eglContext, eglSurface, texture;
    private int curW, curH;
    private IntPtr renderCtx;
    private long frames;
    private string lastError = "";

    /// Present(0): mpv_render_context_render already blocks until the frame's target time, so
    /// also waiting for vblank double-waits and makes mpv drop frames (measured: 39 vs 4 VO
    /// drops over 54s of 1080p60). WINTUBE_SPIKE_PRESENT=1 restores the vsync wait for comparison.
    private static readonly uint PresentInterval =
        Environment.GetEnvironmentVariable("WINTUBE_SPIKE_PRESENT") == "1" ? 1u : 0u;

    // Delegates handed to native code must stay rooted for the session's lifetime.
    private readonly Mpv.GetProcAddressFn getProcAddress;
    private readonly Mpv.UpdateFn onUpdate;

    public MpvRenderSession(SwapChainPanel panel, int w, int h, float scaleX, float scaleY)
    {
        this.panel = panel;
        (wantW, wantH, wantScaleX, wantScaleY) = (w, h, scaleX, scaleY);
        getProcAddress = (_, name) => egl!.GetProcAddress(name);
        onUpdate = _ => wake.Set();
        thread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv-render" };
    }

    public void Start(string url, string userAgent)
    {
        (this.url, this.userAgent) = (url, userAgent);
        CreateDeviceAndSwapChain();   // UI thread: SetSwapChain must be called here
        CreateMpv();
        thread.Start();
    }

    public void RequestResize(int w, int h, float scaleX, float scaleY)
    {
        lock (sizeGate) (wantW, wantH, wantScaleX, wantScaleY) = (w, h, scaleX, scaleY);
        wake.Set();
    }

    public string Describe()
    {
        var pos = mpv == IntPtr.Zero ? null : Mpv.GetDouble(mpv, "time-pos");
        var hw = mpv == IntPtr.Zero ? null : Mpv.GetString(mpv, "hwdec-current");
        var dropped = mpv == IntPtr.Zero ? null : Mpv.GetString(mpv, "frame-drop-count");
        var decDropped = mpv == IntPtr.Zero ? null : Mpv.GetString(mpv, "decoder-frame-drop-count");
        var delayed = mpv == IntPtr.Zero ? null : Mpv.GetString(mpv, "vo-delayed-frame-count");
        return $"render-API frames={Interlocked.Read(ref frames)} size={curW}x{curH} " +
               $"time-pos={(pos is null ? "-" : pos.Value.ToString("F1"))} hwdec={hw ?? "-"} " +
               $"drop(vo)={dropped ?? "-"} drop(dec)={decDropped ?? "-"} delayed={delayed ?? "-"} present={PresentInterval} {lastError}";
    }

    // MARK: UI thread setup

    private void CreateDeviceAndSwapChain()
    {
        Com.Check(D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA_SUPPORT */,
            IntPtr.Zero, 0, 7, out device, out var level, out context), "D3D11CreateDevice");
        SpikeLog.Write($"d3d11 device feature level 0x{level:x}");

        // mpv's decoder or ANGLE may touch the device off the render thread; be safe.
        var mt = Com.QueryInterface(device, Com.IID_ID3D10Multithread);
        Com.Slot<Com.SetMultithreadProtectedFn>(mt, 5)(mt, 1);
        Com.Release(mt);

        var dxgiDevice = Com.QueryInterface(device, Com.IID_IDXGIDevice);
        Com.Check(Com.Slot<Com.GetAdapterFn>(dxgiDevice, 7)(dxgiDevice, out var adapter), "GetAdapter");
        var factoryIid = Com.IID_IDXGIFactory2;
        Com.Check(Com.Slot<Com.GetParentFn>(adapter, 6)(adapter, ref factoryIid, out var factory), "GetParent");

        var desc = new Com.DXGI_SWAP_CHAIN_DESC1
        {
            Width = (uint)wantW, Height = (uint)wantH,
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
        curW = wantW; curH = wantH;
        ApplyInverseScale(wantScaleX, wantScaleY);

        var panelUnknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr;
        var panelNative = Com.QueryInterface(panelUnknown, Com.IID_ISwapChainPanelNative);
        Com.Check(Com.Slot<Com.SetSwapChainFn>(panelNative, 3)(panelNative, swapChain), "SetSwapChain");
        Com.Release(panelNative);
        SpikeLog.Write($"swap chain {curW}x{curH} attached to SwapChainPanel");
    }

    /// The panel shows swap-chain pixels 1:1 in DIPs; buffers are sized in physical pixels,
    /// so scale them back down by the composition scale or they overflow the panel.
    private void ApplyInverseScale(float scaleX, float scaleY)
    {
        var sc2 = Com.QueryInterface(swapChain, Com.IID_IDXGISwapChain2);
        var m = new Com.DXGI_MATRIX_3X2_F { _11 = 1f / scaleX, _22 = 1f / scaleY };
        Com.Check(Com.Slot<Com.SetMatrixTransformFn>(sc2, 34)(sc2, ref m), "SetMatrixTransform");
        Com.Release(sc2);
    }

    private void CreateMpv()
    {
        mpv = Mpv.mpv_create();
        if (mpv == IntPtr.Zero) throw new InvalidOperationException("mpv_create failed");
        Mpv.SetOption(mpv, "vo", "libmpv");               // required for the render API
        Mpv.SetOption(mpv, "user-agent", userAgent);
        Mpv.SetOption(mpv, "hwdec", Environment.GetEnvironmentVariable("WINTUBE_SPIKE_HWDEC") ?? "auto-safe");
        // ~1080p by default; keeps the spike off the 4K VP9 rung. WINTUBE_SPIKE_HLSBITRATE=max to lift it.
        Mpv.SetOption(mpv, "hls-bitrate", Environment.GetEnvironmentVariable("WINTUBE_SPIKE_HLSBITRATE") ?? "7000000");
        Mpv.SetOption(mpv, "volume", "20");
        Mpv.SetOption(mpv, "log-file", Path.Combine(AppContext.BaseDirectory, "spike-mpv.log"));
        Mpv.SetOption(mpv, "msg-level", "all=v");
        Com.Check(Mpv.mpv_initialize(mpv), "mpv_initialize");
    }

    // MARK: render thread

    private void RenderLoop()
    {
        try
        {
            InitEgl();
            CreateTarget(curW, curH);

            SpikeLog.Write($"GL_VERSION={egl!.GetString(0x1F02)} GL_RENDERER={egl.GetString(0x1F01)}");

            var init = Marshal.AllocHGlobal(16);
            Marshal.WriteIntPtr(init, 0, Marshal.GetFunctionPointerForDelegate(getProcAddress));
            Marshal.WriteIntPtr(init, 8, IntPtr.Zero);
            var apiType = Marshal.StringToHGlobalAnsi("opengl");
            var createParams = Mpv.Params((Mpv.PARAM_API_TYPE, apiType), (Mpv.PARAM_OPENGL_INIT_PARAMS, init));
            Com.Check(Mpv.mpv_render_context_create(out renderCtx, mpv, createParams), "mpv_render_context_create");
            Marshal.FreeHGlobal(createParams);
            Marshal.FreeHGlobal(apiType);
            Marshal.FreeHGlobal(init);
            Mpv.mpv_render_context_set_update_callback(renderCtx, Marshal.GetFunctionPointerForDelegate(onUpdate), IntPtr.Zero);
            SpikeLog.Write("mpv render context created");

            Com.Check(Mpv.Command(mpv, "loadfile", url), "loadfile");

            var fbo = Marshal.AllocHGlobal(16);
            var flipY = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(flipY, 0);
            while (!quit)
            {
                wake.WaitOne(500);
                if (quit) break;

                int w, h; float sx, sy;
                lock (sizeGate) (w, h, sx, sy) = (wantW, wantH, wantScaleX, wantScaleY);
                var resized = w != curW || h != curH;
                if (resized) Resize(w, h, sx, sy);

                var flags = Mpv.mpv_render_context_update(renderCtx);
                if ((flags & 1) == 0 && !resized) continue;

                Marshal.WriteInt32(fbo, 0, 0);
                Marshal.WriteInt32(fbo, 4, curW);
                Marshal.WriteInt32(fbo, 8, curH);
                Marshal.WriteInt32(fbo, 12, 0);
                var renderParams = Mpv.Params((Mpv.PARAM_OPENGL_FBO, fbo), (Mpv.PARAM_FLIP_Y, flipY));
                Mpv.mpv_render_context_render(renderCtx, renderParams);
                Marshal.FreeHGlobal(renderParams);
                egl.Flush();
                Present();
                Mpv.mpv_render_context_report_swap(renderCtx);
                if (Interlocked.Increment(ref frames) == 1) SpikeLog.Write("first frame presented");
            }
            Marshal.FreeHGlobal(fbo);
            Marshal.FreeHGlobal(flipY);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            SpikeLog.Write($"render thread failed: {ex}");
        }
        finally
        {
            if (renderCtx != IntPtr.Zero) Mpv.mpv_render_context_free(renderCtx);
            renderCtx = IntPtr.Zero;
            DestroyTarget();
            if (egl is not null && eglDisplay != IntPtr.Zero)
            {
                egl.MakeCurrent(eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (eglContext != IntPtr.Zero) egl.DestroyContext(eglDisplay, eglContext);
                egl.Terminate(eglDisplay);
            }
            SpikeLog.Write("render thread exited");
        }
    }

    private void InitEgl()
    {
        egl = Egl.Load();
        var eglDevice = egl.CreateDeviceANGLE(0x33A1 /* EGL_D3D11_DEVICE_ANGLE */, device, IntPtr.Zero);
        if (eglDevice == IntPtr.Zero) throw new InvalidOperationException($"eglCreateDeviceANGLE 0x{egl.GetError():x}");
        eglDisplay = egl.GetPlatformDisplayEXT(0x313F /* EGL_PLATFORM_DEVICE_EXT */, eglDevice, [Egl.NONE]);
        if (eglDisplay == IntPtr.Zero) throw new InvalidOperationException($"eglGetPlatformDisplayEXT 0x{egl.GetError():x}");
        if (egl.Initialize(eglDisplay, out var major, out var minor) == 0)
            throw new InvalidOperationException($"eglInitialize 0x{egl.GetError():x}");
        SpikeLog.Write($"EGL {major}.{minor} vendor={egl.QueryString(eglDisplay, 0x3053)} version={egl.QueryString(eglDisplay, 0x3054)}");

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
    /// never holds a reference to a back buffer and ResizeBuffers stays legal.
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

    private void Resize(int w, int h, float sx, float sy)
    {
        DestroyTarget();
        Com.Check(Com.Slot<Com.ResizeBuffersFn>(swapChain, 13)(swapChain, 0, (uint)w, (uint)h, 0, 0), "ResizeBuffers");
        ApplyInverseScale(sx, sy);
        CreateTarget(w, h);
        (curW, curH) = (w, h);
        SpikeLog.Write($"resized to {w}x{h} scale {sx}");
    }

    private void Present()
    {
        var texIid = Com.IID_ID3D11Texture2D;
        Com.Check(Com.Slot<Com.GetBufferFn>(swapChain, 9)(swapChain, 0, ref texIid, out var back), "GetBuffer");
        Com.Slot<Com.CopyResourceFn>(context, 47)(context, back, texture);
        Com.Release(back);
        var hr = Com.Slot<Com.PresentFn>(swapChain, 8)(swapChain, PresentInterval, 0);
        if (hr < 0) lastError = $"Present 0x{hr:x8}";
    }

    public void Dispose()
    {
        quit = true;
        wake.Set();
        if (thread.IsAlive) thread.Join(3000);
        if (mpv != IntPtr.Zero) Mpv.mpv_terminate_destroy(mpv);
        mpv = IntPtr.Zero;
        // UI thread: detach, or the panel keeps its own reference and shows the last frame.
        var panelNative = Com.QueryInterface(((WinRT.IWinRTObject)panel).NativeObject.ThisPtr, Com.IID_ISwapChainPanelNative);
        Com.Slot<Com.SetSwapChainFn>(panelNative, 3)(panelNative, IntPtr.Zero);
        Com.Release(panelNative);
        if (swapChain != IntPtr.Zero) Com.Release(swapChain);
        if (context != IntPtr.Zero) Com.Release(context);
        if (device != IntPtr.Zero) Com.Release(device);
        swapChain = context = device = IntPtr.Zero;
        SpikeLog.Write("session disposed");
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);
}

/// Raw COM vtable calls — enough of DXGI/D3D11/ISwapChainPanelNative for the spike without
/// pulling in a D3D wrapper package. Slot numbers are vtable indices including IUnknown's 3.
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

    public static void Release(IntPtr obj) => Slot<ReleaseFn>(obj, 2)(obj);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:x8}");
    }
}

/// ANGLE loaded dynamically: Avalonia's single-DLL build (av_libglesv2.dll) exports the EGL
/// entry points as EGL_* and resolves GL ones through EGL_GetProcAddress.
internal sealed class Egl
{
    public const int NONE = 0x3038;
    private const string DllName = "av_libglesv2.dll";

    private readonly IntPtr lib;
    private readonly GetProcAddressFn getProcAddress;
    public readonly CreateDeviceANGLEFn CreateDeviceANGLE;
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
    private readonly QueryStringFn queryString;
    private readonly GlFlushFn glFlush;
    private readonly GlGetStringFn glGetString;

    public delegate IntPtr GetProcAddressFn([MarshalAs(UnmanagedType.LPStr)] string name);
    public delegate IntPtr CreateDeviceANGLEFn(int deviceType, IntPtr nativeDevice, IntPtr attribs);
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
    public delegate IntPtr QueryStringFn(IntPtr display, int name);
    public delegate void GlFlushFn();
    public delegate IntPtr GlGetStringFn(int name);

    private Egl(IntPtr lib)
    {
        this.lib = lib;
        getProcAddress = Export<GetProcAddressFn>("EGL_GetProcAddress");
        CreateDeviceANGLE = Export<CreateDeviceANGLEFn>("EGL_CreateDeviceANGLE");
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
        queryString = Export<QueryStringFn>("EGL_QueryString");
        glFlush = Marshal.GetDelegateForFunctionPointer<GlFlushFn>(GetProcAddress("glFlush"));
        glGetString = Marshal.GetDelegateForFunctionPointer<GlGetStringFn>(GetProcAddress("glGetString"));
    }

    /// Beside the exe first; for the spike, fall back to the repo's gitignored libs/angle/.
    public static Egl Load()
    {
        var local = Path.Combine(AppContext.BaseDirectory, DllName);
        var path = File.Exists(local) ? local : FindInRepo();
        SpikeLog.Write($"loading ANGLE from {path}");
        return new Egl(NativeLibrary.Load(path));
    }

    private static string FindInRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "libs", "angle", DllName);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"{DllName} not found beside the exe or in libs/angle");
    }

    private T Export<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

    public IntPtr GetProcAddress(string name) => getProcAddress(name);
    public void Flush() => glFlush();
    public string GetString(int name) => Marshal.PtrToStringAnsi(glGetString(name)) ?? "";
    public string QueryString(IntPtr display, int name) => Marshal.PtrToStringAnsi(queryString(display, name)) ?? "";
}

internal static class Mpv
{
    private const string Lib = "libmpv-2.dll";
    public const int PARAM_API_TYPE = 1, PARAM_OPENGL_INIT_PARAMS = 2, PARAM_OPENGL_FBO = 3, PARAM_FLIP_Y = 4;
    private const int FORMAT_STRING = 1, FORMAT_DOUBLE = 5;

    public delegate IntPtr GetProcAddressFn(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name);
    public delegate void UpdateFn(IntPtr ctx);

    [DllImport(Lib)] public static extern IntPtr mpv_create();
    [DllImport(Lib)] public static extern int mpv_initialize(IntPtr h);
    [DllImport(Lib)] public static extern void mpv_terminate_destroy(IntPtr h);
    [DllImport(Lib)] private static extern int mpv_set_option_string(IntPtr h, byte[] name, byte[] value);
    [DllImport(Lib)] private static extern int mpv_command(IntPtr h, IntPtr[] args);
    [DllImport(Lib)] private static extern int mpv_get_property(IntPtr h, byte[] name, int format, out double data);
    [DllImport(Lib)] private static extern int mpv_get_property(IntPtr h, byte[] name, int format, out IntPtr data);
    [DllImport(Lib)] private static extern void mpv_free(IntPtr data);
    [DllImport(Lib)] public static extern int mpv_render_context_create(out IntPtr ctx, IntPtr mpv, IntPtr parameters);
    [DllImport(Lib)] public static extern void mpv_render_context_set_update_callback(IntPtr ctx, IntPtr callback, IntPtr callbackCtx);
    [DllImport(Lib)] public static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib)] public static extern int mpv_render_context_render(IntPtr ctx, IntPtr parameters);
    [DllImport(Lib)] public static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Lib)] public static extern void mpv_render_context_free(IntPtr ctx);

    private static byte[] Utf8(string s) => System.Text.Encoding.UTF8.GetBytes(s + "\0");

    public static void SetOption(IntPtr h, string name, string value)
    {
        var rc = mpv_set_option_string(h, Utf8(name), Utf8(value));
        if (rc < 0) SpikeLog.Write($"mpv option {name}={value} rc={rc}");
    }

    public static int Command(IntPtr h, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        try { return mpv_command(h, ptrs); }
        finally { foreach (var p in ptrs[..^1]) Marshal.FreeCoTaskMem(p); }
    }

    public static double? GetDouble(IntPtr h, string name) =>
        mpv_get_property(h, Utf8(name), FORMAT_DOUBLE, out double v) == 0 ? v : null;

    public static string? GetString(IntPtr h, string name)
    {
        if (mpv_get_property(h, Utf8(name), FORMAT_STRING, out IntPtr p) != 0) return null;
        var s = Marshal.PtrToStringUTF8(p);
        mpv_free(p);
        return s;
    }

    /// A zero-terminated mpv_render_param array ({int type; void* data} = 16 bytes each on x64).
    public static IntPtr Params(params (int Type, IntPtr Data)[] items)
    {
        var p = Marshal.AllocHGlobal(16 * (items.Length + 1));
        for (var i = 0; i <= items.Length; i++)
        {
            var (type, data) = i < items.Length ? items[i] : (0, IntPtr.Zero);
            Marshal.WriteInt64(p, i * 16, 0);
            Marshal.WriteInt32(p, i * 16, type);
            Marshal.WriteIntPtr(p, i * 16 + 8, data);
        }
        return p;
    }
}
