using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Vyu.Kinetic;
using Vyu.Mpv;
using Vyu.Services;

namespace Vyu.Views;

/// <summary>
/// Everything you see on top of the video. It's a separate transparent window owned by the video window
/// (WPF can't draw over a native swap chain), kept glued to it. When the controls fade out it stops
/// redrawing entirely, so watching costs nothing beyond mpv itself.
/// </summary>
public partial class ControlsWindow : Window
{
    private readonly MainWindow _main;
    private readonly MpvPlayer _mpv;
    private readonly MpvThumbnailer _thumbs = new();
    private readonly VyuSettings _s;
    private readonly Recents _recents;
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _clickTimer;
    private IntPtr _mainHwnd;

    private bool _paused = true, _idle = true, _seeking, _overSeek, _showRemaining, _fullscreen, _pip, _controlsShown = true, _panelOpen, _pinned;
    private double _duration, _position, _hoverTime = -1;
    private string? _path;
    private string _title = "";
    private List<MpvChapter> _chapters = new();
    private Rect _restore;
    private DateTime _lastMove = DateTime.UtcNow, _lastSeekCmd = DateTime.MinValue, _lastSave = DateTime.MinValue;
    private Point _downPos;
    private bool _downArmed, _dragged, _volFromMpv;
    private long _playlistPos, _playlistCount;
    private int _rotate, _aspectIdx;
    private static readonly string[] Aspects = { "-1", "16:9", "4:3", "21:9", "1:1", "9:16" };

    public ControlsWindow(MainWindow main, MpvPlayer mpv, VyuSettings settings, Recents recents)
    {
        InitializeComponent();
        _main = main;
        _mpv = mpv;
        _s = settings;
        _recents = recents;
        Owner = main;

        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick(), Dispatcher);
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Min(260, System.Windows.Forms.SystemInformation.DoubleClickTime)) };
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (_s.ClickToPause && !_idle) TogglePause(); };

        _mpv.PropertyChanged += (n, v) => Dispatcher.BeginInvoke(() => OnProp(n, v));
        _mpv.FileLoaded += () => Dispatcher.BeginInvoke(OnFileLoaded);
        _mpv.EndFile += (reason, err) => Dispatcher.BeginInvoke(() => OnEndFile(reason, err));
        _mpv.PlaybackRestart += () => Dispatcher.BeginInvoke(() => Spinner.Visibility = Visibility.Collapsed);
        _thumbs.Ready += (t, bmp) => Dispatcher.BeginInvoke(() =>
        {
            if (_hoverTime >= 0 && (int)(_hoverTime / 2) == (int)(t / 2)) { PreviewImage.Source = bmp; }
        });

        main.LocationChanged += (_, _) => SyncBounds();
        main.SizeChanged += (_, _) => { SyncBounds(); UpdateCompact(); };
        main.StateChanged += (_, _) => { SyncBounds(); MaxBtn.Content = main.WindowState == WindowState.Maximized ? "" : ""; };
        main.Activated += (_, _) => { if (IsVisible && !_fullscreen) Activate(); };
        main.PreviewKeyDown += OnKey;
        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) => HookNative();
        Loaded += (_, _) => { SyncBounds(); UpdateCompact(); };

        VolSlider.Value = _s.Volume;
        SubSize.Value = _s.SubtitleScale;
        NightBox.IsChecked = _s.NightMode;
        HwBox.IsChecked = _s.HardwareDecoding;
        ResumeBox.IsChecked = _s.ResumePlayback;
        NextBox.IsChecked = _s.AutoPlayNext;
        ClickBox.IsChecked = _s.ClickToPause;
        PreviewBox.IsChecked = _s.SeekPreviews;
        AdminBox.IsChecked = _s.RunAsAdmin;
        (_s.Quality switch { QualityProfile.Fast => QFast, QualityProfile.Cinema => QCin, _ => QBal }).IsChecked = true;
        BuildSpeedChips();
        HelpText.Text = HelpLines;
        var hypt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hypt");
        if (Directory.Exists(hypt)) HyptBtn.Visibility = Visibility.Visible;

        RefreshHome();
        _tick.Start();
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever };
        SpinRot.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    // ================================================================== window glue

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("shell32.dll")] private static extern void DragAcceptFiles(IntPtr hwnd, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint index, StringBuilder? file, uint size);
    [DllImport("shell32.dll")] private static extern void DragFinish(IntPtr hDrop);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint msg, uint action, IntPtr info);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }

    private void HookNative()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _mainHwnd = new WindowInteropHelper(_main).Handle;
        // Drag & drop that also works when Vyu runs as administrator (OLE drag-drop is blocked across integrity levels).
        foreach (uint msg in new uint[] { 0x0233 /*WM_DROPFILES*/, 0x004A /*WM_COPYDATA*/, 0x0049 /*WM_COPYGLOBALDATA*/ })
        {
            ChangeWindowMessageFilterEx(hwnd, msg, 1, IntPtr.Zero);
            ChangeWindowMessageFilterEx(_mainHwnd, msg, 1, IntPtr.Zero);
        }
        DragAcceptFiles(hwnd, true);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0233)
        {
            var files = new List<string>();
            uint n = DragQueryFile(wParam, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < n; i++)
            {
                var sb = new StringBuilder(1024);
                DragQueryFile(wParam, i, sb, (uint)sb.Capacity);
                files.Add(sb.ToString());
            }
            DragFinish(wParam);
            handled = true;
            if (files.Count > 0) Dispatcher.BeginInvoke(() => OpenPaths(files));
        }
        return IntPtr.Zero;
    }

    /// <summary>Glue this window over the video window (inset by the resize border when it can be resized).</summary>
    public void SyncBounds()
    {
        if (_mainHwnd == IntPtr.Zero) _mainHwnd = new WindowInteropHelper(_main).Handle;
        if (_mainHwnd == IntPtr.Zero || !GetWindowRect(_mainHwnd, out var r)) return;
        var src = PresentationSource.FromVisual(_main);
        double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        double inset = _main.WindowState == WindowState.Maximized || _fullscreen ? 0 : 6;
        double maxInset = _main.WindowState == WindowState.Maximized ? 7 * sx : 0; // maximized borderless windows overhang
        Left = (r.L + maxInset) / sx + inset;
        Top = (r.T + maxInset) / sy + inset;
        Width = Math.Max(10, (r.R - r.L - 2 * maxInset) / sx - 2 * inset);
        Height = Math.Max(10, (r.B - r.T - 2 * maxInset) / sy - 2 * inset);
    }

    private void UpdateCompact()
    {
        double w = _main.ActualWidth;
        var compact = w < 700 ? Visibility.Collapsed : Visibility.Visible;
        VolSlider.Visibility = ChapterText.Visibility = SpeedBtn.Visibility = compact;
        TitleText.MaxWidth = Math.Max(120, w - 380);
        ChapterText.Visibility = w < 980 ? Visibility.Collapsed : Visibility.Visible;
        int cols = w < 760 ? 1 : w < 1100 ? 2 : 3;
        if (cols != _homeColumns)
        {
            _homeColumns = cols;
            var f = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
            f.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, cols);
            RecentList.ItemsPanel = new ItemsPanelTemplate(f);
        }
    }
    private int _homeColumns = 3;

    private void BeginWindowDrag()
    {
        if (_fullscreen || _main.WindowState == WindowState.Maximized) return;
        ReleaseCapture();
        SendMessage(_mainHwnd, 0x00A1 /*WM_NCLBUTTONDOWN*/, new IntPtr(2) /*HTCAPTION*/, IntPtr.Zero);
    }

    // ================================================================== mpv state

    private void OnProp(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                _paused = value is true;
                PlayGlyph.Text = _paused ? "" : "";
                PlayGlyph.Margin = _paused ? new Thickness(2, 0, 0, 0) : new Thickness(0);
                if (_paused) ShowControls();
                break;
            case "duration":
                _duration = value is double d ? d : 0;
                DrawChapters();
                break;
            case "volume":
                if (value is double vol)
                {
                    _volFromMpv = true;
                    VolSlider.Value = vol;
                    _volFromMpv = false;
                    UpdateVolumeGlyph();
                }
                break;
            case "mute":
                UpdateVolumeGlyph();
                break;
            case "speed":
                if (value is double sp) SpeedBtn.Content = $"{sp.ToString("0.##", CultureInfo.InvariantCulture)}×";
                break;
            case "media-title":
                _title = value as string ?? "";
                TitleText.Text = _title.Length > 0 ? _title : "Vyu";
                _main.Title = _title.Length > 0 ? $"{_title} — Vyu" : "Vyu";
                break;
            case "path":
                _path = value as string;
                bool local = _path is not null && File.Exists(_path);
                _thumbs.SetFile(local && _s.SeekPreviews ? _path : null);
                break;
            case "idle-active":
                _idle = value is true;
                Home.Visibility = _idle ? Visibility.Visible : Visibility.Collapsed;
                Bottom.Visibility = _idle ? Visibility.Collapsed : Visibility.Visible;
                if (_idle) { RefreshHome(); TitleText.Text = "Vyu"; MetaText.Text = ""; _main.Title = "Vyu"; ShowControls(); }
                break;
            case "track-list/count":
            case "video-params/w":
            case "video-params/primaries":
                UpdateMeta();
                if (_panelOpen && TracksView.Visibility == Visibility.Visible) BuildTracks();
                break;
            case "chapter-list/count":
                _chapters = _mpv.Chapters();
                DrawChapters();
                break;
            case "paused-for-cache":
                Spinner.Visibility = value is true ? Visibility.Visible : Visibility.Collapsed;
                break;
            case "ab-loop-a":
            case "ab-loop-b":
                DrawAB();
                break;
            case "playlist-pos":
                _playlistPos = value is long p ? p : 0;
                UpdatePrevNext();
                break;
            case "playlist-count":
                _playlistCount = value is long c ? c : 0;
                UpdatePrevNext();
                break;
        }
    }

    private void UpdatePrevNext()
    {
        PrevBtn.Opacity = _playlistPos > 0 || _position > 3 ? 1 : 0.35;
        NextBtn.Opacity = _playlistPos < _playlistCount - 1 ? 1 : 0.35;
        if (_panelOpen && ListView.Visibility == Visibility.Visible) BuildPlaylist();
    }

    private void UpdateMeta()
    {
        if (_idle) return;
        var parts = new List<string>();
        long w = _mpv.GetLong("video-params/w"), h = _mpv.GetLong("video-params/h");
        if (w > 0) parts.Add($"{w}×{h}");
        var vcodec = _mpv.GetString("video-codec-name") ?? _mpv.GetString("video-format");
        if (!string.IsNullOrEmpty(vcodec)) parts.Add(vcodec.ToUpperInvariant());
        var prim = _mpv.GetString("video-params/primaries");
        var gamma = _mpv.GetString("video-params/gamma");
        if (gamma is "pq" or "hlg" || prim == "bt.2020") parts.Add("HDR");
        var hw = _mpv.GetString("hwdec-current");
        if (!string.IsNullOrEmpty(hw) && hw != "no") parts.Add("GPU");
        var subs = _mpv.Tracks().Count(t => t.Type == "sub");
        if (subs > 0) parts.Add($"{subs} subtitle{(subs == 1 ? "" : "s")}");
        MetaText.Text = string.Join("  ·  ", parts);
    }

    private void OnFileLoaded()
    {
        Spinner.Visibility = Visibility.Collapsed;
        _duration = _mpv.GetDouble("duration");
        _chapters = _mpv.Chapters();
        DrawChapters();
        UpdateMeta();
        _main.FitToVideo(_mpv.GetLong("video-params/w"), _mpv.GetLong("video-params/h"));
        // resume when the file came from the playlist (direct opens pass start= instead)
        if (_s.ResumePlayback && _path is not null && _recents.Find(_path) is { } r && r.Progress is > 0.02 and < 0.97 && _mpv.GetDouble("time-pos") < 1)
        {
            _mpv.Command("seek", r.Position.ToString(CultureInfo.InvariantCulture), "absolute");
            ShowOsd("", $"Resumed at {TimeText.Format(r.Position)}");
        }
        ShowControls();
    }

    private void OnEndFile(int reason, string? error)
    {
        SaveProgress(force: true);
        if (reason == 4) ShowOsd("", "Can't play this file: " + (error ?? "unknown error"));
    }

    private void SaveProgress(bool force = false)
    {
        if (_path is null || _idle) return;
        if (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(5)) return;
        _lastSave = DateTime.UtcNow;
        double pos = _mpv.GetDouble("time-pos", _position);
        _recents.Touch(_path, _title.Length > 0 ? _title : Path.GetFileName(_path), pos, _duration);
        _recents.Save();
    }

    // ================================================================== tick

    private void Tick()
    {
        if (!_idle)
        {
            if (!_seeking) _position = _mpv.GetDouble("time-pos", _position);
            if (_controlsShown) UpdateSeekVisuals();
            SaveProgress();
        }
        bool overControls = Bottom.IsMouseOver || TopBar.IsMouseOver || Panel.IsMouseOver;
        if (_controlsShown && !_paused && !_idle && !_panelOpen && !_seeking && !overControls && UrlLayer.Visibility != Visibility.Visible
            && DateTime.UtcNow - _lastMove > TimeSpan.FromSeconds(2.4))
            HideControls();
        _tick.Interval = TimeSpan.FromMilliseconds(_controlsShown ? 100 : 500);
    }

    private void UpdateSeekVisuals()
    {
        double w = SeekTrackHost.ActualWidth;
        double f = _duration > 0 ? Math.Clamp(_position / _duration, 0, 1) : 0;
        SeekFill.Width = f * w;
        double cache = _mpv.GetDouble("demuxer-cache-time", 0);
        CacheFill.Width = _duration > 0 ? Math.Clamp(cache / _duration, 0, 1) * w : 0;
        Canvas.SetLeft(SeekKnob, f * w - 7);
        var cur = TimeText.Format(_position);
        TimeLabel.Text = _showRemaining ? $"{cur} / -{TimeText.Format(Math.Max(0, _duration - _position))}" : $"{cur} / {TimeText.Format(_duration)}";
        var chapter = _chapters.LastOrDefault(c => c.Time <= _position + 0.01);
        ChapterText.Text = chapter is null ? "" : chapter.Title;
    }

    private void ShowControls()
    {
        _lastMove = DateTime.UtcNow;
        Surface.Cursor = null;
        if (_controlsShown) return;
        _controlsShown = true;
        TopBar.Visibility = Visibility.Visible;
        if (!_idle) Bottom.Visibility = Visibility.Visible;
        UpdateSeekVisuals();
        Motion.Fade(TopBar, 1, 200);
        Motion.Fade(Bottom, 1, 200);
        var tt = Motion.EnsureTranslate(Bottom);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = Motion.Out });
    }

    private void HideControls()
    {
        if (!_controlsShown) return;
        _controlsShown = false;
        Motion.Fade(TopBar, 0, 380, () => { if (!_controlsShown) TopBar.Visibility = Visibility.Hidden; });
        Motion.Fade(Bottom, 0, 380, () => { if (!_controlsShown) Bottom.Visibility = Visibility.Hidden; });
        Motion.Fade(Preview, 0, 120);
        if (IsMouseOver) Surface.Cursor = Cursors.None;
    }

    // ================================================================== mouse on the video

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(Surface);
        if (_downArmed && e.LeftButton == MouseButtonState.Pressed && (Math.Abs(p.X - _downPos.X) > 4 || Math.Abs(p.Y - _downPos.Y) > 4))
        {
            _downArmed = false;
            _dragged = true;
            BeginWindowDrag();
            return;
        }
        if ((p - _lastMovePoint).Length > 1.5) { _lastMovePoint = p; ShowControls(); }
    }
    private Point _lastMovePoint;

    private void OnSurfaceLeave(object sender, MouseEventArgs e) => _lastMove = DateTime.UtcNow - TimeSpan.FromSeconds(1.5);

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not (Grid or Image or Border) || IsInside(e.OriginalSource, Bottom) || IsInside(e.OriginalSource, TopBar) || IsInside(e.OriginalSource, Panel))
            return;
        if (e.ClickCount == 2)
        {
            _clickTimer.Stop();
            _downArmed = false;
            if (!_idle) ToggleFullscreen();
            return;
        }
        _downPos = e.GetPosition(Surface);
        _downArmed = true;
        _dragged = false;
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_downArmed && !_dragged && !_idle && e.ClickCount == 1) _clickTimer.Start();
        _downArmed = false;
    }

    private static bool IsInside(object? source, DependencyObject container)
    {
        var d = source as DependencyObject;
        while (d is not null)
        {
            if (d == container) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_idle || Panel.IsMouseOver) return;
        ChangeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        void Add(string glyph, string text, string keys, Action a, bool enabled = true)
        {
            var mi = new MenuItem { Header = text, Icon = glyph, InputGestureText = keys, IsEnabled = enabled };
            mi.Click += (_, _) => a();
            menu.Items.Add(mi);
        }
        Add("", "Open file…", "Ctrl+O", OpenFileDialog);
        Add("", "Open link…", "Ctrl+U", ShowUrl);
        menu.Items.Add(new Separator());
        Add(_paused ? "" : "", _paused ? "Play" : "Pause", "Space", TogglePause, !_idle);
        Add("", "Subtitles & audio", "V / A", () => OpenPanel(TabTracks), !_idle);
        Add("", "Screenshot", "S", Screenshot, !_idle);
        Add("", "A-B loop", "B", () => { _mpv.Command("ab-loop"); }, !_idle);
        menu.Items.Add(new Separator());
        Add("", _pinned ? "Don't keep on top" : "Keep on top", "T", TogglePin);
        Add("", "Picture-in-picture", "P", TogglePip);
        Add("", _fullscreen ? "Exit full screen" : "Full screen", "F", ToggleFullscreen);
        Add("", "Picture, sound & settings", "", () => OpenPanel(TabPicture));
        Add("", "Keyboard shortcuts", "?", ShowHelp);
        menu.PlacementTarget = Surface;
        menu.IsOpen = true;
    }

    // ================================================================== seek bar

    private void OnSeekEnter(object sender, MouseEventArgs e)
    {
        _overSeek = true;
        if (_idle || _duration <= 0) return;
        SeekTrackHost.Height = 6;
        Motion.Fade(SeekKnob, 1, 120);
        KnobScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.Spring });
        KnobScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = Motion.Spring });
        Motion.Fade(HoverFill, 1, 120);
    }

    private void OnSeekLeave(object sender, MouseEventArgs e)
    {
        _overSeek = false;
        if (_seeking) return;
        SeekTrackHost.Height = 4;
        Motion.Fade(SeekKnob, 0, 160);
        KnobScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.4, TimeSpan.FromMilliseconds(160)));
        KnobScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.4, TimeSpan.FromMilliseconds(160)));
        Motion.Fade(HoverFill, 0, 160);
        Motion.Fade(Preview, 0, 140);
        _hoverTime = -1;
    }

    private double TimeAt(double x) => _duration <= 0 ? 0 : Math.Clamp(x / Math.Max(1, SeekTrackHost.ActualWidth), 0, 1) * _duration;

    private void OnSeekMove(object sender, MouseEventArgs e)
    {
        if (_idle || _duration <= 0) return;
        double x = e.GetPosition(SeekTrackHost).X;
        double t = TimeAt(x);
        HoverFill.Width = Math.Clamp(x, 0, SeekTrackHost.ActualWidth);
        ShowPreview(t, x);
        if (_seeking)
        {
            _position = t;
            UpdateSeekVisuals();
            if (DateTime.UtcNow - _lastSeekCmd > TimeSpan.FromMilliseconds(60))
            {
                _lastSeekCmd = DateTime.UtcNow;
                _mpv.CommandAsync("seek", t.ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes");
            }
        }
    }

    private void ShowPreview(double t, double x)
    {
        _hoverTime = t;
        PreviewTime.Text = TimeText.Format(t);
        var chapter = _chapters.LastOrDefault(c => c.Time <= t + 0.01);
        PreviewChapter.Text = chapter?.Title ?? "";
        PreviewChapter.Visibility = string.IsNullOrEmpty(chapter?.Title) ? Visibility.Collapsed : Visibility.Visible;
        bool thumbs = _s.SeekPreviews && _path is not null && File.Exists(_path);
        PreviewImageHost.Visibility = thumbs ? Visibility.Visible : Visibility.Collapsed;
        if (thumbs) _thumbs.Request(t);
        Preview.UpdateLayout();
        var origin = SeekTrackHost.TranslatePoint(new Point(0, 0), PreviewLayer);
        double pw = Preview.ActualWidth, ph = Preview.ActualHeight;
        Canvas.SetLeft(Preview, Math.Clamp(origin.X + x - pw / 2, 8, ActualWidth - pw - 8));
        Canvas.SetTop(Preview, origin.Y - ph - 14);
        if (Preview.Opacity < 1) Motion.Fade(Preview, 1, 120);
    }

    private void OnSeekDown(object sender, MouseButtonEventArgs e)
    {
        if (_idle || _duration <= 0) return;
        _seeking = true;
        SeekArea.CaptureMouse();
        double t = TimeAt(e.GetPosition(SeekTrackHost).X);
        _position = t;
        UpdateSeekVisuals();
        _mpv.CommandAsync("seek", t.ToString("0.###", CultureInfo.InvariantCulture), "absolute+keyframes");
        e.Handled = true;
    }

    private void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        SeekArea.ReleaseMouseCapture();
        double t = TimeAt(e.GetPosition(SeekTrackHost).X);
        _position = t;
        _mpv.Command("seek", t.ToString("0.###", CultureInfo.InvariantCulture), "absolute+exact");
        if (!_overSeek) OnSeekLeave(sender, e);
        e.Handled = true;
    }

    private void DrawChapters()
    {
        ChapterTicks.Children.Clear();
        double w = SeekTrackHost.ActualWidth;
        if (_duration <= 0 || w <= 0) return;
        foreach (var c in _chapters.Where(c => c.Time > 0.5))
        {
            var tick = new Rectangle { Width = 3, Height = 8, Fill = Brushes.Black, Opacity = 0.75 };
            Canvas.SetLeft(tick, c.Time / _duration * w - 1.5);
            ChapterTicks.Children.Add(tick);
        }
    }

    private void DrawAB()
    {
        ABMarks.Children.Clear();
        double w = SeekTrackHost.ActualWidth;
        if (_duration <= 0) return;
        foreach (var name in new[] { "ab-loop-a", "ab-loop-b" })
        {
            var v = _mpv.GetString(name);
            if (v is null || !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) continue;
            var mark = new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = (Brush)FindResource("K.WarningBrush") };
            Canvas.SetLeft(mark, t / _duration * w - 1.5);
            ABMarks.Children.Add(mark);
        }
    }

    // ================================================================== transport

    public void TogglePause()
    {
        if (_idle) return;
        _mpv.Command("cycle", "pause");
        Flash(_paused ? "" : "");
    }

    private void Flash(string glyph)
    {
        HudGlyph.Text = glyph;
        Hud.BeginAnimation(OpacityProperty, null);
        var st = Motion.EnsureScale(Hud);
        Hud.Opacity = 1;
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.7, 1.15, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Out });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.7, 1.15, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Out });
        Hud.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = TimeSpan.FromMilliseconds(120) });
    }

    private void SeekBy(double seconds)
    {
        if (_idle) return;
        _mpv.Command("seek", seconds.ToString(CultureInfo.InvariantCulture), "relative+exact");
        var hud = seconds < 0 ? SeekHudLeft : SeekHudRight;
        (seconds < 0 ? SeekHudLeftText : SeekHudRightText).Text = $"{(seconds > 0 ? "+" : "")}{seconds:0} s";
        hud.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = Motion.InOut });
        Motion.Pop(hud, 0.85, 400);
    }

    private void ChangeVolume(double delta)
    {
        double v = Math.Clamp(_mpv.GetDouble("volume", _s.Volume) + delta, 0, 150);
        _mpv.SetDouble("volume", v);
        if (_mpv.GetString("mute") == "yes") _mpv.SetFlag("mute", false);
        _s.Volume = v;
        VolHudFill.Height = Math.Max(4, v / 150 * 150);
        VolHudText.Text = $"{v:0}%";
        VolHud.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void UpdateVolumeGlyph()
    {
        bool mute = _mpv.GetString("mute") == "yes";
        double v = VolSlider.Value;
        VolBtn.Content = mute || v <= 0 ? "" : v < 34 ? "" : v < 67 ? "" : "";
    }

    private void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed * 100) / 100, 0.1, 8);
        _mpv.SetDouble("speed", speed);
        ShowOsd("", $"Speed {speed.ToString("0.##", CultureInfo.InvariantCulture)}×");
    }

    private void ShowOsd(string glyph, string text)
    {
        OsdGlyph.Text = glyph;
        OsdText.Text = text;
        Osd.BeginAnimation(OpacityProperty, null);
        Osd.Opacity = 1;
        Motion.Rise(Osd, 0, 8);
        Osd.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(1600) });
    }

    private void CycleTrack(string type)
    {
        var tracks = _mpv.Tracks().Where(t => t.Type == type).ToList();
        if (tracks.Count == 0) { ShowOsd("", type == "sub" ? "No subtitles in this file" : "No audio tracks"); return; }
        _mpv.Command("cycle", type == "sub" ? "sid" : "aid");
        var now = _mpv.Tracks().FirstOrDefault(t => t.Type == type && t.Selected);
        ShowOsd(type == "sub" ? "" : "", now is null ? (type == "sub" ? "Subtitles off" : "Audio off") : now.Label);
    }

    public void Screenshot()
    {
        if (_idle) return;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Vyu");
        Directory.CreateDirectory(dir);
        _mpv.SetString("screenshot-directory", dir);
        _mpv.Command("screenshot", "video");
        ShowOsd("", "Screenshot saved to Pictures\\Vyu");
    }

    // ================================================================== window modes

    public void ToggleFullscreen()
    {
        if (_pip) TogglePip();
        if (!_fullscreen)
        {
            _restore = new Rect(_main.Left, _main.Top, _main.Width, _main.Height);
            if (_main.WindowState == WindowState.Maximized) _main.WindowState = WindowState.Normal;
            var screen = System.Windows.Forms.Screen.FromHandle(_mainHwnd).Bounds;
            var src = PresentationSource.FromVisual(_main);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            _fullscreen = true;
            _main.ResizeMode = ResizeMode.NoResize;
            _main.Left = screen.Left / sx; _main.Top = screen.Top / sy;
            _main.Width = screen.Width / sx; _main.Height = screen.Height / sy;
            FullBtn.Content = "";
        }
        else
        {
            _fullscreen = false;
            _main.ResizeMode = ResizeMode.CanResize;
            _main.Left = _restore.Left; _main.Top = _restore.Top; _main.Width = _restore.Width; _main.Height = _restore.Height;
            FullBtn.Content = "";
        }
        SyncBounds();
        Activate();
    }

    public void TogglePip()
    {
        if (_fullscreen) ToggleFullscreen();
        if (!_pip)
        {
            _restore = new Rect(_main.Left, _main.Top, _main.Width, _main.Height);
            if (_main.WindowState == WindowState.Maximized) _main.WindowState = WindowState.Normal;
            var wa = SystemParameters.WorkArea;
            long vw = _mpv.GetLong("video-params/w", 16), vh = _mpv.GetLong("video-params/h", 9);
            double w = 480, h = vw > 0 ? Math.Round(w * vh / Math.Max(1, vw)) : 270;
            h = Math.Clamp(h, 200, 600);
            _pip = true;
            _main.Topmost = Topmost = true;
            _main.Width = w; _main.Height = h;
            _main.Left = wa.Right - w - 24; _main.Top = wa.Bottom - h - 24;
            ShowOsd("", "Picture-in-picture · P to go back");
        }
        else
        {
            _pip = false;
            _main.Topmost = Topmost = _pinned;
            _main.Left = _restore.Left; _main.Top = _restore.Top; _main.Width = _restore.Width; _main.Height = _restore.Height;
        }
        SyncBounds();
    }

    private void TogglePin()
    {
        _pinned = !_pinned;
        _main.Topmost = Topmost = _pinned || _pip;
        PinBtn.Foreground = _pinned ? (Brush)FindResource("K.Accent1Brush") : (Brush)FindResource("K.Text2Brush");
        ShowOsd("", _pinned ? "Always on top" : "Not on top");
    }

    // ================================================================== opening things

    public void HandleArgs(IEnumerable<string> args)
    {
        var list = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (args.Contains("--show") || list.Count == 0)
        {
            if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
            _main.Activate();
            if (list.Count == 0) return;
        }
        OpenPaths(list);
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void OpenPaths(IList<string> paths)
    {
        if (paths.Count == 0) return;
        var subExt = new[] { ".srt", ".ass", ".ssa", ".vtt", ".sub", ".sup" };
        if (paths.Count == 1 && subExt.Contains(Path.GetExtension(paths[0]).ToLowerInvariant()) && !_idle)
        {
            _mpv.Command("sub-add", paths[0], "select");
            ShowOsd("", "Subtitle loaded");
            return;
        }
        var expanded = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p)) expanded.AddRange(MediaFilesIn(p));
            else expanded.Add(p);
        }
        if (expanded.Count == 0) { ShowOsd("", "No videos in that folder"); return; }
        if (expanded.Count == 1) OpenWithFolder(expanded[0]);
        else
        {
            Play(expanded[0]);
            foreach (var p in expanded.Skip(1)) _mpv.Append(p);
        }
    }

    private static IEnumerable<string> MediaFilesIn(string dir)
    {
        var ext = new HashSet<string>(FileAssociations.VideoExtensions.Concat(FileAssociations.AudioExtensions), StringComparer.OrdinalIgnoreCase);
        try
        {
            return Directory.EnumerateFiles(dir).Where(f => ext.Contains(Path.GetExtension(f))).OrderBy(f => f, NaturalComparer.Instance).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Plays a file and queues its neighbours, so "next" just works like in a TV show folder.</summary>
    private void OpenWithFolder(string file)
    {
        Play(file);
        if (!_s.AutoPlayNext || file.Contains("://") || !File.Exists(file)) return;
        var siblings = MediaFilesIn(Path.GetDirectoryName(file)!).ToList();
        int idx = siblings.FindIndex(s => string.Equals(s, file, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || siblings.Count > 500) return;
        for (int i = 0; i < idx; i++)
        {
            _mpv.Append(siblings[i]);
            _mpv.Command("playlist-move", (i + 1).ToString(CultureInfo.InvariantCulture), i.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var s in siblings.Skip(idx + 1)) _mpv.Append(s);
    }

    private void Play(string pathOrUrl)
    {
        SaveProgress(force: true);
        double start = 0;
        if (_s.ResumePlayback && _recents.Find(pathOrUrl) is { } r && r.Progress is > 0.02 and < 0.97) start = r.Position;
        Spinner.Visibility = Visibility.Visible;
        _mpv.Load(pathOrUrl, start);
        if (start > 1) ShowOsd("", $"Resumed at {TimeText.Format(start)} · Home to restart");
    }

    private void OpenFileDialog()
    {
        var exts = string.Join(";", FileAssociations.VideoExtensions.Concat(FileAssociations.AudioExtensions).Select(e => "*" + e));
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open with Vyu", Multiselect = true,
            Filter = $"Video & audio|{exts}|Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.sup|All files|*.*",
        };
        if (dlg.ShowDialog(this) == true) OpenPaths(dlg.FileNames);
    }

    private void ShowUrl()
    {
        UrlLayer.Visibility = Visibility.Visible;
        Motion.Fade(UrlLayer, 1, 180);
        try { if (Clipboard.ContainsText() && Clipboard.GetText().Trim() is var c && c.Contains("://")) UrlBox.Text = c; } catch { }
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void RefreshHome()
    {
        var items = _recents.Items.Take(9).ToList();
        RecentList.ItemsSource = items;
        RecentHead.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== side panel

    private void OpenPanel(RadioButton tab)
    {
        _panelOpen = true;
        if (tab.IsChecked == true) OnTab(tab, new RoutedEventArgs()); else tab.IsChecked = true;
        if (Panel.Visibility != Visibility.Visible)
        {
            Panel.Visibility = Visibility.Visible;
            Motion.SlideInX(Panel, 60, 420);
        }
        ShowControls();
    }

    private void ClosePanel()
    {
        if (!_panelOpen) return;
        _panelOpen = false;
        Motion.SlideOutX(Panel, 50, 200, () => { if (!_panelOpen) Panel.Visibility = Visibility.Collapsed; });
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        TracksView.Visibility = sender == TabTracks ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = sender == TabList ? Visibility.Visible : Visibility.Collapsed;
        TuneView.Visibility = sender == TabPicture ? Visibility.Visible : Visibility.Collapsed;
        InfoView.Visibility = sender == TabInfo ? Visibility.Visible : Visibility.Collapsed;
        if (sender == TabTracks) BuildTracks();
        if (sender == TabList) BuildPlaylist();
        if (sender == TabInfo) BuildInfo();
        if (sender == TabPicture) SyncTune();
    }

    private Button ListButton(string text, string? sub, bool selected, Action click, string? glyph = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var check = new TextBlock
        {
            Text = selected ? "" : glyph ?? "", FontFamily = (FontFamily)FindResource("K.Icons"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
            Foreground = selected ? (Brush)FindResource("K.Accent1Brush") : (Brush)FindResource("K.Text3Brush"),
        };
        grid.Children.Add(check);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, FontSize = 13.5 });
        if (!string.IsNullOrEmpty(sub)) stack.Children.Add(new TextBlock { Text = sub, Style = (Style)FindResource("K.Caption"), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        var b = new Button { Style = (Style)FindResource("ListItem"), Content = grid };
        if (selected) b.Background = (Brush)FindResource("K.AccentSoftBrush");
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildTracks()
    {
        var tracks = _mpv.Tracks();
        SubList.Children.Clear();
        bool anySub = tracks.Any(t => t.Type == "sub" && t.Selected);
        SubList.Children.Add(ListButton("Off", null, !anySub, () => { _mpv.SetString("sid", "no"); BuildTracks(); }));
        foreach (var t in tracks.Where(t => t.Type == "sub"))
            SubList.Children.Add(ListButton(t.Label, null, t.Selected, () => { _mpv.SetString("sid", t.Id.ToString(CultureInfo.InvariantCulture)); BuildTracks(); }));
        AudioList.Children.Clear();
        foreach (var t in tracks.Where(t => t.Type == "audio"))
            AudioList.Children.Add(ListButton(t.Label, null, t.Selected, () => { _mpv.SetString("aid", t.Id.ToString(CultureInfo.InvariantCulture)); BuildTracks(); }));
        if (!tracks.Any(t => t.Type == "audio")) AudioList.Children.Add(new TextBlock { Text = "No audio in this file", Style = (Style)FindResource("K.Caption") });
        _syncingSliders = true;
        SubDelay.Value = _mpv.GetDouble("sub-delay");
        AudioDelay.Value = _mpv.GetDouble("audio-delay");
        _syncingSliders = false;
        SubDelayText.Text = $"{SubDelay.Value:+0.0;-0.0;0} s";
        AudioDelayText.Text = $"{AudioDelay.Value:+0.00;-0.00;0} s";
        SubSizeText.Text = $"{SubSize.Value:0.0}×";
    }

    private bool _syncingSliders;

    private void BuildPlaylist()
    {
        PlaylistItems.Children.Clear();
        var json = _mpv.GetString("playlist");
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            int i = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                int index = i++;
                var file = e.GetProperty("filename").GetString() ?? "";
                bool current = e.TryGetProperty("current", out var c) && c.GetBoolean();
                var title = e.TryGetProperty("title", out var t) ? t.GetString() : null;
                var resume = _recents.Find(file);
                var sub = resume is { Duration: > 0 } ? resume.Subtitle : null;
                PlaylistItems.Children.Add(ListButton(title ?? Path.GetFileName(file), sub, current,
                    () => _mpv.Command("playlist-play-index", index.ToString(CultureInfo.InvariantCulture)), ""));
            }
            ListHead.Text = $"PLAYLIST · {i} ITEM{(i == 1 ? "" : "S")}";
        }
        catch { }
    }

    private void BuildInfo()
    {
        if (_idle) { InfoText.Text = "Nothing playing."; return; }
        string G(string p) => _mpv.GetString(p) ?? "—";
        var sb = new StringBuilder();
        sb.AppendLine($"File      {Path.GetFileName(_path ?? "")}");
        sb.AppendLine($"Duration  {TimeText.Format(_duration)}");
        sb.AppendLine($"Video     {G("video-codec-name")} {G("video-params/w")}×{G("video-params/h")} @ {_mpv.GetDouble("container-fps"):0.###} fps");
        sb.AppendLine($"Pixel     {G("video-params/pixelformat")} · {G("video-params/colormatrix")} · {G("video-params/primaries")}/{G("video-params/gamma")}");
        sb.AppendLine($"Decoder   {(G("hwdec-current") is var hw && hw != "no" && hw != "—" ? "GPU (" + hw + ")" : "CPU")}");
        sb.AppendLine($"Renderer  {G("current-vo")} · {G("current-gpu-context")}");
        sb.AppendLine($"Audio     {G("audio-codec-name")} · {G("audio-params/samplerate")} Hz · {G("audio-params/hr-channels")}");
        long bitrate = _mpv.GetLong("video-bitrate");
        if (bitrate > 0) sb.AppendLine($"Bitrate   {bitrate / 1000.0:0} kbps video");
        long size = _mpv.GetLong("file-size");
        if (size > 0) sb.AppendLine($"Size      {size / 1048576.0:0.0} MB");
        sb.Append($"Dropped   {G("frame-drop-count")} frames");
        InfoText.Text = sb.ToString();
    }

    private void BuildSpeedChips()
    {
        foreach (var sp in new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2, 3 })
        {
            var b = new Button { Style = (Style)FindResource("K.Button"), Height = 30, Padding = new Thickness(10, 0, 10, 0), FontSize = 12, Margin = new Thickness(0, 0, 6, 6), Content = $"{sp.ToString("0.##", CultureInfo.InvariantCulture)}×" };
            b.Click += (_, _) => SetSpeed(sp);
            SpeedChips.Children.Add(b);
        }
    }

    private void SyncTune()
    {
        _syncingSliders = true;
        Bri.Value = _mpv.GetDouble("brightness"); Con.Value = _mpv.GetDouble("contrast");
        Sat.Value = _mpv.GetDouble("saturation"); Gam.Value = _mpv.GetDouble("gamma");
        _syncingSliders = false;
        BriText.Text = $"{Bri.Value:0}"; ConText.Text = $"{Con.Value:0}"; SatText.Text = $"{Sat.Value:0}"; GamText.Text = $"{Gam.Value:0}";
    }

    // ================================================================== keyboard

    private const string HelpLines =
        "Space / K        play · pause            F / Enter      full screen\n" +
        "← →              seek 5 s (Shift 1 s)    J / L          back / forward 10 s\n" +
        "Ctrl ← →         seek 60 s               Home / End     start / end\n" +
        "↑ ↓ / wheel      volume                  M              mute\n" +
        "[ ]              slower / faster         Backspace      normal speed\n" +
        ", .              frame back / forward    0 – 9          jump to 0–90 %\n" +
        "V / A            subtitles / audio       B              A-B loop\n" +
        "S                screenshot              I              info\n" +
        "N / Shift+N      next / previous file    Q              playlist\n" +
        "P                picture-in-picture      T              keep on top\n" +
        "Ctrl+O / Ctrl+U  open file / link        Ctrl+V         paste file or link\n" +
        "Esc              leave full screen       ?              this list";

    private void ShowHelp()
    {
        HelpLayer.Visibility = Visibility.Visible;
        Motion.Fade(HelpLayer, 1, 160);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        if (ctrl && key is Key.O or Key.U or Key.W or Key.Q or Key.V)
        {
            switch (key)
            {
                case Key.O: OpenFileDialog(); break;
                case Key.U: ShowUrl(); break;
                case Key.W: _mpv.Command("stop"); break;
                case Key.Q: Application.Current.Shutdown(); break;
                case Key.V: PasteFromClipboard(); break;
            }
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Space: case Key.K: case Key.MediaPlayPause: TogglePause(); break;
            case Key.Left: SeekBy(ctrl ? -60 : shift ? -1 : -_s.SeekStep); break;
            case Key.Right: SeekBy(ctrl ? 60 : shift ? 1 : _s.SeekStep); break;
            case Key.J: SeekBy(-10); break;
            case Key.L: SeekBy(10); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M: case Key.VolumeMute: _mpv.Command("cycle", "mute"); ShowOsd("", _mpv.GetString("mute") == "yes" ? "Muted" : "Sound on"); break;
            case Key.F: case Key.Enter: ToggleFullscreen(); break;
            case Key.Escape:
                if (HelpLayer.Visibility == Visibility.Visible) HelpLayer.Visibility = Visibility.Collapsed;
                else if (UrlLayer.Visibility == Visibility.Visible) UrlLayer.Visibility = Visibility.Collapsed;
                else if (_panelOpen) ClosePanel();
                else if (_fullscreen) ToggleFullscreen();
                else if (_pip) TogglePip();
                break;
            case Key.OemOpenBrackets: SetSpeed(_mpv.GetDouble("speed", 1) - 0.25); break;
            case Key.OemCloseBrackets: SetSpeed(_mpv.GetDouble("speed", 1) + 0.25); break;
            case Key.Back: SetSpeed(1); break;
            case Key.OemComma: _mpv.Command("frame-back-step"); ShowOsd("", "Frame back"); break;
            case Key.OemPeriod: _mpv.Command("frame-step"); ShowOsd("", "Frame forward"); break;
            case Key.V: CycleTrack("sub"); break;
            case Key.A: CycleTrack("audio"); break;
            case Key.B:
                _mpv.Command("ab-loop");
                ShowOsd("", _mpv.GetString("ab-loop-b") is "no" or null ? (_mpv.GetString("ab-loop-a") is "no" or null ? "A-B loop cleared" : "Loop start set — press B again") : "Looping A → B");
                break;
            case Key.S when !ctrl: Screenshot(); break;
            case Key.I: OpenPanel(TabInfo); break;
            case Key.Q: OpenPanel(TabList); break;
            case Key.N when shift: case Key.PageUp: case Key.MediaPreviousTrack: _mpv.Command("playlist-prev"); break;
            case Key.N: case Key.PageDown: case Key.MediaNextTrack: _mpv.Command("playlist-next"); break;
            case Key.P: TogglePip(); break;
            case Key.T: TogglePin(); break;
            case Key.Home: _mpv.Command("seek", "0", "absolute"); break;
            case Key.End: _mpv.Command("seek", "100", "absolute-percent"); break;

            case Key.OemQuestion: ShowHelp(); break;
            case >= Key.D0 and <= Key.D9 when !ctrl:
                _mpv.Command("seek", ((key - Key.D0) * 10).ToString(CultureInfo.InvariantCulture), "absolute-percent");
                break;
            default: handled = false; break;
        }
        if (handled) { e.Handled = true; ShowControls(); }
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) { OpenPaths(Clipboard.GetFileDropList().Cast<string>().ToList()); return; }
            var text = Clipboard.GetText().Trim().Trim('"');
            if (text.Contains("://") || File.Exists(text) || Directory.Exists(text)) OpenPaths(new[] { text });
        }
        catch { }
    }

    // ================================================================== button handlers

    private void OnPlayPause(object sender, RoutedEventArgs e) => TogglePause();
    private void OnPrev(object sender, RoutedEventArgs e)
    {
        if (_position > 3 || _playlistPos == 0) _mpv.Command("seek", "0", "absolute"); else _mpv.Command("playlist-prev");
    }
    private void OnNext(object sender, RoutedEventArgs e) => _mpv.Command("playlist-next");
    private void OnMuteClick(object sender, RoutedEventArgs e) => _mpv.Command("cycle", "mute");
    private void OnVolumeSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volFromMpv || !IsLoaded) return;
        _mpv.SetDouble("volume", e.NewValue);
        _s.Volume = e.NewValue;
        UpdateVolumeGlyph();
    }
    private void OnTimeClick(object sender, MouseButtonEventArgs e) { _showRemaining = !_showRemaining; UpdateSeekVisuals(); }
    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        double sp = _mpv.GetDouble("speed", 1);
        double[] steps = { 1, 1.25, 1.5, 2, 0.5, 0.75 };
        int i = Array.FindIndex(steps, s => Math.Abs(s - sp) < 0.01);
        SetSpeed(steps[(i + 1) % steps.Length]);
    }
    private void OnTracks(object sender, RoutedEventArgs e) { if (_panelOpen && TabTracks.IsChecked == true) ClosePanel(); else OpenPanel(TabTracks); }
    private void OnPlaylist(object sender, RoutedEventArgs e) { if (_panelOpen && TabList.IsChecked == true) ClosePanel(); else OpenPanel(TabList); }
    private void OnSettings(object sender, RoutedEventArgs e) { if (_panelOpen && TabPicture.IsChecked == true) ClosePanel(); else OpenPanel(TabPicture); }
    private void OnClosePanel(object sender, RoutedEventArgs e) => ClosePanel();
    private void OnFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnPin(object sender, RoutedEventArgs e) => TogglePin();
    private void OnPip(object sender, RoutedEventArgs e) => TogglePip();
    private void OnMinimize(object sender, RoutedEventArgs e) => _main.WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        _main.WindowState = _main.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void OnClose(object sender, RoutedEventArgs e) => _main.Close();
    private void OnPanelDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void OnPanelWheel(object sender, MouseWheelEventArgs e) { }

    private void OnOpenFile(object sender, RoutedEventArgs e) => OpenFileDialog();
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Play every video in a folder", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) OpenPaths(new[] { dlg.SelectedPath });
    }
    private void OnOpenUrl(object sender, RoutedEventArgs e) => ShowUrl();
    /// <summary>Plays every Hypt clip, newest first (Hypt is Vyu's sibling clip recorder).</summary>
    private void OnHyptClips(object sender, RoutedEventArgs e)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Hypt");
        List<string> clips;
        try
        {
            clips = Directory.EnumerateFiles(root, "*.mp4", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith('.'))
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(200).ToList();
        }
        catch { clips = new List<string>(); }
        if (clips.Count == 0) { ShowOsd("", "No Hypt clips yet"); return; }
        OpenPaths(clips);
    }

    private void OnRecent(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecentItem r }) OpenWithFolder(r.Path);
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RecentItem r }) { _recents.Remove(r.Path); _recents.Save(); RefreshHome(); }
        e.Handled = true;
    }

    private void OnUrlKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnUrlPlay(sender, e);
        if (e.Key == Key.Escape) OnUrlCancel(sender, e);
    }
    private void OnUrlPlay(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        UrlLayer.Visibility = Visibility.Collapsed;
        if (url.Length > 0) OpenPaths(new[] { url });
    }
    private void OnUrlCancel(object sender, RoutedEventArgs e) => UrlLayer.Visibility = Visibility.Collapsed;
    private void OnUrlScrim(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == UrlLayer) UrlLayer.Visibility = Visibility.Collapsed; }
    private void OnHelpClose(object sender, MouseButtonEventArgs e) => HelpLayer.Visibility = Visibility.Collapsed;

    private void OnLoadSub(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Load subtitles", Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.sup;*.idx|All files|*.*" };
        if (_path is not null && File.Exists(_path)) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) == true) { _mpv.Command("sub-add", dlg.FileName, "select"); BuildTracks(); }
    }
    private void OnSubDelay(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSliders || !IsLoaded) return;
        _mpv.SetDouble("sub-delay", Math.Round(e.NewValue, 1));
        SubDelayText.Text = $"{e.NewValue:+0.0;-0.0;0} s";
    }
    private void OnAudioDelay(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSliders || !IsLoaded) return;
        _mpv.SetDouble("audio-delay", Math.Round(e.NewValue, 2));
        AudioDelayText.Text = $"{e.NewValue:+0.00;-0.00;0} s";
    }
    private void OnSubSize(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _mpv.SetDouble("sub-scale", e.NewValue);
        _s.SubtitleScale = e.NewValue;
        SubSizeText.Text = $"{e.NewValue:0.0}×";
    }
    private void OnEq(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSliders || !IsLoaded || sender is not Slider { Tag: string prop }) return;
        _mpv.SetDouble(prop, Math.Round(e.NewValue));
        SyncTune();
    }
    private void OnAspect(object sender, RoutedEventArgs e)
    {
        _aspectIdx = (_aspectIdx + 1) % Aspects.Length;
        _mpv.SetString("video-aspect-override", Aspects[_aspectIdx]);
        ShowOsd("", _aspectIdx == 0 ? "Aspect: original" : $"Aspect: {Aspects[_aspectIdx]}");
    }
    private void OnRotate(object sender, RoutedEventArgs e)
    {
        _rotate = (_rotate + 90) % 360;
        _mpv.SetString("video-rotate", _rotate.ToString(CultureInfo.InvariantCulture));
        ShowOsd("", $"Rotated {_rotate}°");
    }
    private void OnZoomFill(object sender, RoutedEventArgs e)
    {
        bool on = _mpv.GetString("panscan") is "0.000000" or null;
        _mpv.SetDouble("panscan", on ? 1 : 0);
        ShowOsd("", on ? "Zoomed to fill" : "Fit to window");
    }
    private void OnResetPicture(object sender, RoutedEventArgs e)
    {
        foreach (var p in new[] { "brightness", "contrast", "saturation", "gamma", "panscan" }) _mpv.SetDouble(p, 0);
        _mpv.SetString("video-aspect-override", "-1");
        _mpv.SetString("video-rotate", "0");
        _rotate = 0; _aspectIdx = 0;
        SyncTune();
    }
    private void OnNight(object sender, RoutedEventArgs e)
    {
        _s.NightMode = NightBox.IsChecked == true;
        _mpv.Command("af", _s.NightMode ? "add" : "remove", "@night:lavfi=[dynaudnorm=f=250:g=15:p=0.7]");
        ShowOsd("", _s.NightMode ? "Night mode on" : "Night mode off");
    }
    private void OnQuality(object sender, RoutedEventArgs e)
    {
        _s.Quality = QFast.IsChecked == true ? QualityProfile.Fast : QCin.IsChecked == true ? QualityProfile.Cinema : QualityProfile.Balanced;
        App.ApplyQuality(_mpv, _s.Quality);
        ShowOsd("", $"Picture: {(_s.Quality == QualityProfile.Fast ? "battery saver" : _s.Quality == QualityProfile.Cinema ? "cinema quality" : "balanced")}");
    }
    private void OnHw(object sender, RoutedEventArgs e)
    {
        _s.HardwareDecoding = HwBox.IsChecked == true;
        _mpv.SetString("hwdec", _s.HardwareDecoding ? "auto-safe" : "no");
    }
    private void OnPrefs(object sender, RoutedEventArgs e)
    {
        _s.ResumePlayback = ResumeBox.IsChecked == true;
        _s.AutoPlayNext = NextBox.IsChecked == true;
        _s.ClickToPause = ClickBox.IsChecked == true;
        _s.SeekPreviews = PreviewBox.IsChecked == true;
        _s.RunAsAdmin = AdminBox.IsChecked == true;
        _mpv.SetString("keep-open", _s.AutoPlayNext ? "yes" : "always");
        _s.Save();
    }
    private void OnAssociate(object sender, RoutedEventArgs e)
    {
        try
        {
            FileAssociations.Register();
            ShowOsd("", "Vyu registered — pick it in Default apps");
            FileAssociations.OpenDefaultAppsSettings();
        }
        catch (Exception ex) { ShowOsd("", "Couldn't register: " + ex.Message); }
    }
    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Add to playlist" };
        if (dlg.ShowDialog(this) == true) { foreach (var f in dlg.FileNames) _mpv.Append(f); BuildPlaylist(); }
    }
    private void OnShuffle(object sender, RoutedEventArgs e)
    {
        _mpv.Command(ShuffleBox.IsChecked == true ? "playlist-shuffle" : "playlist-unshuffle");
        BuildPlaylist();
    }
    private void OnScreenshot(object sender, RoutedEventArgs e) => Screenshot();
    private void OnShowInFolder(object sender, RoutedEventArgs e)
    {
        if (_path is not null && File.Exists(_path)) Process.Start("explorer.exe", $"/select,\"{_path}\"");
    }

    // ================================================================== snapshots (docs)

    public void ShowSnapFrame(ImageSource frame)
    {
        SnapFrame.Source = frame;
        SnapFrame.Visibility = Visibility.Visible;
    }

    public void PrepareSnap(string state)
    {
        switch (state)
        {
            case "hover":
                ShowControls();
                double x = SeekTrackHost.ActualWidth * 0.62;
                HoverFill.Width = x; HoverFill.Opacity = 1; SeekKnob.Opacity = 1; SeekTrackHost.Height = 6;
                KnobScale.ScaleX = KnobScale.ScaleY = 1;
                ShowPreview(TimeAt(x), x);
                break;
            case "tracks": OpenPanel(TabTracks); break;
            case "tune": OpenPanel(TabPicture); break;
            case "closed": ClosePanel(); Motion.Fade(Preview, 0, 1); break;
        }
    }

    public void SetPreviewImage(ImageSource img) => PreviewImage.Source = img;

    protected override void OnClosed(EventArgs e)
    {
        SaveProgress(force: true);
        _thumbs.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>"Episode 2" sorts before "Episode 10".</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int StrCmpLogicalW(string a, string b);
    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
}
