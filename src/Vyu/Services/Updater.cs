// Same updater as Hypt's (Hypt.Core/Update/Updater.cs) — keep the two in step.
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Vyu.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string AssetUrl, long Size, string? Sha256, string PageUrl);

/// <summary>
/// Updates from the app's GitHub releases. The new version is downloaded next to the app, checked (size + SHA-256
/// from GitHub), then swapped in: Windows lets a running program's files be renamed, so the current files become
/// "*.old-update", the new ones take their place, and the app restarts into them (or simply exits, for
/// "install when I close it"). Leftovers are cleaned up on the next start. No installer, no admin prompt.
/// </summary>
public sealed class Updater
{
    private const string OldSuffix = ".old-update";
    private readonly string _repo;        // "MSEVENDEV/vyu"
    private readonly string _assetPrefix; // "Vyu-"
    private readonly string _feedOverride; // tests: a local release JSON file

    public Version Current { get; }
    public string InstallDir { get; }
    public string StagingRoot => Path.Combine(InstallDir, ".update");

    public Updater(string repo, string assetPrefix, Version current, string? feedOverride = null, string? installDir = null)
    {
        _repo = repo;
        _assetPrefix = assetPrefix;
        Current = current;
        _feedOverride = feedOverride ?? "";
        InstallDir = installDir ?? AppContext.BaseDirectory.TrimEnd('\\');
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Hypt-Vyu-Updater", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>The latest release if it is newer than this build, otherwise null. Never throws.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            string json;
            if (_feedOverride.Length > 0) json = await File.ReadAllTextAsync(_feedOverride, ct);
            else
            {
                using var http = NewClient();
                json = await http.GetStringAsync($"https://api.github.com/repos/{_repo}/releases/latest", ct);
            }
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
            var tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Normalize(version) <= Normalize(Current)) return null;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith(_assetPrefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
                return new UpdateInfo(version, tag,
                    r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                    r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(), sha,
                    r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
            }
            return null;
        }
        catch (Exception ex)
        {
            App.Log("Update check skipped: " + ex.Message);
            return null;
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    /// <summary>Can this app replace its own files (e.g. not in a protected Program Files folder)?</summary>
    public bool CanSelfUpdate()
    {
        try
        {
            var probe = Path.Combine(InstallDir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Downloads, verifies and unpacks the update next to the app. Returns the folder with the new files.</summary>
    public async Task<string> DownloadAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StagingRoot);
        var zip = Path.Combine(StagingRoot, $"{u.Tag}.zip");
        var target = Path.Combine(StagingRoot, u.Tag);
        if (Directory.Exists(target)) Directory.Delete(target, true);

        if (File.Exists(u.AssetUrl)) // tests / local feeds
        {
            File.Copy(u.AssetUrl, zip, true);
            progress?.Report(1);
        }
        else
        {
            if (!u.AssetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Refusing a non-HTTPS download.");
            using var http = NewClient();
            using var res = await http.GetAsync(u.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? u.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(zip))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                var tick = Stopwatch.StartNew();
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (tick.ElapsedMilliseconds > 120) { progress?.Report(total > 0 ? (double)done / total : 0); tick.Restart(); }
                }
            }
            progress?.Report(1);
        }

        var size = new FileInfo(zip).Length;
        if (u.Size > 0 && size != u.Size) throw new InvalidDataException($"Download is {size} bytes, expected {u.Size}.");
        if (u.Sha256 is { } expected)
        {
            await using var fs = File.OpenRead(zip);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Download failed its SHA-256 check.");
        }
        ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
        File.Delete(zip);
        // the zip holds the app's folder contents directly (Vyu.exe at the root)
        return target;
    }

    /// <summary>
    /// Swaps the staged files in. On failure everything already moved is put back and the exception is rethrown.
    /// </summary>
    public void Apply(string staged)
    {
        var moved = new List<(string Target, string Old)>();
        var added = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staged, file);
                var target = Path.Combine(InstallDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    var old = target + OldSuffix;
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(target, old);
                    moved.Add((target, old));
                }
                else added.Add(target);
                File.Move(file, target);
            }
            App.Log($"Update applied from {staged} ({moved.Count} replaced, {added.Count} new)");
        }
        catch (Exception ex)
        {
            App.Log("Update failed, rolling back: " + ex);
            foreach (var t in added) try { File.Delete(t); } catch { }
            foreach (var (t, old) in Enumerable.Reverse(moved))
                try { if (File.Exists(t)) File.Delete(t); File.Move(old, t); } catch { }
            throw;
        }
        try { Directory.Delete(staged, true); } catch { }
    }

    /// <summary>Starts the (new) app once this process has exited.</summary>
    public static void Relaunch(string exePath, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exePath)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("--updated");
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    /// <summary>Started by <see cref="Relaunch"/>: wait (up to 20 s) for the old version to finish exiting.</summary>
    public static void WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        int i = args.ToList().FindIndex(a => a.TrimStart('-', '/').Equals("wait-pid", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Count || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(20000); } catch { }
    }

    /// <summary>Removes the previous version's renamed files and unfinished downloads.</summary>
    public void CleanUp()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*" + OldSuffix, SearchOption.AllDirectories))
                try { File.Delete(f); } catch { }
            if (Directory.Exists(StagingRoot)) Directory.Delete(StagingRoot, true);
        }
        catch { }
    }
}
