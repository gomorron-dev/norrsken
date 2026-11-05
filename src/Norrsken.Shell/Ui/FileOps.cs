using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Norrsken.Shell.Core;
using Path = System.IO.Path;

namespace Norrsken.Shell.Ui;

/// <summary>One row/tile in Files.</summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    public required string FullPath { get; init; }
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public bool IsHidden { get; init; }
    public DateTime Modified { get; init; }
    public long Size { get; init; }
    public string TypeName { get; set; } = "";
    /// <summary>Recycle Bin items: where the file came from; search and Recent: the folder it's in.</summary>
    public string? Location { get; init; }
    /// <summary>Second line under a tile (drives: free space).</summary>
    public string? Detail { get; init; }
    /// <summary>A drive on This PC: fraction used.</summary>
    public double? Used { get; init; }
    /// <summary>Recycle Bin item handle (Shell FolderItem) for restore/delete.</summary>
    internal object? ShellItem { get; init; }

    public string ModifiedText => Files.FriendlyDate(Modified);
    public string SizeText => Detail ?? (IsDir ? "" : Files.FormatSize(Size));
    public double Opacity => IsHidden ? 0.55 : 1;
    public string Tip => Location != null ? $"{Name}\n{Location}" : Name;

    ImageSource? _icon;
    public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>File operations Files uses, built on the Windows shell so recycle, conflicts and progress behave as everywhere else.</summary>
public static class Files
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool SHObjectProperties(IntPtr hwnd, uint type, string name, string? page);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? root, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfoW(uint action, uint param, string value, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    const ushort FOF_RENAMEONCOLLISION = 0x8, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;

    public static string FormatSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:0.#} MB",
        _ => $"{b / 1073741824.0:0.##} GB",
    };

    /// <summary>"Today, 14:32" · "Yesterday, 09:10" · "Monday, 18:00" · a plain date beyond a week.</summary>
    public static string FriendlyDate(DateTime d)
    {
        if (d == default) return "";
        var c = CultureInfo.CurrentCulture;
        var days = (DateTime.Today - d.Date).TotalDays;
        string time = d.ToString("t", c);
        return days switch
        {
            0 => $"Today, {time}",
            1 => $"Yesterday, {time}",
            > 1 and < 7 => $"{char.ToUpper(d.ToString("dddd", c)[0], c)}{d.ToString("dddd", c)[1..]}, {time}",
            _ => d.ToString(d.Year == DateTime.Today.Year ? "d MMM, " + c.DateTimeFormat.ShortTimePattern : "d MMM yyyy", c),
        };
    }

    static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);

    public static string TypeName(string path, bool dir)
    {
        string ext = dir ? "<dir>" : Path.GetExtension(path);
        return TypeNames.GetOrAdd(ext, _ =>
        {
            var info = new SHFILEINFO();
            SHGetFileInfoW(dir ? "folder" : "x" + ext, dir ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x400 | 0x10 /* TYPENAME | USEFILEATTRIBUTES */);
            return info.szTypeName is { Length: > 0 } t ? t : (dir ? "Folder" : ext.TrimStart('.').ToUpperInvariant() + " file");
        });
    }

    public static string? KnownFolder(string guid)
    {
        var id = new Guid(guid);
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    public static string Downloads => KnownFolder("374DE290-123F-4565-9164-39C4925E467B") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    static bool Op(IntPtr owner, uint func, IEnumerable<string> from, string? to, ushort flags)
    {
        var list = from.ToList();
        if (list.Count == 0) return true;
        var op = new SHFILEOPSTRUCT { hwnd = owner, wFunc = func, pFrom = string.Join('\0', list) + "\0\0", pTo = to == null ? null : to + "\0\0", fFlags = flags };
        int rc = SHFileOperationW(ref op);
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    public static bool Recycle(IntPtr owner, IEnumerable<string> paths) => Op(owner, FO_DELETE, paths, null, FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING);
    public static bool DeleteForever(IntPtr owner, IEnumerable<string> paths) => Op(owner, FO_DELETE, paths, null, 0);
    public static bool Copy(IntPtr owner, IEnumerable<string> paths, string dest) => Op(owner, FO_COPY, paths, dest, FOF_ALLOWUNDO | FOF_RENAMEONCOLLISION);
    public static bool Move(IntPtr owner, IEnumerable<string> paths, string dest) => Op(owner, FO_MOVE, paths, dest, FOF_ALLOWUNDO);
    public static void Properties(IntPtr owner, string path) => SHObjectProperties(owner, 2, path, null);
    public static void EmptyRecycleBin(IntPtr owner) => SHEmptyRecycleBinW(owner, null, 0);
    public static void SetWallpaper(string path) => SystemParametersInfoW(0x0014, 0, path, 0x01 | 0x02);

    /// <summary>Runs a blocking shell operation on its own STA thread (the shell shows its own progress for big copies).</summary>
    public static Task RunSta(Action work)
    {
        var tcs = new TaskCompletionSource();
        var t = new Thread(() => { try { work(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    public static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() => { try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); } });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        return tcs.Task;
    }

    public static void Open(string path, string? args = null)
    {
        if (App.TestMode) { App.TestLaunches.Add("open:" + path); return; }
        Unelevated.Start(path, args ?? "", Path.GetDirectoryName(path));
    }

    /// <summary>Restores items from the Recycle Bin by their original full paths (undo of a delete).</summary>
    public static int Restore(IEnumerable<string> originals)
    {
        var want = originals.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            foreach (dynamic item in shell.NameSpace(10).Items())
            {
                string from = "";
                try { from = item.ExtendedProperty("System.Recycle.DeletedFrom") as string ?? ""; } catch (Exception) { }
                string name = item.Name;
                if (from.Length == 0 || !want.Contains(Path.Combine(from, name)) && !want.Any(w => string.Equals(Path.GetDirectoryName(w), from, StringComparison.OrdinalIgnoreCase) && Path.GetFileNameWithoutExtension(w).Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                item.InvokeVerb("undelete");
                n++;
            }
        }
        catch (Exception ex) { Log.Write("Restore failed: " + ex.Message); }
        return n;
    }

    /// <summary>Total size and item count of a folder (cancellable; skips what it can't read).</summary>
    public static (long Bytes, int Files, int Folders) Measure(string dir, CancellationToken ct)
    {
        long bytes = 0; int files = 0, folders = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var fsi in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts))
            {
                if (ct.IsCancellationRequested) break;
                if (fsi is FileInfo f) { bytes += f.Length; files++; } else folders++;
            }
        }
        catch (Exception) { }
        return (bytes, files, folders);
    }
}
