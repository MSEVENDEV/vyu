using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using static Vyu.Mpv.MpvNative;

namespace Vyu.Mpv;

public sealed record MpvTrack(long Id, string Type, string Title, string? Lang, string? Codec, bool Selected, bool External, bool Default, int? Channels, int? Width, int? Height)
{
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title);
            if (!string.IsNullOrWhiteSpace(Lang)) parts.Add(LangName(Lang!));
            if (parts.Count == 0) parts.Add(Type == "sub" ? $"Subtitle {Id}" : Type == "audio" ? $"Audio {Id}" : $"Track {Id}");
            var extra = new List<string>();
            if (!string.IsNullOrWhiteSpace(Codec)) extra.Add(Codec switch { "subrip" => "SRT", "hdmv_pgs_subtitle" => "PGS", "ass" => "ASS", "webvtt" => "VTT", var other => other!.ToUpperInvariant() });
            if (Channels is int c) extra.Add(c switch { 1 => "mono", 2 => "stereo", 6 => "5.1", 8 => "7.1", _ => $"{c} ch" });
            if (External) extra.Add("external");
            return extra.Count > 0 ? $"{string.Join(" · ", parts.Distinct())}  ({string.Join(", ", extra)})" : string.Join(" · ", parts.Distinct());
        }
    }

    private static string LangName(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; } catch { return code; }
    }
}

public sealed record MpvChapter(double Time, string Title);

/// <summary>
/// libmpv driven from WPF. Video is rendered by mpv's own GPU renderer (gpu-next, D3D11) straight into a
/// child HWND, so playback is as fast and as pretty as mpv itself; everything else is Vyu.
/// Property changes arrive on mpv's event thread and are marshalled through <see cref="PropertyChanged"/>.
/// </summary>
public sealed class MpvPlayer : IDisposable
{
    private IntPtr _ctx;
    private Thread? _events;
    private volatile bool _disposed;
    private readonly Dictionary<ulong, string> _observed = new();

    public event Action<string, object?>? PropertyChanged;
    public event Action? FileLoaded;
    public event Action<int, string?>? EndFile;
    public event Action? PlaybackRestart;
    public event Action<string>? LogLine;

    public bool IsReady => _ctx != IntPtr.Zero;

    public static bool LibraryAvailable()
    {
        try { return NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"), out _); }
        catch { return false; }
    }

    public void Initialize(IntPtr hwnd, IDictionary<string, string> options)
    {
        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("libmpv could not be created.");
        long wid = hwnd.ToInt64();
        if (hwnd != IntPtr.Zero) Check(mpv_set_option(_ctx, Utf8("wid"), FORMAT_INT64, ref wid), "wid");
        foreach (var (k, v) in options)
        {
            int r = mpv_set_option_string(_ctx, Utf8(k), Utf8(v));
            if (r < 0) LogLine?.Invoke($"option {k}={v}: {Error(r)}");
        }
        Check(mpv_initialize(_ctx), "initialize");
        mpv_request_log_messages(_ctx, Utf8("warn"));
    }

    /// <summary>Start observing properties and pumping events. Call after the UI has subscribed, so the initial values aren't lost.</summary>
    public void StartEvents()
    {
        if (_events is not null || _ctx == IntPtr.Zero) return;
        foreach (var (name, fmt) in new (string, int)[]
        {
            ("pause", FORMAT_FLAG), ("duration", FORMAT_DOUBLE), ("volume", FORMAT_DOUBLE), ("mute", FORMAT_FLAG),
            ("speed", FORMAT_DOUBLE), ("media-title", FORMAT_STRING), ("path", FORMAT_STRING), ("idle-active", FORMAT_FLAG),
            ("eof-reached", FORMAT_FLAG), ("track-list/count", FORMAT_INT64), ("chapter-list/count", FORMAT_INT64),
            ("video-params/w", FORMAT_INT64), ("video-params/h", FORMAT_INT64), ("paused-for-cache", FORMAT_FLAG),
            ("ab-loop-a", FORMAT_STRING), ("ab-loop-b", FORMAT_STRING), ("aid", FORMAT_STRING), ("sid", FORMAT_STRING),
            ("playlist-pos", FORMAT_INT64), ("playlist-count", FORMAT_INT64), ("loop-file", FORMAT_STRING),
            ("video-params/primaries", FORMAT_STRING), ("seekable", FORMAT_FLAG),
        })
        {
            ulong id = (ulong)(_observed.Count + 1);
            _observed[id] = name;
            mpv_observe_property(_ctx, id, Utf8(name), fmt);
        }

        _events = new Thread(EventLoop) { IsBackground = true, Name = "mpv events" };
        _events.Start();
    }

    private static void Check(int code, string what)
    {
        if (code < 0) throw new InvalidOperationException($"mpv {what} failed: {Error(code)}");
    }

    private void EventLoop()
    {
        var ctx = _ctx; // _ctx is cleared on Dispose; keep our own copy until mpv says goodbye
        while (!_disposed)
        {
            var ptr = mpv_wait_event(ctx, -1);
            if (ptr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<Event>(ptr);
            try
            {
                switch (ev.EventId)
                {
                    case EventId.Shutdown:
                        return;
                    case EventId.PropertyChange:
                        var p = Marshal.PtrToStructure<EventProperty>(ev.Data);
                        var name = PtrToUtf8(p.Name) ?? "";
                        object? value = p.Format switch
                        {
                            FORMAT_FLAG => Marshal.ReadInt32(p.Data) != 0,
                            FORMAT_DOUBLE => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(p.Data)),
                            FORMAT_INT64 => Marshal.ReadInt64(p.Data),
                            FORMAT_STRING => PtrToUtf8(Marshal.ReadIntPtr(p.Data)),
                            _ => null,
                        };
                        PropertyChanged?.Invoke(name, value);
                        break;
                    case EventId.FileLoaded:
                        FileLoaded?.Invoke();
                        break;
                    case EventId.EndFile:
                        var ef = Marshal.PtrToStructure<EventEndFile>(ev.Data);
                        EndFile?.Invoke(ef.Reason, ef.Reason == 4 ? Error(ef.Error) : null);
                        break;
                    case EventId.PlaybackRestart:
                        PlaybackRestart?.Invoke();
                        break;
                    case EventId.LogMessage:
                        // struct mpv_event_log_message { const char *prefix, *level, *text; int log_level; }
                        var prefix = PtrToUtf8(Marshal.ReadIntPtr(ev.Data));
                        var text = PtrToUtf8(Marshal.ReadIntPtr(ev.Data, 2 * IntPtr.Size));
                        LogLine?.Invoke($"[{prefix}] {text?.TrimEnd()}");
                        break;
                }
            }
            catch (Exception ex) { LogLine?.Invoke("event error: " + ex.Message); }
        }
    }

    // ------------------------------------------------------------------ commands

    public void Command(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var argv = Args(args);
        try
        {
            int r = mpv_command(_ctx, argv);
            if (r < 0) LogLine?.Invoke($"command {string.Join(' ', args)}: {Error(r)}");
        }
        finally { FreeArgs(argv); }
    }

    public void CommandAsync(params string[] args)
    {
        if (_ctx == IntPtr.Zero) return;
        var argv = Args(args);
        try { mpv_command_async(_ctx, 0, argv); }
        finally { FreeArgs(argv); }
    }

    public void Load(string pathOrUrl, double startSeconds = 0)
    {
        var opts = startSeconds > 1 ? $"start={startSeconds.ToString("0.###", CultureInfo.InvariantCulture)}" : "";
        Command("loadfile", pathOrUrl, "replace", "-1", opts);
    }

    public void Append(string path) => Command("loadfile", path, "append");

    public void SetString(string name, string value)
    {
        if (_ctx == IntPtr.Zero) return;
        int r = mpv_set_property_string(_ctx, Utf8(name), Utf8(value));
        if (r < 0) LogLine?.Invoke($"set {name}={value}: {Error(r)}");
    }

    public void SetDouble(string name, double value)
    {
        if (_ctx == IntPtr.Zero) return;
        mpv_set_property(_ctx, Utf8(name), FORMAT_DOUBLE, ref value);
    }

    public void SetFlag(string name, bool value) => SetString(name, value ? "yes" : "no");

    public double GetDouble(string name, double fallback = 0)
    {
        if (_ctx == IntPtr.Zero) return fallback;
        return mpv_get_property(_ctx, Utf8(name), FORMAT_DOUBLE, out double v) >= 0 ? v : fallback;
    }

    public long GetLong(string name, long fallback = 0)
    {
        if (_ctx == IntPtr.Zero) return fallback;
        return mpv_get_property(_ctx, Utf8(name), FORMAT_INT64, out long v) >= 0 ? v : fallback;
    }

    public string? GetString(string name)
    {
        if (_ctx == IntPtr.Zero) return null;
        var p = mpv_get_property_string(_ctx, Utf8(name));
        if (p == IntPtr.Zero) return null;
        try { return PtrToUtf8(p); }
        finally { mpv_free(p); }
    }

    public List<MpvTrack> Tracks()
    {
        var list = new List<MpvTrack>();
        var json = GetString("track-list");
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                string S(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                bool B(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                int? I(string n) => t.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                list.Add(new MpvTrack(t.GetProperty("id").GetInt64(), S("type"), S("title"), S("lang"), S("codec"),
                    B("selected"), B("external"), B("default"), I("demux-channel-count"), I("demux-w"), I("demux-h")));
            }
        }
        catch (Exception ex) { LogLine?.Invoke("track-list parse: " + ex.Message); }
        return list;
    }

    public List<MpvChapter> Chapters()
    {
        var list = new List<MpvChapter>();
        var json = GetString("chapter-list");
        if (string.IsNullOrEmpty(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var c in doc.RootElement.EnumerateArray())
                list.Add(new MpvChapter(c.GetProperty("time").GetDouble(), c.TryGetProperty("title", out var t) ? t.GetString() ?? "" : ""));
        }
        catch { }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var ctx = _ctx;
        _ctx = IntPtr.Zero;
        if (ctx != IntPtr.Zero)
        {
            mpv_wakeup(ctx);
            _events?.Join(300);
            mpv_terminate_destroy(ctx);
        }
    }
}
