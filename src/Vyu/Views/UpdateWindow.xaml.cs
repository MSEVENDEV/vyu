using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Vyu.Kinetic;
using Vyu.Services;

namespace Vyu.Views;

/// <summary>"Vyu 1.1.0 is here" — Later / Schedule / Update now &amp; restart (same pop-up as Hypt's).</summary>
public partial class UpdateWindow : Window
{
    private readonly UpdateService? _svc;
    private readonly UpdateInfo _info;
    private readonly VyuSettings _settings;
    private readonly Action<string> _notify;
    private bool _busy;

    /// <param name="svc">Null for the screenshot preview.</param>
    public UpdateWindow(UpdateService? svc, UpdateInfo info, Version current, VyuSettings settings, Action<string> notify)
    {
        InitializeComponent();
        _svc = svc;
        _info = info;
        _settings = settings;
        _notify = notify;
        TitleText.Text = $"Vyu {info.Version.ToString(3)} is here";
        SubText.Text = $"You have {current.ToString(3)}. Updating takes a few seconds and keeps your settings and where you stopped watching.";
        NotesText.Text = PlainNotes(info.Notes);
        if (svc is not null && !svc.CanSelfUpdate) NowText.Text = "Download";
        Loaded += (_, _) => Motion.Pop(Root, 0.94, 420);
    }

    public static string PlainNotes(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return "Bug fixes and improvements.";
        var lines = md.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Select(l => Regex.Replace(l, @"\[([^\]]+)\]\([^)]+\)", "$1"))
            .Select(l => l.Replace("**", "").Replace("`", ""))
            .Select(l => Regex.Replace(l, @"^#+\s*", ""))
            .Select(l => Regex.Replace(l, @"^\s*[-*]\s+", "•  "))
            .Where(l => !l.StartsWith("<!--") && !l.StartsWith("|---"))
            .Take(60);
        return Regex.Replace(string.Join("\n", lines).Trim(), @"\n{3,}", "\n\n");
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { }
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _settings.UpdateSnoozedUntil = DateTime.Now.AddHours(20);
        _settings.UpdateSnoozedVersion = _info.Tag;
        _settings.Save();
        Close();
    }

    private void OnSchedule(object sender, RoutedEventArgs e)
    {
        SchedulePanel.Visibility = SchedulePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SchedulePanel.Visibility == Visibility.Visible) Motion.Rise(SchedulePanel, 0, 10);
    }

    private async void OnScheduleExit(object sender, RoutedEventArgs e) => await ScheduleAsync(UpdateSchedule.OnExit, "Update installs when you close Vyu");
    private async void OnScheduleTonight(object sender, RoutedEventArgs e) => await ScheduleAsync(UpdateSchedule.Tonight, "Update scheduled for 3:00 AM");

    private async Task ScheduleAsync(UpdateSchedule when, string message)
    {
        if (_svc is null || _busy) return;
        if (!_svc.CanSelfUpdate) { OpenPage(); Close(); return; }
        Close();
        _notify(message);
        await _svc.ScheduleAsync(when);
    }

    private async void OnNow(object sender, RoutedEventArgs e)
    {
        if (_svc is null || _busy) return;
        if (!_svc.CanSelfUpdate) { OpenPage(); Close(); return; }
        _busy = true;
        LaterBtn.IsEnabled = ScheduleBtn.IsEnabled = NowBtn.IsEnabled = false;
        SchedulePanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        StatusText.Text = "Downloading…";
        void OnProgress(double p)
        {
            Bar.Value = p;
            StatusText.Text = p < 1 ? $"Downloading… {p:P0} of {_info.Size / 1048576.0:0} MB" : "Checking and installing…";
        }
        _svc.Progress += OnProgress;
        try
        {
            await _svc.DownloadAsync();
            StatusText.Text = "Restarting into the new version…";
            await _svc.InstallNowAsync();
        }
        catch (Exception ex)
        {
            _busy = false;
            StatusText.Text = "Update failed: " + ex.Message + " — nothing was changed.";
            LaterBtn.IsEnabled = ScheduleBtn.IsEnabled = NowBtn.IsEnabled = true;
        }
        finally { _svc.Progress -= OnProgress; }
    }

    private void OpenPage()
    {
        try { Process.Start(new ProcessStartInfo(_info.PageUrl.Length > 0 ? _info.PageUrl : "https://github.com/MSEVENDEV/vyu/releases/latest") { UseShellExecute = true }); } catch { }
    }
}
