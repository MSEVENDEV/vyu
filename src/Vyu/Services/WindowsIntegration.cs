using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using Windows.Media;

namespace Vyu.Services;

/// <summary>
/// Windows' own media controls, like Windows Media Player has: the media card in the volume / quick-settings
/// flyout (with a seek bar), keyboard media keys even while another window is in front, and the lock screen.
/// </summary>
public sealed class MediaControls
{
    private readonly SystemMediaTransportControls _smtc;
    private DateTime _lastTimeline = DateTime.MinValue;
    private string _title = "";

    /// <summary>"play", "pause", "next", "previous" or "stop" — raised on a background thread.</summary>
    public event Action<string>? Pressed;
    /// <summary>Seconds to seek to, from the flyout's seek bar — raised on a background thread.</summary>
    public event Action<double>? SeekRequested;

    public static MediaControls? TryCreate(IntPtr hwnd)
    {
        try { return new MediaControls(SystemMediaTransportControlsInterop.GetForWindow(hwnd)); }
        catch (Exception ex) { App.Log("Media controls unavailable: " + ex.Message); return null; }
    }

    private MediaControls(SystemMediaTransportControls smtc)
    {
        _smtc = smtc;
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = smtc.IsPauseEnabled = smtc.IsStopEnabled = smtc.IsNextEnabled = smtc.IsPreviousEnabled = true;
        smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        smtc.ButtonPressed += (_, e) => Pressed?.Invoke(e.Button switch
        {
            SystemMediaTransportControlsButton.Play => "play",
            SystemMediaTransportControlsButton.Pause => "pause",
            SystemMediaTransportControlsButton.Next => "next",
            SystemMediaTransportControlsButton.Previous => "previous",
            SystemMediaTransportControlsButton.Stop => "stop",
            _ => "",
        });
        smtc.PlaybackPositionChangeRequested += (_, e) => SeekRequested?.Invoke(e.RequestedPlaybackPosition.TotalSeconds);
    }

    public void SetMedia(string title, bool audioOnly)
    {
        if (title == _title) return;
        _title = title;
        try
        {
            var d = _smtc.DisplayUpdater;
            d.ClearAll();
            d.AppMediaId = "Vyu";
            d.Type = audioOnly ? MediaPlaybackType.Music : MediaPlaybackType.Video;
            if (audioOnly) d.MusicProperties.Title = title; else d.VideoProperties.Title = title;
            d.Update();
        }
        catch (Exception ex) { App.Log("Media controls: " + ex.Message); }
    }

    public void SetState(bool idle, bool paused)
    {
        try { _smtc.PlaybackStatus = idle ? MediaPlaybackStatus.Closed : paused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing; }
        catch { }
        if (idle) _title = "";
    }

    /// <summary>Position for the flyout's seek bar (at most once a second).</summary>
    public void SetTimeline(double position, double duration)
    {
        if (duration <= 0 || DateTime.UtcNow - _lastTimeline < TimeSpan.FromSeconds(1)) return;
        _lastTimeline = DateTime.UtcNow;
        try
        {
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero,
                EndTime = TimeSpan.FromSeconds(duration), MaxSeekTime = TimeSpan.FromSeconds(duration),
                Position = TimeSpan.FromSeconds(Math.Clamp(position, 0, duration)),
            });
        }
        catch { }
    }
}

/// <summary>Previous / play-pause / next under the taskbar thumbnail, like Windows Media Player.</summary>
public sealed class TaskbarButtons
{
    private readonly ThumbButtonInfo _prev, _play, _next;
    private readonly ImageSource _playIcon = Glyph(""), _pauseIcon = Glyph("");

    public TaskbarButtons(Window window, Action prev, Action playPause, Action next)
    {
        _prev = new ThumbButtonInfo { ImageSource = Glyph(""), Description = "Previous" };
        _play = new ThumbButtonInfo { ImageSource = _playIcon, Description = "Play" };
        _next = new ThumbButtonInfo { ImageSource = Glyph(""), Description = "Next" };
        _prev.Click += (_, _) => prev();
        _play.Click += (_, _) => playPause();
        _next.Click += (_, _) => next();
        window.TaskbarItemInfo ??= new TaskbarItemInfo();
        window.TaskbarItemInfo.ThumbButtonInfos = new ThumbButtonInfoCollection { _prev, _play, _next };
        SetPlaying(false);
    }

    public void SetPlaying(bool playing)
    {
        _play.ImageSource = playing ? _pauseIcon : _playIcon;
        _play.Description = playing ? "Pause" : "Play";
    }

    public void SetEnabled(bool hasMedia, bool hasPrev, bool hasNext)
    {
        _play.IsEnabled = hasMedia;
        _prev.IsEnabled = hasMedia && hasPrev;
        _next.IsEnabled = hasMedia && hasNext;
    }

    /// <summary>A white Segoe Fluent / MDL2 icon as an image (thumbnail buttons need bitmaps, not text).</summary>
    private static ImageSource Glyph(string glyph)
    {
        var family = Fonts.SystemFontFamilies.FirstOrDefault(f => f.Source == "Segoe Fluent Icons") ?? new FontFamily("Segoe MDL2 Assets");
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16, Brushes.White, 1.0);
        var geo = text.BuildGeometry(new Point(0, 0));
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        drawing.Children.Add(new GeometryDrawing(Brushes.White, null, geo));
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}

/// <summary>Right-click Vyu on the taskbar: the videos you were watching.</summary>
public static class TaskbarJumpList
{
    public static void Update(IEnumerable<(string Path, string Title)> recent)
    {
        try
        {
            var exe = Environment.ProcessPath!;
            var list = new JumpList { ShowRecentCategory = false, ShowFrequentCategory = false };
            foreach (var (path, title) in recent.Where(r => File.Exists(r.Path)).Take(8))
            {
                list.JumpItems.Add(new JumpTask
                {
                    Title = title, Description = path, CustomCategory = "Continue watching",
                    ApplicationPath = exe, Arguments = $"\"{path}\"", IconResourcePath = exe, WorkingDirectory = Path.GetDirectoryName(path),
                });
            }
            JumpList.SetJumpList(Application.Current, list);
            list.Apply();
        }
        catch (Exception ex) { App.Log("Jump list: " + ex.Message); }
    }
}
