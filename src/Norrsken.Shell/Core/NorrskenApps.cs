using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Norrsken.Shell.Core;

/// <summary>
/// Norrsken's own apps (Files, Notes, Screenshot, Settings). Each gets a Start-menu shortcut with
/// its own AppUserModelID, so the launcher lists it and the dock groups its windows under its icon.
/// Files and Notes run as their own processes (`--app files|notes`); Screenshot and Settings live
/// inside the running dock and are reached through named events.
/// </summary>
public static class NorrskenApps
{
    public sealed record Info(string Id, string Aumid, string Name, string Glyph, string Description);

    public static readonly IReadOnlyList<Info> All =
    [
        new("files", "Norrsken.Files", "Files", "files", "Browse and manage your files"),
        new("notes", "Norrsken.Notes", "Notes", "note", "Plain text editor"),
        new("capture", "Norrsken.Capture", "Screenshot", "snip", "Capture part of the screen"),
        new("settings", "Norrsken.Settings", "Settings", "settings", "Norrsken and Windows settings"),
        new("terminal", "Norrsken.Terminal", "Terminal", "terminal", "PowerShell, Command Prompt and more"),
        new("taskmgr", "Norrsken.TaskManager", "Task Manager", "activity", "See and stop running apps"),
        new("bin", "Norrsken.Bin", "Bin", "trash", "Deleted files: put back, keep or shred"),
    ];

    /// <summary>Windows apps that Norrsken replaces: hidden from the launcher, removed on Norrsken OS.</summary>
    public static readonly string[] ReplacedParsingNames =
    [
        "Microsoft.Windows.Explorer", "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel",
        "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "Microsoft.ScreenSketch_8wekyb3d8bbwe!App",
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App",
    ];

    /// <summary>Windows media apps hidden from the launcher once Flickerbox is installed.</summary>
    public static readonly string[] ReplacedByFlickerbox = ["Microsoft.Windows.Photos_", "Microsoft.ZuneMusic_", "Microsoft.ZuneVideo_"];

    /// <summary>Windows tools Norrsken replaces, hidden from the launcher by the program they start.</summary>
    public static readonly string[] ReplacedExecutables = ["taskmgr.exe", "cmd.exe", "powershell.exe", "notepad.exe", "SnippingTool.exe", "explorer.exe", "control.exe", "SystemSettings.exe"];

    public const string CaptureEventName = @"Local\Norrsken.Shell.Capture";
    public const string SettingsEventName = @"Local\Norrsken.Shell.Settings";
    public const string ThemeEventName = @"Local\Norrsken.Shell.Theme";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

    public static Info Get(string id) => All.First(a => a.Id == id);

    /// <summary>Starts a Norrsken app in its own process (the running dock stays untouched).</summary>
    public static void Launch(string id, string? arg = null)
    {
        if (App.TestMode) { App.TestLaunches.Add("norrsken:" + id + (arg != null ? " " + arg : "")); return; }
        AllowSetForegroundWindow(-1);
        // A trailing backslash would escape the closing quote ("C:\" → C:"), so double it.
        string args = $"--app {id}" + (arg != null ? $" \"{arg}{(arg.EndsWith('\\') ? "\\" : "")}\"" : "");
        Unelevated.Start(Environment.ProcessPath!, args);
    }

    /// <summary>`--app id [arg]`: runs one Norrsken app window in this process.</summary>
    public static void RunApp(Application app, string id, string? arg)
    {
        var info = All.FirstOrDefault(a => a.Id == id);
        if (info == null) { app.Shutdown(64); return; }
        SetCurrentProcessExplicitAppUserModelID(info.Aumid);
        App.LoadSettingsForApp();
        FollowThemeChanges();

        // Screenshot and Settings belong to the running dock when there is one.
        if (id is "capture" or "settings" && EventWaitHandle.TryOpenExisting(id == "capture" ? CaptureEventName : SettingsEventName, out var signal))
        {
            signal.Set();
            app.Shutdown();
            return;
        }
        app.ShutdownMode = ShutdownMode.OnLastWindowClose;
        switch (id)
        {
            case "files": new Ui.FilesWindow(arg).Show(); break;
            case "notes": new Ui.NotesWindow(arg).Show(); break;
            case "settings": new Ui.SettingsWindow(null).Show(); break;
            case "taskmgr": new Ui.TaskManagerWindow().Show(); break;
            case "bin": Bin.Watch(); Ui.BinWindow.Open(arg == "--empty"); break;
            case "terminal":
                if (arg == "--admin" && !Unelevated.IsElevated)
                {
                    try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--app terminal") { UseShellExecute = true, Verb = "runas" }); } catch (Exception) { }
                    app.Shutdown();
                    return;
                }
                new Ui.TerminalWindow(arg == "--admin" ? null : arg).Show();
                break;
            case "capture":
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Ui.CaptureOverlay.Start(onDone: () => app.Shutdown());
                break;
        }
    }

    /// <summary>App processes repaint live when the theme changes in the dock.</summary>
    /// <summary>
    /// App windows (Files, Bin, Settings in its own process, Norrsken Capture) follow the dock's look and sounds live:
    /// the theme, accent, corners, motion and sound settings, not just the theme's name.
    /// </summary>
    internal static void FollowThemeChanges()
    {
        var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ThemeEventName);
        var dispatcher = Application.Current.Dispatcher;
        new Thread(() =>
        {
            string last = App.Settings.LookKey;
            while (true)
            {
                ev.WaitOne();
                Thread.Sleep(150);
                dispatcher.BeginInvoke(() =>
                {
                    var s = ShellSettings.Load();
                    if (s.LookKey == last) return;
                    last = s.LookKey;
                    App.Settings.TakeLook(s);
                    Theme.Apply(s.Theme);
                });
                Thread.Sleep(600);
            }
        }) { IsBackground = true }.Start();
    }

    /// <summary>Pulses the theme event so open Norrsken app windows pick up a new theme.</summary>
    public static void BroadcastTheme()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ThemeEventName);
            ev.Set();
            Task.Delay(400).ContinueWith(_ => { try { ev.Reset(); } catch (ObjectDisposedException) { } });
        }
        catch (Exception ex) { Log.Write("Theme broadcast failed: " + ex.Message); }
    }

    // ───────────────────────── Start-menu shortcuts ─────────────────────────

    public static string IconDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "icons");

    /// <summary>Creates (or refreshes) the Start-menu shortcuts. Cheap and idempotent; runs at every dock start.</summary>
    public static void EnsureShortcuts()
    {
        if (App.TestMode) return;
        try
        {
            Directory.CreateDirectory(IconDir);
            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            foreach (var a in All)
            {
                string ico = Path.Combine(IconDir, a.Id + ".ico");
                if (!File.Exists(ico)) WriteIcon(ico, a.Glyph);
                string args = a.Id == "capture" ? "--app capture" : $"--app {a.Id}";
                ShellLink.Save(Path.Combine(programs, a.Name + ".lnk"), Environment.ProcessPath!, args, ico, a.Description, a.Aumid);
            }
        }
        catch (Exception ex) { Log.Write("Shortcuts not created: " + ex.Message); }
        MediaDefaults.EnsureShortcut(); // Flickerbox installed by an older Norrsken had none
    }

    /// <summary>A multi-size .ico (PNG frames): the glyph in white on a graphite tile.</summary>
    public static void WriteIcon(string path, string glyph)
    {
        int[] sizes = [256, 64, 48, 32, 24, 16];
        var frames = sizes.Select(s => Png(RenderTile(glyph, s))).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0); w.Write((short)1); w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int s = sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
            w.Write(frames[i].Length); w.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) w.Write(f);
    }

    public static BitmapSource RenderTile(string glyph, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double inset = size * 0.06, r = size * 0.24;
            var tile = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(62, 62, 64), Color.FromRgb(28, 28, 30), 90),
                new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), Math.Max(1, size / 96.0)), tile, r, r);
            if (IconPack.Named(glyph) is { } g)
            {
                double scale = (size - 2 * inset) * 0.62 / 24;
                dc.PushTransform(new TranslateTransform(size / 2.0 - 12 * scale, size / 2.0 - 12 * scale));
                dc.PushTransform(new ScaleTransform(scale, scale));
                dc.DrawGeometry(null, new Pen(Brushes.White, size <= 24 ? 2.2 : 1.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
                dc.Pop(); dc.Pop();
            }
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// For an app whose own icon is missing: a Norrsken tile with its initial, instead of Windows' blank page.
    /// </summary>
    public static BitmapSource RenderLetter(string name, int size)
    {
        string letter = name.FirstOrDefault(char.IsLetterOrDigit) is var c && c != default ? char.ToUpperInvariant(c).ToString() : "?";
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            double inset = size * 0.08, r = size * 0.24;
            var tile = new Rect(inset, inset, size - 2 * inset, size - 2 * inset);
            dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromRgb(70, 70, 74), Color.FromRgb(34, 34, 37), 90),
                new Pen(new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)), Math.Max(1, size / 96.0)), tile, r, r);
            var text = new FormattedText(letter, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                size * 0.42, Brushes.White, 1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
    static byte[] Png(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>Writes .lnk files with an AppUserModelID (IShellLink + IPropertyStore).</summary>
internal static class ShellLink
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class CShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellLinkW
    {
        void GetPath(IntPtr a, int b, IntPtr c, int d);
        void GetIDList(out IntPtr p);
        void SetIDList(IntPtr p);
        void GetDescription(IntPtr a, int b);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetWorkingDirectory(IntPtr a, int b);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetArguments(IntPtr a, int b);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string s);
        void GetHotkey(out short k);
        void SetHotkey(short k);
        void GetShowCmd(out int c);
        void SetShowCmd(int c);
        void GetIconLocation(IntPtr a, int b, out int i);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string s, int i);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string s, int r);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string s);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFile
    {
        void GetClassID(out Guid g);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string f, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string f, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string f);
        void GetCurFile(out IntPtr f);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PropKey k);
        void GetValue(ref PropKey k, out PropVariant v);
        void SetValue(ref PropKey k, ref PropVariant v);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] struct PropVariant { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    public static void Save(string lnk, string target, string args, string? icon, string description, string? aumid)
    {
        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(args);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? target);
            if (icon != null) link.SetIconLocation(icon, 0);
            link.SetDescription(description);
            if (aumid == null) { ((IPersistFile)link).Save(lnk, true); return; }
            var store = (IPropertyStore)link;
            var key = new PropKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            var pv = new PropVariant { vt = 31, p = Marshal.StringToCoTaskMemUni(aumid) };
            try { store.SetValue(ref key, ref pv); store.Commit(); }
            finally { Marshal.FreeCoTaskMem(pv.p); }
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }
}
