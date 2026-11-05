using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Norrsken.Shell.Core;
using Norrsken.Shell.Interop;
using static Norrsken.Shell.Interop.Native;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Norrsken title bars on other apps: a bar exactly over Windows' own title bar with the window's
/// title centred and Norrsken's pebble buttons on the right. Drag it to move the window,
/// double-click to maximise, right-click for the window menu. Apps that draw their own title bar
/// (Chrome, VS Code, Discord…) keep theirs; full-screen windows are never touched.
/// </summary>
public static class TitleBars
{
    [StructLayout(LayoutKind.Sequential)]
    struct TITLEBARINFO { public int cbSize; public RECT rcTitleBar; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public uint[] rgstate; }
    [DllImport("user32.dll")] static extern bool GetTitleBarInfo(IntPtr h, ref TITLEBARINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetRect(IntPtr h, int attr, out RECT r, int size);

    static readonly Dictionary<IntPtr, TitleBarOverlay> Overlays = new();
    static WinEventProc? _proc;
    static readonly List<IntPtr> Hooks = new();
    static readonly uint Own = (uint)Environment.ProcessId;

    public static int Count => Overlays.Count;

    public static void Start()
    {
        if (App.TestMode || Hooks.Count > 0 || !App.Settings.NorrskenTitleBars) return;
        _proc = OnEvent;
        foreach (var (min, max) in new (uint, uint)[] { (0x0003, 0x0003), (0x0016, 0x0017), (0x8001, 0x8003), (0x800B, 0x800C) })
            Hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS));
        EnumWindows((h, _) => { Consider(h); return true; }, IntPtr.Zero);
    }

    public static void Stop()
    {
        foreach (var h in Hooks) UnhookWinEvent(h);
        Hooks.Clear();
        foreach (var o in Overlays.Values.ToList()) o.Close();
        Overlays.Clear();
    }

    public static void Refresh()
    {
        Stop();
        Start();
    }

    static void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int child, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        switch (evt)
        {
            case 0x8001: Remove(hwnd); break;                                  // destroyed
            case 0x8003: if (Overlays.TryGetValue(hwnd, out var hid)) hid.Hide(); break; // hidden
            case 0x0016: if (Overlays.TryGetValue(hwnd, out var min)) min.Hide(); break; // minimised
            case 0x800C: if (Overlays.TryGetValue(hwnd, out var named)) named.UpdateTitle(); break;
            case 0x0003: foreach (var o in Overlays.Values) o.SetActive(o.Target == hwnd); Consider(hwnd); break;
            default: Consider(hwnd); break;                                    // shown, restored, moved or resized
        }
    }

    static bool Eligible(IntPtr h, out RECT bar)
    {
        bar = default;
        if (!IsWindowVisible(h) || IsIconic(h) || GetAncestor(h, GA_ROOT) != h) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == Own) return false;
        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64(), ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((style & WS_CAPTION) != WS_CAPTION || (style & 0x80000 /* WS_SYSMENU */) == 0 || (ex & WS_EX_TOOLWINDOW) != 0) return false;
        if (FullscreenWatcher.IsFullscreenWindow(h, Own)) return false;
        var info = new TITLEBARINFO { cbSize = Marshal.SizeOf<TITLEBARINFO>(), rgstate = new uint[6] };
        if (!GetTitleBarInfo(h, ref info) || (info.rgstate[0] & (0x8000 | 0x10000)) != 0) return false; // invisible or off-screen: the app draws its own
        int height = info.rcTitleBar.Bottom - info.rcTitleBar.Top;
        if (height < 16 || height > 72 || info.rcTitleBar.Right - info.rcTitleBar.Left < 120) return false;
        // Browsers, Electron apps, Explorer, Office and Store apps paint tabs and their own buttons inside the
        // caption area (their client area reaches up into it). Decorating those covers their UI, so leave them be.
        var cls = new System.Text.StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        string c = cls.ToString();
        if (c.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || c is "MozillaWindowClass" or "ApplicationFrameWindow" or "CabinetWClass") return false;
        var origin = new POINT();
        if (ClientToScreen(h, ref origin) && origin.Y < info.rcTitleBar.Bottom - 2) return false;
        bar = info.rcTitleBar;
        // Windows leaves the window's icon out of that area; cover the full width of the visible frame.
        if (DwmGetRect(h, 9, out var frame, Marshal.SizeOf<RECT>()) == 0) { bar.Left = frame.Left + 1; bar.Right = frame.Right - 1; bar.Top = Math.Max(bar.Top, frame.Top + 1); }
        return true;
    }

    static void Consider(IntPtr h)
    {
        bool ok = Eligible(h, out var bar);
        if (!ok)
        {
            if (Overlays.TryGetValue(h, out var o)) { if (!IsWindow(h)) Remove(h); else o.Hide(); }
            return;
        }
        if (!Overlays.TryGetValue(h, out var overlay))
        {
            overlay = new TitleBarOverlay(h);
            Overlays[h] = overlay;
            overlay.Closed += (_, _) => Overlays.Remove(h);
        }
        overlay.Place(bar);
    }

    static void Remove(IntPtr h)
    {
        if (Overlays.Remove(h, out var o)) o.Close();
    }
}

/// <summary>One Norrsken title bar, owned by (and so always just above) the window it decorates.</summary>
internal sealed class TitleBarOverlay : Window
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetSystemMenu(IntPtr h, bool revert);
    [DllImport("user32.dll")] static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    const uint WM_SYSCOMMAND = 0x0112, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120, SC_CLOSE = 0xF060;

    public IntPtr Target { get; }
    readonly TextBlock _title = new() { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(90, 0, 90, 0) };
    IntPtr _hwnd;
    POINT _dragFrom;
    RECT _dragStartRect;
    bool _dragging;

    public TitleBarOverlay(IntPtr target)
    {
        Target = target;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        SetResourceReference(BackgroundProperty, "SurfaceSolid");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var grid = new Grid { Background = Brushes.Transparent };
        grid.Children.Add(_title);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        buttons.Children.Add(Pebble("M0,0 L6,0", "Minimise", () => Post(SC_MINIMIZE)));
        buttons.Children.Add(Pebble("M0,3 L6,3 M3,0 L3,6", "Maximise", () => Post(IsZoomed(Target) ? SC_RESTORE : SC_MAXIMIZE)));
        buttons.Children.Add(Pebble("M0,0 L6,6 M6,0 L0,6", "Close", () => Post(SC_CLOSE)));
        grid.Children.Add(buttons);
        _buttons = buttons;
        // A hairline where the bar meets the app, so the two read as one calm frame.
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.7, IsHitTestVisible = false };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        grid.Children.Add(line);
        Content = grid;
        Loaded += (_, _) => { Motion.From(grid, OpacityProperty, 0, 1, 220); Motion.From(_title, OpacityProperty, 0, _title.Opacity, 320); };
        grid.MouseLeftButtonDown += Down;
        grid.MouseMove += Move;
        grid.MouseLeftButtonUp += (_, _) => { _dragging = false; grid.ReleaseMouseCapture(); };
        grid.MouseRightButtonUp += (_, e) => { SystemMenu(); e.Handled = true; };
        UpdateTitle();
    }

    Button Pebble(string glyph, string tip, Action click)
    {
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(glyph), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "SurfaceSolid");
        var b = new Button { Style = (Style)FindResource("Pebble"), ToolTip = tip, Content = path };
        b.Click += (_, _) => click();
        return b;
    }

    void Post(uint cmd) => PostMessageW(Target, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        SetWindowLongPtr(_hwnd, -8 /* GWLP_HWNDPARENT */, Target); // owned: always just above its window, minimised with it
    }

    public void Place(RECT bar)
    {
        if (!IsVisible) Show();
        SetWindowPos(_hwnd, IntPtr.Zero, bar.Left, bar.Top, bar.Right - bar.Left, bar.Bottom - bar.Top, 0x0004 | 0x0010 | 0x0040); // no z-order, no activate, show
        SetActive(GetForegroundWindow() == Target);
    }

    public void UpdateTitle() => _title.Text = GetWindowText(Target);

    StackPanel? _buttons;
    bool? _active;

    /// <summary>The window you're in has a bright title and buttons; others quietly fade back.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        Motion.To(_title, OpacityProperty, active ? 1 : 0.5, 220);
        if (_buttons != null) Motion.To(_buttons, OpacityProperty, active ? 1 : 0.45, 220);
    }

    void Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Post(IsZoomed(Target) ? SC_RESTORE : SC_MAXIMIZE); return; }
        Native.ForceForeground(Target);
        GetCursorPos(out _dragFrom);
        GetWindowRect(Target, out _dragStartRect);
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
    }

    void Move(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        GetCursorPos(out var now);
        int dx = now.X - _dragFrom.X, dy = now.Y - _dragFrom.Y;
        if (IsZoomed(Target))
        {
            if (Math.Abs(dx) + Math.Abs(dy) < 6) return;
            // Dragging a maximised window down restores it under the pointer, like Windows does.
            ShowWindow(Target, SW_RESTORE);
            GetWindowRect(Target, out var r);
            int w = r.Right - r.Left;
            _dragStartRect = new RECT { Left = now.X - w / 2, Top = now.Y - 12, Right = now.X + w / 2, Bottom = now.Y - 12 + (r.Bottom - r.Top) };
            _dragFrom = now;
            dx = dy = 0;
        }
        SetWindowPos(Target, IntPtr.Zero, _dragStartRect.Left + dx, _dragStartRect.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010); // move only
    }

    void SystemMenu()
    {
        IntPtr menu = GetSystemMenu(Target, false);
        if (menu == IntPtr.Zero) return;
        GetCursorPos(out var p);
        int cmd = TrackPopupMenu(menu, 0x0100 /* TPM_RETURNCMD */, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        if (cmd != 0) PostMessageW(Target, WM_SYSCOMMAND, (IntPtr)cmd, IntPtr.Zero);
    }
}
