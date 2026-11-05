using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows.Threading;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's Bin, on top of Windows' own Recycle Bin (so Explorer, apps and Norrsken all share one bin).
/// It reads the bin directly: every deleted item is a pair of files in <c>X:\$Recycle.Bin\&lt;your SID&gt;</c>,
/// <c>$I…</c> (where it came from, its size, when it was deleted) and <c>$R…</c> (the item itself).
///
/// What it adds over Windows: undo the last delete in one go, restore into a folder that no longer exists
/// (it's rebuilt), restore somewhere else, shred instead of delete, keep items safe from auto-emptying,
/// auto-empty after N days, and a running total of the space you've freed.
/// </summary>
public static class Bin
{
    public sealed record Item(string Name, string OriginalPath, long Size, DateTime Deleted, string DataPath, string InfoPath, bool IsFolder)
    {
        public string Id => Path.GetFileName(InfoPath) + "@" + Path.GetPathRoot(InfoPath);
        public string FromFolder => Path.GetDirectoryName(OriginalPath) ?? "";
        public string Extension => IsFolder ? "" : Path.GetExtension(OriginalPath);
    }

    /// <summary>Raised on the UI thread (debounced) whenever something enters or leaves the bin.</summary>
    public static event Action? Changed;

    /// <summary>Tests point the bin at a folder of their own.</summary>
    public static string? TestRoot { get; set; }

    static string Sid => WindowsIdentity.GetCurrent().User?.Value ?? "";

    static IEnumerable<string> Folders()
    {
        if (TestRoot != null) { yield return TestRoot; yield break; }
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            string f = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", Sid);
            if (Directory.Exists(f)) yield return f;
        }
    }

    public static List<Item> List()
    {
        var items = new List<Item>();
        foreach (var folder in Folders())
        {
            IEnumerable<string> infos;
            try { infos = Directory.EnumerateFiles(folder, "$I*").ToList(); } catch (Exception) { continue; }
            foreach (var info in infos)
                if (Read(info) is { } item) items.Add(item);
        }
        return items.OrderByDescending(i => i.Deleted).ToList();
    }

    /// <summary>Parses a <c>$I</c> file (Windows 10/11 version 2, and Vista/7's version 1).</summary>
    static Item? Read(string info)
    {
        try
        {
            string data = Path.Combine(Path.GetDirectoryName(info)!, "$R" + Path.GetFileName(info)[2..]);
            bool isDir = Directory.Exists(data);
            if (!isDir && !File.Exists(data)) return null;
            var b = File.ReadAllBytes(info);
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0), size = BitConverter.ToInt64(b, 8);
            var deleted = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            string path;
            if (version >= 2)
            {
                int chars = BitConverter.ToInt32(b, 24);
                path = System.Text.Encoding.Unicode.GetString(b, 28, Math.Min(chars * 2, b.Length - 28)).TrimEnd('\0');
            }
            else path = System.Text.Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24)).Split('\0')[0];
            if (path.Length == 0) return null;
            return new Item(Path.GetFileName(path.TrimEnd('\\')), path, size, deleted, data, info, isDir);
        }
        catch (Exception) { return null; }
    }

    public static (int Count, long Bytes) Totals()
    {
        var all = List();
        return (all.Count, all.Sum(i => i.Size));
    }

    // ───────────────────────── restore ─────────────────────────

    /// <summary>
    /// Puts an item back where it was (or into <paramref name="folder"/>). A folder that has since been
    /// removed is created again; a name that's taken gets " (restored)". Returns the path it ended up at.
    /// </summary>
    public static string Restore(Item item, string? folder = null)
    {
        string dir = folder ?? item.FromFolder;
        Directory.CreateDirectory(dir);
        string target = Free(Path.Combine(dir, item.Name));
        MoveAny(item.DataPath, target);
        TryDelete(item.InfoPath);
        Notify(dir);
        Notify(Path.GetDirectoryName(item.InfoPath)!);
        return target;
    }

    /// <summary>Everything that went into the bin together with the newest item: one Delete keypress, one undo.</summary>
    public static List<Item> LastBatch()
    {
        var all = List();
        if (all.Count == 0) return [];
        var newest = all[0].Deleted;
        return all.Where(i => (newest - i.Deleted).TotalSeconds <= 3).ToList();
    }

    public static List<string> UndoLastDelete()
    {
        var back = new List<string>();
        foreach (var item in LastBatch())
            try { back.Add(Restore(item)); } catch (Exception ex) { Log.Write($"Bin: couldn't restore {item.OriginalPath}: {ex.Message}"); }
        return back;
    }

    static string Free(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        if (Directory.Exists(path)) { name = Path.GetFileName(path); ext = ""; }
        for (int i = 1; ; i++)
        {
            string p = Path.Combine(dir, $"{name} (restored{(i > 1 ? " " + i : "")}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static void MoveAny(string from, string to)
    {
        bool sameVolume = string.Equals(Path.GetPathRoot(from), Path.GetPathRoot(to), StringComparison.OrdinalIgnoreCase);
        if (File.Exists(from)) { File.Move(from, to); return; }
        if (sameVolume) { Directory.Move(from, to); return; }
        CopyTree(from, to);
        Directory.Delete(from, true);
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }

    // ───────────────────────── delete ─────────────────────────

    /// <summary>
    /// Gone for good. With <paramref name="shred"/> every byte is overwritten with random data first, so
    /// recovery tools find noise. (On SSDs this is best effort: the drive may keep old blocks around.)
    /// </summary>
    public static void DeleteForever(Item item, bool shred)
    {
        if (shred) Shred(item.DataPath);
        if (Directory.Exists(item.DataPath)) Directory.Delete(item.DataPath, true);
        else TryDelete(item.DataPath);
        TryDelete(item.InfoPath);
        AddFreed(item.Size);
        Notify(Path.GetDirectoryName(item.InfoPath)!);
    }

    static void Shred(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) Shred(f);
                return;
            }
            var info = new FileInfo(path);
            if (!info.Exists) return;
            info.Attributes = FileAttributes.Normal;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough);
            var buf = new byte[1 << 16];
            for (long done = 0; done < info.Length; done += buf.Length)
            {
                RandomNumberGenerator.Fill(buf);
                fs.Write(buf, 0, (int)Math.Min(buf.Length, info.Length - done));
            }
            fs.Flush(true);
        }
        catch (Exception ex) { Log.Write($"Bin: shredding {path} failed: {ex.Message}"); }
    }

    /// <summary>Empties the whole bin (every drive), like Windows' "Empty Recycle Bin".</summary>
    public static void Empty(bool shred)
    {
        var all = List();
        if (shred || TestRoot != null)
        {
            foreach (var i in all) try { DeleteForever(i, shred); } catch (Exception) { }
            return;
        }
        long bytes = all.Sum(i => i.Size);
        SHEmptyRecycleBinW(IntPtr.Zero, null, 0x7); // no confirmation, progress or sound: Norrsken already asked
        AddFreed(bytes);
        Raise();
    }

    /// <summary>Removes items older than the setting allows, except the ones you've kept.</summary>
    public static int AutoEmpty()
    {
        int days = App.Settings.BinAutoEmptyDays;
        if (days <= 0) return 0;
        var keep = App.Settings.BinKept;
        int n = 0;
        foreach (var i in List().Where(i => (DateTime.Now - i.Deleted).TotalDays > days && !keep.Contains(i.Id)))
            try { DeleteForever(i, App.Settings.BinShred); n++; } catch (Exception) { }
        if (n > 0) Log.Write($"Bin: auto-emptied {n} item(s) older than {days} days");
        return n;
    }

    public static bool IsKept(Item i) => App.Settings.BinKept.Contains(i.Id);

    public static void SetKept(Item i, bool keep)
    {
        var list = App.Settings.BinKept;
        if (keep && !list.Contains(i.Id)) list.Add(i.Id);
        if (!keep) list.Remove(i.Id);
        App.Settings.Save();
    }

    static void AddFreed(long bytes)
    {
        string month = DateTime.Now.ToString("yyyy-MM");
        if (App.Settings.BinFreedMonth != month) { App.Settings.BinFreedMonth = month; App.Settings.BinFreedBytes = 0; }
        App.Settings.BinFreedBytes += bytes;
        App.Settings.Save();
    }

    public static long FreedThisMonth => App.Settings.BinFreedMonth == DateTime.Now.ToString("yyyy-MM") ? App.Settings.BinFreedBytes : 0;

    static void TryDelete(string path) { try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch (Exception) { } }

    // ───────────────────────── watching ─────────────────────────

    static readonly List<FileSystemWatcher> Watchers = [];
    static DispatcherTimer? _debounce;

    /// <summary>Starts watching every drive's bin; also runs the auto-empty now and every six hours.</summary>
    public static void Watch()
    {
        if (_debounce != null) return;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Changed?.Invoke(); };
        foreach (var f in Folders())
        {
            try
            {
                var w = new FileSystemWatcher(f, "$I*") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
                w.Created += (_, _) => Raise();
                w.Deleted += (_, _) => Raise();
                Watchers.Add(w);
            }
            catch (Exception ex) { Log.Write($"Bin: can't watch {f}: {ex.Message}"); }
        }
        var clean = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        clean.Tick += (_, _) => AutoEmpty();
        clean.Start();
        Task.Delay(TimeSpan.FromMinutes(1)).ContinueWith(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => AutoEmpty()));
    }

    static void Raise() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => { if (_debounce != null) { _debounce.Stop(); _debounce.Start(); } else Changed?.Invoke(); });

    static void Notify(string dir)
    {
        try { SHChangeNotify(0x00001000 /* SHCNE_UPDATEDIR */, 0x0005 /* SHCNF_PATHW */, dir, IntPtr.Zero); } catch (Exception) { }
        Raise();
    }

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1048576.0:0.#} MB",
        _ => $"{bytes / 1073741824.0:0.##} GB",
    };

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHChangeNotify(int eventId, int flags, string item1, IntPtr item2);
}
