using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Vyu.Mpv;
using Vyu.Services;
using Vyu.Views;

namespace Vyu;

public partial class App : Application
{
    private SingleInstance? _single;
    private MpvPlayer? _mpv;
    private VyuSettings _settings = new();
    private Recents _recents = new();
    private ControlsWindow? _controls;

    public static bool SnapMode { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // started by an update: let the old version finish exiting first, and don't treat the flags as files
        Updater.WaitForPreviousInstance(e.Args);
        var raw = e.Args.ToList();
        bool updated = raw.Remove("--updated");
        int waitAt = raw.IndexOf("--wait-pid");
        if (waitAt >= 0) raw.RemoveRange(waitAt, Math.Min(2, raw.Count - waitAt));
        int snapAt = raw.FindIndex(a => a.Equals("--snap", StringComparison.OrdinalIgnoreCase));
        string? snapDir = snapAt >= 0 && snapAt + 1 < raw.Count ? raw[snapAt + 1] : null;
        string? snapFile = snapAt >= 0 && snapAt + 2 < raw.Count ? raw[snapAt + 2] : null;
        SnapMode = snapDir is not null;
        raw.RemoveAll(a => a is "--elevated" or "--no-elevate"); // flags from Vyu 1.0 shortcuts
        var args = SnapMode ? new List<string>() : raw;

        DispatcherUnhandledException += (_, ex) => { Log("UI: " + ex.Exception); ex.Handled = true; };

        if (!SnapMode)
        {
            // a Vyu is already open → give it the files, no second window
            if (SingleInstance.AnotherIsRunning() && SingleInstance.Forward(args.ToArray())) { Shutdown(); return; }
            _settings = VyuSettings.Load();
            _single = new SingleInstance();
            _single.Listen(a => Dispatcher.BeginInvoke(() => _controls?.HandleArgs(a)));
            // after an update or a move, keep "open with Vyu" pointing at this Vyu.exe
            try { FileAssociations.RefreshIfMoved(); } catch (Exception ex) { Log("Associations: " + ex.Message); }
        }
        else
        {
            _settings = new VyuSettings();
        }
        _recents = SnapMode ? new Recents() : Recents.Load();

        if (!MpvPlayer.LibraryAvailable())
        {
            MessageBox.Show("libmpv-2.dll is missing next to Vyu.exe. Please reinstall Vyu.", "Vyu", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var main = new MainWindow();
        MainWindow = main;
        if (SnapMode)
        {
            main.ShowActivated = false;
            main.ShowInTaskbar = false;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -32000; main.Top = 0;
        }
        main.Show();

        _mpv = new MpvPlayer();
        _mpv.LogLine += Log;
        try
        {
            var options = BuildOptions(_settings);
            if (SnapMode) options["ao"] = "null"; // screenshots are silent
            _mpv.Initialize(main.VideoHandle, options);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Vyu couldn't start the video engine:\n" + ex.Message, "Vyu", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        ApplyQuality(_mpv, _settings.Quality);
        if (_settings.NightMode) _mpv.Command("af", "add", "@night:lavfi=[dynaudnorm=f=250:g=15:p=0.7]");

        _controls = new ControlsWindow(main, _mpv, _settings, _recents);
        _mpv.StartEvents();
        if (SnapMode) { _controls.ShowActivated = false; }
        _controls.Show();
        main.Closing += (_, _) =>
        {
            _controls.Close();
            _settings.Save();
        };

        if (SnapMode) { await Snapshots.RunAsync(main, _controls, _mpv, snapDir!, snapFile); Shutdown(); return; }
        if (args.Count > 0) _controls.HandleArgs(args);

        var controls = _controls;
        Updates = new UpdateService { IsWatching = () => controls.IsWatching };
        Updates.CleanUp();
        if (updated) controls.Notify($"Updated to Vyu {Updates.Current.ToString(3)}");
        _ = CheckForUpdatesAsync(manual: false);
    }

    // ------------------------------------------------------------------ updates

    public static UpdateService? Updates { get; private set; }
    private UpdateWindow? _updateWindow;
    private DispatcherTimer? _updateWait;

    /// <summary>Checks GitHub and shows the update pop-up — right away on the home screen, or once the video ends.</summary>
    public async Task<bool> CheckForUpdatesAsync(bool manual)
    {
        if (Updates is null || _controls is null || (!manual && !_settings.CheckForUpdates)) return false;
        if (!manual) await Task.Delay(TimeSpan.FromSeconds(3));
        if (Updates.Schedule != UpdateSchedule.None) return true;
        var info = await Updates.CheckAsync();
        if (info is null) return false;
        if (!manual && _settings.UpdateSnoozedVersion == info.Tag && _settings.UpdateSnoozedUntil > DateTime.Now) return true;
        _updateWait ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            // never on top of a video you're watching
            if (_controls is null || !_controls.IsIdle || _updateWindow is not null || Updates.Available is not { } u) return;
            _updateWait!.Stop();
            _updateWindow = new UpdateWindow(Updates, u, Updates.Current, _settings, _controls.Notify) { Owner = MainWindow };
            _updateWindow.Closed += (_, _) => _updateWindow = null;
            _updateWindow.Show();
        }, Dispatcher);
        _updateWait.Start();
        return true;
    }

    public static Dictionary<string, string> BuildOptions(VyuSettings s)
    {
        var configDir = Path.Combine(VyuSettings.Folder, "mpv");
        Directory.CreateDirectory(configDir);
        var o = new Dictionary<string, string>
        {
            ["vo"] = "gpu-next",
            ["hwdec"] = s.HardwareDecoding ? "auto-safe" : "no",
            ["keep-open"] = s.AutoPlayNext ? "yes" : "always",
            ["idle"] = "yes",
            ["force-window"] = "yes",
            ["input-default-bindings"] = "no",
            ["input-vo-keyboard"] = "no",
            ["input-cursor"] = "no",
            ["osc"] = "no",
            ["osd-bar"] = "no",
            ["osd-level"] = "0",
            ["cursor-autohide"] = "no",
            ["volume"] = s.Volume.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
            ["volume-max"] = "150",
            ["sub-auto"] = s.AutoLoadSubtitles ? "fuzzy" : "no",
            ["audio-file-auto"] = "fuzzy",
            ["sub-scale"] = s.SubtitleScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            ["sub-font"] = "Segoe UI Semibold",
            ["sub-font-size"] = "44",
            ["sub-border-size"] = "2.4",
            ["sub-shadow-offset"] = "1.2",
            ["sub-shadow-color"] = "#80000000",
            ["sub-margin-y"] = "38",
            ["ytdl"] = "yes",
            ["ytdl-format"] = "bestvideo[height<=?2160]+bestaudio/best",
            ["screenshot-format"] = "png",
            ["screenshot-template"] = "Vyu %F %wH-%wM-%wS",
            ["demuxer-max-bytes"] = "256MiB",
            ["demuxer-max-back-bytes"] = "64MiB",
            ["config-dir"] = configDir, // power users: drop an mpv.conf here
            ["config"] = "yes",
            ["load-scripts"] = "no",
            ["msg-level"] = "all=warn",
            ["terminal"] = "no",
            ["save-position-on-quit"] = "no",
        };
        if (s.PreferredAudioLanguage.Length > 0) o["alang"] = s.PreferredAudioLanguage;
        if (s.PreferredSubtitleLanguage.Length > 0) o["slang"] = s.PreferredSubtitleLanguage;
        return o;
    }

    /// <summary>Battery = cheapest scaling, Balanced = mpv defaults, Cinema = mpv's high-quality look.</summary>
    public static void ApplyQuality(MpvPlayer mpv, QualityProfile q)
    {
        var map = q switch
        {
            QualityProfile.Fast => new Dictionary<string, string>
            {
                ["scale"] = "bilinear", ["dscale"] = "bilinear", ["cscale"] = "bilinear", ["deband"] = "no", ["dither-depth"] = "no",
                ["correct-downscaling"] = "no", ["linear-downscaling"] = "no", ["sigmoid-upscaling"] = "no", ["hdr-compute-peak"] = "no",
            },
            QualityProfile.Cinema => new Dictionary<string, string>
            {
                ["scale"] = "ewa_lanczossharp", ["dscale"] = "mitchell", ["cscale"] = "ewa_lanczossharp", ["deband"] = "yes", ["dither-depth"] = "auto",
                ["correct-downscaling"] = "yes", ["linear-downscaling"] = "yes", ["sigmoid-upscaling"] = "yes", ["hdr-compute-peak"] = "yes",
            },
            _ => new Dictionary<string, string>
            {
                ["scale"] = "lanczos", ["dscale"] = "hermite", ["cscale"] = "lanczos", ["deband"] = "no", ["dither-depth"] = "auto",
                ["correct-downscaling"] = "yes", ["linear-downscaling"] = "yes", ["sigmoid-upscaling"] = "yes", ["hdr-compute-peak"] = "auto",
            },
        };
        foreach (var (k, v) in map) mpv.SetString(k, v);
    }

    private static readonly object LogLock = new();
    public static void Log(string line)
    {
        try
        {
            lock (LogLock)
                File.AppendAllText(Path.Combine(VyuSettings.Folder, "vyu.log"), $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settings.Save();
        _recents.Save();
        _mpv?.Dispose();
        _single?.Dispose();
        Updates?.OnExit(); // installs a scheduled or "now" update, restarting if asked
        base.OnExit(e);
    }
}

/// <summary>"Vyu.exe --snap &lt;folder&gt; &lt;video&gt;": renders the UI off-screen for the docs.</summary>
internal static class Snapshots
{
    public static async Task RunAsync(MainWindow main, ControlsWindow controls, MpvPlayer mpv, string outDir, string? file)
    {
        Directory.CreateDirectory(outDir);
        await Task.Delay(1500);
        Save(controls, Path.Combine(outDir, "vyu-home.png"));

        var sample = new UpdateInfo(new Version(1, 2, 0), "v1.2.0", "Vyu 1.2.0",
            "## What's new\n- **Make Vyu your default player** in one click\n- No more permission prompt when a video opens\n- Media keys and the Windows media flyout work like Media Player\n- Update pop-up: Later, Schedule or Update now",
            "", 113_000_000, null, "");
        var update = new UpdateWindow(null, sample, new Version(1, 1, 0), new VyuSettings(), _ => { })
            { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0 };
        update.Show();
        await Task.Delay(900);
        Save(update, Path.Combine(outDir, "vyu-update.png"));
        update.Close();
        if (file is null) return;

        // the native video surface can't be captured off-screen, so grab a real frame with the software renderer
        var grabber = new MpvThumbnailer(1600);
        var frameReady = new TaskCompletionSource<BitmapSource>();
        var thumbReady = new TaskCompletionSource<BitmapSource>();
        grabber.Ready += (t, bmp) => { if (t > 30) thumbReady.TrySetResult(bmp); else frameReady.TrySetResult(bmp); };
        grabber.SetFile(file);
        grabber.Request(12);
        var frame = await frameReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        controls.ShowSnapFrame(frame);

        controls.OpenPaths(new[] { file });
        await Task.Delay(2500);
        mpv.SetFlag("pause", true);
        mpv.Command("seek", "12", "absolute");
        await Task.Delay(1200);
        Save(controls, Path.Combine(outDir, "vyu-player.png"));

        grabber.Request(48);
        try { controls.SetPreviewImage(await thumbReady.Task.WaitAsync(TimeSpan.FromSeconds(10))); } catch { }
        controls.PrepareSnap("hover");
        await Task.Delay(900);
        grabber.Request(48);
        Save(controls, Path.Combine(outDir, "vyu-seek.png"));
        controls.PrepareSnap("tracks");
        await Task.Delay(900);
        Save(controls, Path.Combine(outDir, "vyu-tracks.png"));
        controls.PrepareSnap("tune");
        await Task.Delay(900);
        Save(controls, Path.Combine(outDir, "vyu-tune.png"));
        grabber.Dispose();
    }

    private static void Save(Window w, string path)
    {
        if (VisualTreeHelper.GetChildrenCount(w) == 0 || VisualTreeHelper.GetChild(w, 0) is not FrameworkElement root) return;
        double width = root.ActualWidth, height = root.ActualHeight;
        const double scale = 1.5;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var area = new Rect(0, 0, width, height);
            dc.DrawRectangle(Brushes.Black, null, area);
            dc.DrawRectangle(new VisualBrush(root) { Viewbox = area, ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill }, null, area);
        }
        var rtb = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
