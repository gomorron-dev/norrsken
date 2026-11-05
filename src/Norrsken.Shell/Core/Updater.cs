using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken updates itself (and the Flickerbox it ships) from GitHub releases of gomorron-dev/norrsken.
/// A release carries `Norrsken-&lt;version&gt;.zip`: the whole payload folder. Installing downloads and unpacks it
/// next to the logs, then the new copy (`--apply-update`) waits for the running dock to close, mirrors itself
/// into Program Files, refreshes Flickerbox and the default apps, and starts the dock again.
/// </summary>
public static class Updater
{
    public const string Repo = "gomorron-dev/norrsken";

    public sealed record Release(Version Version, string Tag, string Url, long Size, string? Sha256, string Notes);

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"Norrsken/{App.Version}");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static Version Current => Version.TryParse(App.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>Only an installed Norrsken updates itself (never a build running from a source folder).</summary>
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Norrsken");
    public static bool CanUpdateHere => Environment.ProcessPath is { } p && p.StartsWith(InstallDir + "\\", StringComparison.OrdinalIgnoreCase);

    public static Release? Available { get; private set; }
    public static event Action? Changed;

    /// <summary>The newest release, or null when this Norrsken is already that version (or newer).</summary>
    public static async Task<Release?> CheckAsync()
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var r = doc.RootElement;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return Set(null);
        foreach (var a in r.GetProperty("assets").EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith("Norrsken-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            string? digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            var rel = new Release(v, tag, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(),
                digest?.StartsWith("sha256:") == true ? digest[7..] : null,
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
            return Set(v > Current ? rel : null);
        }
        return Set(null);
    }

    static Release? Set(Release? r) { Available = r; Changed?.Invoke(); return r; }

    /// <summary>Downloads, checks and unpacks the release, then hands over to it. The dock closes and comes back updated.</summary>
    public static async Task InstallAsync(Release rel, IProgress<double>? progress = null)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "updates");
        Directory.CreateDirectory(root);
        string zip = Path.Combine(root, $"Norrsken-{rel.Version}.zip");
        string dir = Path.Combine(root, rel.Version.ToString());

        using (var resp = await Http.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? rel.Size;
            await using var input = await resp.Content.ReadAsStreamAsync();
            await using var output = File.Create(zip);
            var buffer = new byte[1 << 20];
            long done = 0;
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }
        if (rel.Sha256 is { } want)
        {
            await using var check = File.OpenRead(zip);
            string got = Convert.ToHexString(await SHA256.HashDataAsync(check));
            if (!got.Equals(want, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); throw new InvalidDataException("The download doesn't match the release (SHA-256). Nothing was changed."); }
        }
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir));
        // The zip may hold the payload at its root or inside one folder.
        string payload = File.Exists(Path.Combine(dir, "Norrsken.Shell.exe")) ? dir
            : Directory.EnumerateDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "Norrsken.Shell.exe")))
              ?? throw new InvalidDataException("The release doesn't contain Norrsken.Shell.exe.");
        File.Delete(zip);

        var psi = new ProcessStartInfo(Path.Combine(payload, "Norrsken.Shell.exe"), $"--apply-update \"{InstallDir}\" {Environment.ProcessId}")
        { UseShellExecute = true, WorkingDirectory = payload };
        if (!Unelevated.IsElevated) psi.Verb = "runas"; // Program Files needs administrator rights
        Process.Start(psi);
        Log.Write($"Updating to Norrsken {rel.Version}");
        Application.Current.Dispatcher.BeginInvoke(() => App.Quit());
    }

    /// <summary>`--apply-update target pid`, run from the unpacked release.</summary>
    public static int Apply(string target, int pid)
    {
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        try { Process.GetProcessById(pid).WaitForExit(15_000); } catch (ArgumentException) { }
        foreach (var name in new[] { "Norrsken.Shell", "Flickerbox" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (p.Id == Environment.ProcessId) continue;
                string? path = Interop.Native.GetProcessPath((uint)p.Id);
                if (path == null || path.StartsWith(source, StringComparison.OrdinalIgnoreCase)) continue;
                if (name == "Flickerbox" || path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
            }
        using (var rc = Process.Start(new ProcessStartInfo("robocopy", $"\"{source}\" \"{target}\" /MIR /R:3 /W:1 /NP /NFL /NDL /NJH /NJS")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
        {
            rc.StandardOutput.ReadToEnd();
            rc.WaitForExit();
            if (rc.ExitCode >= 8) { Log.Write($"Update copy failed (robocopy {rc.ExitCode})"); }
        }
        string exe = Path.Combine(target, "Norrsken.Shell.exe");
        // Flickerbox, Notes and the integration follow the new version.
        foreach (var args in new[] { "--register-defaults", "--integrate" })
            try { using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(120_000); } catch (Exception ex) { Log.Write($"{args} after update: {ex.Message}"); }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = target });
        Log.Write("Update applied");
        return 0;
    }

    /// <summary>Checks a little after start and then every six hours; installs on its own when allowed.</summary>
    public static void StartBackground()
    {
        if (App.TestMode || !CanUpdateHere) return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        t.Tick += async (_, _) =>
        {
            t.Interval = TimeSpan.FromHours(6);
            try
            {
                if (await CheckAsync() is { } rel && App.Settings.AutoUpdate && App.MainDock?.IsSteppedAside != true)
                    await InstallAsync(rel);
            }
            catch (Exception ex) { Log.Write("Update check: " + ex.Message); }
        };
        t.Start();
    }
}
