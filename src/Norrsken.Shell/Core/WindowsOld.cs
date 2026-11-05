using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Norrsken.Shell.Core;

/// <summary>
/// A clean Windows install that didn't format the disk parks the previous system in C:\Windows.old.
/// This brings the user's files back into the new profile — copying, never moving or overwriting,
/// then checking every file — and spots which apps the old system had, so they can be preselected.
/// Windows deletes Windows.old by itself after about ten days, so this is offered on first sign-in.
/// </summary>
public static class WindowsOld
{
    static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// <summary>NORRSKEN_WINDOWS_OLD / NORRSKEN_RESTORE_TARGET let the UI tests use fake data in a temp folder.</summary>
    public static string Root => Env("NORRSKEN_WINDOWS_OLD") ?? Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Windows.old");
    static string UsersDir => Path.Combine(Root, "Users");
    public static bool Exists => Directory.Exists(UsersDir);

    static readonly HashSet<string> SystemProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Public", "Default", "Default User", "All Users", "defaultuser0", "WDAGUtilityAccount",
    };

    public static List<string> Profiles()
    {
        try
        {
            return Directory.EnumerateDirectories(UsersDir).Select(d => Path.GetFileName(d)!)
                .Where(n => !SystemProfiles.Contains(n)).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public sealed record KnownFolder(string Name, string Label, Guid Id);

    /// <summary>Restored into the new profile's real known folders (which may be redirected, e.g. to OneDrive).</summary>
    public static readonly IReadOnlyList<KnownFolder> Known =
    [
        new("Desktop", "Desktop", new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        new("Documents", "Documents", new("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        new("Downloads", "Downloads", new("374DE290-123F-4565-9164-39C4925E467B")),
        new("Pictures", "Pictures", new("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        new("Videos", "Videos", new("18989B1D-99B5-455B-841C-AB7C74E4DDFC")),
        new("Music", "Music", new("4BD8D571-6D19-48D3-BE97-422220080E43")),
    ];

    // From the old profile root: never cloud-synced folders (they re-sync), legacy junctions,
    // or app data (opt-in separately); never the old registry hive files.
    static readonly string[] RootExcludeDirs =
    [
        "AppData", "OneDrive", "MicrosoftEdgeBackups", "Application Data", "Cookies", "Local Settings", "My Documents",
        "NetHood", "PrintHood", "Recent", "SendTo", "Start Menu", "Templates", "Searches",
    ];
    static readonly string[] HiveFiles = ["NTUSER*", "ntuser*", "UsrClass*"];
    static readonly string[] AppDataExcludeDirs = ["Temp", "INetCache", "CrashDumps", "D3DSCache", "NVIDIA", "Packages"];

    public sealed record RestoreTask(string Id, string Label, string Source, string Target, string[] ExcludeDirs, string[] ExcludeFiles);

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    static string KnownFolderPath(KnownFolder f)
    {
        if (Env("NORRSKEN_RESTORE_TARGET") is { } fake) return Path.Combine(fake, f.Name);
        var id = f.Id;
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0) return Path.Combine(ProfileTarget, f.Name);
        try { return Marshal.PtrToStringUni(p)!; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    static string ProfileTarget => Env("NORRSKEN_RESTORE_TARGET") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static List<RestoreTask> Plan(string profile, IEnumerable<string> known, bool other, bool appData)
    {
        string src = Path.Combine(UsersDir, profile);
        var tasks = Known.Where(k => known.Contains(k.Name))
            .Select(k => new RestoreTask(k.Name, k.Label, Path.Combine(src, k.Name), KnownFolderPath(k), [], []))
            .ToList();
        if (other)
        {
            var exclude = RootExcludeDirs.Concat(Known.Select(k => k.Name)).Select(d => Path.Combine(src, d)).ToArray();
            tasks.Add(new("other", "Other folders and files", src, ProfileTarget, exclude, HiveFiles));
        }
        if (appData)
            tasks.Add(new("appdata", "App data", Path.Combine(src, "AppData"), Path.Combine(ProfileTarget, "AppData"),
                AppDataExcludeDirs.ToArray(), []));
        return tasks;
    }

    // ───────────── the copy (runs in the elevated helper: old profiles belong to an old account) ─────────────

    public static int RunHelper(string planFile, string log)
    {
        var tasks = JsonSerializer.Deserialize<List<RestoreTask>>(File.ReadAllText(planFile)) ?? [];
        foreach (var t in tasks)
        {
            if (!Directory.Exists(t.Source)) { Elevated.Report(log, t.Id, InstallState.Done, "Nothing to bring back"); continue; }
            Elevated.Report(log, t.Id, InstallState.Installing, "Copying…");
            int code = Robocopy(t);
            var v = Verify(t);
            if (code >= 8 || v.Missing > 0)
                Elevated.Report(log, t.Id, InstallState.Failed,
                    $"{Files(v.Missing)} of {v.Files:N0} couldn't be copied — the originals are still in {t.Source}");
            else
                Elevated.Report(log, t.Id, InstallState.Done,
                    $"{Files(v.Files)} · {Size(v.Bytes)} · checked" + (v.Kept > 0 ? $" · {Files(v.Kept)} already here, kept as is" : ""));
        }
        return 0;
    }

    static string Q(string path) => "\"" + path.TrimEnd('\\') + "\"";

    /// <summary>
    /// Copy only what's missing: /XC /XN /XO never touch a file that already exists at the target.
    /// /COPY:DAT keeps contents, attributes (hidden etc.) and timestamps; /XJ skips junction loops.
    /// </summary>
    static int Robocopy(RestoreTask t)
    {
        var args = new List<string>
        {
            Q(t.Source), Q(t.Target), "/E", "/COPY:DAT", "/DCOPY:DAT", "/XJ", "/XC", "/XN", "/XO",
            "/R:1", "/W:1", "/MT:8", "/NP", "/NFL", "/NDL", "/NJH", "/NJS",
        };
        if (t.ExcludeDirs.Length > 0) { args.Add("/XD"); args.AddRange(t.ExcludeDirs.Select(Q)); }
        if (t.ExcludeFiles.Length > 0) { args.Add("/XF"); args.AddRange(t.ExcludeFiles.Select(Q)); }
        using var p = Process.Start(new ProcessStartInfo("robocopy", string.Join(' ', args))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    public readonly record struct Verification(int Files, long Bytes, int Missing, int Kept);

    /// <summary>Every source file must exist at the target; a different size means we kept a file that was already there.</summary>
    public static Verification Verify(RestoreTask t)
    {
        int files = 0, missing = 0, kept = 0;
        long bytes = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint, // include hidden/system files, skip junctions
        };
        string src = t.Source.TrimEnd('\\');
        foreach (var path in Directory.EnumerateFiles(src, "*", options))
        {
            if (t.ExcludeDirs.Any(d => path.StartsWith(d.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            if (t.ExcludeDirs.Any(d => !Path.IsPathRooted(d) && path[(src.Length + 1)..].Split('\\').SkipLast(1).Contains(d, StringComparer.OrdinalIgnoreCase))) continue;
            string name = Path.GetFileName(path);
            if (t.ExcludeFiles.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name))) continue;
            var info = new FileInfo(path);
            files++;
            bytes += info.Length;
            var dest = new FileInfo(Path.Combine(t.Target, path[(src.Length + 1)..]));
            if (!dest.Exists) missing++;
            else if (dest.Length != info.Length) kept++;
        }
        return new(files, bytes, missing, kept);
    }

    static string Files(int n) => n == 1 ? "1 file" : $"{n:N0} files";

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    // ───────────── which apps did the old system have? ─────────────

    // Names that differ between the catalogue and what installers call themselves.
    static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["Microsoft.VisualStudioCode"] = ["Visual Studio Code"],
        ["OpenJS.NodeJS.LTS"] = ["Node.js"],
        ["EclipseAdoptium.Temurin.21.JDK"] = ["Temurin 21", "Eclipse Temurin JDK with Hotspot 21"],
        ["EclipseAdoptium.Temurin.25.JDK"] = ["Temurin 25", "Eclipse Temurin JDK with Hotspot 25"],
        ["Oracle.JavaRuntimeEnvironment"] = ["Java 8"],
        ["EpicGames.EpicGamesLauncher"] = ["Epic Games Launcher"],
        ["Moonsworth.LunarClient"] = ["Lunar Client"],
        ["Microsoft.VisualStudio.Community"] = ["Visual Studio 2022", "Visual Studio 2026"],
    };

    /// <summary>
    /// Catalogue apps that appear in the old system's Start menu or Program Files folders.
    /// Matching is by whole words, so "Go" doesn't match "Google".
    /// </summary>
    public static HashSet<string> DetectApps(IEnumerable<CatalogApp> catalogue)
    {
        var names = new List<string>();
        void Add(IEnumerable<string> items) => names.AddRange(items);
        var deep = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
        var flat = new EnumerationOptions { IgnoreInaccessible = true };
        try
        {
            string menu = Path.Combine(Root, @"ProgramData\Microsoft\Windows\Start Menu\Programs");
            if (Directory.Exists(menu)) Add(Directory.EnumerateFileSystemEntries(menu, "*", deep).Select(Path.GetFileNameWithoutExtension)!);
            foreach (var pf in new[] { "Program Files", "Program Files (x86)" }.Select(d => Path.Combine(Root, d)).Where(Directory.Exists))
                foreach (var dir in Directory.EnumerateDirectories(pf, "*", flat))
                {
                    names.Add(Path.GetFileName(dir));
                    names.AddRange(Directory.EnumerateDirectories(dir, "*", flat).Select(Path.GetFileName)!);
                }
            foreach (var profile in Profiles())
            {
                string local = Path.Combine(UsersDir, profile, @"AppData\Local\Programs");
                if (Directory.Exists(local)) names.AddRange(Directory.EnumerateDirectories(local, "*", flat).Select(Path.GetFileName)!);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        var wordSets = names.Select(Words).Where(w => w.Length > 0).ToList();
        var found = new HashSet<string>();
        foreach (var app in catalogue)
        {
            var candidates = (Aliases.TryGetValue(app.Id, out var a) ? a : []).Append(app.Name).Select(Words);
            if (candidates.Any(c => c.Length > 0 && wordSets.Any(ws => ContainsRun(ws, c)))) found.Add(app.Id);
        }
        return found;
    }

    static string[] Words(string? s) =>
        (s ?? "").ToLowerInvariant().Split(c => !char.IsLetterOrDigit(c)).Where(w => w.Length > 0).ToArray();

    static bool ContainsRun(string[] haystack, string[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
            if (needle.Select((w, j) => haystack[i + j] == w).All(x => x)) return true;
        return false;
    }
}

static class StringSplitExtensions
{
    public static string[] Split(this string s, Func<char, bool> isSeparator)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || isSeparator(s[i]))
            {
                if (i > start) parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        return parts.ToArray();
    }
}
