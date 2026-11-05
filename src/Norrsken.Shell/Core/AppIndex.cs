using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Core;

/// <summary>An installed app exactly as Windows lists it in shell:AppsFolder (desktop and Store apps).</summary>
public sealed class AppEntry(string name, string parsingName, ImageSource? icon = null) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string ParsingName { get; } = parsingName;
    public string ShellPath => @"shell:AppsFolder\" + ParsingName;

    /// <summary>The executable the entry starts, when Windows exposes it (used to match running windows).</summary>
    public string? TargetPath { get; init; }

    ImageSource? _icon = icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    bool _glyphResolved;
    Geometry? _glyph;
    /// <summary>Norrsken line icon for built-in apps; null for third-party apps, which keep their own icon.</summary>
    public Geometry? Glyph
    {
        get
        {
            if (!_glyphResolved) { _glyph = IconPack.ForApp(ParsingName, TargetPath); _glyphResolved = true; }
            return _glyph;
        }
    }
    public bool HasGlyph => Glyph != null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class AppIndex
{
    /// <summary>Enumerates names on a background STA thread; Shell.Application is apartment-bound.</summary>
    public static Task<List<AppEntry>> LoadAsync() => RunSta(Enumerate);

    /// <summary>Second pass: icons are the slow part, so they stream in after the list is usable.</summary>
    public static Task<int> LoadIconsAsync(IReadOnlyList<AppEntry> apps) => RunSta(() =>
    {
        int n = 0;
        foreach (var a in apps)
            if (a.Icon == null && (a.Icon = ShellIcons.Get(a.ShellPath)) != null) n++;
        return n;
    });

    static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    static List<AppEntry> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
                   ?? throw new InvalidOperationException("Shell.Application is not registered.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        dynamic items = folder.Items();
        int count = items.Count;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppEntry>(count);
        for (int i = 0; i < count; i++)
        {
            dynamic item = items.Item(i);
            string name = item.Name ?? "";
            string path = item.Path ?? "";
            if (name.Length == 0 || path.Length == 0 || !seen.Add(path)) continue;
            string? link = null;
            try { link = item.ExtendedProperty("System.Link.TargetParsingPath") as string; }
            catch (Exception) { }
            list.Add(new AppEntry(name, path) { TargetPath = ResolveTarget(path, link) });
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    // Desktop entries are either links (target exposed) or "{KnownFolderGuid}\relative\app.exe".
    static string? ResolveTarget(string parsingName, string? linkTarget)
    {
        if (!string.IsNullOrEmpty(linkTarget)) return linkTarget;
        int slash = parsingName.IndexOf('\\');
        if (parsingName.StartsWith('{') && slash > 0 && Guid.TryParse(parsingName[..slash], out var folderId)
            && SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr p) == 0)
        {
            try { return Path.Combine(Marshal.PtrToStringUni(p)!, parsingName[(slash + 1)..]); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.IsPathRooted(parsingName) ? parsingName : null;
    }

    public static void Launch(AppEntry app)
    {
        try
        {
            Unelevated.Start(app.ShellPath);
        }
        catch (Exception ex)
        {
            Log.Write($"ShellExecute failed for {app.ParsingName}: {ex.Message}; retrying via explorer.exe");
            Process.Start(new ProcessStartInfo("explorer.exe", app.ShellPath) { UseShellExecute = false });
        }
    }

    /// <summary>
    /// Ranked search: prefix > word start > initials ("vsc" → Visual Studio Code) > substring >
    /// in-order letters, nudged up by how often you launch the app.
    /// </summary>
    public static IEnumerable<AppEntry> Search(IEnumerable<AppEntry> apps, string query, IReadOnlyDictionary<string, int>? launches = null)
    {
        query = query.Trim();
        if (query.Length == 0) return apps;
        return apps
            .Select(a => (App: a, Score: Score(a.Name, query) is int s and > 0 ? s + Math.Min(launches?.GetValueOrDefault(a.ParsingName) ?? 0, 25) : 0))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.App);
    }

    public static int Score(string name, string q)
    {
        var cmp = StringComparison.CurrentCultureIgnoreCase;
        if (name.StartsWith(q, cmp)) return 100;
        var words = name.Split([' ', '-', '_', '.', '(', ')'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(w => w.StartsWith(q, cmp))) return 80;
        if (q.Length >= 2 && string.Concat(words.Select(w => w[0])).StartsWith(q, cmp)) return 70;
        if (name.Contains(q, cmp)) return 50;
        // Letters in order ("tmgr" → Task Manager); only for 3+ characters to avoid noise.
        if (q.Length >= 3)
        {
            int i = 0;
            foreach (char c in name)
                if (i < q.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(q[i])) i++;
            if (i == q.Length) return 20;
        }
        return 0;
    }

    /// <summary>The most-launched apps, most first.</summary>
    public static List<AppEntry> Frequent(IEnumerable<AppEntry> apps, IReadOnlyDictionary<string, int> launches, int count) =>
        apps.Where(a => launches.GetValueOrDefault(a.ParsingName) > 0)
            .OrderByDescending(a => launches[a.ParsingName])
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(count)
            .ToList();
}
