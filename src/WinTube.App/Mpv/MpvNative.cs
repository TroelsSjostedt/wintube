using System.Runtime.InteropServices;
using System.Text;

namespace WinTube.App.Mpv;

/// The libmpv functions WinTube uses, straight from client.h/render.h. Strings cross as UTF-8;
/// mpv owns every pointer it returns from wait_event until the next call.
/// DllImport, not LibraryImport: the source-generated form needs AllowUnsafeBlocks, which this
/// project does not set (the spike proved DllImport builds clean under TreatWarningsAsErrors as-is).
internal static class MpvNative
{
    private const string Lib = "libmpv-2";

    [DllImport(Lib)] internal static extern IntPtr mpv_create();
    [DllImport(Lib)] internal static extern int mpv_initialize(IntPtr handle);
    [DllImport(Lib)] internal static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Lib)] internal static extern int mpv_set_option_string(IntPtr handle, byte[] name, byte[] value);
    [DllImport(Lib)] internal static extern int mpv_command(IntPtr handle, IntPtr[] args);
    [DllImport(Lib)] internal static extern int mpv_set_property(IntPtr handle, byte[] name, int format, ref double data);
    [DllImport(Lib)] internal static extern int mpv_observe_property(IntPtr handle, ulong userdata, byte[] name, int format);
    [DllImport(Lib)] internal static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [DllImport(Lib)] internal static extern void mpv_wakeup(IntPtr handle);
    [DllImport(Lib)] internal static extern IntPtr mpv_error_string(int error);

    // Render API (opengl, drawn through ANGLE). `parameters` is a raw pointer to a zero-terminated
    // array of mpv_render_param ({int type; void* data}, 16 bytes each on x64) built by Params below
    // -- NOT a managed array, since each entry needs a type tag alongside its pointer.
    [DllImport(Lib)] internal static extern int mpv_render_context_create(out IntPtr ctx, IntPtr handle, IntPtr parameters);
    [DllImport(Lib)] internal static extern void mpv_render_context_set_update_callback(IntPtr ctx, IntPtr callback, IntPtr callbackCtx);
    [DllImport(Lib)] internal static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib)] internal static extern int mpv_render_context_render(IntPtr ctx, IntPtr parameters);
    [DllImport(Lib)] internal static extern void mpv_render_context_report_swap(IntPtr ctx);
    [DllImport(Lib)] internal static extern void mpv_render_context_free(IntPtr ctx);

    internal const int FormatFlag = 3;     // MPV_FORMAT_FLAG
    internal const int FormatDouble = 5;   // MPV_FORMAT_DOUBLE

    // mpv_render_param_type (render.h)
    internal const int ParamApiType = 1, ParamOpenGlInitParams = 2, ParamOpenGlFbo = 3, ParamFlipY = 4;

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
    internal const int EndFileError = 4;   // MPV_END_FILE_REASON_*

    internal delegate IntPtr GetProcAddressFn(IntPtr ctx, [MarshalAs(UnmanagedType.LPStr)] string name);
    internal delegate void UpdateFn(IntPtr ctx);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    internal static void SetOption(IntPtr handle, string name, string value) =>
        mpv_set_option_string(handle, Utf8(name), Utf8(value));

    internal static int Command(IntPtr handle, params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];
        for (var i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        try { return mpv_command(handle, ptrs); }
        finally { for (var i = 0; i < args.Length; i++) Marshal.FreeCoTaskMem(ptrs[i]); }
    }

    internal static void SetPropertyDouble(IntPtr handle, string name, double value) =>
        mpv_set_property(handle, Utf8(name), FormatDouble, ref value);

    internal static string ErrorString(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? $"mpv error {error}";

    /// A zero-terminated mpv_render_param array, as mpv_render_context_create/render expect it.
    /// Caller frees the returned block (Marshal.FreeHGlobal) once mpv has consumed it.
    internal static IntPtr Params(params (int Type, IntPtr Data)[] items)
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
