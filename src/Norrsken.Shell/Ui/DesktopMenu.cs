using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>Norrsken's right-click menu on the desktop: for an icon, or for the desktop itself.</summary>
public static class DesktopMenu
{
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("shell32.dll")] static extern void SHChangeNotify(int evt, uint flags, IntPtr a, IntPtr b);

    static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    static string PublicDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    /// <summary>The desktop item under a screen point (via UI Automation), as a file path; null for empty desktop.</summary>
    internal static string? ItemAt(int x, int y)
    {
        try
        {
            var el = AutomationElement.FromPoint(new Point(x, y));
            if (el?.Current.ControlType != ControlType.ListItem) return null;
            string name = el.Current.Name;
            foreach (var dir in new[] { UserDesktop, PublicDesktop })
                foreach (var e in Directory.EnumerateFileSystemEntries(dir))
                    if (System.IO.Path.GetFileName(e).Equals(name, StringComparison.OrdinalIgnoreCase) || System.IO.Path.GetFileNameWithoutExtension(e).Equals(name, StringComparison.OrdinalIgnoreCase))
                        return e;
        }
        catch (Exception) { }
        return null;
    }

    public static void Show(Window owner, int x, int y)
    {
        string? item = ItemAt(x, y);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(owner);
        var m = new ContextMenu { Placement = PlacementMode.AbsolutePoint, HorizontalOffset = x / dpi.DpiScaleX, VerticalOffset = y / dpi.DpiScaleY };
        void Item(string header, Action a, bool enabled = true) { var mi = new MenuItem { Header = header, IsEnabled = enabled }; mi.Click += (_, _) => { try { a(); } catch (Exception ex) { Log.Write("Desktop menu: " + ex.Message); } }; m.Items.Add(mi); }
        void Sep() => m.Items.Add(new Separator());
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;

        if (item != null)
        {
            bool dir = Directory.Exists(item);
            Item("Open", () => { if (dir) NorrskenApps.Launch("files", item); else Unelevated.Start(item); });
            if (!dir)
            {
                Item("Open with…", () => Unelevated.Start("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {item}"));
                if (System.IO.Path.GetExtension(item).ToLowerInvariant() is ".txt" or ".md" or ".log" or ".json" or ".ini" or ".cfg" or ".csv" or ".xml" or ".ps1" or ".bat" or ".cmd")
                    Item("Edit in Notes", () => NorrskenApps.Launch("notes", item));
                if (System.IO.Path.GetExtension(item).ToLowerInvariant() is ".exe" or ".lnk" or ".bat" or ".cmd" or ".msi")
                    Item("Run as administrator", () => Unelevated.StartAdmin(item));
            }
            Item("Show in Files", () => NorrskenApps.Launch("files", item));
            Sep();
            Item("Cut", () => ToClipboard(item, move: true));
            Item("Copy", () => ToClipboard(item, move: false));
            Item("Copy as path", () => Clipboard.SetText(item));
            Item("Rename", () => NamePrompt.Ask("Rename", System.IO.Path.GetFileName(item), name =>
            {
                string target = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item)!, name);
                if (dir) Directory.Move(item, target); else File.Move(item, target);
            }));
            Item("Delete", () => Files.Recycle(hwnd, [item]));
            Sep();
            Item("Properties", () => Files.Properties(hwnd, item));
        }
        else
        {
            Item("New folder", () => NamePrompt.Ask("New folder", Unique("New folder"), name => Directory.CreateDirectory(System.IO.Path.Combine(UserDesktop, name))));
            Item("New text document", () => NamePrompt.Ask("New text document", Unique("New text document.txt"), name => File.WriteAllText(System.IO.Path.Combine(UserDesktop, name), "")));
            Item("Paste", () => Paste(hwnd), Clipboard.ContainsFileDropList());
            Sep();
            bool hidden = (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")?.GetValue("HideIcons") as int?) == 1;
            Item(hidden ? "Show desktop icons" : "Hide desktop icons", () => SetIconsHidden(!hidden));
            Item("Refresh", () => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero));
            Sep();
            Item("Open in Files", () => NorrskenApps.Launch("files", UserDesktop));
            Item("Open in Terminal", () => NorrskenApps.Launch("terminal", UserDesktop));
            Sep();
            Item("Wallpaper and theme", () => App.MainDock?.OpenSettings("personal"));
            Item("Display", () => App.MainDock?.OpenSettings("display"));
            Item("Settings", () => App.MainDock?.OpenSettings());
        }

        // A menu from a background process needs the foreground, or it can't close when you click elsewhere.
        keybd_event(0x12, 0, 0, IntPtr.Zero);
        keybd_event(0x12, 0, 2, IntPtr.Zero);
        Interop.Native.ForceForeground(hwnd);
        m.IsOpen = true;
    }

    static string Unique(string name)
    {
        string path = System.IO.Path.Combine(UserDesktop, name);
        if (!File.Exists(path) && !Directory.Exists(path)) return name;
        string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
        for (int i = 2; ; i++) { string n = $"{stem} ({i}){ext}"; if (!File.Exists(System.IO.Path.Combine(UserDesktop, n)) && !Directory.Exists(System.IO.Path.Combine(UserDesktop, n))) return n; }
    }

    static void ToClipboard(string path, bool move)
    {
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(move ? 2 : 5)));
        Clipboard.SetDataObject(data, true);
    }

    static void Paste(IntPtr hwnd)
    {
        var files = Clipboard.GetFileDropList().Cast<string>().ToList();
        bool move = Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4 && (BitConverter.ToInt32(ms.ToArray(), 0) & 2) != 0;
        _ = Files.RunSta(() => { if (move) Files.Move(hwnd, files, UserDesktop); else Files.Copy(hwnd, files, UserDesktop); });
    }

    static void SetIconsHidden(bool hide)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
            k.SetValue("HideIcons", hide ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        // Explorer re-reads it when its desktop view is told to toggle (the same command its own menu sends).
        var progman = FindWindowW("Progman", null);
        var view = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view == IntPtr.Zero)
            Interop.Native.EnumWindows((h, _) => { var v = FindWindowExW(h, IntPtr.Zero, "SHELLDLL_DefView", null); if (v != IntPtr.Zero) { view = v; return false; } return true; }, IntPtr.Zero);
        if (view != IntPtr.Zero) Interop.Native.PostMessageW(view, 0x0111 /* WM_COMMAND */, (IntPtr)0x7402, IntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
}

/// <summary>A small Norrsken prompt for a name (rename, new folder…).</summary>
public sealed class NamePrompt : CardWindow
{
    readonly TextBox _box = new();
    readonly TextBlock _error;
    readonly Action<string> _done;

    public static void Ask(string title, string initial, Action<string> done)
    {
        var p = new NamePrompt(title, initial, done);
        p.Show();
        p.Activate();
    }

    NamePrompt(string title, string initial, Action<string> done) : base(400)
    {
        _done = done;
        Title = title;
        var s = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        s.Children.Add(Text(title, 16, bold: true));
        var field = new Border { CornerRadius = Theme.R(10), BorderThickness = new Thickness(1), Height = 38, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 14, 0, 0) };
        field.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        field.SetResourceReference(Border.BorderBrushProperty, "Border");
        _box.Style = (Style)FindResource("PlainTextBox");
        _box.FontSize = 14;
        _box.Text = initial;
        field.Child = _box;
        s.Children.Add(field);
        _error = Text("", 12, dim: true, wrap: true);
        _error.Margin = new Thickness(2, 8, 0, 0);
        s.Children.Add(_error);
        Card.Child = s;
        _box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        Loaded += (_, _) =>
        {
            _box.Focus();
            int stem = System.IO.Path.HasExtension(initial) && !initial.StartsWith("New folder") ? System.IO.Path.GetFileNameWithoutExtension(initial).Length : initial.Length;
            _box.Select(0, stem);
        };
    }

    void Commit()
    {
        string name = _box.Text.Trim();
        if (name.Length == 0 || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { _error.Text = "A name can't be empty or contain \\ / : * ? \" < > |"; return; }
        try { _done(name); Dismiss(); }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
}
