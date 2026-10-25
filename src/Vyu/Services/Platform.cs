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

public static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Restarts Vyu through UAC. Returns false if the user said no (Vyu then just runs normally).</summary>
    public static bool RelaunchElevated(string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = string.Join(" ", args.Select(a => $"\"{a.Replace("\"", "\\\"")}\"").Append("--elevated")),
                WorkingDirectory = Environment.CurrentDirectory,
            };
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return false; } // cancelled
        catch { return false; }
    }
}

/// <summary>Registers Vyu as a video player (per user). Windows then lets you pick it in Default apps.</summary>
public static class FileAssociations
{
    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".ogv",
        ".vob", ".divx", ".f4v", ".hevc", ".y4m", ".rmvb", ".asf",
    };
    public static readonly string[] AudioExtensions = { ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac", ".ape" };

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        var cmd = $"\"{exe}\" \"%1\"";
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Vyu.exe"))
        {
            app.SetValue("FriendlyAppName", "Vyu");
            using (var c = app.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var e in VideoExtensions.Concat(AudioExtensions)) types.SetValue(e, "");
        }
        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Vyu.Media"))
        {
            prog.SetValue(null, "Video (Vyu)");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var c = prog.CreateSubKey(@"shell\open\command")) c.SetValue(null, cmd);
        }
        using (var cap = Registry.CurrentUser.CreateSubKey(@"Software\Vyu\Capabilities"))
        {
            cap.SetValue("ApplicationName", "Vyu");
            cap.SetValue("ApplicationDescription", "Fast, beautiful video player powered by mpv.");
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var e in VideoExtensions.Concat(AudioExtensions))
            {
                fa.SetValue(e, "Vyu.Media");
                using var ow = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{e}\OpenWithProgids");
                ow.SetValue("Vyu.Media", Array.Empty<byte>(), RegistryValueKind.None);
            }
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue("Vyu", @"Software\Vyu\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Applications\Vyu.exe\shell\open\command");
        return k?.GetValue(null) is string s && s.Contains(Environment.ProcessPath!, StringComparison.OrdinalIgnoreCase);
    }

    public static void OpenDefaultAppsSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Vyu") { UseShellExecute = true }); }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
}
