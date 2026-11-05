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
/// Norrsken's pointers, drawn here and installed as this account's cursor scheme (the previous one is saved
/// and put back if you switch them off). Rounded shapes with a crisp rim, a soft real shadow and a faint sheen,
/// in six sizes up to 128 px so they stay sharp at any display scaling. The busy pointers actually spin.
/// Style: dark (default), light, or your accent colour.
/// </summary>
public static class NorrskenCursors
{
    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint flags);
    const uint SPI_SETCURSORS = 0x57;
    const string Key = @"Control Panel\Cursors";
    const int Version = 2;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Norrsken", "cursors");
    static string Backup => Path.Combine(ShellSettings.Directory, "cursors-before-norrsken.json");

    record Shape(string Name, string[] Fill, string[] Strokes, double HotX, double HotY, double Rotate = 0, bool Spins = false);

    // Rounded corners everywhere: it reads softer and closer to the rest of Norrsken than a sharp classic arrow.
    const string Arrow = "M4.5,2.6 L4.5,23.9 C4.5,24.7 5.4,25.1 6,24.5 L10.4,20.1 L13.4,27.2 C13.6,27.8 14.3,28 14.8,27.8 L17,26.9 C17.5,26.7 17.8,26.1 17.5,25.5 L14.5,18.6 L20.7,18.6 C21.5,18.6 21.9,17.6 21.3,17.1 L5.9,2 C5.4,1.5 4.5,1.9 4.5,2.6 Z";
    const string DoubleArrow = "M16,3 L21.6,9.4 C21.9,9.8 21.6,10.4 21.1,10.4 H18 V21.6 H21.1 C21.6,21.6 21.9,22.2 21.6,22.6 L16,29 L10.4,22.6 C10.1,22.2 10.4,21.6 10.9,21.6 H14 V10.4 H10.9 C10.4,10.4 10.1,9.8 10.4,9.4 Z";

    static readonly Shape[] Shapes =
    [
        new("Arrow", [Arrow], [], 4.5, 2.6),
        new("Hand", ["M11,3.6 C12.5,3.6 13.5,4.6 13.5,6.1 V12.8 C14.2,12.3 16,12.3 16.6,13.5 C17.4,12.9 19.2,13 19.7,14.4 C20.6,13.9 22.4,14.4 22.4,16.2 V21 C22.4,25.2 19.8,28.3 15.6,28.3 H14 C11.4,28.3 9.9,27 8.4,24.9 L4.8,19.6 C4.1,18.5 4.5,17.2 5.6,16.7 C6.6,16.3 7.6,16.8 8.4,17.8 L8.5,18 V6.1 C8.5,4.6 9.5,3.6 11,3.6 Z"], [], 11, 3.6),
        new("IBeam", [], ["M11.5,4.5 H13 C14.4,4.5 15.2,5 16,5.8 C16.8,5 17.6,4.5 19,4.5 H20.5 M16,5.8 V26.2 M11.5,27.5 H13 C14.4,27.5 15.2,27 16,26.2 C16.8,27 17.6,27.5 19,27.5 H20.5"], 16, 16),
        new("Wait", [], [], 16, 16, Spins: true),
        new("AppStarting", [Arrow], [], 4.5, 2.6, Spins: true),
        new("Crosshair", [], ["M16,4 V12.5 M16,19.5 V28 M4,16 H12.5 M19.5,16 H28 M16,15.9 V16.1"], 16, 16),
        new("SizeNS", [DoubleArrow], [], 16, 16),
        new("SizeWE", [DoubleArrow], [], 16, 16, 90),
        new("SizeNWSE", [DoubleArrow], [], 16, 16, -45),
        new("SizeNESW", [DoubleArrow], [], 16, 16, 45),
        new("SizeAll", ["M16,2 L20.6,7.1 H17.6 V14.4 H24.9 V11.4 L30,16 L24.9,20.6 V17.6 H17.6 V24.9 H20.6 L16,30 L11.4,24.9 H14.4 V17.6 H7.1 V20.6 L2,16 L7.1,11.4 V14.4 H14.4 V7.1 H11.4 Z"], [], 16, 16),
        new("No", [], ["M16,6.5 A9.5,9.5 0 1 1 15.99,6.5 Z M9.3,9.3 L22.7,22.7"], 16, 16),
        new("UpArrow", ["M16,3 L24,13 H19 V28 H13 V13 H8 Z"], [], 16, 3),
        new("Help", [Arrow], ["M21.2,8.2 A3,3 0 1 1 25.4,10.8 C24.5,11.3 24.2,11.9 24.2,13 M24.2,16.1 H24.25"], 4.5, 2.6),
        new("NWPen", ["M21.5,4.5 L27.5,10.5 L12.5,25.5 L5,27 L6.5,19.5 Z"], [], 5, 27),
    ];

    static string File_(Shape s) => Path.Combine(Dir, s.Name + (s.Spins ? ".ani" : ".cur"));

    public static void Apply()
    {
        if (App.TestMode) return;
        try
        {
            if (!App.Settings.CustomCursors) { Restore(); return; }
            Directory.CreateDirectory(Dir);
            string style = Style();
            string stamp = Path.Combine(Dir, $"v{Version}-{style}");
            bool fresh = false;
            if (!File.Exists(stamp))
            {
                foreach (var old in Directory.EnumerateFiles(Dir, "v*")) File.Delete(old);
                foreach (var s in Shapes) Write(File_(s), s);
                File.WriteAllText(stamp, "");
                fresh = true;
            }
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (!fresh && (key.GetValue("") as string) == "Norrsken" && key.GetValue("Arrow") is string a && a.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)
                && key.GetValue("Wait") is string w && w.EndsWith(".ani", StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(Backup))
            {
                var before = key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "");
                Directory.CreateDirectory(ShellSettings.Directory);
                File.WriteAllText(Backup, JsonSerializer.Serialize(before));
            }
            key.SetValue("", "Norrsken");
            foreach (var s in Shapes) key.SetValue(s.Name, File_(s), RegistryValueKind.ExpandString);
            SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0x01 | 0x02);
            Log.Write($"Norrsken cursors applied ({style})");
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

    static string Style()
    {
        string s = App.Settings.CursorStyle;
        return s == "accent" && Theme.AccentColor() is { } c ? $"accent{c.R:X2}{c.G:X2}{c.B:X2}" : s is "light" ? "light" : "dark";
    }

    /// <summary>Fill (top, bottom), rim, and the stroke colour for line pointers.</summary>
    static (Color Top, Color Bottom, Color Rim) Palette()
    {
        if (App.Settings.CursorStyle == "light") return (Colors.White, Color.FromRgb(232, 232, 236), Color.FromRgb(22, 22, 24));
        if (App.Settings.CursorStyle == "accent" && Theme.AccentColor() is { } a)
            return (Color.FromRgb((byte)Math.Min(255, a.R + 28), (byte)Math.Min(255, a.G + 28), (byte)Math.Min(255, a.B + 28)), a, Colors.White);
        return (Color.FromRgb(46, 46, 50), Color.FromRgb(14, 14, 16), Colors.White);
    }

    /// <summary>Test/preview: one cursor drawn at a size.</summary>
    internal static BitmapSource Render(string name, int size, double spin = 0) => Draw(Shapes.First(s => s.Name == name), size, spin);
    internal static IEnumerable<string> Names => Shapes.Select(s => s.Name);

    static BitmapSource Draw(Shape s, int size, double spin)
    {
        var (top, bottom, rimColor) = Palette();
        Brush Frozen(Brush b) { b.Freeze(); return b; }
        var fill = Frozen(new LinearGradientBrush(top, bottom, 90));
        var rim = new Pen(Frozen(new SolidColorBrush(rimColor)), 1.7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var ink = Frozen(new SolidColorBrush(bottom));
        double k = size / 32.0;

        // The shape on its own, so the shadow can be drawn from exactly the same outline.
        void Shapes_(DrawingContext dc, bool shadow)
        {
            dc.PushTransform(new ScaleTransform(k, k));
            if (s.Rotate != 0) dc.PushTransform(new RotateTransform(s.Rotate, 16, 16));
            var shadowBrush = Frozen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
            foreach (var f in s.Fill)
            {
                var g = Geometry.Parse(f);
                if (shadow) dc.DrawGeometry(shadowBrush, new Pen(shadowBrush, 2.2) { LineJoin = PenLineJoin.Round }, g);
                else dc.DrawGeometry(fill, rim, g);
            }
            foreach (var st in s.Strokes)
            {
                var g = Geometry.Parse(st);
                var outer = new Pen(shadow ? shadowBrush : Frozen(new SolidColorBrush(rimColor)), 4.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, outer, g);
                if (!shadow) dc.DrawGeometry(null, new Pen(ink, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
            }
            if (s.Spins)
            {
                // A ring with a bright sweep that turns (one frame per angle in the animated cursor).
                double cx = s.Name == "Wait" ? 16 : 23.5, cy = s.Name == "Wait" ? 16 : 22.5, r = s.Name == "Wait" ? 9 : 4.6, w = s.Name == "Wait" ? 3.4 : 2.4;
                var ring = new EllipseGeometry(new Point(cx, cy), r, r);
                dc.DrawGeometry(null, new Pen(shadow ? shadowBrush : Frozen(new SolidColorBrush(rimColor)), w + 2.4), ring);
                if (!shadow)
                {
                    dc.DrawGeometry(null, new Pen(Frozen(new SolidColorBrush(Color.FromArgb(90, bottom.R, bottom.G, bottom.B))), w), ring);
                    double a0 = spin * Math.PI / 180, a1 = a0 + Math.PI * 0.75;
                    var arc = new PathGeometry([new PathFigure(new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0)),
                        [new ArcSegment(new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1)), new Size(r, r), 0, false, SweepDirection.Clockwise, true)], false)]);
                    dc.DrawGeometry(null, new Pen(ink, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, arc);
                }
            }
            if (s.Rotate != 0) dc.Pop();
            dc.Pop();
        }

        // A real, soft shadow: the same shape, blurred and nudged down a little.
        var shadowVisual = new DrawingVisual { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2.4 * k, KernelType = System.Windows.Media.Effects.KernelType.Gaussian }, Opacity = 0.5 };
        using (var dc = shadowVisual.RenderOpen()) { dc.PushTransform(new TranslateTransform(0.5 * k, 1.3 * k)); Shapes_(dc, true); dc.Pop(); }
        var shape = new DrawingVisual();
        using (var dc = shape.RenderOpen()) Shapes_(dc, false);
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(shadowVisual);
        rtb.Render(shape);
        var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        straight.Freeze();
        return straight;
    }

    static readonly int[] Sizes = [32, 48, 64, 80, 96, 128];

    /// <summary>A multi-size .cur (32-bit frames, hotspot scaled per size).</summary>
    static byte[] Cur(Shape s, double spin)
    {
        var frames = new List<byte[]>();
        foreach (int size in Sizes)
        {
            var bmp = Draw(s, size, spin);
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
        using var file = new MemoryStream();
        using var o = new BinaryWriter(file);
        o.Write((short)0); o.Write((short)2); o.Write((short)Sizes.Length);
        int offset = 6 + 16 * Sizes.Length;
        for (int i = 0; i < Sizes.Length; i++)
        {
            int size = Sizes[i];
            o.Write((byte)(size >= 256 ? 0 : size)); o.Write((byte)(size >= 256 ? 0 : size)); o.Write((byte)0); o.Write((byte)0);
            o.Write((short)Math.Round(s.HotX * size / 32)); o.Write((short)Math.Round(s.HotY * size / 32));
            o.Write(frames[i].Length); o.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var f in frames) o.Write(f);
        o.Flush();
        return file.ToArray();
    }

    /// <summary>Writes a .cur, or for spinning pointers an animated .ani (RIFF "ACON", 12 frames, ~0.8 s a turn).</summary>
    static void Write(string path, Shape s)
    {
        if (!s.Spins) { File.WriteAllBytes(path, Cur(s, 0)); return; }
        const int frames = 12, jiffies = 4; // 1 jiffy = 1/60 s
        var icons = Enumerable.Range(0, frames).Select(i => Cur(s, i * 360.0 / frames)).ToList();
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        int list = 4 + icons.Sum(c => 8 + c.Length + (c.Length & 1));
        w.Write("RIFF"u8.ToArray()); w.Write(4 + (8 + 36) + (8 + list)); w.Write("ACON"u8.ToArray());
        w.Write("anih"u8.ToArray()); w.Write(36);
        w.Write(36); w.Write(frames); w.Write(frames); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(jiffies); w.Write(1 /* AF_ICON */);
        w.Write("LIST"u8.ToArray()); w.Write(list); w.Write("fram"u8.ToArray());
        foreach (var c in icons)
        {
            w.Write("icon"u8.ToArray()); w.Write(c.Length); w.Write(c);
            if ((c.Length & 1) == 1) w.Write((byte)0);
        }
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
