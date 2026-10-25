using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Vyu.Mpv.MpvNative;

namespace Vyu.Mpv;

/// <summary>
/// Seek-bar previews. A second, silent libmpv instance decodes only keyframes and renders them in software
/// into a tiny 288-px bitmap. Requests are "latest wins", results are cached per 2-second bucket.
/// </summary>
public sealed class MpvThumbnailer : IDisposable
{
    private readonly int Width;
    private IntPtr _mpv, _render;
    private readonly Thread _worker;
    private readonly AutoResetEvent _request = new(false);
    private readonly AutoResetEvent _frame = new(false);
    private readonly UpdateCallback _cb; // keep alive: called from native code
    private readonly ConcurrentDictionary<int, BitmapSource> _cache = new();
    private volatile bool _disposed;
    private string? _file, _loaded;
    private double _wanted = -1;
    private int _height;
    private readonly object _lock = new();

    /// <summary>Raised (on the worker thread) with the requested time and the frozen bitmap.</summary>
    public event Action<double, BitmapSource>? Ready;

    public MpvThumbnailer(int width = 288)
    {
        Width = width;
        _cb = _ => _frame.Set();
        _worker = new Thread(Work) { IsBackground = true, Name = "Vyu thumbnails", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    public void SetFile(string? path)
    {
        lock (_lock)
        {
            _file = path;
            _cache.Clear();
            _wanted = -1;
        }
    }

    public void Request(double seconds)
    {
        int bucket = (int)(seconds / 2);
        if (_cache.TryGetValue(bucket, out var hit)) { Ready?.Invoke(seconds, hit); return; }
        lock (_lock) _wanted = seconds;
        _request.Set();
    }

    private bool EnsureInstance()
    {
        if (_mpv != IntPtr.Zero) return true;
        _mpv = mpv_create();
        if (_mpv == IntPtr.Zero) return false;
        foreach (var (k, v) in new Dictionary<string, string>
        {
            ["vo"] = "libmpv", ["hwdec"] = "no", ["aid"] = "no", ["sid"] = "no", ["audio"] = "no", ["pause"] = "yes",
            ["keep-open"] = "always", ["hr-seek"] = "no", ["cache"] = "no", ["demuxer-readahead-secs"] = "0",
            ["vd-lavc-skiploopfilter"] = "all", ["vd-lavc-fast"] = "yes", ["sw-fast"] = "yes", ["idle"] = "yes",
            ["terminal"] = "no", ["load-scripts"] = "no", ["ytdl"] = "no", ["osd-level"] = "0", ["config"] = "no",
            ["vd-lavc-threads"] = "2", ["msg-level"] = "all=no",
        })
            mpv_set_option_string(_mpv, Utf8(k), Utf8(v));
        if (mpv_initialize(_mpv) < 0) return false;

        var api = Marshal.StringToHGlobalAnsi("sw");
        try
        {
            var p = new[] { new RenderParam(RENDER_PARAM_API_TYPE, api), new RenderParam(RENDER_PARAM_INVALID, IntPtr.Zero) };
            if (mpv_render_context_create(out _render, _mpv, p) < 0) return false;
        }
        finally { Marshal.FreeHGlobal(api); }
        mpv_render_context_set_update_callback(_render, _cb, IntPtr.Zero);
        return true;
    }

    private void Work()
    {
        while (!_disposed)
        {
            _request.WaitOne();
            if (_disposed) break;
            try
            {
                string? file; double t;
                lock (_lock) { file = _file; t = _wanted; }
                if (file is null || t < 0 || !EnsureInstance()) continue;
                if (file != _loaded)
                {
                    Cmd("loadfile", file, "replace");
                    _loaded = file;
                    _height = 0;
                    if (!WaitForVideo()) continue;
                }
                int bucket = (int)(t / 2);
                if (_cache.TryGetValue(bucket, out var cached)) { Ready?.Invoke(t, cached); continue; }
                _frame.Reset();
                Cmd("seek", (bucket * 2.0 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "absolute+keyframes");
                var bmp = RenderNext(TimeSpan.FromMilliseconds(1500));
                if (bmp is null) continue;
                _cache[bucket] = bmp;
                Ready?.Invoke(t, bmp);
            }
            catch { /* thumbnails are best effort */ }
        }
    }

    private bool WaitForVideo()
    {
        for (int i = 0; i < 60; i++)
        {
            mpv_get_property(_mpv, Utf8("video-params/w"), FORMAT_INT64, out long w);
            mpv_get_property(_mpv, Utf8("video-params/h"), FORMAT_INT64, out long h);
            if (w > 0 && h > 0)
            {
                _height = (int)Math.Max(2, Math.Round(Width * (h / (double)w) / 2) * 2);
                return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    private unsafe BitmapSource? RenderNext(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_frame.WaitOne(deadline - DateTime.UtcNow)) return null;
            ulong flags = mpv_render_context_update(_render);
            if ((flags & RENDER_UPDATE_FRAME) == 0) continue;

            int w = Width, h = _height > 0 ? _height : 162;
            int stride = w * 4;
            var pixels = new byte[stride * h];
            var size = Marshal.AllocHGlobal(8);
            var fmt = Marshal.StringToHGlobalAnsi("bgr0");
            var strideMem = Marshal.AllocHGlobal(IntPtr.Size);
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Marshal.WriteInt32(size, 0, w);
                Marshal.WriteInt32(size, 4, h);
                Marshal.WriteIntPtr(strideMem, new IntPtr(stride));
                var p = new[]
                {
                    new RenderParam(RENDER_PARAM_SW_SIZE, size), new RenderParam(RENDER_PARAM_SW_FORMAT, fmt),
                    new RenderParam(RENDER_PARAM_SW_STRIDE, strideMem), new RenderParam(RENDER_PARAM_SW_POINTER, handle.AddrOfPinnedObject()),
                    new RenderParam(RENDER_PARAM_INVALID, IntPtr.Zero),
                };
                if (mpv_render_context_render(_render, p) < 0) return null;
            }
            finally
            {
                handle.Free();
                Marshal.FreeHGlobal(size); Marshal.FreeHGlobal(fmt); Marshal.FreeHGlobal(strideMem);
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            bmp.Freeze();
            return bmp;
        }
        return null;
    }

    private void Cmd(params string[] args)
    {
        var argv = Args(args);
        try { mpv_command(_mpv, argv); }
        finally { FreeArgs(argv); }
    }

    public void Dispose()
    {
        _disposed = true;
        _request.Set();
        _worker.Join(500);
        if (_render != IntPtr.Zero) mpv_render_context_free(_render);
        if (_mpv != IntPtr.Zero) mpv_terminate_destroy(_mpv);
        _render = _mpv = IntPtr.Zero;
    }
}
