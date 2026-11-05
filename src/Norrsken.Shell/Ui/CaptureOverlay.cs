using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Norrsken.Shell.Core;
using Path = System.Windows.Shapes.Path;

namespace Norrsken.Shell.Ui;

public enum CaptureMode { Rectangle, Window, Screen, Freeform }

/// <summary>
/// Norrsken Screenshot. The screen is frozen the instant the shortcut is pressed; you pick a
/// rectangle, a window, a whole screen or a free-form shape on the frozen image. The result is
/// copied to the clipboard and saved to Pictures\Screenshots, and a small card offers to open it.
/// </summary>
public sealed class CaptureOverlay : Window
{
    static CaptureOverlay? _open;
    static CaptureMode _lastMode = CaptureMode.Rectangle;

    readonly BitmapSource _frozen;
    readonly Int32Rect _virtual;
    readonly List<Int32Rect> _windows;
    readonly Action? _onDone;
    CaptureMode _mode = _lastMode;

    readonly Grid _root = new();
    readonly Path _veil = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
    readonly Path _outline = new() { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false, SnapsToDevicePixels = true };
    readonly Border _sizeTag = new() { CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)), Padding = new Thickness(7, 3, 7, 3), IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11.5 };
    readonly Border _toolbar = new();
    readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal };
    readonly TranslateTransform _toolbarShift = new();
    readonly Canvas _canvas = new() { Background = Brushes.Transparent };

    Point? _start;
    Rect _selection = Rect.Empty;
    readonly List<Point> _free = new();
    bool _finished;

    /// <summary>Set by tests: the saved file and what would have been put on the clipboard.</summary>
    internal static string? LastSaved { get; private set; }
    internal static string? TestDir { get; set; }

    /// <summary>Win+PrtScn: the screen under the pointer, saved and copied at once (no selection).</summary>
    public static void SaveWholeScreen()
    {
        var c = ScreenGrab.Cursor();
        var area = ScreenGrab.MonitorAt(c.X, c.Y);
        Deliver(ScreenGrab.Grab(area), area);
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetRect(IntPtr h, int attr, out Interop.Native.RECT r, int size);

    /// <summary>Alt+PrtScn: the window you're in, without its shadow.</summary>
    public static void CaptureActiveWindow()
    {
        var fg = Interop.Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || DwmGetRect(fg, 9, out var r, Marshal.SizeOf<Interop.Native.RECT>()) != 0) { SaveWholeScreen(); return; }
        var v = ScreenGrab.VirtualScreen;
        int x = Math.Max(r.Left, v.X), y = Math.Max(r.Top, v.Y);
        int w = Math.Min(r.Right, v.X + v.Width) - x, h = Math.Min(r.Bottom, v.Y + v.Height) - y;
        if (w < 8 || h < 8) { SaveWholeScreen(); return; }
        var rect = new Int32Rect(x, y, w, h);
        Deliver(ScreenGrab.Grab(rect), rect);
    }

    public static void Start(Action? onDone = null, BitmapSource? testImage = null)
    {
        if (_open != null) { _open.Activate(); return; }
        var area = testImage == null ? ScreenGrab.VirtualScreen : new Int32Rect(0, 0, testImage.PixelWidth, testImage.PixelHeight);
        var frozen = testImage ?? ScreenGrab.Grab(area); // before anything of ours appears
        var windows = testImage == null ? ScreenGrab.Windows(IntPtr.Zero) : new List<Int32Rect>();
        _open = new CaptureOverlay(frozen, area, windows, onDone);
        _open.Show();
        _open.Activate();
    }

    internal static CaptureOverlay? Open => _open;

    CaptureOverlay(BitmapSource frozen, Int32Rect area, List<Int32Rect> windows, Action? onDone)
    {
        _frozen = frozen; _virtual = area; _windows = windows; _onDone = onDone;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual; Background = Brushes.Black; Title = "Screenshot";
        Left = area.X; Top = area.Y; Width = Math.Max(area.Width, 200); Height = Math.Max(area.Height, 200);
        Cursor = Cursors.Cross; FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        _root.Children.Add(new Image { Source = frozen, Stretch = Stretch.Fill });
        _root.Children.Add(_veil);
        _root.Children.Add(_outline);
        _sizeTag.Child = _sizeText;
        _canvas.Children.Add(_sizeTag);
        _root.Children.Add(_canvas);
        BuildToolbar();
        _root.Children.Add(_toolbar);
        Content = _root;

        _canvas.MouseLeftButtonDown += Down;
        _canvas.MouseMove += Move;
        _canvas.MouseLeftButtonUp += Up;
        _canvas.MouseRightButtonUp += (_, _) => Cancel();
        PreviewKeyDown += Key;
        Loaded += (_, _) =>
        {
            PlaceToolbar();
            UpdateVeil();
            Motion.From(_veil, OpacityProperty, 0, 1, 140);
            Motion.From(_toolbar, OpacityProperty, 0, 1, 200);
            Motion.From(_toolbarShift, TranslateTransform.YProperty, -18, 0, 320, Motion.SoftSpring);
            if (_mode is CaptureMode.Window or CaptureMode.Screen) Hover(Mouse.GetPosition(_canvas));
        };
        Deactivated += (_, _) => { if (!_finished && !App.TestMode) Cancel(); };
    }

    /// <summary>
    /// The toolbar sits at the top of the monitor the pointer is on, not in the middle of all monitors
    /// together (which lands it on the seam between two screens).
    /// </summary>
    void PlaceToolbar()
    {
        var c = ScreenGrab.Cursor();
        var monitor = ToDip(ScreenGrab.MonitorAt(c.X, c.Y));
        _toolbar.UpdateLayout();
        double w = _toolbar.ActualWidth > 0 ? _toolbar.ActualWidth : _toolbar.DesiredSize.Width;
        _toolbar.HorizontalAlignment = HorizontalAlignment.Left;
        _toolbar.Margin = new Thickness(Math.Max(0, monitor.X + (monitor.Width - w) / 2), Math.Max(0, monitor.Y) + 22, 0, 0);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Cover the whole virtual screen in physical pixels, whatever each monitor's scaling.
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height, 0x0010 | 0x0040);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);

    // ───────────────────────── toolbar ─────────────────────────

    void BuildToolbar()
    {
        _toolbar.HorizontalAlignment = HorizontalAlignment.Center;
        _toolbar.VerticalAlignment = VerticalAlignment.Top;
        _toolbar.Margin = new Thickness(0, 22, 0, 0);
        _toolbar.CornerRadius = new CornerRadius(14);
        _toolbar.Padding = new Thickness(6);
        _toolbar.SetResourceReference(Border.BackgroundProperty, "Surface");
        _toolbar.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        _toolbar.BorderThickness = new Thickness(1);
        _toolbar.RenderTransform = _toolbarShift;
        _toolbar.Cursor = Cursors.Arrow;
        _toolbar.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (mode, glyph, label, key) in new[]
                 {
                     (CaptureMode.Rectangle, "M4,4 H20 V20 H4 Z", "Rectangle", "1"),
                     (CaptureMode.Window, "M3.5,5 H20.5 V19 H3.5 Z M3.5,8.5 H20.5", "Window", "2"),
                     (CaptureMode.Screen, "M3.5,5 H20.5 V16 H3.5 Z M9,20 H15 M12,16 V20", "Full screen", "3"),
                     (CaptureMode.Freeform, "M6,5 C11,2 20,5 18,11 C16.5,15.5 9,13 7.5,17 C6.8,19 9,21 12,20", "Free form", "4"),
                 })
        {
            var b = ToolButton(glyph, $"{label}  ({key})");
            b.Tag = mode;
            b.Click += (_, _) => SetMode(mode);
            _modes.Children.Add(b);
        }
        row.Children.Add(_modes);
        row.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 8, 6, 8), Background = (Brush)FindResource("Border") });
        var close = ToolButton("M7,7 L17,17 M17,7 L7,17", "Cancel  (Esc)");
        close.Click += (_, _) => Cancel();
        row.Children.Add(close);
        _toolbar.Child = row;
        HighlightMode();
    }

    Button ToolButton(string glyph, string tip)
    {
        var path = new Path { Data = Geometry.Parse(glyph), StrokeThickness = 1.7, Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        var b = new Button { Style = (Style)FindResource("IconButton"), Width = 40, Height = 36, Content = path, ToolTip = tip, Margin = new Thickness(1, 0, 1, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    void HighlightMode()
    {
        foreach (Button b in _modes.Children)
        {
            bool on = (CaptureMode)b.Tag == _mode;
            if (on) b.SetResourceReference(BackgroundProperty, "Pressed"); else b.Background = Brushes.Transparent;
            b.SetResourceReference(ForegroundProperty, on ? "Text" : "TextDim");
        }
    }

    internal void SetMode(CaptureMode mode)
    {
        _mode = _lastMode = mode;
        _start = null; _free.Clear(); _selection = Rect.Empty;
        HighlightMode();
        UpdateVeil();
        if (mode is CaptureMode.Window or CaptureMode.Screen) Hover(Mouse.GetPosition(_canvas));
    }

    // ───────────────────────── pointer ─────────────────────────

    double Fx => _frozen.PixelWidth / Math.Max(1, _canvas.ActualWidth);
    double Fy => _frozen.PixelHeight / Math.Max(1, _canvas.ActualHeight);
    Rect ToDip(Int32Rect r) => new((r.X - _virtual.X) / Fx, (r.Y - _virtual.Y) / Fy, r.Width / Fx, r.Height / Fy);

    void Down(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_mode is CaptureMode.Window or CaptureMode.Screen)
        {
            Hover(p);
            if (!_selection.IsEmpty) Finish(_selection, null);
            return;
        }
        _start = p;
        _free.Clear();
        _free.Add(p);
        _canvas.CaptureMouse();
    }

    void Move(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_canvas);
        if (_mode is CaptureMode.Window or CaptureMode.Screen) { Hover(p); return; }
        if (_start is not { } s || e.LeftButton != MouseButtonState.Pressed) return;
        if (_mode == CaptureMode.Freeform)
        {
            if ((p - _free[^1]).Length > 2) _free.Add(p);
            UpdateVeil();
        }
        else
        {
            _selection = new Rect(s, p);
            UpdateVeil();
        }
    }

    void Up(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        if (_start == null) return;
        _start = null;
        if (_mode == CaptureMode.Freeform)
        {
            if (_free.Count < 3) { _free.Clear(); UpdateVeil(); return; }
            var g = FreeGeometry();
            Finish(g.Bounds, g);
        }
        else if (_selection.Width >= 3 && _selection.Height >= 3) Finish(_selection, null);
        else { _selection = Rect.Empty; UpdateVeil(); }
    }

    void Hover(Point p)
    {
        int px = (int)(p.X * Fx) + _virtual.X, py = (int)(p.Y * Fy) + _virtual.Y;
        Int32Rect? hit = _mode == CaptureMode.Screen
            ? (App.TestMode ? new Int32Rect(_virtual.X, _virtual.Y, _virtual.Width, _virtual.Height) : ScreenGrab.MonitorAt(px, py))
            : _windows.FirstOrDefault(w => px >= w.X && py >= w.Y && px < w.X + w.Width && py < w.Y + w.Height) is { Width: > 0 } w ? w : null;
        _selection = hit is { } r ? Rect.Intersect(ToDip(r), new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight)) : Rect.Empty;
        UpdateVeil();
    }

    PathGeometry FreeGeometry()
    {
        var fig = new PathFigure { StartPoint = _free[0], IsClosed = true, IsFilled = true };
        fig.Segments.Add(new PolyLineSegment(_free.Skip(1), true));
        return new PathGeometry([fig]);
    }

    void UpdateVeil()
    {
        var full = new RectangleGeometry(new Rect(0, 0, Math.Max(_canvas.ActualWidth, ActualWidth), Math.Max(_canvas.ActualHeight, ActualHeight)));
        Geometry? hole = _mode == CaptureMode.Freeform
            ? (_free.Count > 1 ? FreeGeometry() : null)
            : (_selection.IsEmpty ? null : new RectangleGeometry(_selection, _mode == CaptureMode.Rectangle ? 0 : 0, 0));
        _veil.Data = hole == null ? full : new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { full, hole } };
        _outline.Data = hole;
        if (hole != null && hole.Bounds.Width > 0)
        {
            var b = hole.Bounds;
            _sizeText.Text = $"{Math.Round(b.Width * Fx)} × {Math.Round(b.Height * Fy)}";
            _sizeTag.Visibility = Visibility.Visible;
            Canvas.SetLeft(_sizeTag, b.Left);
            Canvas.SetTop(_sizeTag, b.Bottom + 8 + 24 > _canvas.ActualHeight ? b.Top - 30 : b.Bottom + 8);
        }
        else _sizeTag.Visibility = Visibility.Collapsed;
    }

    void Key(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape: Cancel(); break;
            case System.Windows.Input.Key.D1 or System.Windows.Input.Key.NumPad1: SetMode(CaptureMode.Rectangle); break;
            case System.Windows.Input.Key.D2 or System.Windows.Input.Key.NumPad2: SetMode(CaptureMode.Window); break;
            case System.Windows.Input.Key.D3 or System.Windows.Input.Key.NumPad3: SetMode(CaptureMode.Screen); break;
            case System.Windows.Input.Key.D4 or System.Windows.Input.Key.NumPad4: SetMode(CaptureMode.Freeform); break;
            case System.Windows.Input.Key.Enter:
                SetMode(CaptureMode.Screen);
                if (!_selection.IsEmpty) Finish(_selection, null);
                break;
            default: return;
        }
        e.Handled = true;
    }

    // ───────────────────────── result ─────────────────────────

    /// <summary>Test entry: capture a DIP rectangle (or a free-form path) as if dragged.</summary>
    internal void TestCapture(Rect dip, IList<Point>? freeform = null)
    {
        if (freeform != null) { _mode = CaptureMode.Freeform; _free.Clear(); _free.AddRange(freeform); var g = FreeGeometry(); Finish(g.Bounds, g); }
        else Finish(dip, null);
    }

    internal Rect Selection => _selection;
    internal CaptureMode Mode => _mode;

    void Finish(Rect dip, Geometry? shape)
    {
        if (_finished) return;
        _finished = true;
        var px = new Int32Rect((int)Math.Round(dip.X * Fx), (int)Math.Round(dip.Y * Fy), (int)Math.Round(dip.Width * Fx), (int)Math.Round(dip.Height * Fy));
        px.X = Math.Clamp(px.X, 0, _frozen.PixelWidth - 1); px.Y = Math.Clamp(px.Y, 0, _frozen.PixelHeight - 1);
        px.Width = Math.Clamp(px.Width, 1, _frozen.PixelWidth - px.X); px.Height = Math.Clamp(px.Height, 1, _frozen.PixelHeight - px.Y);
        BitmapSource shot = new CroppedBitmap(_frozen, px);
        if (shot.CanFreeze) shot.Freeze();
        if (shape != null)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var clip = shape.Clone();
                clip.Transform = new TransformGroup { Children = { new TranslateTransform(-dip.X, -dip.Y), new ScaleTransform(Fx, Fy) } };
                dc.PushClip(clip);
                dc.DrawImage(shot, new Rect(0, 0, px.Width, px.Height));
                dc.Pop();
            }
            var rtb = new RenderTargetBitmap(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            shot = rtb;
        }
        Hide(); // the frozen screen disappears at once; the card appears as it closes
        Deliver(shot, new Int32Rect(_virtual.X + px.X, _virtual.Y + px.Y, px.Width, px.Height));
        Close();
    }

    /// <summary>
    /// The shot is acknowledged quietly: a thin outline traces exactly what was captured, draws in a
    /// touch, and fades. No white flash over the screen. <paramref name="px"/> is in physical pixels.
    /// </summary>
    static void Mark(Int32Rect px)
    {
        if (Motion.Reduced || App.TestMode || px.Width < 4 || px.Height < 4) return;
        var line = new Border
        {
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(6), IsHitTestVisible = false,
            BorderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var scale = new ScaleTransform(1, 1);
        line.RenderTransform = scale;
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true, ShowInTaskbar = false,
            ShowActivated = false, ResizeMode = ResizeMode.NoResize, Content = line, IsHitTestVisible = false, Left = -32000, Top = -32000, Width = 10, Height = 10,
        };
        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            // Physical pixels straight to Windows: right on any monitor at any scale.
            SetWindowPos(hwnd, new IntPtr(-1), px.X, px.Y, px.Width, px.Height, 0x0010 /* NOACTIVATE */);
            long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
            SetWindowLongPtr(hwnd, -20, new IntPtr(ex | 0x20 /* TRANSPARENT */ | 0x80 /* TOOLWINDOW */));
        };
        w.Show();
        double shrink = 1 - Math.Min(0.02, 16.0 / Math.Max(px.Width, px.Height));
        Motion.To(scale, ScaleTransform.ScaleXProperty, shrink, 420, Motion.Out);
        Motion.To(scale, ScaleTransform.ScaleYProperty, shrink, 420, Motion.Out);
        Motion.From(line, OpacityProperty, 0, 1, 90, done: () => Motion.To(line, OpacityProperty, 0, 380, Motion.InOut, w.Close));
    }

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    static void Deliver(BitmapSource shot, Int32Rect where)
    {
        Mark(where);
        string dir = TestDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        string file = "";
        try
        {
            Directory.CreateDirectory(dir);
            file = System.IO.Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            for (int i = 2; File.Exists(file); i++) file = System.IO.Path.Combine(dir, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss} ({i}).png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(shot));
            using (var fs = File.Create(file)) enc.Save(fs);
            LastSaved = file;
        }
        catch (Exception ex) { Log.Write("Screenshot not saved: " + ex.Message); file = ""; }

        bool copied = false;
        if (!App.TestMode)
        {
            for (int attempt = 0; attempt < 5 && !copied; attempt++)
            {
                try
                {
                    var data = new DataObject();
                    data.SetImage(shot);
                    if (file.Length > 0)
                    {
                        data.SetData("PNG", new MemoryStream(File.ReadAllBytes(file)));
                        data.SetFileDropList(new System.Collections.Specialized.StringCollection { file });
                    }
                    Clipboard.SetDataObject(data, true);
                    copied = true;
                }
                catch (COMException) { Thread.Sleep(60); } // clipboard briefly held by another app
            }
        }
        else copied = true;
        CaptureToast.Show(shot, file, copied);
    }

    void Cancel()
    {
        if (_finished) return;
        _finished = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _open = null;
        if (!App.TestMode && _onDone != null && CaptureToast.Current == null) _onDone();
        else if (_onDone != null) CaptureToast.AfterClose = _onDone;
    }
}

/// <summary>The card after a capture: thumbnail, "Copied", and quick actions. Leaves on its own.</summary>
public sealed class CaptureToast : Window
{
    internal static CaptureToast? Current { get; private set; }
    internal static Action? AfterClose { get; set; }
    readonly string _file;
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly TranslateTransform _shift = new();

    public static void Show(BitmapSource shot, string file, bool copied)
    {
        Current?.Close();
        Current = new CaptureToast(shot, file, copied);
        Current.Show();
    }

    CaptureToast(BitmapSource shot, string file, bool copied)
    {
        _file = file;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; ShowActivated = false; SizeToContent = SizeToContent.WidthAndHeight; Title = "Screenshot";
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var card = new Border { CornerRadius = new CornerRadius(16), Padding = new Thickness(10), Margin = new Thickness(20), BorderThickness = new Thickness(1), RenderTransform = _shift, Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Opacity = 0.4, Color = Colors.Black };

        double scale = Math.Min(260.0 / shot.PixelWidth, 150.0 / shot.PixelHeight);
        var thumb = new Border
        {
            CornerRadius = new CornerRadius(9), ClipToBounds = true, Width = Math.Max(40, shot.PixelWidth * Math.Min(scale, 1)), Height = Math.Max(30, shot.PixelHeight * Math.Min(scale, 1)),
            Background = new ImageBrush(shot) { Stretch = Stretch.Uniform },
        };
        var title = new TextBlock { Text = copied ? "Copied to clipboard" : "Screenshot saved", FontSize = 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 0) };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var sub = new TextBlock { Text = file.Length > 0 ? "Saved to Pictures › Screenshots" : "Couldn't save the file", FontSize = 12, Margin = new Thickness(2, 2, 0, 2) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextDim");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Action("Open", (_, _) => Open(_file)));
        actions.Children.Add(Action("Show in Files", (_, _) => { if (_file.Length > 0) NorrskenApps.Launch("files", _file); Close(); }));
        actions.Children.Add(Action("Edit", (_, _) => { if (_file.Length > 0) StartShell(File.Exists(AppInstaller.FlickerboxExe) ? AppInstaller.FlickerboxExe : "mspaint.exe", (File.Exists(AppInstaller.FlickerboxExe) ? "--edit " : "") + $"\"{_file}\""); Close(); }));

        var stack = new StackPanel();
        stack.Children.Add(thumb);
        stack.Children.Add(title);
        stack.Children.Add(sub);
        stack.Children.Add(actions);
        card.Child = stack;
        thumb.MouseLeftButtonUp += (_, _) => Open(_file);
        Content = card;

        card.MouseEnter += (_, _) => _timer.Stop();
        card.MouseLeave += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => Leave();
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Right - ActualWidth;
            Top = work.Bottom - ActualHeight;
            Motion.From(card, OpacityProperty, 0, 1, 180);
            Motion.From(_shift, TranslateTransform.XProperty, 40, 0, 360, Motion.SoftSpring);
            _timer.Start();
        };
    }

    Button Action(string text, RoutedEventHandler click)
    {
        var b = new Button { Style = (Style)FindResource("IconButton"), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 4, 0), Content = new TextBlock { Text = text, FontSize = 12.5, Margin = new Thickness(10, 6, 10, 6) } };
        b.Click += click;
        return b;
    }

    void Open(string file)
    {
        if (file.Length > 0) StartShell(file, null);
        Close();
    }

    static void StartShell(string target, string? args)
    {
        if (App.TestMode) { App.TestLaunches.Add("open:" + target); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target, args ?? "") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Open failed: " + ex.Message); }
    }

    void Leave()
    {
        _timer.Stop();
        Motion.To(_shift, TranslateTransform.XProperty, 40, 220, Motion.InOut);
        Motion.To((UIElement)Content, OpacityProperty, 0, 200, done: Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        if (Current == this) Current = null;
        var after = AfterClose;
        AfterClose = null;
        after?.Invoke();
    }
}
