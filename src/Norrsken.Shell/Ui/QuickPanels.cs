using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Norrsken.Shell.Core;
using Path = System.Windows.Shapes.Path;

namespace Norrsken.Shell.Ui;

/// <summary>A floating Norrsken card: borderless, themed, fades in, closes on Esc or when it loses focus.</summary>
public abstract class CardWindow : Window
{
    protected readonly Border Card = new();
    readonly ScaleTransform _scale = new(1, 1);
    bool _closing;

    protected CardWindow(double width)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.Height; Width = width + 48;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Card.Margin = new Thickness(24);
        Card.CornerRadius = Theme.R(20);
        Card.BorderThickness = new Thickness(1);
        Card.SetResourceReference(Border.BackgroundProperty, "Surface");
        Card.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        Card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.42, Color = Colors.Black };
        Card.RenderTransformOrigin = new Point(0.5, 0.5);
        Card.RenderTransform = _scale;
        Content = Card;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Dismiss(); e.Handled = true; } };
        Deactivated += (_, _) => { if (!App.TestMode) Dismiss(); };
        Loaded += (_, _) =>
        {
            Place();
            Motion.From(Card, OpacityProperty, 0, 1, 150);
            Motion.From(_scale, ScaleTransform.ScaleXProperty, 0.96, 1, 260, Motion.SoftSpring);
            Motion.From(_scale, ScaleTransform.ScaleYProperty, 0.96, 1, 260, Motion.SoftSpring);
        };
    }

    /// <summary>Where the card sits; default: centred, a little above the middle.</summary>
    protected virtual void Place()
    {
        var a = SystemParameters.WorkArea;
        Left = a.Left + (a.Width - ActualWidth) / 2;
        Top = a.Top + (a.Height - ActualHeight) * 0.32;
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Motion.To(Card, OpacityProperty, 0, 120, done: Close);
    }

    protected static TextBlock Text(string s, double size, bool bold = false, bool dim = false, bool wrap = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDim" : "Text");
        return t;
    }

    protected static Path Glyph(string data, double size)
    {
        var p = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.7, Width = size, Height = size, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text");
        return p;
    }

    /// <summary>A large square choice: glyph above a label.</summary>
    protected Button Choice(string glyph, string label, Action click, string? hint = null)
    {
        var stack = new StackPanel { Margin = new Thickness(6, 14, 6, 12) };
        var g = Glyph(glyph, 24);
        g.Margin = new Thickness(0, 0, 0, 10);
        stack.Children.Add(g);
        var t = Text(label, 12.5);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = stack, Margin = new Thickness(3), ToolTip = hint, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        System.Windows.Automation.AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }
}

/// <summary>Win+R: run a program, open a folder, file or web address. Remembers what you ran.</summary>
public sealed class RunWindow : CardWindow
{
    static RunWindow? _open;
    readonly TextBox _box = new();
    readonly StackPanel _suggest = new();
    readonly TextBlock _hint;
    int _selected = -1;
    List<string> _items = new();

    public static void Toggle()
    {
        if (_open != null) { _open.Dismiss(); return; }
        _open = new RunWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    RunWindow() : base(560)
    {
        Title = "Run";
        var stack = new StackPanel { Margin = new Thickness(20, 18, 20, 16) };
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 10) };
        var g = Glyph("M4.5,12 H18 M13,7 L18,12 L13,17", 15);
        g.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(g);
        head.Children.Add(Text("Run", 15, bold: true));
        stack.Children.Add(head);
        var field = new Border { CornerRadius = Theme.R(12), BorderThickness = new Thickness(1), Height = 44, Padding = new Thickness(14, 0, 14, 0) };
        field.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        field.SetResourceReference(Border.BorderBrushProperty, "Border");
        _box.Style = (Style)FindResource("PlainTextBox");
        _box.FontSize = 15;
        _box.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        field.Child = _box;
        stack.Children.Add(field);
        stack.Children.Add(_suggest);
        _hint = Text("Enter to run  ·  Ctrl+Shift+Enter as administrator  ·  Esc to close", 11.5, dim: true);
        _hint.Margin = new Thickness(4, 10, 0, 0);
        stack.Children.Add(_hint);
        Card.Child = stack;
        _box.TextChanged += (_, _) => Suggest();
        _box.PreviewKeyDown += Keys;
        Loaded += (_, _) => { Suggest(); _box.Focus(); };
    }

    void Suggest()
    {
        string q = _box.Text.Trim();
        var history = App.Settings.RunHistory;
        IEnumerable<string> list = q.Length == 0 ? history.Take(6) : history.Where(h => h.StartsWith(q, StringComparison.OrdinalIgnoreCase) && !h.Equals(q, StringComparison.OrdinalIgnoreCase));
        var more = new List<string>();
        if (q.Length > 0)
        {
            try
            {
                // Paths: complete the folder you're typing.
                string expanded = Environment.ExpandEnvironmentVariables(q);
                string? dir = expanded.EndsWith('\\') ? expanded : System.IO.Path.GetDirectoryName(expanded);
                if (dir != null && System.IO.Path.IsPathRooted(expanded) && Directory.Exists(dir))
                {
                    string stem = expanded.EndsWith('\\') ? "" : System.IO.Path.GetFileName(expanded);
                    more.AddRange(new DirectoryInfo(dir).EnumerateFileSystemInfos(stem + "*").Take(6).Select(f => f.FullName + (f is DirectoryInfo ? "\\" : "")));
                }
                else
                    // Programs on PATH and registered App Paths.
                    more.AddRange(PathPrograms().Where(p => p.StartsWith(q, StringComparison.OrdinalIgnoreCase)).Take(6));
            }
            catch (Exception) { }
        }
        _items = list.Concat(more).Distinct(StringComparer.OrdinalIgnoreCase).Take(7).ToList();
        _selected = -1;
        Render();
    }

    static List<string>? _pathPrograms;
    static List<string> PathPrograms()
    {
        if (_pathPrograms != null) return _pathPrograms;
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(Directory.Exists))
            try { foreach (var f in Directory.EnumerateFiles(dir, "*.exe")) set.Add(System.IO.Path.GetFileNameWithoutExtension(f)); } catch (Exception) { }
        using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"))
            foreach (var n in k?.GetSubKeyNames() ?? []) set.Add(System.IO.Path.GetFileNameWithoutExtension(n));
        return _pathPrograms = set.ToList();
    }

    void Render()
    {
        _suggest.Children.Clear();
        if (_items.Count > 0) _suggest.Margin = new Thickness(0, 8, 0, 0);
        for (int i = 0; i < _items.Count; i++)
        {
            string s = _items[i];
            var row = new Border { CornerRadius = Theme.R(9), Padding = new Thickness(12, 6, 12, 6), Cursor = Cursors.Hand, Child = Text(s, 13), Background = Brushes.Transparent };
            if (i == _selected) row.SetResourceReference(Border.BackgroundProperty, "Selection");
            row.MouseEnter += (_, _) => { if (_suggest.Children.IndexOf(row) != _selected) row.SetResourceReference(Border.BackgroundProperty, "Hover"); };
            row.MouseLeave += (_, _) => { if (_suggest.Children.IndexOf(row) != _selected) row.Background = Brushes.Transparent; };
            row.MouseLeftButtonUp += (_, _) => { _box.Text = s; _box.CaretIndex = s.Length; if (!s.EndsWith('\\')) Execute(false); };
            _suggest.Children.Add(row);
        }
    }

    void Keys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when _items.Count > 0: _selected = Math.Min(_items.Count - 1, _selected + 1); Render(); break;
            case Key.Up when _items.Count > 0: _selected = Math.Max(-1, _selected - 1); Render(); break;
            case Key.Tab when _items.Count > 0: _box.Text = _items[Math.Max(0, _selected)]; _box.CaretIndex = _box.Text.Length; break;
            case Key.Enter:
                if (_selected >= 0) { _box.Text = _items[_selected]; _selected = -1; }
                Execute(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            default: return;
        }
        e.Handled = true;
    }

    void Execute(bool admin)
    {
        string cmd = _box.Text.Trim();
        if (cmd.Length == 0) return;
        if (Run(cmd, admin) is { } error) { _hint.Text = error; return; }
        var h = App.Settings.RunHistory;
        h.RemoveAll(x => x.Equals(cmd, StringComparison.OrdinalIgnoreCase));
        h.Insert(0, cmd);
        if (h.Count > 40) h.RemoveRange(40, h.Count - 40);
        try { App.Settings.Save(); } catch (IOException) { }
        Dismiss();
    }

    /// <summary>Runs a Run-box command the way Windows does; folders open in Files. Returns an error message, or null.</summary>
    public static string? Run(string cmd, bool admin)
    {
        if (App.TestMode) { App.TestLaunches.Add("run:" + cmd); return null; }
        string expanded = Environment.ExpandEnvironmentVariables(cmd.Trim('"'));
        if (Directory.Exists(expanded)) { NorrskenApps.Launch("files", expanded); return null; }
        string file = cmd, args = "";
        if (!File.Exists(expanded))
        {
            if (cmd.StartsWith('"')) { int end = cmd.IndexOf('"', 1); if (end > 0) { file = cmd[1..end]; args = cmd[(end + 1)..].Trim(); } }
            else { int sp = cmd.IndexOf(' '); if (sp > 0) { file = cmd[..sp]; args = cmd[(sp + 1)..]; } }
        }
        else file = expanded;
        try
        {
            if (admin) Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, Verb = "runas" });
            else Unelevated.Start(file, args);
            return null;
        }
        catch (Exception)
        {
            try { Unelevated.Start("cmd.exe", "/c " + cmd); return null; }
            catch (Exception ex) { return $"“{cmd}” couldn't be started: {ex.Message}"; }
        }
    }
}

/// <summary>Alt+F4 on the desktop: Norrsken's power options instead of Windows' "Shut down Windows" box.</summary>
public sealed class PowerDialog : CardWindow
{
    static PowerDialog? _open;

    public static void Open()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new PowerDialog();
        _open.Closed += (_, _) => _open = null;
        ((Window)_open).Show();
        _open.Activate();
    }

    PowerDialog() : base(520)
    {
        Title = "Power";
        var stack = new StackPanel { Margin = new Thickness(18, 18, 18, 14) };
        var t = Text("See you soon", 16, bold: true);
        t.Margin = new Thickness(6, 0, 0, 12);
        stack.Children.Add(t);
        var grid = new UniformGrid { Columns = 5 };
        void Do(Action a) { Dismiss(); if (App.TestMode) App.TestLaunches.Add("power"); else a(); }
        grid.Children.Add(Choice("M12,8 A4,4 0 1 1 11.99,8 Z M19.5,14.5 A8,8 0 1 1 9.5,4.5 A6.5,6.5 0 0 0 19.5,14.5 Z", "Sleep", () => Do(Power.Sleep)));
        grid.Children.Add(Choice("M7,11 V8 A5,5 0 0 1 17,8 V11 M5.5,11 H18.5 V20.5 H5.5 Z", "Lock", () => Do(Power.Lock)));
        grid.Children.Add(Choice("M10,5 H5 V19 H10 M14,8 L18,12 L14,16 M18,12 H9", "Sign out", () => Do(Power.SignOut)));
        grid.Children.Add(Choice("M20,12 A8,8 0 1 1 17.7,6.3 M20,4 V8 H16", "Restart", () => Do(Power.Restart)));
        grid.Children.Add(Choice("M12,3.5 V11 M7.5,6.5 A7.5,7.5 0 1 0 16.5,6.5", "Shut down", () => Do(Power.ShutDown)));
        stack.Children.Add(grid);
        Card.Child = stack;
    }
}

/// <summary>Win+P: how a second screen is used.</summary>
public sealed class ProjectWindow : CardWindow
{
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, IntPtr paths, uint numMode, IntPtr modes, uint flags);
    const uint SDC_APPLY = 0x80, SDC_TOPOLOGY_INTERNAL = 0x1, SDC_TOPOLOGY_CLONE = 0x2, SDC_TOPOLOGY_EXTEND = 0x4, SDC_TOPOLOGY_EXTERNAL = 0x8;
    static ProjectWindow? _open;

    public static void Toggle()
    {
        if (_open != null) { _open.Dismiss(); return; }
        _open = new ProjectWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    ProjectWindow() : base(500)
    {
        Title = "Display mode";
        var stack = new StackPanel { Margin = new Thickness(18, 18, 18, 14) };
        var t = Text("Screens", 16, bold: true);
        t.Margin = new Thickness(6, 0, 0, 12);
        stack.Children.Add(t);
        var grid = new UniformGrid { Columns = 4 };
        void Set(uint topology) { Dismiss(); if (App.TestMode) { App.TestLaunches.Add("display:" + topology); return; } SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | topology); }
        grid.Children.Add(Choice("M3.5,6 H14.5 V15 H3.5 Z M7,19 H11", "This screen", () => Set(SDC_TOPOLOGY_INTERNAL)));
        grid.Children.Add(Choice("M2.5,6 H11 V13 H2.5 Z M13,9 H21.5 V16 H13 Z", "Duplicate", () => Set(SDC_TOPOLOGY_CLONE)));
        grid.Children.Add(Choice("M2,7 H12 V15 H2 Z M12,7 H22 V15 H12 Z", "Extend", () => Set(SDC_TOPOLOGY_EXTEND)));
        grid.Children.Add(Choice("M9.5,6 H20.5 V15 H9.5 Z M13,19 H17", "Other screen", () => Set(SDC_TOPOLOGY_EXTERNAL)));
        stack.Children.Add(grid);
        Card.Child = stack;
    }
}

/// <summary>Win+X: everything power users reach for, as one menu above the dock's corner.</summary>
public static class PowerUserMenu
{
    public static void Show(FrameworkElement anchor)
    {
        var m = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        void Item(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        void Sep() => m.Items.Add(new Separator());
        Item("Apps", () => App.MainDock?.OpenSettings("apps"));
        Item("Power & battery", () => App.MainDock?.OpenSettings("power"));
        Item("Storage", () => App.MainDock?.OpenSettings("storage"));
        Item("Network & internet", () => App.MainDock?.OpenSettings("network"));
        Item("About this PC", () => App.MainDock?.OpenSettings("about"));
        Sep();
        Item("Terminal", () => NorrskenApps.Launch("terminal"));
        Item("Terminal (administrator)", () => NorrskenApps.Launch("terminal", "--admin"));
        Item("Task Manager", () => NorrskenApps.Launch("taskmgr"));
        Item("Files", () => NorrskenApps.Launch("files"));
        Item("Settings", () => App.MainDock?.OpenSettings());
        Item("Search", () => App.MainDock?.ToggleLauncher(forceOpen: true));
        Item("Run", RunWindow.Toggle);
        Sep();
        Item("Lock", () => { if (!App.TestMode) Power.Lock(); });
        Item("Sign out, restart or shut down…", PowerDialog.Open);
        m.IsOpen = true;
    }
}
