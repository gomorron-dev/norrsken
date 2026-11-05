using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken's Alt+Tab: live previews of your windows on a calm card, most recent first.
/// Hold Alt and tap Tab (Shift+Tab goes back, arrows move, Esc cancels); let go of Alt to switch.
/// A quick tap switches to the previous window without showing anything at all.
/// </summary>
public sealed class AltTab : Window
{
    static AltTab? _current;
    static List<TrackedWindow> _order = new();
    static int _index;
    static bool _active;
    static readonly DispatcherTimer Delay = new() { Interval = TimeSpan.FromMilliseconds(110) };

    readonly WrapPanel _cards = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly List<(Border Card, Border Thumb, IntPtr Handle)> _slots = new();
    readonly List<IntPtr> _thumbs = new();
    const double ThumbW = 220, ThumbH = 138;

    public static void Start()
    {
        KeyboardHook.Pressed += a =>
        {
            switch (a)
            {
                case "alttab": Begin(+1); break;
                case "alttab-back": Begin(-1); break;
                case "alttab-left": Step(-1); break;
                case "alttab-right": Step(+1); break;
                case "alttab-commit": Commit(); break;
                case "alttab-cancel": Cancel(); break;
                case "overview": ShowOverview(); break;
                case "overview-left": Step(-1); break;
                case "overview-right": Step(+1); break;
                case "overview-commit": Commit(); break;
                case "overview-cancel": Cancel(); break;
            }
        };
    }

    static AltTab() => Delay.Tick += (_, _) => { Delay.Stop(); if (_active && _current == null && _order.Count > 1) { _current = new AltTab(); _current.Show(); } };

    internal static bool IsOpen => _current != null;

    /// <summary>Win+Tab: every window at once; stays open until you pick one (click, arrows + Enter) or press Esc.</summary>
    internal static void ShowOverview()
    {
        if (_active) { Cancel(); return; }
        App.Tracker.Refresh();
        _order = App.Tracker.Windows.OrderByDescending(w => w.IsActive).ThenByDescending(w => w.LastActivated).ToList();
        if (_order.Count == 0) return;
        _active = true;
        _index = 0;
        KeyboardHook.OverviewOpen = true;
        _current = new AltTab();
        _current.Show();
    }
    internal static int Index => _index;

    internal static void Begin(int dir)
    {
        if (_active) { Step(dir); return; }
        App.Tracker.Refresh();
        _order = App.Tracker.Windows.Where(w => !App.TestMode || w.ExePath != null).OrderByDescending(w => w.IsActive).ThenByDescending(w => w.LastActivated).ToList();
        if (_order.Count == 0) return;
        _active = true;
        _index = _order.Count > 1 ? (dir > 0 ? 1 : _order.Count - 1) : 0;
        Delay.Start();
    }

    internal static void Step(int dir)
    {
        if (!_active || _order.Count == 0) return;
        _index = (_index + dir + _order.Count) % _order.Count;
        _current?.Highlight();
    }

    internal static void Commit()
    {
        if (!_active) return;
        var target = _order.ElementAtOrDefault(_index);
        End();
        if (target != null && !App.TestMode) WindowTracker.Activate(target);
    }

    internal static void Cancel() => End();

    static void End()
    {
        _active = false;
        KeyboardHook.OverviewOpen = false;
        Delay.Stop();
        _current?.Close();
        _current = null;
    }

    AltTab()
    {
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.CenterScreen; Title = "Switch windows";
        SetResourceReference(BackgroundProperty, "SurfaceSolid");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        var work = SystemParameters.WorkArea;
        _cards.MaxWidth = Math.Min(work.Width - 120, 6 * (ThumbW + 28));
        foreach (var w in _order)
        {
            var thumb = new Border { Width = ThumbW, Height = ThumbH, CornerRadius = Theme.R(10) };
            thumb.SetResourceReference(Border.BackgroundProperty, "SurfaceAlt");
            if (w.IsMinimized && w.Icon != null) thumb.Child = new Image { Source = w.Icon, Width = 56, Height = 56 };
            var icon = new Image { Source = w.Icon, Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) };
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(w.Title) ? w.AppName : w.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, MaxWidth = ThumbW - 30 };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var caption = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 9, 2, 0) };
            caption.Children.Add(icon);
            caption.Children.Add(title);
            var stack = new StackPanel();
            stack.Children.Add(thumb);
            stack.Children.Add(caption);
            var card = new Border { CornerRadius = Theme.R(14), Padding = new Thickness(8), Margin = new Thickness(6), BorderThickness = new Thickness(1.5), Child = stack, Cursor = System.Windows.Input.Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
            int i = _slots.Count;
            card.MouseLeftButtonUp += (_, _) => { _index = i; Commit(); };
            _cards.Children.Add(card);
            _slots.Add((card, thumb, w.IsMinimized ? IntPtr.Zero : w.Handle));
        }
        var root = new Border { Padding = new Thickness(14), Child = _cards, BorderThickness = new Thickness(1) };
        root.SetResourceReference(Border.BorderBrushProperty, "DockLine");
        Content = root;
        Highlight();
        Loaded += (_, _) =>
        {
            Motion.From(root, OpacityProperty, 0, 1, 120);
            Motion.Pop(root, 0.96);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, RegisterThumbs);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.RoundCorners(hwnd);
        // Never take focus from the window you're switching away from.
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
    }

    void Highlight()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            var c = _slots[i].Card;
            bool on = i == _index;
            if (on) { c.SetResourceReference(Border.BorderBrushProperty, "Text"); c.SetResourceReference(Border.BackgroundProperty, "Hover"); }
            else { c.BorderBrush = Brushes.Transparent; c.Background = Brushes.Transparent; }
            // The chosen window lifts a little toward you; the rest settle back.
            if (c.RenderTransform is ScaleTransform s && !s.IsFrozen)
            {
                Motion.To(s, ScaleTransform.ScaleXProperty, on ? 1.045 : 1, 240, Motion.SoftSpring);
                Motion.To(s, ScaleTransform.ScaleYProperty, on ? 1.045 : 1, 240, Motion.SoftSpring);
            }
        }
        if (_index < _slots.Count) _slots[_index].Card.BringIntoView();
    }

    void RegisterThumbs()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var m = source.CompositionTarget.TransformToDevice;
        foreach (var (_, thumb, handle) in _slots)
        {
            if (handle == IntPtr.Zero || Dwm.DwmRegisterThumbnail(hwnd, handle, out var t) != 0) continue;
            _thumbs.Add(t);
            var topLeft = thumb.TransformToAncestor(this).Transform(new Point(0, 0));
            Dwm.DwmQueryThumbnailSourceSize(t, out var src);
            double scale = Math.Min(ThumbW / Math.Max(1, src.cx / m.M11), ThumbH / Math.Max(1, src.cy / m.M22));
            double w = src.cx / m.M11 * Math.Min(scale, 1), h = src.cy / m.M22 * Math.Min(scale, 1);
            double x = topLeft.X + (ThumbW - w) / 2, y = topLeft.Y + (ThumbH - h) / 2;
            var props = new Dwm.THUMBNAIL_PROPERTIES
            {
                dwFlags = Dwm.TNP_RECTDESTINATION | Dwm.TNP_VISIBLE | Dwm.TNP_OPACITY,
                rcDestination = new Native.RECT { Left = (int)(x * m.M11), Top = (int)(y * m.M22), Right = (int)((x + w) * m.M11), Bottom = (int)((y + h) * m.M22) },
                opacity = 255, fVisible = true,
            };
            Dwm.DwmUpdateThumbnailProperties(t, ref props);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var t in _thumbs) Dwm.DwmUnregisterThumbnail(t);
        _thumbs.Clear();
        base.OnClosed(e);
        if (_current == this) _current = null;
    }
}
