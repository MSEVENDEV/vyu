using System.Runtime.InteropServices;
using System.Text;

namespace Vyu.Mpv;

/// <summary>P/Invoke surface of libmpv-2.dll (client.h + render.h) — only what Vyu uses.</summary>
internal static unsafe class MpvNative
{
    private const string Lib = "libmpv-2.dll";

    public const int FORMAT_NONE = 0, FORMAT_STRING = 1, FORMAT_FLAG = 3, FORMAT_INT64 = 4, FORMAT_DOUBLE = 5, FORMAT_NODE = 6;

    public enum EventId
    {
        None = 0, Shutdown = 1, LogMessage = 2, GetPropertyReply = 3, SetPropertyReply = 4, CommandReply = 5,
        StartFile = 6, EndFile = 7, FileLoaded = 8, Idle = 11, Tick = 14, ClientMessage = 16, VideoReconfig = 17,
        AudioReconfig = 18, Seek = 20, PlaybackRestart = 21, PropertyChange = 22, QueueOverflow = 24, Hook = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public EventId EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventEndFile
    {
        public int Reason; // 0 eof, 2 stop, 3 quit, 4 error, 5 redirect
        public int Error;
        public long PlaylistEntryId;
        public long PlaylistInsertId;
        public int PlaylistInsertNumEntries;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public IntPtr Data;
        public RenderParam(int type, IntPtr data) { Type = type; Data = data; }
    }

    public const int RENDER_PARAM_INVALID = 0, RENDER_PARAM_API_TYPE = 1, RENDER_PARAM_SW_SIZE = 17,
        RENDER_PARAM_SW_FORMAT = 18, RENDER_PARAM_SW_STRIDE = 19, RENDER_PARAM_SW_POINTER = 20;
    public const ulong RENDER_UPDATE_FRAME = 1;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option(IntPtr ctx, byte[] name, int format, ref long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong replyUserdata, IntPtr[] args);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property(IntPtr ctx, byte[] name, int format, ref double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property(IntPtr ctx, byte[] name, int format, ref long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_get_property(IntPtr ctx, byte[] name, int format, out long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata, byte[] name, int format);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr ctx, byte[] minLevel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_render_context_create(out IntPtr res, IntPtr mpv, RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_set_update_callback(IntPtr ctx, UpdateCallback cb, IntPtr cbCtx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_render_context_update(IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_render_context_render(IntPtr ctx, RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_free(IntPtr ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UpdateCallback(IntPtr ctx);

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    public static string? PtrToUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static string Error(int code) => PtrToUtf8(mpv_error_string(code)) ?? code.ToString();

    /// <summary>Builds a NULL-terminated argv of UTF-8 strings; free with <see cref="FreeArgs"/>.</summary>
    public static IntPtr[] Args(params string[] args)
    {
        var arr = new IntPtr[args.Length + 1];
        for (int i = 0; i < args.Length; i++)
        {
            var bytes = Utf8(args[i]);
            arr[i] = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, arr[i], bytes.Length);
        }
        return arr;
    }

    public static void FreeArgs(IntPtr[] arr)
    {
        foreach (var p in arr) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
    }
}
