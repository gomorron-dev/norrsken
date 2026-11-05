using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Core;

/// <summary>
/// Every app's title bar and border in the Norrsken theme, through the documented DWM colour
/// attributes (the same ones apps use for themselves). Applied as windows appear; reset on exit.
/// Apps that draw their own title bar (Chrome, Discord…) keep theirs.
/// </summary>
public static class FrameStyler
{
    const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
    const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    const uint EVENT_OBJECT_SHOW = 0x8002;
    static WinEventProc? _proc;
    static IntPtr _hook;
    static (int Caption, int Text, int Border) _colors;
    static readonly HashSet<IntPtr> Styled = new();

    public static List<string> TestLog { get; } = new();

    static int Ref(Color c) => c.R | (c.G << 8) | (c.B << 16);

    static (int, int, int) ColorsFor(string theme)
    {
        var (_, surface, text) = Theme.Preview(theme);
        Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        return (Ref(surface), Ref(text), Ref(Mix(surface, text, theme == "high-contrast" ? 1 : 0.16)));
    }

    public static void Start()
    {
        if (!App.Settings.WindowFrames) return;
        _colors = ColorsFor(Theme.Current);
        if (App.TestMode) { TestLog.Add($"frames → caption {_colors.Caption:X6}, text {_colors.Text:X6}, border {_colors.Border:X6}"); return; }
        if (_hook == IntPtr.Zero)
        {
            _proc = (_, _, hwnd, idObject, _, _, _) => { if (idObject == 0 && hwnd != IntPtr.Zero) Style(hwnd); };
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        }
        EnumWindows((h, _) => { Style(h); return true; }, IntPtr.Zero);
    }

    public static void Refresh()
    {
        Styled.Clear();
        if (App.Settings.WindowFrames) Start(); else Stop();
    }

    static void Style(IntPtr h)
    {
        if (GetAncestor(h, GA_ROOT) != h || !IsWindowVisible(h)) return;
        if ((GetWindowLongPtr(h, GWL_STYLE).ToInt64() & WS_CAPTION) != WS_CAPTION) return;
        var (caption, text, border) = _colors;
        DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref caption, 4);
        DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref text, 4);
        DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref border, 4);
        lock (Styled) Styled.Add(h);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        if (App.TestMode) return;
        int def = DWMWA_COLOR_DEFAULT;
        lock (Styled)
        {
            foreach (var h in Styled.Where(IsWindow))
            {
                DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref def, 4);
                DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref def, 4);
                DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref def, 4);
            }
            Styled.Clear();
        }
    }
}

/// <summary>
/// Norrsken's pointer set: black shapes with a white rim (readable on any background), drawn
/// here at 32/48/64 px and installed as this account's cursor scheme. The previous scheme is
/// saved first and put back if the setting is switched off.
/// </summary>
public static class NorrskenCursors
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint flags);
    const uint SPI_SETCURSORS = 0x57;
    const string Key = @"Control Panel\Cursors";
    const int Version = 1;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "cursors");
    static string Backup => Path.Combine(ShellSettings.Directory, "cursors-before-norrsken.json");

    record Shape(string Name, string[] Fill, string[] Strokes, double HotX, double HotY, double Rotate = 0);

    const string Arrow = "M3,2 L3,24 L8.5,18.8 L12,27 L15.6,25.5 L12.2,17.6 L19.5,17.6 Z";
    const string DoubleArrow = "M16,3 L21.5,9.5 H18 V22.5 H21.5 L16,29 L10.5,22.5 H14 V9.5 H10.5 Z";

    static readonly Shape[] Shapes =
    [
        new("Arrow", [Arrow], [], 3, 2),
        new("Hand", ["M11,4 C12.4,4 13.4,5 13.4,6.4 V13 C14,12.6 15.8,12.5 16.4,13.6 C17.2,13 18.9,13.1 19.4,14.4 C20.2,14 22,14.3 22,16 V21 C22,25 19.5,28 15.5,28 H14 C11.5,28 10,26.8 8.6,24.8 L5,19.5 C4.3,18.4 4.7,17.2 5.7,16.8 C6.6,16.4 7.6,16.8 8.4,17.8 L8.6,18 V6.4 C8.6,5 9.6,4 11,4 Z"], [], 11, 4),
        new("IBeam", [], ["M11.5,4.5 H13 C14.4,4.5 15.2,5 16,5.8 C16.8,5 17.6,4.5 19,4.5 H20.5 M16,5.8 V26.2 M11.5,27.5 H13 C14.4,27.5 15.2,27 16,26.2 C16.8,27 17.6,27.5 19,27.5 H20.5"], 16, 16),
        new("Wait", [], ["M16,7 A9,9 0 1 1 15.99,7 Z"], 16, 16),
        new("AppStarting", [Arrow], ["M24,19 A5,5 0 1 1 23.99,19 Z"], 3, 2),
        new("Crosshair", [], ["M16,4 V13 M16,19 V28 M4,16 H13 M19,16 H28"], 16, 16),
        new("SizeNS", [DoubleArrow], [], 16, 16),
        new("SizeWE", [DoubleArrow], [], 16, 16, 90),
        new("SizeNWSE", [DoubleArrow], [], 16, 16, -45),
        new("SizeNESW", [DoubleArrow], [], 16, 16, 45),
        new("SizeAll", ["M16,2 L20.5,7 H17.5 V14.5 H25 V11.5 L30,16 L25,20.5 V17.5 H17.5 V25 H20.5 L16,30 L11.5,25 H14.5 V17.5 H7 V20.5 L2,16 L7,11.5 V14.5 H14.5 V7 H11.5 Z"], [], 16, 16),
        new("No", [], ["M16,6.5 A9.5,9.5 0 1 1 15.99,6.5 Z M9.3,9.3 L22.7,22.7"], 16, 16),
        new("UpArrow", ["M16,3 L24,13 H19 V28 H13 V13 H8 Z"], [], 16, 3),
        new("Help", [Arrow], ["M21,8 A3,3 0 1 1 25.2,10.6 C24.3,11.1 24,11.7 24,12.8 M24,16 H24.05"], 3, 2),
    ];

    public static void Apply()
    {
        if (App.TestMode) return;
        try
        {
            if (!App.Settings.CustomCursors) { Restore(); return; }
            Directory.CreateDirectory(Dir);
            string stamp = Path.Combine(Dir, $"v{Version}");
            if (!File.Exists(stamp))
            {
                foreach (var s in Shapes) Write(Path.Combine(Dir, s.Name + ".cur"), s);
                File.WriteAllText(stamp, "");
            }
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if ((key.GetValue("") as string) == "Norrsken" && key.GetValue("Arrow") is string a && a.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(Backup))
            {
                var before = key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "");
                Directory.CreateDirectory(ShellSettings.Directory);
                File.WriteAllText(Backup, JsonSerializer.Serialize(before));
            }
            key.SetValue("", "Norrsken");
            foreach (var s in Shapes) key.SetValue(s.Name, Path.Combine(Dir, s.Name + ".cur"), RegistryValueKind.ExpandString);
            SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0x01 | 0x02);
            Log.Write("Norrsken cursors applied");
        }
        catch (Exception ex) { Log.Write("Cursors not applied: " + ex.Message); }
    }

    public static void Restore()
    {
        if (App.TestMode || !File.Exists(Backup)) return;
        try
        {
            var before = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Backup)) ?? new();
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            foreach (var s in Shapes) key.SetValue(s.Name, before.GetValueOrDefault(s.Name, ""), RegistryValueKind.ExpandString);
            key.SetValue("", before.GetValueOrDefault("", ""));
            SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0x01 | 0x02);
            File.Delete(Backup);
        }
        catch (Exception ex) { Log.Write("Cursors not restored: " + ex.Message); }
    }

    /// <summary>Test/preview: one cursor drawn at a size.</summary>
    internal static BitmapSource Render(string name, int size) => Draw(Shapes.First(s => s.Name == name), size);

    static BitmapSource Draw(Shape s, int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
            if (s.Rotate != 0) dc.PushTransform(new RotateTransform(s.Rotate, 16, 16));
            var rim = new Pen(Brushes.White, 1.8) { LineJoin = PenLineJoin.Round };
            var shadow = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0));
            foreach (var f in s.Fill)
            {
                var g = Geometry.Parse(f);
                dc.PushTransform(new TranslateTransform(0.6, 1));
                dc.DrawGeometry(shadow, new Pen(shadow, 2.4) { LineJoin = PenLineJoin.Round }, g);
                dc.Pop();
                dc.DrawGeometry(Brushes.Black, rim, g);
            }
            foreach (var st in s.Strokes)
            {
                var g = Geometry.Parse(st);
                var outer = new Pen(Brushes.White, 4.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                var inner = new Pen(Brushes.Black, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                if (s.Name == "Wait" || s.Name == "AppStarting") { outer.Thickness = 5.4; inner.Thickness = 3; }
                dc.DrawGeometry(null, outer, g);
                dc.DrawGeometry(null, inner, g);
                if (s.Name is "Wait" or "AppStarting")
                {
                    // A lighter quarter on the ring reads as "working".
                    var c = g.Bounds;
                    double cx = (c.Left + c.Right) / 2, cy = (c.Top + c.Bottom) / 2, r = c.Width / 2;
                    var arc = Geometry.Parse(FormattableString.Invariant($"M{cx},{cy - r} A{r},{r} 0 0 1 {cx + r},{cy}"));
                    dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(150, 150, 150)), 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
                }
            }
            if (s.Rotate != 0) dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    /// <summary>A multi-size .cur with 32-bit DIB frames (hotspots scaled per size).</summary>
    static void Write(string path, Shape s)
    {
        int[] sizes = [32, 48, 64];
        var frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            var bmp = Draw(s, size);
            var px = new byte[size * size * 4];
            bmp.CopyPixels(px, size * 4, 0);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            int maskRow = ((size + 31) / 32) * 4;
            w.Write(40); w.Write(size); w.Write(size * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(size * size * 4 + maskRow * size); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = size - 1; y >= 0; y--) w.Write(px, y * size * 4, size * 4); // bottom-up
            w.Write(new byte[maskRow * size]);
            w.Flush();
            frames.Add(ms.ToArray());
        }
        using var fs = File.Create(path);
        using var o = new BinaryWriter(fs);
        o.Write((short)0); o.Write((short)2); o.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            o.Write((byte)size); o.Write((byte)size); o.Write((byte)0); o.Write((byte)0);
            o.Write((short)Math.Round(s.HotX * size / 32)); o.Write((short)Math.Round(s.HotY * size / 32));
            o.Write(frames[i].Length); o.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) o.Write(f);
    }
}

/// <summary>
/// Norrsken OS, once per account: the Windows apps Norrsken replaces step aside. Notepad and
/// Snipping Tool are removed for this account (both reinstallable from the Microsoft Store);
/// text files and folders open in Norrsken Notes and Files. File Explorer and Windows Settings
/// can't be removed (Windows itself needs them), so their shortcuts open Norrsken's apps instead.
/// Everything here is per-user and undone by switching "Replace Windows apps" off.
/// </summary>
public static class AppRetirement
{
    static readonly string[] Families = ["Microsoft.WindowsNotepad_8wekyb3d8bbwe", "Microsoft.ScreenSketch_8wekyb3d8bbwe"];
    static readonly string[] TextTypes = [".txt", ".log", ".md", ".ini", ".cfg", ".csv", ".json", ".xml", ".yml", ".yaml", ".toml"];
    const string NotesProgId = "Norrsken.Notes.Text";

    public static List<string> TestLog { get; } = new();

    static readonly string[] MediaFamilies = ["Microsoft.Windows.Photos_8wekyb3d8bbwe", "Microsoft.ZuneMusic_8wekyb3d8bbwe", "Microsoft.ZuneVideo_8wekyb3d8bbwe"];

    /// <summary>Photos, Media Player and Film &amp; TV step aside once Flickerbox is installed (never before: something must open media).</summary>
    public static async Task RetireMediaAppsAsync()
    {
        var s = App.Settings;
        if (App.TestMode || !s.RetireReplacedApps || s.MediaAppsRetired || !s.ReplaceSystemApps || !File.Exists(AppInstaller.FlickerboxExe)) return;
        await RemovePackagesAsync(MediaFamilies);
        s.MediaAppsRetired = true;
        s.Save();
    }

    public static async Task RunOnceAsync()
    {
        await RetireMediaAppsAsync();
        var s = App.Settings;
        if (!s.RetireReplacedApps || s.ReplacedAppsRetired || !s.ReplaceSystemApps) return;
        if (App.TestMode) { TestLog.Add("remove Notepad, Snipping Tool; .txt and folders → Norrsken"); s.ReplacedAppsRetired = true; return; }
        RegisterHandlers(Environment.ProcessPath!);
        await RemovePackagesAsync(Families);
        s.ReplacedAppsRetired = true;
        s.Save();
    }

    static async Task RemovePackagesAsync(string[] families)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            foreach (var family in families)
                foreach (var p in pm.FindPackagesForUser("", family).ToList())
                {
                    try { await pm.RemovePackageAsync(p.Id.FullName); Log.Write($"Removed {p.Id.Name} (replaced by Norrsken)"); }
                    catch (Exception ex) { Log.Write($"{p.Id.Name} not removed: {ex.Message}"); }
                }
        }
        catch (Exception ex) { Log.Write("App removal skipped: " + ex.Message); }
    }

    /// <summary>Per-user handlers: "Open with › Notes" for text files; folders and drives open in Files.</summary>
    public static void RegisterHandlers(string exe)
    {
        try
        {
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{NotesProgId}"))
            {
                prog.SetValue("", "Text document");
                prog.CreateSubKey("DefaultIcon").SetValue("", Path.Combine(NorrskenApps.IconDir, "notes.ico"));
                prog.CreateSubKey(@"shell\open\command").SetValue("", $"\"{exe}\" --app notes \"%1\"");
            }
            foreach (var ext in TextTypes)
                using (var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids")) k.SetValue(NotesProgId, Array.Empty<byte>(), RegistryValueKind.None);
            foreach (var cls in new[] { "Directory", "Drive" })
                using (var shell = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{cls}\shell"))
                {
                    shell.SetValue("", "norrsken");
                    using var verb = shell.CreateSubKey("norrsken");
                    verb.SetValue("", "Open in Files");
                    verb.SetValue("Icon", Path.Combine(NorrskenApps.IconDir, "files.ico"));
                    verb.CreateSubKey("command").SetValue("", $"\"{exe}\" --app files \"%1\"");
                }
            using (var kb = Registry.CurrentUser.CreateSubKey(@"Control Panel\Keyboard")) kb.SetValue("PrintScreenKeyForSnippingEnabled", 0, RegistryValueKind.DWord);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Write("Handlers not registered: " + ex.Message); }
    }

    /// <summary>"Replace Windows apps" switched off: folders open in File Explorer again.</summary>
    public static void UnregisterHandlers()
    {
        if (App.TestMode) { TestLog.Add("handlers removed"); return; }
        try
        {
            foreach (var cls in new[] { "Directory", "Drive" })
                using (var shell = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{cls}\shell", true))
                {
                    if (shell == null) continue;
                    if ((shell.GetValue("") as string) == "norrsken") shell.DeleteValue("");
                    shell.DeleteSubKeyTree("norrsken", false);
                }
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log.Write("Handlers not removed: " + ex.Message); }
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int evt, uint flags, IntPtr a, IntPtr b);
}
