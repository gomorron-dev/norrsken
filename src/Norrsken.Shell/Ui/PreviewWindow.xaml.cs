using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Ui;

/// <summary>Live thumbnails of a dock item's windows, shown above its icon.</summary>
public partial class PreviewWindow : Window
{
    public sealed record Slot(TrackedWindow Window, ImageSource? Icon)
    {
        public string Title => Window.Title;
    }

    const int MaxSlots = 4;
    IntPtr _hwnd;
    readonly List<IntPtr> _thumbs = new();
    Point _anchor;

    public DockItem? Item { get; private set; }
    internal int SlotCount => (Slots.ItemsSource as List<Slot>)?.Count ?? 0;
    internal int ThumbCount => _thumbs.Count;

    /// <summary>The pointer entered (true) or left (false) the preview.</summary>
    public event Action<bool>? PointerOver;
    /// <summary>A window was chosen or closed; the dock should dismiss the preview.</summary>
    public event Action? Done;

    public PreviewWindow()
    {
        InitializeComponent();
        MouseEnter += (_, _) => PointerOver?.Invoke(true);
        MouseLeave += (_, _) => PointerOver?.Invoke(false);
        SizeChanged += (_, _) => { if (IsVisible) Place(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        Native.RoundCorners(_hwnd);
    }

    /// <summary>Show (or switch to) <paramref name="item"/>; <paramref name="anchor"/> is the top-centre point to sit above, in DIPs.</summary>
    public void ShowFor(DockItem item, Point anchor)
    {
        var windows = item.Windows.Where(w => IsWindow(w.Handle)).Take(MaxSlots).ToList();
        if (windows.Count == 0) { HidePreview(); return; }
        Item = item;
        _anchor = anchor;
        ClearThumbs();
        Slots.ItemsSource = windows.Select(w => new Slot(w, w.Icon ?? item.Icon)).ToList();
        bool wasVisible = IsVisible;
        if (!wasVisible) Show();
        ApplyBorderColour();
        UpdateLayout();
        Place();
        if (!wasVisible) Motion.From(Root, OpacityProperty, 0, 1, 160);
        // Thumbnails need final slot positions, so register after this layout pass.
        Dispatcher.InvokeAsync(RegisterThumbnails, DispatcherPriority.Loaded);
    }

    void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(_anchor.X - ActualWidth / 2, area.Left + 8, Math.Max(area.Left + 8, area.Right - ActualWidth - 8));
        Top = _anchor.Y - ActualHeight - 12;
        if (_thumbs.Count > 0) Dispatcher.InvokeAsync(RegisterThumbnails, DispatcherPriority.Loaded);
    }

    public void HidePreview()
    {
        ClearThumbs();
        Item = null;
        if (IsVisible) Hide();
    }

    void ApplyBorderColour()
    {
        if (_hwnd == IntPtr.Zero || TryFindResource("Border") is not SolidColorBrush { Color: var c }) return;
        // Composite the translucent border over the surface so DWM gets an opaque COLORREF.
        var bg = (TryFindResource("SurfaceSolid") as SolidColorBrush)?.Color ?? Colors.Black;
        byte Mix(byte a, byte b) => (byte)(b + (a - b) * c.A / 255);
        int colorref = Mix(c.R, bg.R) | Mix(c.G, bg.G) << 8 | Mix(c.B, bg.B) << 16;
        DwmSetWindowAttribute(_hwnd, Dwm.DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
    }

    void RegisterThumbnails()
    {
        ClearThumbs();
        if (!IsVisible || Slots.ItemsSource is not List<Slot> slots) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        for (int i = 0; i < slots.Count; i++)
        {
            if (Slots.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp
                || cp.ContentTemplate.FindName("Thumb", cp) is not Border box) continue;
            if (Dwm.DwmRegisterThumbnail(_hwnd, slots[i].Window.Handle, out IntPtr thumb) != 0) continue;
            _thumbs.Add(thumb);
            if (Dwm.DwmQueryThumbnailSourceSize(thumb, out var src) != 0 || src.cx <= 0 || src.cy <= 0) continue;

            var r = box.TransformToAncestor(this).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
            // Fit the window into its slot, preserving aspect ratio, centred.
            double fit = Math.Min(r.Width * scale / src.cx, r.Height * scale / src.cy);
            double w = src.cx * fit, h = src.cy * fit;
            double x = r.X * scale + (r.Width * scale - w) / 2, y = r.Y * scale + (r.Height * scale - h) / 2;
            var props = new Dwm.THUMBNAIL_PROPERTIES
            {
                dwFlags = Dwm.TNP_RECTDESTINATION | Dwm.TNP_VISIBLE | Dwm.TNP_OPACITY | Dwm.TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT { Left = (int)x, Top = (int)y, Right = (int)(x + w), Bottom = (int)(y + h) },
                opacity = 255, fVisible = true, fSourceClientAreaOnly = false,
            };
            Dwm.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    void ClearThumbs()
    {
        foreach (var t in _thumbs) Dwm.DwmUnregisterThumbnail(t);
        _thumbs.Clear();
    }

    void Slot_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Slot slot) return;
        WindowTracker.Activate(slot.Window);
        Done?.Invoke();
    }

    void SlotClose_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Slot slot) return;
        WindowTracker.Close(slot.Window);
        e.Handled = true;
        // Give the app a moment to close (it may ask to save), then refresh what's left.
        var item = Item;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (item != null && Item == item) ShowFor(item, _anchor);
        };
        t.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearThumbs();
        base.OnClosed(e);
    }
}
