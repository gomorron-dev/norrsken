using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace Norrsken.Shell.Core;

/// <summary>
/// Updates like a normal app: Norrsken checks GitHub (gomorron-dev/norrsken), downloads a new release in the
/// background, checks it and parks it. Then it asks: restart now, or later (it installs at the next sign-in).
/// Installing shows an update screen while the new files go in, Flickerbox and the integration are refreshed,
/// and Norrsken comes back saying what's new and offering to restart the PC.
/// </summary>
public static class Updater
{
    public const string Repo = "gomorron-dev/norrsken";

    public sealed record Release(Version Version, string Tag, string Url, long Size, string? Sha256, string Notes);

    public enum Stage { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

    public static Stage State { get; private set; } = Stage.Idle;
    public static double Progress { get; private set; }
    public static string Error { get; private set; } = "";
    public static Release? Latest { get; private set; }
    /// <summary>Downloaded, checked and unpacked: installs on restart.</summary>
    public static Release? Staged { get; private set; }
    public static event Action? Changed;

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

    /// <summary>Tests point the staging folder somewhere of their own.</summary>
    internal static string? TestRoot;
    static string Root => TestRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "updates");
    static string ReadyFile => Path.Combine(Root, "ready.json");
    static string AppliedFile => Path.Combine(Root, "applied.json");

    sealed record ReadyInfo(string Version, string Tag, string Notes, string Payload);
    public sealed record AppliedInfo(string From, string To, string Notes);

    static void Set(Stage s, double progress = 0, string error = "")
    {
        State = s; Progress = progress; Error = error;
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Changed?.Invoke(); else d.BeginInvoke(() => Changed?.Invoke());
    }

    public static string ReleasePage(Release? r) => r == null ? $"https://github.com/{Repo}/releases" : $"https://github.com/{Repo}/releases/tag/{r.Tag}";

    // ───────────────────────── check ─────────────────────────

    /// <summary>The newest release when it's newer than this Norrsken; null when you're up to date.</summary>
    public static async Task<Release?> CheckAsync()
    {
        if (State is Stage.Downloading) return Latest;
        Set(Stage.Checking);
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            var r = doc.RootElement;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            Release? found = null;
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var v))
                foreach (var a in r.GetProperty("assets").EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("Norrsken-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    string? digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                    found = new Release(v, tag, a.GetProperty("browser_download_url").GetString()!, a.GetProperty("size").GetInt64(),
                        digest?.StartsWith("sha256:") == true ? digest[7..] : null, r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "");
                    break;
                }
            Latest = found != null && found.Version > Current ? found : null;
            if (Staged != null && (Latest == null || Staged.Version >= Latest.Version)) { Set(Stage.Ready); return Staged; }
            Set(Latest == null ? Stage.UpToDate : Stage.Available);
            return Latest;
        }
        catch (Exception ex)
        {
            Set(Stage.Failed, error: "Couldn't reach GitHub: " + ex.Message);
            throw;
        }
    }

    // ───────────────────────── download and stage ─────────────────────────

    /// <summary>Downloads, checks (SHA-256) and unpacks a release, ready to install on restart.</summary>
    public static async Task DownloadAsync(Release rel)
    {
        if (State == Stage.Downloading) return;
        Set(Stage.Downloading, 0);
        try
        {
            Directory.CreateDirectory(Root);
            string zip = Path.Combine(Root, $"Norrsken-{rel.Version}.zip");
            string dir = Path.Combine(Root, rel.Version.ToString());
            using (var resp = await Http.GetAsync(rel.Url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.Size;
                await using var input = await resp.Content.ReadAsStreamAsync();
                await using var output = File.Create(zip);
                var buffer = new byte[1 << 20];
                long done = 0;
                int n;
                var lastReport = DateTime.UtcNow;
                while ((n = await input.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n));
                    done += n;
                    if (total > 0 && (DateTime.UtcNow - lastReport).TotalMilliseconds > 120) { lastReport = DateTime.UtcNow; Set(Stage.Downloading, done / (double)total * 0.9); }
                }
            }
            if (rel.Sha256 is { } want)
            {
                await using var check = File.OpenRead(zip);
                string got = Convert.ToHexString(await SHA256.HashDataAsync(check));
                if (!got.Equals(want, StringComparison.OrdinalIgnoreCase)) { File.Delete(zip); throw new InvalidDataException("the download doesn't match the release, so nothing was changed"); }
            }
            Set(Stage.Downloading, 0.94);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, dir));
            string payload = File.Exists(Path.Combine(dir, "Norrsken.Shell.exe")) ? dir
                : Directory.EnumerateDirectories(dir).FirstOrDefault(d => File.Exists(Path.Combine(d, "Norrsken.Shell.exe")))
                  ?? throw new InvalidDataException("the release doesn't contain Norrsken");
            File.Delete(zip);
            File.WriteAllText(ReadyFile, JsonSerializer.Serialize(new ReadyInfo(rel.Version.ToString(), rel.Tag, rel.Notes, payload)));
            Staged = rel;
            Log.Write($"Update {rel.Version} downloaded and ready");
            Set(Stage.Ready, 1);
        }
        catch (Exception ex)
        {
            Log.Write("Update download failed: " + ex.Message);
            Set(Stage.Failed, error: "The download failed: " + ex.Message);
            throw;
        }
    }

    /// <summary>At start-up: picks up an update that's waiting, and tidies away ones that are already installed.</summary>
    public static void LoadStaged()
    {
        try
        {
            if (!File.Exists(ReadyFile)) return;
            var info = JsonSerializer.Deserialize<ReadyInfo>(File.ReadAllText(ReadyFile));
            if (info == null || !Version.TryParse(info.Version, out var v) || v <= Current || !File.Exists(Path.Combine(info.Payload, "Norrsken.Shell.exe")))
            {
                File.Delete(ReadyFile);
                foreach (var d in Directory.EnumerateDirectories(Root)) try { Directory.Delete(d, true); } catch (Exception) { }
                return;
            }
            Staged = new Release(v, info.Tag, "", 0, null, info.Notes);
            State = Stage.Ready;
        }
        catch (Exception ex) { Log.Write("Staged update unreadable: " + ex.Message); }
    }

    static string? StagedPayload()
    {
        try { return JsonSerializer.Deserialize<ReadyInfo>(File.ReadAllText(ReadyFile))?.Payload; } catch (Exception) { return null; }
    }

    // ───────────────────────── install ─────────────────────────

    /// <summary>"Restart now": hands over to the update screen, which installs and brings Norrsken back.</summary>
    public static bool InstallNow()
    {
        if (Staged == null || StagedPayload() is not { } payload || App.TestMode) return false;
        try
        {
            File.WriteAllText(AppliedFile, JsonSerializer.Serialize(new AppliedInfo(App.Version, Staged.Version.ToString(), Staged.Notes)));
            var psi = new ProcessStartInfo(Path.Combine(payload, "Norrsken.Shell.exe"), $"--apply-update \"{InstallDir}\" {Environment.ProcessId}")
            { UseShellExecute = true, WorkingDirectory = payload };
            if (!Unelevated.IsElevated) psi.Verb = "runas"; // Program Files needs administrator rights
            Process.Start(psi);
            Log.Write($"Installing Norrsken {Staged.Version}");
            _ = Application.Current.Dispatcher.BeginInvoke(() => App.Quit());
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Update hand-over failed: " + ex.Message);
            Set(Stage.Failed, error: "Couldn't start the update: " + ex.Message);
            return false;
        }
    }

    /// <summary>Signing in with an update waiting installs it before the dock appears.</summary>
    public static bool InstallStagedAtStartup() => Staged != null && CanUpdateHere && !App.TestMode && InstallNow();

    /// <summary>The update that was just installed (once, right after it): for the "what's new" card.</summary>
    public static AppliedInfo? TakeApplied()
    {
        try
        {
            if (!File.Exists(AppliedFile)) return null;
            var a = JsonSerializer.Deserialize<AppliedInfo>(File.ReadAllText(AppliedFile));
            File.Delete(AppliedFile);
            return a != null && a.To == App.Version ? a : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The steps the update screen shows, in order.</summary>
    public static readonly string[] Steps = ["Closing Norrsken", "Copying the new version", "Updating Flickerbox and default apps", "Finishing up"];

    /// <summary>`--apply-update target pid`, run from the unpacked release: the update screen does the work.</summary>
    public static void RunApply(Application app, string target, int pid)
    {
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var screen = new Ui.UpdateScreen(App.Version);
        screen.Show();
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        // Tests only: the real hand-off, screen and copy into a test folder, nothing else on the PC.
        bool dry = Environment.GetEnvironmentVariable("NORRSKEN_UPDATE_DRYRUN") == "1";
        Task.Run(() =>
        {
            bool ok = true;
            try
            {
                screen.Step(0, 0);
                try { var old = Process.GetProcessById(pid); if (!old.WaitForExit(15_000)) old.Kill(); } catch (ArgumentException) { }
                foreach (var name in dry ? [] : new[] { "Norrsken.Shell", "Flickerbox" })
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        if (p.Id == Environment.ProcessId) continue;
                        string? path = Interop.Native.GetProcessPath((uint)p.Id);
                        if (path == null || path.StartsWith(source, StringComparison.OrdinalIgnoreCase)) continue;
                        if (name == "Flickerbox" || path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) { try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
                    }
                screen.Step(1, 0);
                Mirror(source, target, f => screen.Step(1, f));
                screen.Step(2, 0);
                string exe = Path.Combine(target, "Norrsken.Shell.exe");
                foreach (var (args, i) in dry ? [] : new[] { ("--register-defaults", 0), ("--integrate", 1) })
                {
                    try { using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true })!; p.WaitForExit(120_000); }
                    catch (Exception ex) { Log.Write($"{args} after update: {ex.Message}"); }
                    screen.Step(2, (i + 1) / 2.0);
                }
                screen.Step(3, 0.5);
                if (!dry) Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = false, WorkingDirectory = target });
                Log.Write($"Update to {App.Version} installed");
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Write("Update failed: " + ex);
                screen.Failed(ex.Message);
                // Whatever happened, bring a Norrsken back.
                if (!dry) try { Process.Start(new ProcessStartInfo(Path.Combine(target, "Norrsken.Shell.exe")) { UseShellExecute = false, WorkingDirectory = target }); } catch (Exception) { }
            }
            Thread.Sleep(ok ? 900 : 6000);
            app.Dispatcher.BeginInvoke(() => app.Shutdown(ok ? 0 : 1));
        });
    }

    /// <summary>
    /// Makes <paramref name="target"/> match <paramref name="source"/>: changed files are copied (a locked one is
    /// retried), files the new version doesn't have are removed. The sound pack (<c>sfx\</c>) is left alone.
    /// </summary>
    internal static void Mirror(string source, string target, Action<double>? progress = null)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string rel = Path.GetRelativePath(source, files[i]);
            keep.Add(rel);
            string dest = Path.Combine(target, rel);
            var a = new FileInfo(files[i]);
            var b = new FileInfo(dest);
            if (!b.Exists || a.Length != b.Length || a.LastWriteTimeUtc != b.LastWriteTimeUtc)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(files[i], dest, true); File.SetLastWriteTimeUtc(dest, a.LastWriteTimeUtc); break; }
                    catch (IOException) when (attempt < 10) { Thread.Sleep(400); }
                    catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(400); }
                }
            }
            if (i % 8 == 0) progress?.Invoke(i / (double)files.Count);
        }
        if (Directory.Exists(target))
            foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToList())
            {
                string rel = Path.GetRelativePath(target, f);
                if (keep.Contains(rel) || rel.StartsWith("sfx\\", StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch (Exception) { }
            }
        progress?.Invoke(1);
    }

    // ───────────────────────── background ─────────────────────────

    /// <summary>Checks a couple of minutes after start and then every six hours, downloads on its own, then asks.</summary>
    public static void StartBackground()
    {
        if (App.TestMode || !CanUpdateHere) return;
        if (Staged != null) Ui.UpdateCard.Ready(Staged);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        t.Tick += async (_, _) =>
        {
            t.Interval = TimeSpan.FromHours(6);
            try
            {
                if (await CheckAsync() is not { } rel || Staged?.Version >= rel.Version) return;
                if (App.Settings.AutoUpdate)
                {
                    await DownloadAsync(rel);
                    if (App.MainDock?.IsSteppedAside != true) Ui.UpdateCard.Ready(rel);
                }
                else Ui.UpdateCard.Available(rel);
            }
            catch (Exception ex) { Log.Write("Update check: " + ex.Message); }
        };
        t.Start();
    }
}
