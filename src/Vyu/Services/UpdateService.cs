using System.Windows;
using System.Windows.Threading;

namespace Vyu.Services;

public enum UpdateSchedule { None, OnExit, Tonight }

/// <summary>
/// The three answers of the update pop-up: later, schedule (when Vyu closes / tonight at 3:00) or now.
/// Scheduled updates download right away in the background, so installing takes a second when the time comes.
/// </summary>
public sealed class UpdateService
{
    private readonly Updater _updater;
    private Task<string>? _download;
    private string? _staged;
    private bool _restart;
    private DispatcherTimer? _night;
    private DateTime _nightAt;

    public UpdateInfo? Available { get; private set; }
    public UpdateSchedule Schedule { get; private set; }
    public Version Current => _updater.Current;
    public bool CanSelfUpdate => _updater.CanSelfUpdate();
    public event Action<double>? Progress;
    /// <summary>Is a video playing right now? (the tonight install never interrupts one)</summary>
    public Func<bool> IsWatching { get; set; } = () => false;

    public UpdateService()
    {
        var v = typeof(App).Assembly.GetName().Version ?? new Version(1, 0, 0);
        _updater = new Updater("MSEVENDEV/vyu", "Vyu-", v, Environment.GetEnvironmentVariable("VYU_UPDATE_FEED"));
    }

    public void CleanUp() => _updater.CleanUp();

    public async Task<UpdateInfo?> CheckAsync() => Available = await _updater.CheckAsync();

    public async Task<string> DownloadAsync()
    {
        if (Available is null) throw new InvalidOperationException("No update to download.");
        _download ??= _updater.DownloadAsync(Available, new Progress<double>(p => Progress?.Invoke(p)));
        try { return _staged = await _download; }
        catch { _download = null; throw; }
    }

    public async Task InstallNowAsync()
    {
        await DownloadAsync();
        _restart = true;
        Application.Current.MainWindow?.Close(); // App.OnExit installs and restarts
    }

    public async Task ScheduleAsync(UpdateSchedule when)
    {
        Schedule = when;
        if (when == UpdateSchedule.Tonight)
        {
            var today3 = DateTime.Today.AddHours(3);
            _nightAt = DateTime.Now < today3 ? today3 : today3.AddDays(1);
            _night ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
            {
                if (DateTime.Now < _nightAt || _staged is null || IsWatching()) return;
                _night!.Stop();
                _restart = true;
                Application.Current.MainWindow?.Close();
            }, Application.Current.Dispatcher);
            _night.Start();
        }
        try { await DownloadAsync(); }
        catch (Exception ex) { Schedule = UpdateSchedule.None; _night?.Stop(); App.Log("Update download failed: " + ex.Message); }
    }

    /// <summary>Called by App.OnExit, after the video engine has shut down.</summary>
    public void OnExit()
    {
        if (_staged is null || (!_restart && Schedule == UpdateSchedule.None)) return;
        try { _updater.Apply(_staged); }
        catch (Exception ex) { App.Log("Update install failed (rolled back): " + ex.Message); }
        if (_restart) Updater.Relaunch(Environment.ProcessPath!, Array.Empty<string>());
    }
}
