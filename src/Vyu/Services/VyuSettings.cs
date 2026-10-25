using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vyu.Services;

public enum QualityProfile { Fast, Balanced, Cinema }

public sealed class VyuSettings : INotifyPropertyChanged
{
    private double _volume = 100;
    private bool _hwdec = true;
    private QualityProfile _quality = QualityProfile.Balanced;
    private bool _resume = true;
    private bool _autoNext = true;
    private bool _clickPause = true;
    private bool _runAsAdmin = true;
    private bool _nightMode;
    private double _subScale = 1.0;
    private string _audioLang = "";
    private string _subLang = "";
    private bool _subsAuto = true;
    private int _seekStep = 5;
    private bool _thumbnails = true;

    public double Volume { get => _volume; set => Set(ref _volume, Math.Clamp(value, 0, 150)); }
    /// <summary>GPU video decoding (d3d11va). Off = CPU decoding.</summary>
    public bool HardwareDecoding { get => _hwdec; set => Set(ref _hwdec, value); }
    /// <summary>Fast = lowest power, Balanced = mpv defaults (gpu-next), Cinema = high-quality scaling + debanding.</summary>
    public QualityProfile Quality { get => _quality; set => Set(ref _quality, value); }
    public bool ResumePlayback { get => _resume; set => Set(ref _resume, value); }
    public bool AutoPlayNext { get => _autoNext; set => Set(ref _autoNext, value); }
    public bool ClickToPause { get => _clickPause; set => Set(ref _clickPause, value); }
    /// <summary>Ask for administrator (UAC) when Vyu starts. Already-running Vyu takes new files without asking.</summary>
    public bool RunAsAdmin { get => _runAsAdmin; set => Set(ref _runAsAdmin, value); }
    /// <summary>Evens out loud explosions and quiet dialogue (dynamic range compression).</summary>
    public bool NightMode { get => _nightMode; set => Set(ref _nightMode, value); }
    public double SubtitleScale { get => _subScale; set => Set(ref _subScale, Math.Clamp(value, 0.5, 2.5)); }
    public string PreferredAudioLanguage { get => _audioLang; set => Set(ref _audioLang, value); }
    public string PreferredSubtitleLanguage { get => _subLang; set => Set(ref _subLang, value); }
    public bool AutoLoadSubtitles { get => _subsAuto; set => Set(ref _subsAuto, value); }
    public int SeekStep { get => _seekStep; set => Set(ref _seekStep, Math.Clamp(value, 1, 60)); }
    public bool SeekPreviews { get => _thumbnails; set => Set(ref _thumbnails, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static string Folder
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("VYU_HOME");
            var dir = home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vyu");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static VyuSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<VyuSettings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch { }
        return new VyuSettings();
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, true);
        }
        catch { }
    }
}

public sealed class RecentItem
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public double Position { get; set; }
    public double Duration { get; set; }
    public DateTime LastPlayed { get; set; } = DateTime.Now;

    [JsonIgnore] public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    [JsonIgnore] public string Subtitle => Duration > 0
        ? (Progress > 0.97 ? $"Watched · {TimeText.Format(Duration)}" : $"{TimeText.Format(Position)} of {TimeText.Format(Duration)}")
        : System.IO.Path.GetDirectoryName(Path) ?? "";
    [JsonIgnore] public bool IsUrl => Path.Contains("://");
}

/// <summary>Recently played files and where you stopped in each.</summary>
public sealed class Recents
{
    private static string FilePath => System.IO.Path.Combine(VyuSettings.Folder, "recent.json");
    public List<RecentItem> Items { get; private set; } = new();

    public static Recents Load()
    {
        var r = new Recents();
        try
        {
            if (File.Exists(FilePath))
                r.Items = JsonSerializer.Deserialize<List<RecentItem>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        r.Items = r.Items.Where(i => i.IsUrl || File.Exists(i.Path)).ToList();
        return r;
    }

    public RecentItem? Find(string path) => Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Touch(string path, string title, double position, double duration)
    {
        var item = Find(path);
        if (item is null) { item = new RecentItem { Path = path }; Items.Insert(0, item); }
        else { Items.Remove(item); Items.Insert(0, item); }
        item.Title = title;
        item.Position = position;
        if (duration > 0) item.Duration = duration;
        item.LastPlayed = DateTime.Now;
        if (Items.Count > 40) Items.RemoveRange(40, Items.Count - 40);
    }

    public void Remove(string path) => Items.RemoveAll(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}

public static class TimeText
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
