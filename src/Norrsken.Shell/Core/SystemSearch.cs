using System.Data.OleDb;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>
/// Launcher search beyond apps: files and folders from the Windows Search index (the same index
/// Start uses, so results are instant), with a small direct scan of your main folders as a fallback.
/// </summary>
public static class SystemSearch
{
    public sealed record Hit(string Path, string Name, bool IsDir, DateTime Modified);

    /// <summary>Developer and app internals nobody searches for from the launcher.</summary>
    static readonly string[] Noise = [@"\AppData\", @"\node_modules\", @"\.git\", @"\bin\Debug\", @"\bin\Release\", @"\obj\", @"\.cache\", @"\site-packages\", @"\__pycache__\", @"\.gradle\", @"\.nuget\",
        // Browser engines inside apps (Chromium, Electron, WebView2) leave these everywhere; nobody searches for them.
        @"\blob_storage\", @"\Session Storage\", @"\Local Storage\", @"\Code Cache\", @"\GPUCache\", @"\DawnCache\", @"\DawnGraphiteCache\", @"\Service Worker\",
        @"\Crashpad\", @"\IndexedDB\", @"\EBWebView\", @".WebView2\", @"\CacheStorage\", @"\shared_proto_db\", @"\VideoDecodeStats\", @"\GrShaderCache\", @"\ShaderCache\"];

    /// <summary>A hit counts as noise when it sits in one of those folders or is one ("…\blob_storage").</summary>
    static bool IsNoise(string path) => Noise.Any(n => (path + "\\").Contains(n, StringComparison.OrdinalIgnoreCase));

    public static List<Hit> Files(string query, int max, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length < 2) return new();
        try { return Indexed(query, max, ct); }
        catch (Exception ex)
        {
            Log.Write("Search index unavailable, scanning instead: " + ex.Message);
            return Scan(query, max, ct);
        }
    }

    static List<Hit> Indexed(string q, int max, CancellationToken ct)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        string like = q.Replace("'", "''").Replace("%", "[%]").Replace("_", "[_]");
        string sql = $"SELECT TOP {max * 6} System.ItemUrl, System.ItemNameDisplay, System.ItemType, System.DateModified FROM SystemIndex " +
                     $"WHERE SCOPE='file:{home}' AND System.FileName LIKE '%{like}%' ORDER BY System.Search.Rank DESC, System.DateModified DESC";
        var hits = new List<Hit>();
        using var conn = new OleDbConnection("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
        conn.Open();
        using var cmd = new OleDbCommand(sql, conn);
        using var r = cmd.ExecuteReader();
        while (r.Read() && hits.Count < max * 3 && !ct.IsCancellationRequested)
        {
            // ItemUrl is the real path ("file:C:/Users/…"); ItemPathDisplay would be Windows' translated folder names.
            if (r.GetValue(0) is not string url || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            string path = Uri.UnescapeDataString(url[5..]).Replace('/', '\\');
            if (IsNoise(path)) continue;
            string name = r.GetValue(1) as string ?? System.IO.Path.GetFileName(path);
            bool dir = (r.GetValue(2) as string)?.Equals("Directory", StringComparison.OrdinalIgnoreCase) == true || Directory.Exists(path);
            DateTime mod = r.GetValue(3) is DateTime d ? d.ToLocalTime() : default;
            hits.Add(new(path, System.IO.Path.GetFileName(path), dir, mod));
        }
        // Names that start with the query first, then the index's own ranking.
        return hits.OrderByDescending(h => h.Name.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)).Take(max).ToList();
    }

    static List<Hit> Scan(string q, int max, CancellationToken ct)
    {
        var hits = new List<Hit>();
        var roots = new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic }
            .Select(Environment.GetFolderPath).Append(Ui.Files.Downloads).Where(Directory.Exists);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
        foreach (var root in roots)
        {
            try
            {
                foreach (var fsi in new DirectoryInfo(root).EnumerateFileSystemInfos("*" + q + "*", opts))
                {
                    if (ct.IsCancellationRequested || hits.Count >= max) return hits;
                    if (IsNoise(fsi.FullName)) continue;
                    hits.Add(new(fsi.FullName, fsi.Name, fsi is DirectoryInfo, fsi.LastWriteTime));
                }
            }
            catch (Exception) { }
        }
        return hits;
    }
}

/// <summary>
/// Norrsken Files is the only file browser: any File Explorer folder window — opened by another
/// app, a shortcut or `explorer.exe C:\…` — is closed the moment it appears and the same place
/// (with the same item selected) opens in Files. Explorer itself keeps running underneath, because
/// it also hosts the desktop, notifications and Alt+Tab. Windows-only pages such as Control Panel
/// are left alone.
/// </summary>
public static class ExplorerRedirect
{
    const uint EVENT_OBJECT_SHOW = 0x8002;
    static WinEventProc? _proc;
    static IntPtr _hook;
    static readonly Dictionary<string, string> Virtual = new(StringComparer.OrdinalIgnoreCase)
    {
        ["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = Ui.FilesWindow.ThisPC,
        ["::{645FF040-5081-101B-9F08-00AA002F954E}"] = Ui.FilesWindow.RecycleBin,
        ["::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), // Home
        ["::{679F85CB-0220-4080-B29B-5540CC05AAB6}"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), // Quick access
    };

    public static int Redirected { get; private set; }

    public static void Start()
    {
        if (App.TestMode || _hook != IntPtr.Zero) return;
        _proc = (hook, evt, hwnd, idObject, child, thread, time) =>
        {
            if (idObject != 0 || hwnd == IntPtr.Zero || !App.Settings.ReplaceSystemApps) return;
            if (GetClassName(hwnd) is "CabinetWClass" or "ExploreWClass") _ = Redirect(hwnd);
        };
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        // Explorer windows already open when Norrsken starts move over too.
        EnumWindows((h, lp) => { if (IsWindowVisible(h) && GetClassName(h) == "CabinetWClass") _ = Redirect(h); return true; }, IntPtr.Zero);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    static async Task Redirect(IntPtr hwnd)
    {
        ShowWindow(hwnd, 0); // hide at once: no flash of Explorer
        for (int attempt = 0; attempt < 25; attempt++)
        {
            await Task.Delay(attempt == 0 ? 30 : 80);
            if (!IsWindow(hwnd)) return;
            var (window, path, select) = Locate(hwnd);
            if (window == null || path == null) continue;
            string? target = Directory.Exists(path) ? path : Virtual.GetValueOrDefault(path);
            if (target == null)
            {
                ShowWindow(hwnd, 5); // a Windows-only place (Control Panel, network…): leave it to Explorer
                return;
            }
            try { ((dynamic)window).Quit(); } catch (Exception) { PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
            NorrskenApps.Launch("files", select ?? target);
            Redirected++;
            Log.Write($"Explorer window → Files ({select ?? target})");
            return;
        }
        ShowWindow(hwnd, 5); // couldn't read its location; don't leave an invisible window behind
    }

    static (object? Window, string? Path, string? Select) Locate(IntPtr hwnd)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic w in shell.Windows())
            {
                long h;
                try { h = (long)w.HWND; } catch (Exception) { continue; }
                if (h != hwnd.ToInt64()) continue;
                string? path = null, select = null;
                try { path = (string)w.Document.Folder.Self.Path; } catch (Exception) { }
                try
                {
                    var items = w.Document.SelectedItems();
                    if (items.Count == 1) select = (string)items.Item(0).Path;
                }
                catch (Exception) { }
                return (w, string.IsNullOrEmpty(path) ? null : path, select != null && File.Exists(select) ? select : null);
            }
        }
        catch (COMException) { }
        return (null, null, null);
    }
}
