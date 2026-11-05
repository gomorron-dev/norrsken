using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using Path = System.Windows.Shapes.Path;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken's "Today" panel (Win+N, or the time in the dock). One quiet sheet from the right edge:
/// a large clock with the focus session as a ring beside it, a week strip that unfolds into the
/// month, and your notifications as a timeline: each app a node on a thin line, newest first.
/// </summary>
public sealed class NotificationCentre : Window
{
    static NotificationCentre? _current;
    readonly StackPanel _timeline = new();
    readonly Grid _week = new();
    readonly StackPanel _month = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _monthLabel = new() { FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _clock = new() { FontSize = 46, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _date = new() { FontSize = 13.5, Margin = new Thickness(2, -2, 0, 0) };
    readonly TextBlock _countText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    readonly Path _ring = new() { StrokeThickness = 3.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    readonly TextBlock _ringText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    readonly Path _chevron = new() { Data = Geometry.Parse("M6,9 L12,15 L18,9"), StrokeThickness = 1.7, Width = 10, Height = 6, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5) };
    readonly TranslateTransform _slide = new();
    readonly Border _sheet = new();
    readonly HashSet<string> _expanded = new();
    readonly DispatcherTimer _clockTick = new() { Interval = TimeSpan.FromSeconds(1) };
    DateTime _shownMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    bool _closing, _monthOpen;
    const double SheetWidth = 372, RingSize = 64;

    // The focus session outlives the panel.
    static int _focusMinutes = 25;
    static DateTime? _focusStart, _focusEnds;
    static readonly DispatcherTimer FocusTick = new() { Interval = TimeSpan.FromSeconds(1) };
    public static event Action? FocusChanged;
    public static string? FocusRemaining => _focusEnds is { } e && e > DateTime.Now ? (e - DateTime.Now).ToString(@"m\:ss") : null;

    static NotificationCentre()
    {
        FocusTick.Tick += (_, _) =>
        {
            if (_focusEnds is { } e && DateTime.Now >= e)
            {
                _focusEnds = _focusStart = null;
                FocusTick.Stop();
                System.Media.SystemSounds.Asterisk.Play();
                FocusDone.Pop();
            }
            _current?.UpdateFocus();
            FocusChanged?.Invoke();
        };
    }

    public static bool IsOpen => _current != null;
    internal static NotificationCentre? Current => _current;
    internal int GroupCount { get; private set; }

    public static void Toggle()
    {
        if (_current != null) { _current.SlideAway(); return; }
        _current = new NotificationCentre();
        _current.Show();
        _current.Activate();
    }

    NotificationCentre()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Title = "Today";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        var work = SystemParameters.WorkArea;
        Width = SheetWidth + 28; Height = work.Height; Left = work.Right - Width; Top = work.Top;

        var body = new DockPanel { Margin = new Thickness(22, 20, 18, 14) };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top);
        top.Children.Add(BuildHeader());
        top.Children.Add(BuildCalendar());
        top.Children.Add(Hairline(new Thickness(0, 14, 0, 12)));
        top.Children.Add(BuildTimelineHeader());
        body.Children.Add(top);
        body.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _timeline, Focusable = false });

        _sheet.Child = body;
        _sheet.Margin = new Thickness(0, 12, 12, 12);
        _sheet.CornerRadius = new CornerRadius(24);
        _sheet.BorderThickness = new Thickness(1);
        _sheet.SetResourceReference(Border.BackgroundProperty, "Surface");
        _sheet.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        _sheet.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 34, ShadowDepth = 8, Direction = 200, Opacity = 0.4, Color = Colors.Black };
        _sheet.RenderTransform = _slide;
        Content = _sheet;

        Notifications.Changed += OnChanged;
        _clockTick.Tick += (_, _) => UpdateClock();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { SlideAway(); e.Handled = true; } };
        Deactivated += (_, _) => { if (!App.TestMode) SlideAway(); };
        Loaded += (_, _) =>
        {
            UpdateClock();
            RenderWeek();
            RenderMonth();
            Rebuild();
            UpdateFocus();
            _clockTick.Start();
            Motion.From(_slide, TranslateTransform.XProperty, SheetWidth * 0.3, 0, 360, Motion.Out);
            Motion.From(_sheet, OpacityProperty, 0, 1, 200);
            Motion.Stagger(_timeline.Children.Cast<UIElement>(), stepMs: 40, max: 8, rise: 12, startMs: 120);
            Motion.Stagger(_week.Children.Cast<UIElement>(), stepMs: 22, max: 7, rise: 6, startMs: 60);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
    }

    void OnChanged() => Dispatcher.BeginInvoke(Rebuild);

    internal void SlideAway()
    {
        if (_closing) return;
        _closing = true;
        Motion.To(_slide, TranslateTransform.XProperty, SheetWidth * 0.25, 200, Motion.InOut);
        Motion.To(_sheet, OpacityProperty, 0, 180, done: Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        Notifications.Changed -= OnChanged;
        _clockTick.Stop();
        base.OnClosed(e);
        if (_current == this) _current = null;
    }

    // ───────────────────────── building blocks ─────────────────────────

    static TextBlock Text(string s, double size, bool bold = false, bool dim = false, bool wrap = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextDim" : "Text");
        return t;
    }

    static Border Hairline(Thickness margin) { var b = new Border { Height = 1, Margin = margin }; b.SetResourceReference(Border.BackgroundProperty, "Border"); return b; }

    Button Glyph(string data, string tip, Action click, double size = 10)
    {
        var p = new Path { Data = Geometry.Parse(data), StrokeThickness = 1.7, Width = size, Height = size, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        var b = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, ToolTip = tip, Content = p };
        p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = b });
        b.Click += (_, _) => click();
        return b;
    }

    static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    // ───────────────────────── clock and focus ring ─────────────────────────

    FrameworkElement BuildHeader()
    {
        var row = new DockPanel();
        var ring = new Grid { Width = RingSize, Height = RingSize, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, ToolTip = "Focus: click to start or stop, scroll to change the length" };
        var track = new Ellipse { StrokeThickness = 3.2 };
        track.SetResourceReference(Shape.StrokeProperty, "Border");
        _ring.SetResourceReference(Shape.StrokeProperty, "Text");
        ring.Children.Add(track);
        ring.Children.Add(_ring);
        _ringText.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        ring.Children.Add(_ringText);
        ring.MouseLeftButtonUp += (_, _) => ToggleFocus();
        ring.MouseWheel += (_, e) => { if (_focusEnds == null) { _focusMinutes = Math.Clamp(_focusMinutes + Math.Sign(e.Delta) * 5, 5, 180); UpdateFocus(); } e.Handled = true; };
        DockPanel.SetDock(ring, Dock.Right);
        row.Children.Add(ring);
        var stack = new StackPanel();
        _clock.FontFamily = (FontFamily)FindResource("DisplayFont");
        _clock.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _date.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        stack.Children.Add(_clock);
        stack.Children.Add(_date);
        row.Children.Add(stack);
        return row;
    }

    void UpdateClock()
    {
        var c = CultureInfo.CurrentCulture;
        _clock.Text = DateTime.Now.ToString(App.Settings.Clock24h ? "HH:mm" : "h:mm", c);
        _date.Text = Cap(DateTime.Now.ToString("dddd d MMMM", c));
    }

    void ToggleFocus()
    {
        if (_focusEnds != null) StopFocus(); else StartFocus(_focusMinutes);
        UpdateFocus();
    }

    internal static void StartFocus(int minutes) { _focusMinutes = minutes; _focusStart = DateTime.Now; _focusEnds = DateTime.Now.AddMinutes(minutes); FocusTick.Start(); _current?.UpdateFocus(); FocusChanged?.Invoke(); }
    internal static void StopFocus() { _focusEnds = _focusStart = null; FocusTick.Stop(); _current?.UpdateFocus(); FocusChanged?.Invoke(); }

    void UpdateFocus()
    {
        double fraction = 0;
        if (_focusEnds is { } end && _focusStart is { } start)
            fraction = Math.Clamp((end - DateTime.Now).TotalSeconds / Math.Max(1, (end - start).TotalSeconds), 0, 1);
        _ringText.Text = FocusRemaining ?? $"{_focusMinutes}\nmin";
        _ringText.FontSize = FocusRemaining != null ? 12.5 : 11.5;
        _ring.Data = Arc(fraction);
    }

    /// <summary>The remaining share of the session, drawn clockwise from the top.</summary>
    static Geometry Arc(double fraction)
    {
        if (fraction <= 0) return Geometry.Empty;
        double r = RingSize / 2 - 1.6, c = RingSize / 2;
        if (fraction >= 0.999) return new EllipseGeometry(new Point(c, c), r, r);
        double angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var fig = new PathFigure { StartPoint = new Point(c, c - r) };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        return new PathGeometry([fig]);
    }

    // ───────────────────────── week strip and month ─────────────────────────

    FrameworkElement BuildCalendar()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        var row = new DockPanel();
        var toggle = new Button { Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, ToolTip = "Show the month", Content = _chevron };
        _chevron.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { Source = toggle });
        _chevron.RenderTransform = new RotateTransform(0);
        toggle.Click += (_, _) => ToggleMonth();
        DockPanel.SetDock(toggle, Dock.Right);
        row.Children.Add(toggle);
        for (int i = 0; i < 7; i++) _week.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(_week);
        stack.Children.Add(row);

        var nav = new DockPanel { Margin = new Thickness(0, 12, 28, 4) };
        var arrows = new StackPanel { Orientation = Orientation.Horizontal };
        arrows.Children.Add(Glyph("M15,6 L9,12 L15,18", "Previous month", () => { _shownMonth = _shownMonth.AddMonths(-1); RenderMonth(); }));
        arrows.Children.Add(Glyph("M9,6 L15,12 L9,18", "Next month", () => { _shownMonth = _shownMonth.AddMonths(1); RenderMonth(); }));
        DockPanel.SetDock(arrows, Dock.Right);
        nav.Children.Add(arrows);
        _monthLabel.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var label = new Button { Style = (Style)FindResource("LinkButton"), Content = _monthLabel, ToolTip = "Back to this month", HorizontalAlignment = HorizontalAlignment.Left };
        label.Click += (_, _) => { _shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1); RenderMonth(); };
        nav.Children.Add(label);
        _month.Children.Add(nav);
        stack.Children.Add(_month);
        return stack;
    }

    void ToggleMonth()
    {
        _monthOpen = !_monthOpen;
        _month.Visibility = _monthOpen ? Visibility.Visible : Visibility.Collapsed;
        Motion.To((RotateTransform)_chevron.RenderTransform, RotateTransform.AngleProperty, _monthOpen ? 180 : 0, 220);
        if (_monthOpen) Motion.From(_month, OpacityProperty, 0, 1, 200);
    }

    internal bool MonthOpen => _monthOpen;
    internal void OpenMonth() { if (!_monthOpen) ToggleMonth(); }

    FrameworkElement DayCell(DateTime day, bool dimmed, bool withName)
    {
        var c = CultureInfo.CurrentCulture;
        bool today = day == DateTime.Today;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        if (withName)
        {
            var n = Text(c.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek).TrimEnd('.'), 10.5, dim: true);
            n.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(n);
        }
        var pill = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Margin = new Thickness(0, withName ? 4 : 1, 0, 1) };
        var num = Text(day.Day.ToString(c), 13, bold: today, dim: dimmed);
        num.HorizontalAlignment = HorizontalAlignment.Center;
        num.VerticalAlignment = VerticalAlignment.Center;
        if (dimmed) num.Opacity = 0.45;
        if (today) { pill.SetResourceReference(Border.BackgroundProperty, "Text"); num.SetResourceReference(TextBlock.ForegroundProperty, "SurfaceSolid"); }
        pill.Child = num;
        stack.Children.Add(pill);
        return stack;
    }

    void RenderWeek()
    {
        _week.Children.Clear();
        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var start = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek - (int)first + 7) % 7));
        for (int i = 0; i < 7; i++)
        {
            var cell = DayCell(start.AddDays(i), false, true);
            Grid.SetColumn(cell, i);
            _week.Children.Add(cell);
        }
    }

    internal void RenderMonth()
    {
        var c = CultureInfo.CurrentCulture;
        _monthLabel.Text = Cap(_shownMonth.ToString("MMMM yyyy", c));
        while (_month.Children.Count > 1) _month.Children.RemoveAt(1);
        var first = c.DateTimeFormat.FirstDayOfWeek;
        var start = _shownMonth.AddDays(-(((int)_shownMonth.DayOfWeek - (int)first + 7) % 7));
        var grid = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 28, 0) };
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            grid.Children.Add(DayCell(day, day.Month != _shownMonth.Month, i < 7));
        }
        _month.Children.Add(grid);
    }

    // ───────────────────────── timeline ─────────────────────────

    FrameworkElement BuildTimelineHeader()
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = "Clear", FontSize = 12 } };
        clear.Click += (_, _) => { Notifications.ClearAll(); Rebuild(); };
        DockPanel.SetDock(clear, Dock.Right);
        row.Children.Add(clear);
        var title = Text("Notifications", 13.5, bold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        _countText.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        _countText.Margin = new Thickness(8, 1, 0, 0);
        row.Children.Add(_countText);
        return row;
    }

    void Rebuild()
    {
        _timeline.Children.Clear();
        _countText.Text = Notifications.Items.Count > 0 ? Notifications.Items.Count.ToString(CultureInfo.CurrentCulture) : "";
        if (Notifications.Status is Notifications.State.NeedsIdentity or Notifications.State.Denied)
        {
            var box = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            box.Children.Add(Text(Notifications.Status == Notifications.State.Denied ? "Notification access is off" : "Notifications show up here after Norrsken's system setup", 13, bold: true, wrap: true));
            box.Children.Add(Text(Notifications.Status == Notifications.State.Denied
                ? "Allow Norrsken to read notifications in Windows' privacy settings."
                : "Until then they still pop up as usual.", 12, dim: true, wrap: true));
            if (Notifications.Status == Notifications.State.Denied)
            {
                var b = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = "Open privacy settings  ›", FontSize = 12.5 }, Margin = new Thickness(-6, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                b.Click += (_, _) => { if (!App.TestMode) try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true }); } catch (Exception) { } };
                box.Children.Add(b);
            }
            _timeline.Children.Add(box);
            GroupCount = 0;
            return;
        }
        var groups = Notifications.Items.GroupBy(i => i.AppName).OrderByDescending(g => g.Max(i => i.Time)).ToList();
        GroupCount = groups.Count;
        if (groups.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 46, 0, 30), HorizontalAlignment = HorizontalAlignment.Center };
            var mark = new Path { Data = Geometry.Parse("M3,14 C6,4 10,4 12,9.5 C14,15 18,15 21,5"), StrokeThickness = 1.6, Width = 46, Height = 22, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 0, 14), Opacity = 0.6 };
            mark.SetResourceReference(Shape.StrokeProperty, "TextDim");
            empty.Children.Add(mark);
            var t = Text("All quiet", 13.5, bold: true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            var s = Text("New notifications land here.", 12, dim: true);
            s.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(t);
            empty.Children.Add(s);
            _timeline.Children.Add(empty);
            return;
        }
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var items = g.OrderByDescending(i => i.Time).ToList();
            _timeline.Children.Add(TimelineEntry(g.Key, items, _expanded.Contains(g.Key), last: gi == groups.Count - 1));
        }
    }

    /// <summary>One app on the timeline: its node on the line, then its newest notification (or all of them).</summary>
    FrameworkElement TimelineEntry(string app, List<NotificationItem> items, bool open, bool last)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var rail = new Grid();
        var line = new Border { Width = 1, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 0), Visibility = last ? Visibility.Collapsed : Visibility.Visible };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        var node = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, BorderThickness = new Thickness(1), Margin = new Thickness(0, 2, 0, 0) };
        node.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
        node.SetResourceReference(Border.BorderBrushProperty, "Border");
        if (items[0].AppIcon is { } icon) node.Child = new Image { Source = icon, Width = 15, Height = 15 };
        else
        {
            var letter = Text(app.Length > 0 ? app[..1].ToUpperInvariant() : "•", 11, bold: true);
            letter.HorizontalAlignment = HorizontalAlignment.Center;
            letter.VerticalAlignment = VerticalAlignment.Center;
            node.Child = letter;
        }
        rail.Children.Add(line);
        rail.Children.Add(node);
        grid.Children.Add(rail);

        var content = new StackPanel { Margin = new Thickness(10, 0, 0, 16) };
        Grid.SetColumn(content, 1);
        var head = new DockPanel { Margin = new Thickness(0, 5, 0, 4) };
        var dismissAll = Glyph("M7,7 L17,17 M17,7 L7,17", $"Clear {app}", () => { foreach (var i in items) Notifications.Dismiss(i); }, 8);
        dismissAll.Width = dismissAll.Height = 22;
        dismissAll.Opacity = 0;
        DockPanel.SetDock(dismissAll, Dock.Right);
        head.Children.Add(dismissAll);
        var when = Text(When(items[0].Time), 11.5, dim: true);
        DockPanel.SetDock(when, Dock.Right);
        when.Margin = new Thickness(8, 0, 4, 0);
        head.Children.Add(when);
        head.Children.Add(Text(app, 12, bold: true, dim: true));
        content.Children.Add(head);
        foreach (var n in open ? items : items.Take(1)) content.Children.Add(Message(n, open && n != items[0]));
        if (items.Count > 1)
        {
            var more = new Button { Style = (Style)FindResource("LinkButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-6, 2, 0, 0), Content = new TextBlock { Text = open ? "Show less" : $"+{items.Count - 1} more", FontSize = 12 } };
            more.Click += (_, _) => { if (!_expanded.Remove(app)) _expanded.Add(app); Rebuild(); };
            content.Children.Add(more);
        }
        grid.Children.Add(content);
        grid.MouseEnter += (_, _) => dismissAll.Opacity = 1;
        grid.MouseLeave += (_, _) => dismissAll.Opacity = 0;
        return grid;
    }

    Border Message(NotificationItem n, bool showTime)
    {
        var stack = new StackPanel();
        if (showTime) { var w = Text(When(n.Time), 11, dim: true); w.Margin = new Thickness(0, 0, 0, 2); stack.Children.Add(w); }
        if (n.Title.Length > 0) stack.Children.Add(Text(n.Title, 13.5, bold: true, wrap: true));
        if (n.Body.Length > 0) { var b = Text(n.Body, 12.5, dim: true, wrap: true); b.MaxHeight = 72; b.Margin = new Thickness(0, 1, 0, 0); stack.Children.Add(b); }
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 9), Margin = new Thickness(-10, 0, 0, 2), Child = stack, Cursor = Cursors.Hand, Background = Brushes.Transparent, ToolTip = "Open " + n.AppName + " · right-click to dismiss" };
        card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "Hover");
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
        card.MouseLeftButtonUp += (_, _) => Notifications.Open(n);
        card.MouseRightButtonUp += (_, e) => { Notifications.Dismiss(n); e.Handled = true; };
        return card;
    }

    static string When(DateTimeOffset t)
    {
        var ago = DateTimeOffset.Now - t;
        if (ago.TotalMinutes < 1) return "now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min";
        return t.LocalDateTime.Date == DateTime.Today ? t.LocalDateTime.ToString("t", CultureInfo.CurrentCulture) : t.LocalDateTime.ToString("d MMM", CultureInfo.CurrentCulture);
    }
}

/// <summary>The small card when a focus session ends.</summary>
internal sealed class FocusDone : Window
{
    public static void Pop() { var w = new FocusDone(); w.Show(); }

    FocusDone()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(20), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        var s = new StackPanel();
        var a = new TextBlock { Text = "Focus session done", FontSize = 14, FontWeight = FontWeights.SemiBold };
        a.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var b = new TextBlock { Text = "Time for a short break.", FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
        b.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        s.Children.Add(a); s.Children.Add(b);
        card.Child = s;
        Content = card;
        var shift = new TranslateTransform(); card.RenderTransform = shift;
        // A thin bar drains along the bottom while the card waits to leave
        var drain = new Border { Height = 2, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.5 };
        drain.SetResourceReference(Border.BackgroundProperty, "TextDim");
        s.Children.Add(drain);
        bool leaving = false;
        void Leave()
        {
            if (leaving) return; leaving = true;
            Motion.To(shift, TranslateTransform.XProperty, 60, 240, Motion.InOut);
            Motion.To(card, OpacityProperty, 0, 220, done: Close);
        }
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        t.Tick += (_, _) => { t.Stop(); Leave(); };
        Loaded += (_, _) =>
        {
            var w = SystemParameters.WorkArea; Left = w.Right - ActualWidth; Top = w.Top;
            Motion.From(card, OpacityProperty, 0, 1, 200);
            Motion.From(shift, TranslateTransform.XProperty, 60, 0, 420, Motion.SoftSpring);
            drain.Width = s.ActualWidth;
            if (!Motion.Reduced) drain.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(s.ActualWidth, 0, TimeSpan.FromSeconds(8)));
            t.Start();
        };
        MouseLeftButtonUp += (_, _) => Leave();
    }
}
