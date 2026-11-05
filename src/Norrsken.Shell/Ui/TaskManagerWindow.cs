using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using Path = System.Windows.Shapes.Path;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken Task Manager (Ctrl+Shift+Esc): running apps and background processes with live CPU
/// and memory, machine-wide graphs, and the programs that start with Windows.
/// </summary>
public sealed class TaskManagerWindow : Window
{
    sealed record Group(string Key, string Name, string? Path, List<Process> Procs, double Cpu, long Memory, bool IsApp);

    readonly Border _root = new();
    readonly StackPanel _nav = new();
    readonly ContentControl _page = new();
    readonly TextBox _search = new();
    readonly Button _end;
    readonly TextBlock _title = new() { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly SystemMeters _meters = new();
    readonly Dictionary<int, TimeSpan> _lastCpu = new();
    DateTime _lastAt = DateTime.UtcNow;
    readonly Dictionary<string, Queue<double>> _history = new() { ["CPU"] = new(), ["Memory"] = new(), ["Disk"] = new(), ["Network"] = new(), ["GPU"] = new() };
    static readonly ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    string _pageId = "processes", _sort = "cpu", _perf = "CPU";
    string? _selected;
    List<Group> _groups = new();

    static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    { "System", "Idle", "csrss", "wininit", "winlogon", "smss", "services", "lsass", "svchost", "dwm", "fontdrvhost", "Registry", "Memory Compression", "explorer", "sihost", "ctfmon" };

    public TaskManagerWindow()
    {
        Title = "Task Manager";
        Width = 1000; Height = 660; MinWidth = 640; MinHeight = 400;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(BackgroundProperty, "SurfaceSolid");
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 52, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });

        _end = new Button { Style = (Style)FindResource("PrimaryButton"), Content = "End task", FontSize = 12.5, Margin = new Thickness(8, 0, 6, 0), IsEnabled = false };
        _end.Click += (_, _) => EndSelected();
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(_end, true);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var side = new Border { BorderThickness = new Thickness(0, 0, 1, 0) };
        side.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        side.SetResourceReference(Border.BorderBrushProperty, "Border");
        var sideStack = new StackPanel { Margin = new Thickness(10, 16, 10, 10) };
        var name = Txt("Task Manager", 15, bold: true);
        name.FontFamily = (FontFamily)FindResource("DisplayFont");
        name.Margin = new Thickness(12, 0, 0, 14);
        sideStack.Children.Add(name);
        sideStack.Children.Add(_nav);
        side.Child = sideStack;
        grid.Children.Add(side);

        var main = new DockPanel();
        Grid.SetColumn(main, 1);
        var bar = new DockPanel { Height = 52, Margin = new Thickness(14, 0, 0, 0) };
        var pebbles = new WindowPebbles { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pebbles, Dock.Right);
        bar.Children.Add(pebbles);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(tools, true);
        var searchBox = new Border { CornerRadius = Theme.R(10), BorderThickness = new Thickness(1), Width = 220, Height = 32, Padding = new Thickness(10, 0, 10, 0) };
        searchBox.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        searchBox.SetResourceReference(Border.BorderBrushProperty, "Border");
        _search.Style = (Style)FindResource("PlainTextBox");
        _search.FontSize = 13;
        _search.TextChanged += (_, _) => Render();
        var searchHint = Txt("Search", 13, dim: true);
        searchHint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var searchGrid = new Grid();
        searchGrid.Children.Add(searchHint);
        searchGrid.Children.Add(_search);
        searchBox.Child = searchGrid;
        tools.Children.Add(searchBox);
        var run = new Button { Style = (Style)FindResource("IconButton"), Content = new TextBlock { Text = "Run new task", FontSize = 12.5, Margin = new Thickness(10, 6, 10, 6) }, Margin = new Thickness(8, 0, 0, 0) };
        run.Click += (_, _) => RunNewTask();
        tools.Children.Add(run);
        tools.Children.Add(_end);
        DockPanel.SetDock(tools, Dock.Right);
        bar.Children.Add(tools);
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        bar.Children.Add(_title);
        DockPanel.SetDock(bar, Dock.Top);
        main.Children.Add(bar);
        main.Children.Add(_page);
        grid.Children.Add(main);

        _root.Child = grid;
        _root.BorderThickness = new Thickness(1);
        _root.SetResourceReference(Border.BorderBrushProperty, "Border");
        _root.SetResourceReference(Border.BackgroundProperty, "SurfaceSolid");
        _root.SetResourceReference(TextElement.ForegroundProperty, "Text");
        Content = _root;

        foreach (var (id, label, glyph) in new[] { ("processes", "Processes", "M4,4 H10 V10 H4 Z M14,4 H20 V10 H14 Z M4,14 H10 V20 H4 Z M14,14 H20 V20 H14 Z"), ("performance", "Performance", "M3,12.5 H7 L9.5,6 L14,18.5 L16.5,12.5 H21"), ("startup", "Startup apps", "M12,4 V15 M7.5,8.5 L12,4 L16.5,8.5 M5,19.5 H19") })
            _nav.Children.Add(NavButton(id, label, glyph));

        _tick.Tick += (_, _) => Refresh();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && _pageId == "processes") { EndSelected(); e.Handled = true; }
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { _search.Focus(); e.Handled = true; }
        };
        Loaded += (_, _) => { Show("processes"); Refresh(); _tick.Start(); };
        Closed += (_, _) => { _tick.Stop(); _meters.Dispose(); };
        AppChrome.Attach(this, _root);
    }

    static TextBlock Txt(string s, double size, bool bold = false, bool dim = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDim" : "Text");
        return t;
    }

    Button NavButton(string id, string label, string glyph)
    {
        var p = new Path { Data = Geometry.Parse(glyph), StrokeThickness = 1.6, Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Margin = new Thickness(0, 0, 12, 0) };
        p.SetResourceReference(Shape.StrokeProperty, "TextDim");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 7, 4, 7) };
        row.Children.Add(p);
        row.Children.Add(Txt(label, 13));
        var b = new Button { Style = (Style)FindResource("IconButton"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Tag = id, Margin = new Thickness(0, 1, 0, 1) };
        b.Click += (_, _) => Show(id);
        return b;
    }

    internal void Show(string id)
    {
        _pageId = id;
        foreach (Button b in _nav.Children)
            if ((string)b.Tag == id) b.SetResourceReference(BackgroundProperty, "Selection"); else b.Background = Brushes.Transparent;
        _title.Text = id switch { "performance" => "Performance", "startup" => "Startup apps", _ => "Processes" };
        _end.Visibility = id == "processes" ? Visibility.Visible : Visibility.Collapsed;
        Render();
        Motion.From(_page, OpacityProperty, 0, 1, 160);
    }

    // ───────────────────────── sampling ─────────────────────────

    internal int GroupCount => _groups.Count;

    void Refresh()
    {
        _meters.Sample();
        Push("CPU", _meters.Cpu); Push("Memory", _meters.MemoryPercent); Push("Disk", _meters.Disk); Push("GPU", _meters.Gpu); Push("Network", Math.Min(100, _meters.NetworkKbps / 1000));
        var now = DateTime.UtcNow;
        double elapsed = Math.Max(0.2, (now - _lastAt).TotalSeconds) * Environment.ProcessorCount;
        _lastAt = now;
        var windows = new Dictionary<int, string>();
        Native.EnumWindows((h, _) =>
        {
            if (WindowTracker.IsTaskbarWindow(h, 0)) { Native.GetWindowThreadProcessId(h, out uint pid); windows.TryAdd((int)pid, Native.GetWindowText(h)); }
            return true;
        }, IntPtr.Zero);
        var groups = new Dictionary<string, (string Name, string? Path, List<Process> Procs, double Cpu, long Mem, bool App)>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            double cpu = 0;
            try
            {
                var t = p.TotalProcessorTime;
                if (_lastCpu.TryGetValue(p.Id, out var before)) cpu = (t - before).TotalSeconds / elapsed * 100;
                _lastCpu[p.Id] = t;
            }
            catch (Exception) { }
            seen.Add(p.Id);
            long mem = 0;
            try { mem = p.WorkingSet64; } catch (Exception) { }
            string key = p.ProcessName;
            bool app = windows.ContainsKey(p.Id);
            if (!groups.TryGetValue(key, out var g)) g = (Pretty(p), null, new List<Process>(), 0, 0, false);
            g.Procs.Add(p);
            g.Cpu += cpu; g.Mem += mem; g.App |= app;
            g.Path ??= Native.GetProcessPath((uint)p.Id);
            groups[key] = g;
        }
        foreach (var gone in _lastCpu.Keys.Where(k => !seen.Contains(k)).ToList()) _lastCpu.Remove(gone);
        _groups = groups.Select(kv => new Group(kv.Key, kv.Value.Name, kv.Value.Path, kv.Value.Procs, Math.Clamp(kv.Value.Cpu, 0, 100), kv.Value.Mem, kv.Value.App)).ToList();
        Render();
    }

    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The program's own description ("Google Chrome") when it has one; looked up once per program.</summary>
    static string Pretty(Process p)
    {
        if (Names.TryGetValue(p.ProcessName, out var n)) return n;
        string name = p.ProcessName;
        try
        {
            if (Native.GetProcessPath((uint)p.Id) is { } path && FileVersionInfo.GetVersionInfo(path).FileDescription is { Length: > 0 } d) name = d;
        }
        catch (Exception) { }
        return Names[p.ProcessName] = name;
    }

    void Push(string key, double v)
    {
        var q = _history[key];
        q.Enqueue(v);
        while (q.Count > 60) q.Dequeue();
    }

    // ───────────────────────── pages ─────────────────────────

    void Render()
    {
        _page.Content = _pageId switch
        {
            "performance" => PerformancePage(),
            "startup" => StartupPage(),
            _ => ProcessesPage(),
        };
    }

    UIElement ProcessesPage()
    {
        string q = _search.Text.Trim();
        var rows = _groups.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.Key.Contains(q, StringComparison.OrdinalIgnoreCase));
        rows = _sort switch { "name" => rows.OrderBy(g => g.Name), "memory" => rows.OrderByDescending(g => g.Memory), _ => rows.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Memory) };
        var list = rows.ToList();
        var stack = new StackPanel { Margin = new Thickness(16, 0, 18, 16) };
        stack.Children.Add(Header());
        void Section(string title, IEnumerable<Group> items)
        {
            var arr = items.ToList();
            if (arr.Count == 0) return;
            var h = Txt($"{title}  ({arr.Count})", 12, bold: true, dim: true);
            h.Margin = new Thickness(10, 12, 0, 4);
            stack.Children.Add(h);
            foreach (var g in arr) stack.Children.Add(ProcessRow(g));
        }
        Section("Apps", list.Where(g => g.IsApp));
        Section("Background", list.Where(g => !g.IsApp));
        _end.IsEnabled = _selected != null && _groups.Any(g => g.Key == _selected);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    Grid Columns(UIElement name, UIElement cpu, UIElement mem)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        Grid.SetColumn(cpu, 1); Grid.SetColumn(mem, 2);
        g.Children.Add(name); g.Children.Add(cpu); g.Children.Add(mem);
        return g;
    }

    UIElement Header()
    {
        Button H(string label, string key, HorizontalAlignment align)
        {
            var b = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = label + (_sort == key ? "  ↓" : ""), FontSize = 11.5 }, HorizontalAlignment = align };
            b.Click += (_, _) => { _sort = key; Render(); };
            return b;
        }
        var cpu = $"{_meters.Cpu:0}% CPU";
        var mem = $"{_meters.MemoryPercent:0}% memory";
        var g = Columns(H("Name", "name", HorizontalAlignment.Left), H(cpu, "cpu", HorizontalAlignment.Right), H(mem, "memory", HorizontalAlignment.Right));
        g.Margin = new Thickness(4, 4, 12, 0);
        return g;
    }

    Border ProcessRow(Group g)
    {
        var icon = new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), Source = g.Path != null ? Icons.GetOrAdd(g.Path, p => ShellIcons.Get(p, 32)) : null };
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(icon);
        name.Children.Add(Txt(g.Name, 13));
        if (g.Procs.Count > 1) { var c = Txt($"  ({g.Procs.Count})", 12, dim: true); name.Children.Add(c); }
        var cpu = Txt($"{g.Cpu:0.0}%", 12.5, dim: g.Cpu < 1);
        cpu.HorizontalAlignment = HorizontalAlignment.Right;
        var mem = Txt(Files.FormatSize(g.Memory), 12.5, dim: true);
        mem.HorizontalAlignment = HorizontalAlignment.Right;
        var row = Columns(name, cpu, mem);
        var b = new Border { CornerRadius = Theme.R(9), Padding = new Thickness(10, 6, 12, 6), Child = row, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        // Heavy users get a quiet tint, the way you'd scan for the culprit.
        if (g.Cpu > 25) b.SetResourceReference(Border.BackgroundProperty, "Pressed");
        if (g.Key == _selected) b.SetResourceReference(Border.BackgroundProperty, "Selection");
        b.MouseLeftButtonUp += (_, _) => { _selected = g.Key; Render(); };
        b.MouseRightButtonUp += (_, _) => { _selected = g.Key; Render(); RowMenu(g); };
        return b;
    }

    void RowMenu(Group g)
    {
        var m = new ContextMenu();
        void Item(string h, Action a) { var mi = new MenuItem { Header = h }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Item("End task", EndSelected);
        if (g.Path != null)
        {
            Item("Open file location", () => NorrskenApps.Launch("files", g.Path));
            Item("Properties", () => Files.Properties(new System.Windows.Interop.WindowInteropHelper(this).Handle, g.Path));
        }
        Item("Search online", () => Unelevated.Start($"https://www.bing.com/search?q={Uri.EscapeDataString(g.Key + ".exe")}"));
        m.IsOpen = true;
    }

    void EndSelected()
    {
        var g = _groups.FirstOrDefault(x => x.Key == _selected);
        if (g == null) return;
        if (Critical.Contains(g.Key) && MessageBox.Show(this, $"{g.Name} is part of Windows. Ending it can make Windows unstable or log you out. End it anyway?", "Task Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (App.TestMode) { App.TestLaunches.Add("kill:" + g.Key); return; }
        foreach (var p in g.Procs) { try { p.Kill(entireProcessTree: false); } catch (Exception) { } }
        _selected = null;
        Refresh();
    }

    void RunNewTask() => RunWindow.Toggle();

    UIElement PerformancePage()
    {
        var grid = new Grid { Margin = new Thickness(16, 4, 18, 16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var cards = new StackPanel();
        string Value(string k) => k switch
        {
            "CPU" => $"{_meters.Cpu:0}%",
            "Memory" => $"{_meters.MemoryUsedGb:0.0} / {_meters.MemoryTotalGb:0.0} GB",
            "Disk" => $"{_meters.Disk:0}%",
            "GPU" => $"{_meters.Gpu:0}%",
            _ => _meters.NetworkKbps > 1000 ? $"{_meters.NetworkKbps / 1000:0.0} Mbps" : $"{_meters.NetworkKbps:0} Kbps",
        };
        foreach (var k in _history.Keys)
        {
            var spark = Graph(_history[k], 170, 34, 1.4);
            var s = new StackPanel();
            var head = new DockPanel();
            var v = Txt(Value(k), 12, dim: true);
            DockPanel.SetDock(v, Dock.Right);
            head.Children.Add(v);
            head.Children.Add(Txt(k, 13.5, bold: true));
            s.Children.Add(head);
            spark.Margin = new Thickness(0, 6, 0, 0);
            s.Children.Add(spark);
            var card = new Border { CornerRadius = Theme.R(12), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6), Child = s, Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, k == _perf ? "Selection" : "SurfaceAlt");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            string key = k;
            card.MouseLeftButtonUp += (_, _) => { _perf = key; Render(); };
            cards.Children.Add(card);
        }
        grid.Children.Add(cards);
        var detail = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
        Grid.SetColumn(detail, 1);
        var title = Txt(_perf, 26, bold: true);
        title.FontFamily = (FontFamily)FindResource("DisplayFont");
        detail.Children.Add(title);
        detail.Children.Add(Txt(Value(_perf), 14, dim: true));
        var big = Graph(_history[_perf], 600, 260, 2);
        big.Margin = new Thickness(0, 18, 0, 8);
        var frame = new Border { CornerRadius = Theme.R(14), BorderThickness = new Thickness(1), Child = big, Padding = new Thickness(10), Margin = new Thickness(0, 16, 0, 0) };
        frame.SetResourceReference(Border.BorderBrushProperty, "Border");
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        detail.Children.Add(frame);
        var info = Txt(_perf switch
        {
            "CPU" => $"{Environment.ProcessorCount} logical processors  ·  {_groups.Sum(g => g.Procs.Count)} processes",
            "Memory" => $"{_meters.MemoryTotalGb - _meters.MemoryUsedGb:0.0} GB available",
            "Network" => "All connected adapters, sent + received",
            "GPU" => "3D engine, all adapters",
            _ => "Active time, all disks",
        }, 12.5, dim: true);
        info.Margin = new Thickness(2, 10, 0, 0);
        detail.Children.Add(info);
        grid.Children.Add(detail);
        return grid;
    }

    /// <summary>A line graph over the last minute with a soft fill underneath, drawn at its real size.</summary>
    static FrameworkElement Graph(IEnumerable<double> values, double w, double h, double thickness) =>
        new LiveGraph(values.ToList(), thickness, grid: h > 100) { Height = h, MinWidth = Math.Min(w, 120), HorizontalAlignment = HorizontalAlignment.Stretch };

    /// <summary>
    /// Sixty samples, newest on the right. Before a minute of history exists the line starts where the
    /// data starts and a hairline baseline covers the rest, so a fresh graph never looks broken.
    /// </summary>
    sealed class LiveGraph(List<double> values, double thickness, bool grid) : FrameworkElement
    {
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 4 || h < 4) return;
            var ink = (TryFindResource("Text") as SolidColorBrush)?.Color ?? Colors.White;
            Brush Ink(byte a) { var b = new SolidColorBrush(Color.FromArgb(a, ink.R, ink.G, ink.B)); b.Freeze(); return b; }
            double top = thickness, bottom = h - thickness / 2;
            double Y(double v) => bottom - (bottom - top) * Math.Clamp(v, 0, 100) / 100;

            if (grid)
            {
                var gridPen = new Pen(Ink(22), 1);
                gridPen.Freeze();
                for (int i = 1; i < 4; i++) { double y = Math.Round(h * i / 4) + 0.5; dc.DrawLine(gridPen, new Point(0, y), new Point(w, y)); }
                for (int i = 1; i < 6; i++) { double x = Math.Round(w * i / 6) + 0.5; dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h)); }
            }
            var basePen = new Pen(Ink(40), 1);
            basePen.Freeze();
            dc.DrawLine(basePen, new Point(0, bottom), new Point(w, bottom));
            if (values.Count == 0) return;

            double step = w / 59.0, x0 = w - step * (values.Count - 1);
            var pts = values.Select((v, i) => new Point(x0 + step * i, Y(v))).ToList();
            if (pts.Count == 1) pts.Insert(0, new Point(Math.Max(0, pts[0].X - step), pts[0].Y));

            var line = new StreamGeometry();
            using (var g = line.Open())
            {
                g.BeginFigure(pts[0], false, false);
                // Gentle curves between samples instead of sharp corners.
                for (int i = 1; i < pts.Count; i++)
                {
                    var a = pts[i - 1]; var b = pts[i];
                    double mx = (a.X + b.X) / 2;
                    g.BezierTo(new Point(mx, a.Y), new Point(mx, b.Y), b, true, true);
                }
            }
            line.Freeze();
            var area = line.Clone();
            var fill = new PathGeometry();
            fill.AddGeometry(area);
            var figure = fill.Figures[0];
            figure.IsClosed = true;
            figure.Segments.Add(new LineSegment(new Point(pts[^1].X, bottom), false));
            figure.Segments.Add(new LineSegment(new Point(pts[0].X, bottom), false));
            var grad = new LinearGradientBrush(Color.FromArgb(70, ink.R, ink.G, ink.B), Color.FromArgb(0, ink.R, ink.G, ink.B), 90);
            grad.Freeze();
            dc.DrawGeometry(grad, null, fill);
            var pen = new Pen(Ink(235), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, line);
            // The current value.
            dc.DrawEllipse(Ink(255), null, pts[^1], thickness * 1.6, thickness * 1.6);
        }
    }
    UIElement StartupPage()
    {
        var stack = new StackPanel { Margin = new Thickness(16, 4, 18, 16) };
        var note = Txt("Programs that start when you sign in. Turning one off takes effect next time.", 12.5, dim: true);
        note.Margin = new Thickness(6, 0, 0, 10);
        stack.Children.Add(note);
        foreach (var e in StartupApps.List())
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 0) };
            var toggle = new CheckBox { Style = (Style)FindResource("Toggle"), IsChecked = e.Enabled, Content = "", Margin = new Thickness(0, -8, 0, -8), VerticalAlignment = VerticalAlignment.Center };
            var entry = e;
            toggle.Checked += (_, _) => Set(entry, true, toggle);
            toggle.Unchecked += (_, _) => Set(entry, false, toggle);
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Txt(e.Name, 13.5));
            text.Children.Add(Txt($"{e.Source}  ·  {e.Command}", 11.5, dim: true));
            row.Children.Add(text);
            var card = new Border { CornerRadius = Theme.R(12), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 4), Child = row, BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            card.SetResourceReference(Border.BorderBrushProperty, "Border");
            stack.Children.Add(card);
        }
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    }

    void Set(StartupApps.Entry e, bool on, CheckBox toggle)
    {
        if (App.TestMode) { App.TestLaunches.Add($"startup {e.Name} {on}"); return; }
        try { StartupApps.Set(e, on); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"“{e.Name}” starts for all users, so changing it needs administrator rights.\n\n{ex.Message}", "Task Manager");
            toggle.IsChecked = !on;
        }
    }
}
