using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Norrsken.Shell.Core;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Ui;

/// <summary>
/// The Norrsken dock: a floating pill, centred at the bottom of the primary screen.
/// The window is a transparent full-width strip: transparent pixels pass clicks through,
/// and a registered AppBar keeps maximised windows clear of the pill.
/// It is a non-activating tool window, so it never steals focus and never appears in Alt+Tab.
/// </summary>
public partial class DockWindow : Window
{
    const int HotkeyLauncher = 1;
    const double ReserveDip = 72;
    const double IconSize = 42, CellWidth = 54, Amp = 0.55, Sigma = 64;
    const double DragThreshold = 6, RemoveLift = 70, HiddenOffset = 100;

    IntPtr _hwnd;
    uint _callbackMsg, _taskbarCreatedMsg, _shellHookMsg;
    bool _appBarRegistered, _risen;
    LauncherWindow? _launcher;
    QuickSettingsWindow? _quick;
    SettingsWindow? _settings;
    ContextMenu? _menu;
    readonly DispatcherTimer _clock = new();

    // Motion state: magnification scale per cell, plus a "flip" offset per cell that eases to its
    // target (drag gaps, or zero after a reorder so icons glide from where they were).
    readonly Dictionary<FrameworkElement, double> _scale = new();
    readonly Dictionary<FrameworkElement, double> _flip = new();
    readonly Stopwatch _time = Stopwatch.StartNew();
    double _lastFrame, _mouseX, _labelOpacity;
    bool _hovering, _rendering;
    FrameworkElement? _labelTarget;

    public static string LauncherHotkey { get; private set; } = "none";

    public DockWindow()
    {
        InitializeComponent();
        DockItems.ItemsSource = App.Dock.Items;
        App.Dock.AnimateOut = AnimateOut;
        App.Dock.Revived = Revive;
        foreach (var item in App.Dock.Items) item.PropertyChanged += OnItemChanged;
        App.Dock.Items.CollectionChanged += WatchItems;
        _clock.Tick += (_, _) => { _clock.Stop(); UpdateClock(); ScheduleClock(); };
        _autoHide.Tick += (_, _) => AutoHideTick();
        SystemStatus.Changed += UpdateStatusGlyphs;
        DockHost.SizeChanged += (_, _) => RevealZone.Width = DockHost.ActualWidth + 160;
        UpdateStatusGlyphs();
        InitPreviews();
        ApplySettings();
        Loaded += (_, _) => Rise();
        Loaded += (_, _) => StartBin();
    }

    // ───────────────────────── window plumbing ─────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);

        PositionWindow();
        // Shell hook: tells us when an app flashes for attention (new message, finished download...).
        _shellHookMsg = RegisterWindowMessageW("SHELLHOOK");
        RegisterShellHookWindow(_hwnd);
        if (App.TestMode) return; // isolated test desktop: no AppBar, hotkey or taskbar changes
        _callbackMsg = RegisterWindowMessageW("NorrskenAppBarMessage");
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        RegisterAppBar();
        RegisterLauncherHotkey();
        TaskbarHider.Apply(App.Settings.HideWindowsTaskbar);
    }

    void RegisterAppBar()
    {
        var abd = NewAppBarData();
        abd.uCallbackMessage = _callbackMsg;
        // Always registered: besides reserving space, it's how Windows tells us a full-screen app is up.
        _appBarRegistered = SHAppBarMessage(ABM_NEW, ref abd) != 0;
        if (!_appBarRegistered) Log.Write("ABM_NEW failed; dock will float without reserving space");
        SetAppBarPos();
    }

    /// <summary>Explorer restarted: it has forgotten every AppBar, so register again and re-apply.</summary>
    void OnExplorerRestarted()
    {
        Log.Write("Explorer restarted; re-registering the dock");
        if (_appBarRegistered)
        {
            var abd = NewAppBarData();
            SHAppBarMessage(ABM_REMOVE, ref abd);
        }
        RegisterAppBar();
        if (App.Settings.HideWindowsTaskbar) TaskbarHider.Apply(true);
    }

    void PositionWindow()
    {
        Width = SystemParameters.PrimaryScreenWidth;
        Left = 0;
        Top = SystemParameters.PrimaryScreenHeight - Height;
    }

    APPBARDATA NewAppBarData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    void SetAppBarPos()
    {
        if (!_appBarRegistered) return;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
        bool reserve = App.Settings.ReserveSpace && !App.Settings.AutoHide;
        int h = reserve ? (int)Math.Round(ReserveDip * scale) : 0;
        var abd = NewAppBarData();
        abd.uEdge = ABE_BOTTOM;
        abd.rc = new RECT { Left = 0, Top = sh - h, Right = sw, Bottom = sh };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.Top = abd.rc.Bottom - h;
        SHAppBarMessage(ABM_SETPOS, ref abd);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(() => { PositionWindow(); SetAppBarPos(); });
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_callbackMsg != 0 && (uint)msg == _callbackMsg)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED: SetAppBarPos(); break;
                case ABN_FULLSCREENAPP: App.Fullscreen?.ShellSignal(lParam != IntPtr.Zero); break;
            }
            handled = true;
        }
        else if (_shellHookMsg != 0 && (uint)msg == _shellHookMsg)
        {
            if (wParam.ToInt32() == HSHELL_FLASH) App.Tracker?.SetAttention(lParam);
        }
        else if (_taskbarCreatedMsg != 0 && (uint)msg == _taskbarCreatedMsg)
        {
            Dispatcher.BeginInvoke(OnExplorerRestarted);
        }
        else if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyLauncher)
        {
            ToggleLauncher();
            handled = true;
        }
        else if (msg == WM_DISPLAYCHANGE)
        {
            Dispatcher.BeginInvoke(() => { PositionWindow(); SetAppBarPos(); });
        }
        return IntPtr.Zero;
    }

    bool _fullscreen;

    /// <summary>
    /// Games, YouTube and video go full screen: the dock and every Norrsken popup get out of the
    /// way and leave the topmost band; the auto-hide edge strip is switched off so it can't catch
    /// the pointer mid-game. Everything comes back when full screen ends.
    /// </summary>
    internal void OnFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        _summoned = false;
        Topmost = !fullscreen;
        DockHost.IsHitTestVisible = !fullscreen;
        if (fullscreen)
        {
            _launcher?.HideLauncher();
            _quick?.HidePanel();
            HidePreview();
            if (_menu != null) _menu.IsOpen = false;
            _autoHide.Stop();
            RevealZone.Visibility = Visibility.Collapsed;
            Motion.To(DockHost, OpacityProperty, 0, 160);
        }
        else
        {
            RevealZone.Visibility = _dockHidden ? Visibility.Visible : Visibility.Collapsed;
            Motion.To(DockHost, OpacityProperty, _dockHidden ? 0 : 1, 240);
        }
    }

    internal bool IsSteppedAside => _fullscreen;

    // Norrsken never takes a Windows shortcut: only Ctrl+Alt combos, and never Ctrl+Alt+Space (Claude Desktop).
    static readonly (uint Mods, uint Vk, string Label)[] HotkeyCandidates =
    [
        (MOD_CONTROL | MOD_ALT, 0x4E /* N */, "Ctrl+Alt+N"),
        (MOD_CONTROL | MOD_ALT, 0x4B /* K */, "Ctrl+Alt+K"),
    ];

    void RegisterLauncherHotkey()
    {
        foreach (var (mods, vk, label) in HotkeyCandidates)
        {
            if (RegisterHotKey(_hwnd, HotkeyLauncher, mods | MOD_NOREPEAT, vk))
            {
                LauncherHotkey = label;
                Log.Write($"Launcher hotkey: {label}");
                return;
            }
            Log.Write($"{label} is taken by another app");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _clock.Stop();
        if (_hwnd != IntPtr.Zero)
        {
            DeregisterShellHookWindow(_hwnd);
            UnregisterHotKey(_hwnd, HotkeyLauncher);
            if (_appBarRegistered)
            {
                var abd = NewAppBarData();
                SHAppBarMessage(ABM_REMOVE, ref abd);
                _appBarRegistered = false;
            }
        }
        base.OnClosed(e);
        if (_hwnd == IntPtr.Zero) return; // never shown (self-test render): not the live dock
        if (TaskbarHider.IsHidden) TaskbarHider.Restore();
        App.Tracker?.Dispose();
        Log.Write("Norrsken stopped; AppBar removed");
        if (!App.TestMode) Application.Current.Shutdown(); // the dock is the app; nothing should outlive it
    }

    // ───────────────────────── settings & clock ─────────────────────────

    public void ApplySettings()
    {
        var vis = App.Settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        ClockCell.Visibility = Div2.Visibility = vis;
        BinCell.Visibility = Div4.Visibility = App.Settings.ShowBinInDock ? Visibility.Visible : Visibility.Collapsed;
        UpdateClock();
        ScheduleClock();
        SetAppBarPos();
        if (_hwnd != IntPtr.Zero && !App.TestMode) TaskbarHider.Apply(App.Settings.HideWindowsTaskbar);
        if (App.Settings.AutoHide) { if (!_hovering) _autoHide.Start(); }
        else { _autoHide.Stop(); if (_dockHidden) RevealDock(); }
        StartRendering(); // settles magnification if it was just switched off
    }

    public void UpdateClock()
    {
        UpdateBadges();
        if (!_mediaHooked && !App.TestMode) _ = UpdateMediaAsync();
        var now = DateTime.Now;
        string fmt = App.Settings.Clock24h ? "HH:mm" : "h:mm";
        if (App.Settings.ShowSeconds) fmt += ":ss";
        string time = now.ToString(fmt, CultureInfo.CurrentCulture);
        if (TimeText.Text.Length > 0 && TimeText.Text != time && !App.Settings.ShowSeconds && !Motion.Reduced)
        {
            // The new minute rolls up into place.
            var roll = TimeText.RenderTransform as TranslateTransform ?? new TranslateTransform();
            TimeText.RenderTransform = roll;
            Motion.From(roll, TranslateTransform.YProperty, 7, 0, 360, Motion.Out);
            Motion.From(TimeText, OpacityProperty, 0.15, 1, 300);
        }
        TimeText.Text = time;
        var b = Power.Battery();
        BatteryText.Visibility = b == null ? Visibility.Collapsed : Visibility.Visible;
        if (b is { } bat) BatteryText.Text = (bat.Charging ? "+" : "") + bat.Percent + "%";
    }

    // Tick on the minute (or second) boundary instead of polling.
    public void ScheduleClock()
    {
        var now = DateTime.Now;
        _clock.Interval = App.Settings.ShowSeconds
            ? TimeSpan.FromMilliseconds(1005 - now.Millisecond)
            : TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond - 5);
        _clock.Start();
    }

    static string DateLabel() => DateTime.Now.ToString("dddd d MMMM", CultureInfo.CurrentCulture);

    // ───────────────────────── auto-hide ─────────────────────────

    readonly DispatcherTimer _autoHide = new() { Interval = TimeSpan.FromMilliseconds(700) };
    bool _dockHidden;

    bool PopupOpen =>
        _launcher?.IsVisible == true || _quick?.IsVisible == true || _preview?.IsVisible == true
        || _menu?.IsOpen == true || _dragging;

    void AutoHideTick()
    {
        if (!App.Settings.AutoHide || _hovering) { _autoHide.Stop(); return; }
        if (PopupOpen) return; // keep checking until whatever is open closes
        _autoHide.Stop();
        HideDock();
    }

    void HideDock()
    {
        if (_dockHidden) return;
        _dockHidden = true;
        Motion.To(DockShift, TranslateTransform.YProperty, HiddenOffset, 300, Motion.InOut);
        Motion.To(DockHost, OpacityProperty, 0, 300);
        if (!_fullscreen) RevealZone.Visibility = Visibility.Visible;
    }

    void RevealDock()
    {
        if (!_dockHidden || _fullscreen) return;
        _dockHidden = false;
        RevealZone.Visibility = Visibility.Collapsed;
        Motion.To(DockShift, TranslateTransform.YProperty, 0, 420, Motion.SoftSpring);
        Motion.To(DockHost, OpacityProperty, 1, 180);
    }

    void RevealZone_MouseEnter(object sender, MouseEventArgs e)
    {
        RevealDock();
        _autoHide.Stop();
        if (!_hovering) _autoHide.Start(); // hides again if the pointer doesn't come up to the dock
    }

    // ───────────────────────── entrance & item animations ─────────────────────────

    void Rise()
    {
        if (Motion.Reduced) { _risen = true; return; }
        Motion.From(DockShift, TranslateTransform.YProperty, 90, 0, 620, Motion.SoftSpring);
        Motion.From(DockHost, OpacityProperty, 0, 1, 360, done: () => _risen = true);
    }

    static Grid? CellOf(DependencyObject? container) =>
        container is ContentPresenter cp && VisualTreeHelper.GetChildrenCount(cp) > 0 ? VisualTreeHelper.GetChild(cp, 0) as Grid : null;

    static FrameworkElement LiftOf(Grid cell) => (FrameworkElement)cell.Children[0];
    static Border DotOf(Grid cell) => (Border)cell.Children[1];

    // Transforms declared in a DataTemplate are frozen and shared, so each icon gets its own live set:
    // [0] magnify scale, [1] appear scale, [2] bounce offset.
    static Transform Part(FrameworkElement lift, int i)
    {
        if (lift.RenderTransform is not TransformGroup { IsFrozen: false } group)
        {
            group = new TransformGroup { Children = { new ScaleTransform(), new ScaleTransform(), new TranslateTransform() } };
            lift.RenderTransform = group;
        }
        return group.Children[i];
    }

    void Cell_Loaded(object sender, RoutedEventArgs e)
    {
        var cell = (Grid)sender;
        if (cell.DataContext is DockItem { SkipAppear: true } moved) { moved.SkipAppear = false; cell.Tag = "shown"; return; }
        if (!_risen || Motion.Reduced) return; // first icons arrive with the dock's rise
        if (cell.Tag is "shown") return;
        cell.Tag = "shown";
        var appear = (ScaleTransform)Part(LiftOf(cell), 1);
        Motion.From(cell, WidthProperty, 0, CellWidth, 340, Motion.Out);
        Motion.From(cell, OpacityProperty, 0, 1, 280);
        Motion.From(appear, ScaleTransform.ScaleXProperty, 0.3, 1, 460, Motion.Spring);
        Motion.From(appear, ScaleTransform.ScaleYProperty, 0.3, 1, 460, Motion.Spring);
    }

    void AnimateOut(DockItem item, Action done)
    {
        if (Motion.Reduced || CellOf(DockItems.ItemContainerGenerator.ContainerFromItem(item)) is not { } cell)
        {
            done();
            return;
        }
        var appear = (ScaleTransform)Part(LiftOf(cell), 1);
        Motion.To(appear, ScaleTransform.ScaleXProperty, 0.4, 220);
        Motion.To(appear, ScaleTransform.ScaleYProperty, 0.4, 220);
        Motion.To(cell, OpacityProperty, 0, 180);
        Motion.To(cell, WidthProperty, 0, 280, Motion.InOut, done);
    }

    void Revive(DockItem item)
    {
        if (CellOf(DockItems.ItemContainerGenerator.ContainerFromItem(item)) is not { } cell) return;
        var appear = (ScaleTransform)Part(LiftOf(cell), 1);
        Motion.To(cell, WidthProperty, CellWidth, 260);
        Motion.To(cell, OpacityProperty, 1, 200);
        Motion.To(appear, ScaleTransform.ScaleXProperty, 1, 300, Motion.Spring);
        Motion.To(appear, ScaleTransform.ScaleYProperty, 1, 300, Motion.Spring);
    }

    // The dot's resting state is bound (Opacity <- DotOpacity, Width <- IsActive); these
    // transitions play on top and then stop, so they can never leave a dot in a stale state.
    readonly Dictionary<DockItem, double> _lastDot = new();

    void WatchItems(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DockItem item in e.OldItems ?? Array.Empty<DockItem>())
        {
            item.PropertyChanged -= OnItemChanged;
            _lastDot.Remove(item);
        }
        foreach (DockItem item in e.NewItems ?? Array.Empty<DockItem>()) item.PropertyChanged += OnItemChanged;
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DockItem item || e.PropertyName is not (nameof(DockItem.DotOpacity)
            or nameof(DockItem.IsActive) or nameof(DockItem.NeedsAttention))) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (CellOf(DockItems.ItemContainerGenerator.ContainerFromItem(item)) is not { } cell) return;
            var dot = DotOf(cell);
            switch (e.PropertyName)
            {
                case nameof(DockItem.DotOpacity):
                    double from = _lastDot.GetValueOrDefault(item, item.DotOpacity == 0 ? 1 : 0);
                    _lastDot[item] = item.DotOpacity;
                    if (!Motion.Reduced && !item.NeedsAttention)
                        dot.BeginAnimation(OpacityProperty, Transition(from, item.DotOpacity, 260, Motion.Out));
                    break;
                case nameof(DockItem.IsActive):
                    if (!Motion.Reduced)
                        dot.BeginAnimation(WidthProperty, Transition(item.IsActive ? 4 : 16, item.IsActive ? 16 : 4, 340, Motion.Spring));
                    break;
                case nameof(DockItem.NeedsAttention):
                    SetAttention(cell, item.NeedsAttention);
                    break;
            }
        });
    }

    /// <summary>An app wants you: it hops twice, then its dot breathes until you switch to it.</summary>
    void SetAttention(Grid cell, bool on)
    {
        var dot = DotOf(cell);
        if (!on) { dot.BeginAnimation(OpacityProperty, null); return; }
        Bounce(LiftOf(cell), times: 2);
        if (Motion.Reduced) return;
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut,
        });
    }

    static DoubleAnimation Transition(double from, double to, int ms, IEasingFunction ease) =>
        new(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };

    static void Bounce(FrameworkElement lift, int times = 1)
    {
        if (Motion.Reduced) return;
        var t = (TranslateTransform)Part(lift, 2);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(760), RepeatBehavior = new RepeatBehavior(times) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(170)), Motion.Out));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(500)), Motion.Out));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(640)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        t.BeginAnimation(TranslateTransform.YProperty, a);

        // Squash and stretch: tall on the way up, squashed as it lands, then settles.
        var s = (ScaleTransform)Part(lift, 1);
        DoubleAnimationUsingKeyFrames Shape(double up, double land, double rebound)
        {
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(760), RepeatBehavior = new RepeatBehavior(times), FillBehavior = FillBehavior.Stop };
            k.KeyFrames.Add(new EasingDoubleKeyFrame(up, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150)), Motion.Out));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(330))));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(land, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), Motion.Out));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), Motion.Out));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(rebound, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(660)), Motion.Out));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(760)), Motion.Out));
            return k;
        }
        s.BeginAnimation(ScaleTransform.ScaleXProperty, Shape(0.94, 1.12, 1.04));
        s.BeginAnimation(ScaleTransform.ScaleYProperty, Shape(1.08, 0.86, 0.96));
    }

    static readonly IEasingFunction Jelly = Frozen(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 });
    static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    /// <summary>Pressing an icon squishes it into the dock; letting go springs it back.</summary>
    static void Press(FrameworkElement lift, bool down)
    {
        Motion.To(lift, OpacityProperty, down ? 0.78 : 1, down ? 70 : 220);
        if (Motion.Reduced) return;
        var s = (ScaleTransform)Part(lift, 1);
        Motion.To(s, ScaleTransform.ScaleXProperty, down ? 0.86 : 1, down ? 90 : 420, down ? Motion.Out : Jelly);
        Motion.To(s, ScaleTransform.ScaleYProperty, down ? 0.82 : 1, down ? 90 : 420, down ? Motion.Out : Jelly);
    }

    // ───────────────────────── magnification & motion loop ─────────────────────────

    readonly record struct Cell(FrameworkElement Element, FrameworkElement? Lift, Func<string>? Label, DockItem? Item);

    List<Cell> Cells()
    {
        var list = new List<Cell> { new(LauncherCell, LauncherLift, () => "Norrsken", null), new(Div1, null, null, null) };
        var gen = DockItems.ItemContainerGenerator;
        for (int i = 0; i < DockItems.Items.Count; i++)
        {
            if (gen.ContainerFromIndex(i) is not ContentPresenter cp || CellOf(cp) is not { } cell) continue;
            var item = DockItems.Items[i] as DockItem;
            list.Add(new(cp, LiftOf(cell), item is { Leaving: false } ? () => item.Name : null, item));
        }
        if (MediaCell.Visibility == Visibility.Visible)
        {
            list.Add(new(Div3, null, null, null));
            list.Add(new(MediaCell, null, () => _mediaLabel, null));
        }
        if (BinCell.Visibility == Visibility.Visible)
        {
            list.Add(new(Div4, null, null, null));
            list.Add(new(BinCell, BinLift, () => _binLabel, null));
        }
        if (ClockCell.Visibility == Visibility.Visible)
        {
            list.Add(new(Div2, null, null, null));
            list.Add(new(ClockCell, null, DateLabel, null));
        }
        return list;
    }

    // Layout centre in Strip coordinates, ignoring render transforms.
    double BaseCenter(FrameworkElement e)
    {
        double x = VisualTreeHelper.GetOffset(e).X;
        if (e is ContentPresenter) x += VisualTreeHelper.GetOffset(DockItems).X;
        return x + e.ActualWidth / 2;
    }

    static TranslateTransform Spread(FrameworkElement e)
    {
        if (e.RenderTransform is TranslateTransform t) return t;
        t = new TranslateTransform();
        e.RenderTransform = t;
        return t;
    }

    /// <summary>
    /// One frame: icons near the pointer grow on a Gaussian falloff, neighbours slide apart so
    /// nothing overlaps, the pill widens to fit, and drag gaps / reorder glides ease into place.
    /// Returns true when everything has settled.
    /// </summary>
    bool Step(double dt)
    {
        var cells = Cells();
        double k = 1 - Math.Pow(0.74, dt * 60);
        bool magnify = _hovering && !_dragging && App.Settings.Magnify && !Motion.Reduced;
        bool settled = true;
        var growth = new double[cells.Count];
        double total = 0;
        FrameworkElement? hovered = null;
        string? label = null;

        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            double center = BaseCenter(c.Element);
            if (_hovering && !_dragging && c.Label != null && Math.Abs(_mouseX - center) <= c.Element.ActualWidth / 2)
            {
                hovered = c.Lift ?? c.Element;
                label = c.Label();
            }
            if (c.Lift == null) continue;
            double d = _mouseX - center;
            double target = magnify ? 1 + Amp * Math.Exp(-d * d / (2 * Sigma * Sigma)) : 1;
            double cur = _scale.GetValueOrDefault(c.Element, 1);
            cur += (target - cur) * k;
            if (Math.Abs(target - cur) < 0.002) cur = target; else settled = false;
            _scale[c.Element] = cur;
            growth[i] = (cur - 1) * IconSize;
            total += growth[i];
            var s = (ScaleTransform)Part(c.Lift, 0);
            s.ScaleX = s.ScaleY = cur;
            if (c.Element is ContentPresenter cp && CellOf(cp) is { Children.Count: > 2 } cellGrid)
                cellGrid.Children[2].Opacity = Math.Clamp((cur - 1) / Amp, 0, 1);
        }

        if (_dragging) settled &= StepDrag(cells, k);
        else settled &= EaseFlips(cells, _ => 0, k);

        double acc = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var t = Spread(cells[i].Element);
            t.X = acc + growth[i] / 2 - total / 2 + _flip.GetValueOrDefault(cells[i].Element);
            if (!_dragging || cells[i].Item != _dragItem) t.Y = 0;
            acc += growth[i];
        }
        Pill.Margin = Rim.Margin = new Thickness(-total / 2, 0, -total / 2, 0); // the glass rim grows with the pill

        foreach (var stale in _scale.Keys.Where(e => cells.All(c => c.Element != e)).ToList()) _scale.Remove(stale);
        foreach (var stale in _flip.Keys.Where(e => cells.All(c => c.Element != e)).ToList()) _flip.Remove(stale);

        if (_dragging)
        {
            hovered = _dragCell;
            label = _dragRemove ? "Remove from dock" : _dragItem?.Name;
        }
        if (hovered != null && label != null)
        {
            HoverText.Text = label;
            _labelTarget = hovered;
        }
        double labelTarget = hovered != null && _preview?.IsVisible != true ? 1 : 0;
        _labelOpacity += (labelTarget - _labelOpacity) * Math.Min(1, k * 1.5);
        if (Math.Abs(labelTarget - _labelOpacity) < 0.01) _labelOpacity = labelTarget; else settled = false;
        HoverLabel.Opacity = _labelOpacity;
        if (_labelTarget != null && _labelOpacity > 0) PlaceLabel(_labelTarget);
        return settled && !_dragging;
    }

    bool EaseFlips(List<Cell> cells, Func<Cell, double> target, double k)
    {
        bool settled = true;
        foreach (var c in cells)
        {
            double t = target(c);
            double cur = _flip.GetValueOrDefault(c.Element);
            if (cur == t) continue;
            cur += (t - cur) * k;
            if (Math.Abs(t - cur) < 0.3) cur = t; else settled = false;
            _flip[c.Element] = cur;
        }
        return settled;
    }

    void PlaceLabel(FrameworkElement target)
    {
        HoverLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = HoverLabel.DesiredSize;
        var top = target.TranslatePoint(new Point(target.ActualWidth / 2, 0), Overlay);
        Canvas.SetLeft(HoverLabel, Math.Round(top.X - size.Width / 2));
        Canvas.SetTop(HoverLabel, Math.Round(top.Y - size.Height - 9 + (1 - _labelOpacity) * 4));
    }

    void StartRendering()
    {
        if (_rendering) return;
        _rendering = true;
        _lastFrame = _time.Elapsed.TotalSeconds;
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        double now = _time.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastFrame, 0, 0.05);
        _lastFrame = now;
        if (!Step(dt)) return;
        CompositionTarget.Rendering -= OnFrame; // idle: no per-frame work at all
        _rendering = false;
    }

    void Dock_MouseMove(object sender, MouseEventArgs e) => PointerAt(e.GetPosition(Strip).X);

    void Dock_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return; // the dragged cell holds capture; leaving is part of dragging off
        PointerLeft();
    }

    void PointerAt(double x)
    {
        _hovering = true;
        _mouseX = x;
        _autoHide.Stop();
        StartRendering();
        if (!_dragging) UpdateHoverItem();
    }

    void PointerLeft()
    {
        _hovering = false;
        StartRendering();
        _hoverItem = null;
        _previewShow.Stop();
        if (_preview?.IsVisible == true) _previewHide.Start();
        if (App.Settings.AutoHide) _autoHide.Start();
    }

    // ───────────────────────── drag to reorder / drag off to unpin ─────────────────────────

    DockItem? _pressItem, _dragItem;
    Grid? _pressGrid;
    FrameworkElement? _dragCell;
    Point _pressPoint, _dragPoint;
    bool _dragging, _dragRemove;
    int _dragFrom, _dragTo;

    void PressStart(DockItem item, Grid grid, Point stripPoint)
    {
        _pressItem = item;
        _pressGrid = grid;
        _pressPoint = stripPoint;
        Press(LiftOf(grid), true);
    }

    void PressMove(Point stripPoint)
    {
        if (_pressItem == null) return;
        if (!_dragging)
        {
            if (!_pressItem.IsPinned || (stripPoint - _pressPoint).Length < DragThreshold) return;
            BeginDrag();
        }
        _dragPoint = stripPoint;
        _mouseX = stripPoint.X;
        StartRendering();
    }

    void BeginDrag()
    {
        if (_pressItem?.Entry == null || _pressGrid == null) return;
        _dragging = true;
        _dragItem = _pressItem;
        _dragCell = DockItems.ItemContainerGenerator.ContainerFromItem(_dragItem) as FrameworkElement;
        _dragFrom = _dragTo = App.Pinned.IndexOf(_dragItem.Entry);
        _dragRemove = false;
        HidePreview();
        Press(LiftOf(_pressGrid), false);
        Panel.SetZIndex(_dragCell!, 10);
    }

    bool StepDrag(List<Cell> cells, double k)
    {
        if (_dragCell == null || _dragItem == null) return true;
        var pinned = cells.Where(c => c.Item is { IsPinned: true, Leaving: false }).ToList();
        double dx = _dragPoint.X - _pressPoint.X, dy = _dragPoint.Y - _pressPoint.Y;
        double dragCentre = BaseCenter(_dragCell) + dx;

        // Where would it land? Count pinned neighbours whose centre is left of the dragged icon.
        _dragTo = pinned.Count(c => c.Element != _dragCell && BaseCenter(c.Element) < dragCentre);
        bool remove = dy < -RemoveLift;
        if (remove != _dragRemove)
        {
            _dragRemove = remove;
            Motion.To(LiftOf(CellOf(_dragCell)!), OpacityProperty, remove ? 0.35 : 1, 160);
        }

        _flip[_dragCell] = dx; // follows the pointer exactly
        Spread(_dragCell).Y = Math.Min(0, dy);
        int from = _dragFrom, to = remove ? int.MaxValue : _dragTo;
        return EaseFlips(cells.Where(c => c.Element != _dragCell).ToList(), c =>
        {
            int j = pinned.FindIndex(p => p.Element == c.Element);
            if (j < 0) return remove ? 0 : 0;
            if (remove) return j > from ? -CellWidth : 0; // close the gap it leaves behind
            if (from < to && j > from && j <= to) return -CellWidth;
            if (to < from && j >= to && j < from) return CellWidth;
            return 0;
        }, k);
    }

    void EndDrag()
    {
        var item = _dragItem;
        var cell = _dragCell;
        _dragging = false;
        _dragItem = null;
        _dragCell = null;
        if (item?.Entry == null || cell == null) return;
        Panel.SetZIndex(cell, 0);
        var lift = LiftOf(CellOf(cell)!);

        if (_dragRemove)
        {
            // Poof: shrink and fade where it was dropped, then unpin.
            var appear = (ScaleTransform)Part(lift, 1);
            Motion.To(appear, ScaleTransform.ScaleXProperty, 0.2, 200);
            Motion.To(appear, ScaleTransform.ScaleYProperty, 0.2, 200);
            Motion.To(lift, OpacityProperty, 0, 200, done: () =>
            {
                App.TogglePin(item.Entry);
                App.Dock.RefreshNow();
                // Still running? It stays as a running icon, so bring it back into view.
                Motion.To(appear, ScaleTransform.ScaleXProperty, 1, 0);
                Motion.To(appear, ScaleTransform.ScaleYProperty, 1, 0);
                Motion.To(lift, OpacityProperty, 1, 0);
                _flip.Clear();
                StartRendering();
            });
            _dragRemove = false;
            return;
        }

        // Reorder with a FLIP glide: remember where every icon is drawn now, apply the move,
        // then offset each icon back to its old spot and let the motion loop ease it home.
        var before = new Dictionary<DockItem, double>();
        foreach (var c in Cells().Where(c => c.Item != null))
            before[c.Item!] = BaseCenter(c.Element) + Spread(c.Element).X;
        if (_dragTo != _dragFrom)
        {
            item.SkipAppear = true;
            App.MovePin(item.Entry, _dragTo);
            App.Dock.RefreshNow();
            DockItems.UpdateLayout();
        }
        _flip.Clear();
        foreach (var c in Cells().Where(c => c.Item != null && before.ContainsKey(c.Item!)))
            _flip[c.Element] = before[c.Item!] - BaseCenter(c.Element);
        StartRendering();
    }

    // ───────────────────────── window previews ─────────────────────────

    PreviewWindow? _preview;
    readonly DispatcherTimer _previewShow = new() { Interval = TimeSpan.FromMilliseconds(550) };
    readonly DispatcherTimer _previewHide = new() { Interval = TimeSpan.FromMilliseconds(320) };
    DockItem? _hoverItem;
    FrameworkElement? _hoverCell;

    void InitPreviews()
    {
        _previewShow.Tick += (_, _) => { _previewShow.Stop(); ShowPreview(); };
        _previewHide.Tick += (_, _) =>
        {
            _previewHide.Stop();
            if (_preview?.IsMouseOver != true && !_hovering) HidePreview();
        };
    }

    (DockItem? Item, FrameworkElement? Cell) ItemAt(double x)
    {
        var gen = DockItems.ItemContainerGenerator;
        for (int i = 0; i < DockItems.Items.Count; i++)
        {
            if (gen.ContainerFromIndex(i) is not ContentPresenter cp || DockItems.Items[i] is not DockItem item || item.Leaving) continue;
            if (Math.Abs(x - BaseCenter(cp)) <= cp.ActualWidth / 2) return (item, cp);
        }
        return (null, null);
    }

    void UpdateHoverItem()
    {
        var (item, cell) = ItemAt(_mouseX);
        if (item == _hoverItem) return;
        _hoverItem = item;
        _hoverCell = cell;
        _previewShow.Stop();
        bool previewable = item is { IsRunning: true } && App.Settings.WindowPreviews;
        bool open = _preview?.IsVisible == true;
        if (!previewable)
        {
            if (open) _previewHide.Start();
            return;
        }
        _previewHide.Stop();
        // Once a preview is open, moving along the dock switches it almost instantly.
        _previewShow.Interval = TimeSpan.FromMilliseconds(open ? 70 : 550);
        _previewShow.Start();
    }

    void ShowPreview()
    {
        if (_hoverItem == null || _hoverCell == null || !_hovering || _dragging) return;
        if (_preview == null)
        {
            _preview = new PreviewWindow();
            _preview.PointerOver += inside =>
            {
                if (inside) { _previewHide.Stop(); _autoHide.Stop(); }
                else { _previewHide.Start(); if (App.Settings.AutoHide) _autoHide.Start(); }
            };
            _preview.Done += HidePreview;
        }
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toDip = source.CompositionTarget.TransformFromDevice;
        var centre = toDip.Transform(_hoverCell.PointToScreen(new Point(_hoverCell.ActualWidth / 2, 0)));
        var pillTop = toDip.Transform(Pill.PointToScreen(new Point(0, 0)));
        _preview.ShowFor(_hoverItem, new Point(centre.X, pillTop.Y - 22)); // clear of magnified icons
        StartRendering(); // fades the hover label out
    }

    void HidePreview()
    {
        _previewShow.Stop();
        _previewHide.Stop();
        _preview?.HidePreview();
        StartRendering();
    }

    // ───────────────────────── interaction ─────────────────────────

    static DockItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DockItem;

    void Item_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || ItemOf(sender) is not { } item) return;
        var grid = (Grid)sender;
        PressStart(item, grid, e.GetPosition(Strip));
        grid.CaptureMouse();
    }

    void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PressMove(e.GetPosition(Strip));
    }

    void Item_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var grid = (Grid)sender;
        grid.ReleaseMouseCapture();
        Release(item, grid, e.ChangedButton);
        e.Handled = true;
    }

    void Release(DockItem item, Grid grid, MouseButton button)
    {
        var lift = LiftOf(grid);
        bool wasDrag = _dragging;
        _pressItem = null;
        _pressGrid = null;
        Press(lift, false);
        HidePreview();
        if (wasDrag) { EndDrag(); return; }
        switch (button)
        {
            case MouseButton.Left: Activate(item, lift); break;
            case MouseButton.Middle when item.Entry != null: Bounce(lift); App.LaunchSafely(item.Entry); break;
            case MouseButton.Right: ShowItemMenu(item, grid); break;
        }
    }

    /// <summary>Win+1…9: the dock's n-th app, as if clicked.</summary>
    internal void ActivateDockItem(int index)
    {
        var cells = Cells().Where(c => c.Item != null).ToList();
        if (index < 0 || index >= cells.Count) return;
        RevealDock();
        Activate(cells[index].Item!, cells[index].Lift ?? cells[index].Element);
    }

    /// <summary>Win+T: bring a hidden dock up.</summary>
    internal void RevealForShortcut() => RevealDock();

    /// <summary>Where Win+X's menu opens from: the launcher button at the dock's left end.</summary>
    internal FrameworkElement LauncherAnchor => LauncherCell;

    /// <summary>Not running: launch. One window: focus/minimise. Several: cycle through them.</summary>
    void Activate(DockItem item, FrameworkElement lift)
    {
        var windows = item.Windows.Where(w => IsWindow(w.Handle)).ToList();
        if (windows.Count == 0)
        {
            if (item.Entry == null) return;
            Bounce(lift);
            App.LaunchSafely(item.Entry);
            return;
        }
        if (windows.Count == 1) { App.Tracker.Toggle(windows[0]); return; }
        var active = windows.FirstOrDefault(w => w.IsActive);
        var next = active != null
            ? windows[(windows.IndexOf(active) + 1) % windows.Count]
            : windows.OrderByDescending(w => w.LastActivated).First();
        WindowTracker.Activate(next);
    }

    ContextMenu NewMenu(UIElement target)
    {
        _menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Top, VerticalOffset = -8 };
        _menu.Closed += (_, _) => { if (App.Settings.AutoHide && !_hovering) _autoHide.Start(); };
        return _menu;
    }

    static MenuItem MenuEntry(string text, Action action)
    {
        var m = new MenuItem { Header = text };
        m.Click += (_, _) => action();
        return m;
    }

    static MenuItem MenuTitle(string text) => new()
    {
        Header = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 },
        IsHitTestVisible = false, Focusable = false,
    };

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    void ShowItemMenu(DockItem item, UIElement target)
    {
        var m = NewMenu(target);
        m.Items.Add(MenuTitle(item.Name));
        var windows = item.Windows.ToList();
        if (windows.Count > 0)
        {
            m.Items.Add(new Separator());
            foreach (var w in windows.Take(10)) m.Items.Add(MenuEntry(Trim(w.Title, 52), () => WindowTracker.Activate(w)));
        }
        m.Items.Add(new Separator());
        if (item.Entry is { } entry)
        {
            m.Items.Add(MenuEntry(windows.Count > 0 ? "New window" : "Open", () => App.LaunchSafely(entry)));
            m.Items.Add(MenuEntry(item.IsPinned ? "Remove from dock" : "Keep in dock", () => App.TogglePin(entry)));
        }
        if (windows.Count > 0)
            m.Items.Add(MenuEntry(windows.Count == 1 ? "Close window" : $"Close {windows.Count} windows",
                () => windows.ForEach(WindowTracker.Close)));
        m.IsOpen = true;
    }

    // ───────────────────────── the Bin ─────────────────────────

    string _binLabel = "Bin";
    int _binCount = -1;

    void StartBin()
    {
        if (!App.TestMode) Bin.Watch();
        Bin.Changed += UpdateBin;
        UpdateBin();
    }

    async void UpdateBin()
    {
        var (count, bytes) = await Task.Run(Bin.Totals);
        _binLabel = count == 0 ? "Bin" : $"Bin  ·  {count} item{(count == 1 ? "" : "s")}, {Bin.Size(bytes)}";
        BinGlyph.Glyph = count == 0 ? "trash" : "trash-full";
        BinBadge.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BinCount.Text = count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture);
        // Something just landed in the Bin (from anywhere: Files, Explorer, an app): it gulps.
        if (_binCount >= 0 && count > _binCount && !Motion.Reduced) Gulp();
        if (_binCount >= 0 && count != _binCount && count > 0) Motion.Pop(BinBadge, 0.6);
        _binCount = count;
    }

    void Gulp()
    {
        Press(BinLift, true);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        t.Tick += (_, _) => { t.Stop(); Press(BinLift, false); };
        t.Start();
    }

    void Bin_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) Press(BinLift, true);
        e.Handled = true;
    }

    void Bin_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Press(BinLift, false);
        if (e.ChangedButton == MouseButton.Left) { if (App.TestMode) App.TestLaunches.Add("bin"); else NorrskenApps.Launch("bin"); }
        else if (e.ChangedButton == MouseButton.Right) ShowBinMenu();
        e.Handled = true;
    }

    void ShowBinMenu()
    {
        var m = NewMenu(BinCell);
        m.Items.Add(MenuTitle(_binLabel.Replace("  ·  ", ": ")));
        m.Items.Add(new Separator());
        m.Items.Add(MenuEntry("Open Bin", () => NorrskenApps.Launch("bin")));
        int last = _binCount > 0 ? Bin.LastBatch().Count : 0;
        var undo = MenuEntry(last <= 1 ? "Put back the last deleted item" : $"Put back the last {last} deleted items", async () =>
        {
            var back = await Task.Run(Bin.UndoLastDelete);
            if (back.Count > 0) Bounce(BinLift);
        });
        undo.IsEnabled = last > 0;
        m.Items.Add(undo);
        var empty = MenuEntry("Empty Bin…", () => NorrskenApps.Launch("bin", "--empty"));
        empty.IsEnabled = _binCount > 0;
        m.Items.Add(empty);
        m.Items.Add(new Separator());
        m.Items.Add(MenuEntry("Remove Bin from the dock", () => { App.Settings.ShowBinInDock = false; App.Settings.Save(); ApplySettings(); }));
        m.IsOpen = true;
    }

    static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    void Bin_DragEnter(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Move : DragDropEffects.None;
        if (HasFiles(e))
        {
            BinGlyph.Glyph = "trash-open";
            var s = (ScaleTransform)BinTile.RenderTransform;
            Motion.To(s, ScaleTransform.ScaleXProperty, 1.16, 260, Motion.Spring);
            Motion.To(s, ScaleTransform.ScaleYProperty, 1.16, 260, Motion.Spring);
        }
        e.Handled = true;
    }

    void Bin_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasFiles(e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void Bin_DragLeave(object sender, DragEventArgs e) => BinRest();

    void BinRest()
    {
        BinGlyph.Glyph = _binCount > 0 ? "trash-full" : "trash";
        var s = (ScaleTransform)BinTile.RenderTransform;
        Motion.To(s, ScaleTransform.ScaleXProperty, 1, 320, Motion.Spring);
        Motion.To(s, ScaleTransform.ScaleYProperty, 1, 320, Motion.Spring);
    }

    async void Bin_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        BinRest();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        Gulp();
        if (App.TestMode) { App.TestLaunches.Add("bin-drop:" + paths.Length); return; }
        var hwnd = _hwnd;
        await Files.RunSta(() => Files.Recycle(hwnd, paths));
    }

    void Launcher_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) Press(LauncherLift, true);
    }

    void Launcher_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Press(LauncherLift, false);
        if (e.ChangedButton == MouseButton.Left) ToggleLauncher();
        else if (e.ChangedButton == MouseButton.Right)
        {
            var m = NewMenu(LauncherCell);
            m.Items.Add(MenuTitle("Norrsken"));
            m.Items.Add(new Separator());
            m.Items.Add(MenuEntry("Settings", OpenSettings));
            m.Items.Add(MenuEntry("Task Manager", () => Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true })));
            m.Items.Add(new Separator());
            m.Items.Add(MenuEntry("Quit Norrsken", App.Quit));
            m.IsOpen = true;
        }
        e.Handled = true;
    }

    // ───────────────────────── now playing ─────────────────────────

    string _mediaLabel = "Now playing";
    string _mediaSource = "";
    bool _mediaHooked;

    /// <summary>The dock's now-playing section: artwork, title, artist or channel, and controls.</summary>
    internal async Task UpdateMediaAsync()
    {
        if (!_mediaHooked) { _mediaHooked = true; SystemStatus.Media.Changed += () => _ = UpdateMediaAsync(); }
        var state = await SystemStatus.Media.GetAsync();
        ApplyMedia(state);
    }

    internal bool MediaShown => MediaCell.Visibility == Visibility.Visible;

    internal void ApplyMedia(MediaController.State? state)
    {
        bool show = state != null;
        bool was = MediaCell.Visibility == Visibility.Visible;
        if (state != null)
        {
            _mediaSource = state.SourceAppId;
            string source = MediaController.SourceName(state.SourceAppId);
            MediaTitle.Text = state.Title;
            MediaArtist.Text = state.Artist.Length > 0 ? state.Artist : source;
            _mediaLabel = state.Artist.Length > 0 ? $"{state.Title} · {state.Artist}" : state.Title;
            MediaPlayGlyph.Glyph = state.Playing ? "pause" : "play-solid";
            MediaPlayGlyph.Filled = !state.Playing;
            if (state.Art != null) { MediaArt.Background = new ImageBrush(state.Art) { Stretch = Stretch.UniformToFill }; MediaArtGlyph.Visibility = Visibility.Collapsed; }
            else { MediaArt.SetResourceReference(Border.BackgroundProperty, "Pressed"); MediaArtGlyph.Visibility = Visibility.Visible; }
        }
        if (state != null)
        {
            // A new track fades its artwork and title in; the equaliser dances only while playing.
            if (_mediaTrack != state.Title + state.Artist)
            {
                _mediaTrack = state.Title + state.Artist;
                if (was) { Motion.From(MediaArt, OpacityProperty, 0.2, 1, 380); Motion.From(MediaInfo, OpacityProperty, 0, 1, 320); }
            }
            SetEqualiser(state.Playing);
        }
        MediaCell.Visibility = Div3.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !was) { Motion.From(MediaCell, OpacityProperty, 0, 1, 260); }
        if (show != was) { UpdateLayout(); SetAppBarPos(); StartRendering(); }
    }

    string _mediaTrack = "";

    void SetEqualiser(bool playing)
    {
        MediaEq.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (bar, low, high, ms) in new[] { (Eq1, 2.0, 8.0, 420), (Eq2, 3.0, 9.0, 330), (Eq3, 2.0, 7.0, 510) })
        {
            if (!playing || Motion.Reduced) { bar.BeginAnimation(HeightProperty, null); continue; }
            bar.BeginAnimation(HeightProperty, new DoubleAnimation(low, high, TimeSpan.FromMilliseconds(ms))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut,
            });
        }
    }

    async void MediaPlay_Click(object sender, RoutedEventArgs e) { if (!App.TestMode) await SystemStatus.Media.PlayPauseAsync(); }
    async void MediaNext_Click(object sender, RoutedEventArgs e) { if (!App.TestMode) await SystemStatus.Media.NextAsync(); }
    async void MediaPrev_Click(object sender, RoutedEventArgs e) { if (!App.TestMode) await SystemStatus.Media.PreviousAsync(); }

    /// <summary>The artwork or title brings the playing app forward (browser tab, Spotify, Flickerbox…).</summary>
    void MediaInfo_Click(object sender, MouseButtonEventArgs e)
    {
        string id = _mediaSource;
        string exe = Path.GetFileNameWithoutExtension(id);
        var w = App.Tracker.Windows.FirstOrDefault(x => string.Equals(x.Aumid, id, StringComparison.OrdinalIgnoreCase))
                ?? App.Tracker.Windows.FirstOrDefault(x => x.ExePath != null && (string.Equals(Path.GetFileNameWithoutExtension(x.ExePath), exe, StringComparison.OrdinalIgnoreCase)
                                                                                 || id.Contains(Path.GetFileNameWithoutExtension(x.ExePath), StringComparison.OrdinalIgnoreCase)));
        if (w != null && !App.TestMode) WindowTracker.Activate(w);
        else ToggleQuickSettings();
        e.Handled = true;
    }

    bool _badgesHooked;

    /// <summary>A dot by the time when notifications wait; the focus countdown under it while a session runs.</summary>
    void UpdateBadges()
    {
        if (!_badgesHooked)
        {
            _badgesHooked = true;
            Notifications.Changed += () => Dispatcher.BeginInvoke(UpdateBadges);
            NotificationCentre.FocusChanged += () => Dispatcher.BeginInvoke(UpdateBadges);
        }
        NotifyDot.Visibility = Notifications.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        string? focus = NotificationCentre.FocusRemaining;
        FocusText.Visibility = focus != null ? Visibility.Visible : Visibility.Collapsed;
        FocusText.Text = focus ?? "";
    }

    static bool IsWithin(DependencyObject d, DependencyObject target)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x)) if (x == target) return true;
        return false;
    }

    void Clock_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // The time opens notifications and the calendar; the network and volume icons open quick settings.
            if (e.OriginalSource is DependencyObject d && (IsWithin(d, TimeText) || IsWithin(d, BatteryText))) NotificationCentre.Toggle();
            else ToggleQuickSettings();
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            var m = NewMenu(ClockCell);
            m.Items.Add(MenuTitle(DateLabel()));
            m.Items.Add(new Separator());
            m.Items.Add(MenuEntry("Notifications and calendar", NotificationCentre.Toggle));
            m.Items.Add(MenuEntry("Clock", () => OpenUri("ms-clock:")));
            m.Items.Add(MenuEntry("Sound settings", () => OpenUri("ms-settings:sound")));
            m.Items.Add(MenuEntry("Date and time settings", () => OpenUri("ms-settings:dateandtime")));
            m.IsOpen = true;
        }
        e.Handled = true;
    }

    public void ToggleQuickSettings()
    {
        _quick ??= new QuickSettingsWindow();
        if (_quick.IsVisible) { _quick.HidePanel(); return; }
        if ((DateTime.UtcNow - _quick.LastHidden).TotalMilliseconds < 250) return;
        // Anchor: just above the clock's top-right corner, in DIPs.
        var source = PresentationSource.FromVisual(this);
        var corner = ClockCell.PointToScreen(new Point(ClockCell.ActualWidth + 12, 0));
        var hostTop = DockHost.PointToScreen(new Point(0, 0));
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var c = toDip.Transform(corner);
        // Above the dock's resting top edge (not the clock, and not wherever a reveal animation has it now),
        // so the panel can never sit on the dock.
        double top = toDip.Transform(hostTop).Y - DockShift.Y;
        if (_dockHidden) RevealDock();
        _quick.ShowAbove(new Point(c.X, top));
    }

    void UpdateStatusGlyphs()
    {
        NetGlyph.Glyph = SystemStatus.Network().Glyph;
        VolGlyph.Glyph = SystemStatus.VolumeGlyph;
        VolGlyph.Visibility = SystemStatus.HasAudio ? Visibility.Visible : Visibility.Collapsed;
    }

    static void OpenUri(params string[] uris)
    {
        if (App.TestMode) { App.TestLaunches.Add(uris[0]); return; }
        foreach (var uri in uris)
        {
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return; }
            catch (Exception ex) { Log.Write($"{uri} failed: {ex.Message}"); }
        }
    }

    public void ToggleLauncher(bool forceOpen = false)
    {
        _launcher ??= new LauncherWindow();
        if (_launcher.IsVisible)
        {
            if (forceOpen) _launcher.Activate(); else _launcher.HideLauncher();
            return;
        }
        // The click that deactivated (and hid) the launcher shouldn't immediately reopen it.
        if ((DateTime.UtcNow - _launcher.LastHidden).TotalMilliseconds < 250) return;
        SummonForLauncher();
        _launcher.ShowCentered();
    }

    bool _summoned, _launcherHooked;

    /// <summary>
    /// The Win key brings the dock up with the launcher: out of auto-hide, and over a full-screen
    /// window too. It goes back to how it was when the launcher closes.
    /// </summary>
    void SummonForLauncher()
    {
        if (!_launcherHooked && _launcher != null) { _launcher.Hiding += EndSummon; _launcherHooked = true; }
        if (_fullscreen)
        {
            _summoned = true;
            Topmost = true;
            DockHost.IsHitTestVisible = true;
            Motion.To(DockShift, TranslateTransform.YProperty, 0, 380, Motion.SoftSpring);
            Motion.To(DockHost, OpacityProperty, 1, 180);
            return;
        }
        if (_dockHidden)
        {
            RevealDock();
            _autoHide.Stop();
            if (!_hovering) _autoHide.Start(); // hides again once the launcher has closed
        }
    }

    void EndSummon()
    {
        if (!_summoned) return;
        _summoned = false;
        if (!_fullscreen) return;
        Topmost = false;
        DockHost.IsHitTestVisible = false;
        Motion.To(DockHost, OpacityProperty, 0, 160);
        if (_dockHidden) Motion.To(DockShift, TranslateTransform.YProperty, HiddenOffset, 240, Motion.InOut);
    }

    public void OpenSettings() => OpenSettings(null);

    public void OpenSettings(string? page)
    {
        if (_settings is not { IsLoaded: true })
        {
            _settings = new SettingsWindow(this);
            _settings.Show();
        }
        if (page != null) _settings.ShowPage(page);
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    // ───────────────────────── test hooks (self-test and isolated UI test) ─────────────────────────

    internal LauncherWindow? Launcher => _launcher;

    /// <summary>The visible dock's top edge in screen DIPs (null while hidden or not laid out).</summary>
    internal double? DockHostTop()
    {
        if (_dockHidden || DockHost.ActualHeight < 1) return null;
        var p = DockHost.TranslatePoint(new Point(0, 0), this);
        return Top + p.Y - DockShift.Y;
    }
    internal QuickSettingsWindow? Quick => _quick;
    internal SettingsWindow? SettingsWin => _settings;
    internal PreviewWindow? Preview => _preview;
    internal ContextMenu? Menu => _menu;
    internal bool IsDockHidden => _dockHidden;
    internal double DockOffsetY => DockShift.Y;
    internal double DockOpacity => DockHost.Opacity;
    internal bool IsAnimating => _rendering;
    internal bool RevealZoneVisible => RevealZone.Visibility == Visibility.Visible;
    internal string HoverLabelText => HoverText.Text;
    internal double HoverLabelOpacity => _labelOpacity;

    List<ContentPresenter> ItemContainers() =>
        Enumerable.Range(0, DockItems.Items.Count)
            .Select(i => DockItems.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter)
            .Where(c => c != null).Select(c => c!).ToList();

    internal double CenterOf(int index) => BaseCenter(ItemContainers()[index]);
    internal double ScaleOf(int index) => _scale.GetValueOrDefault(ItemContainers()[index], 1);
    internal double VisualOffsetOf(int index) => Spread(ItemContainers()[index]).X;
    internal Border DotOfItem(int index) => DotOf(CellOf(ItemContainers()[index])!);
    internal double BounceOf(int index) => ((TranslateTransform)Part(LiftOf(CellOf(ItemContainers()[index])!), 2)).Y;

    internal void TestPointer(double stripX) => PointerAt(stripX);
    internal void TestLeave() => PointerLeft();
    internal void TestReveal() => RevealZone_MouseEnter(RevealZone, null!);

    internal void TestClick(int index, MouseButton button = MouseButton.Left)
    {
        var cp = ItemContainers()[index];
        Release((DockItem)cp.Content, CellOf(cp)!, button);
    }

    internal void TestDragStart(int index)
    {
        var cp = ItemContainers()[index];
        PressStart((DockItem)cp.Content, CellOf(cp)!, new Point(BaseCenter(cp), 30));
    }

    internal void TestDragTo(double dx, double dy) => PressMove(new Point(_pressPoint.X + dx, _pressPoint.Y + dy));

    internal void TestDragEnd()
    {
        if (_pressItem is { } item && _pressGrid is { } grid) Release(item, grid, MouseButton.Left);
    }

    /// <summary>Self-test: every running item shows its dot, every idle one doesn't.</summary>
    internal bool RunningDotsShown(out string detail)
    {
        int ok = 0, total = 0, seen = 0;
        for (int i = 0; i < DockItems.Items.Count; i++)
        {
            if (DockItems.Items[i] is not DockItem item) continue;
            total++;
            if (CellOf(DockItems.ItemContainerGenerator.ContainerFromIndex(i)) is not { } cell) continue;
            seen++;
            if ((DotOf(cell).Opacity > 0.3) == item.IsRunning) ok++;
        }
        detail = $"running dots correct on {ok}/{total} items ({seen} containers)";
        return ok == total;
    }

    /// <summary>Self-test: average cost of one magnification frame while the pointer sweeps the dock.</summary>
    internal double MeasureFrameMs(int frames)
    {
        bool forced = Motion.ForceInstant;
        Motion.ForceInstant = false;
        _hovering = true;
        double width = Strip.ActualWidth;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
        {
            _mouseX = width * (i % 120) / 120.0;
            Step(1 / 60.0);
        }
        sw.Stop();
        _hovering = false;
        Motion.ForceInstant = forced;
        return sw.Elapsed.TotalMilliseconds / frames;
    }

    /// <summary>Self-test: settle the dock as if the pointer rested over item <paramref name="index"/>.</summary>
    internal void PreviewHover(int index)
    {
        var target = Cells().Where(c => c.Element is ContentPresenter).ElementAtOrDefault(Math.Max(0, index));
        if (target.Element == null) return;
        _hovering = index >= 0; // -1 settles back to rest after a measurement sweep
        _mouseX = BaseCenter(target.Element);
        bool forced = Motion.ForceInstant;
        Motion.ForceInstant = false; // magnification is the thing being previewed
        for (int i = 0; i < 120; i++) Step(1 / 60.0);
        Motion.ForceInstant = forced;
    }
}
