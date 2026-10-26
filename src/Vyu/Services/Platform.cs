using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Vyu.Services;

/// <summary>
/// One Vyu at a time. A second launch hands its files to the running Vyu over a named pipe (which accepts
/// non-elevated clients), so double-clicking a video while an elevated Vyu is open never shows UAC again.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string PipeName = "Vyu.Open.v1";
    private const string MutexName = @"Local\Vyu.SingleInstance";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        IsFirst = created;
    }

    public static bool AnotherIsRunning()
    {
        try { using var m = Mutex.OpenExisting(MutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; }
    }

    public static bool Forward(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            var payload = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? new[] { "--show" } : args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch { return false; }
    }

    public void Listen(Action<string[]> onArgs)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var security = new PipeSecurity();
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                        PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                    await using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 0, 0, security);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, _cts.Token);
                    var args = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    onArgs(args);
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(300); }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

/// <summary>
/// Registers Vyu as a media player for this user (no administrator needed) the way Windows' own players do:
/// one file type per extension ("MP4 Video", "MKV Video"…) with a document-style icon, so once Vyu is the default
/// your files still look like normal video files (with their thumbnails), not like a wall of app logos.
/// Windows only lets the user pick the default, so <see cref="OpenDefaultAppsSettings"/> takes them to Vyu's page.
/// </summary>
public static class FileAssociations
{
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".ogv",
        ".vob", ".divx", ".f4v", ".hevc", ".y4m", ".rmvb", ".asf",
    };
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac", ".ape" };

    public const string ProgIdPrefix = "Vyu.";
    public static string ProgId(string ext) => ProgIdPrefix + ext.TrimStart('.').ToUpperInvariant();

    private static string Exe => Environment.ProcessPath!;
    private static string IconFor(bool audio) => Path.Combine(AppContext.BaseDirectory, "icons", audio ? "vyu-audio.ico" : "vyu-video.ico");

    public static void Register()
    {
        var exe = Exe;
        var cmd = $"\"{exe}\" \"%1\"";
        var all = VideoExtensions.Select(e => (e, audio: false)).Concat(AudioExtensions.Select(e => (e, audio: true))).ToList();
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Vyu.exe"))
        {
            app.SetValue("FriendlyAppName", "Vyu");
            using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var (e, _) in all) types.SetValue(e, "");
        }
        foreach (var (e, audio) in all)
        {
            using var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId(e)}");
            var label = $"{e.TrimStart('.').ToUpperInvariant()} {(audio ? "Audio" : "Video")}";
            prog.SetValue(null, label);
            prog.SetValue("FriendlyTypeName", label);
            var icon = IconFor(audio);
            using (var i = prog.CreateSubKey("DefaultIcon")) i.SetValue(null, File.Exists(icon) ? $"\"{icon}\",0" : $"\"{exe}\",0");
            using (var shell = prog.CreateSubKey("shell")) shell.SetValue(null, "open");
            using (var open = prog.CreateSubKey(@"shell\open"))
            {
                open.SetValue(null, "Play with Vyu");
                open.SetValue("Icon", $"\"{exe}\",0");
                using var c = open.CreateSubKey("command");
                c.SetValue(null, cmd);
            }
        }
        using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Vyu\Capabilities"))
        {
            cap.SetValue("ApplicationName", "Vyu");
            cap.SetValue("ApplicationIcon", $"\"{exe}\",0");
            cap.SetValue("ApplicationDescription", "Plays every video and music file. Fast, simple, powered by mpv.");
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var (e, _) in all)
            {
                fa.SetValue(e, ProgId(e));
                using var ow = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{e}\OpenWithProgids");
                ow.SetValue(ProgId(e), Array.Empty<byte>(), RegistryValueKind.None);
                try { ow.DeleteValue("Vyu.Media", false); } catch { } // the single type Vyu 1.0 used
            }
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue("Vyu", @"Software\Vyu\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED: Explorer refreshes icons
    }

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\Vyu.exe\shell\open\command");
        return k?.GetValue(null) is string s && s.Contains(Exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Vyu was registered from another folder (moved, or an older install): point everything at this exe.</summary>
    public static void RefreshIfMoved()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\Vyu.exe\shell\open\command");
        if (k?.GetValue(null) is string s && !s.Contains(Exe, StringComparison.OrdinalIgnoreCase)) Register();
        else if (k is not null && Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId(".mp4")}") is null) Register(); // 1.0 → per-type
    }

    /// <summary>The program Windows opens this extension with (the user's choice in Default apps), as a ProgID.</summary>
    public static string? DefaultProgId(string ext)
    {
        using var k = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}\UserChoice");
        return k?.GetValue("ProgId") as string;
    }

    public static bool IsDefaultFor(string ext) =>
        DefaultProgId(ext) is { } p && (p.StartsWith(ProgIdPrefix, StringComparison.OrdinalIgnoreCase) || p.Equals("Applications\\Vyu.exe", StringComparison.OrdinalIgnoreCase));

    public static void OpenDefaultAppsSettings()
    {
        // Windows 11 opens Vyu's own page in Default apps; if that link isn't understood, the Default apps list
        // opens instead, where Vyu is now listed
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Vyu") { UseShellExecute = true }); }
        catch { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch { } }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
