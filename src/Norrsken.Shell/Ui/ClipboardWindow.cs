using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>Win+V: pick something you copied earlier; it's pasted straight into the app you were in.</summary>
public sealed class ClipboardWindow : CardWindow
{
    static ClipboardWindow? _open;
    readonly StackPanel _list = new();
    readonly IntPtr _target;
    int _selected;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    public static void Toggle()
    {
        if (_open != null) { _open.Dismiss(); return; }
        _open = new ClipboardWindow(GetForegroundWindow());
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    internal static ClipboardWindow? Open => _open;
    internal int Count => ClipboardHistory.Items.Count;

    ClipboardWindow(IntPtr target) : base(420)
    {
        _target = target;
        Title = "Clipboard";
        var root = new DockPanel { Margin = new Thickness(16, 14, 16, 12) };
        var head = new DockPanel { Margin = new Thickness(4, 0, 0, 10) };
        var clear = new Button { Style = (Style)FindResource("LinkButton"), Content = new TextBlock { Text = "Clear", FontSize = 12 } };
        clear.Click += (_, _) => { ClipboardHistory.Clear(); Render(); };
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        head.Children.Add(Text("Clipboard", 15, bold: true));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var hint = Text("Enter pastes  ·  P pins  ·  Del removes", 11, dim: true);
        hint.Margin = new Thickness(4, 8, 0, 0);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false });
        Card.Child = root;
        PreviewKeyDown += Keys;
        Loaded += (_, _) => Render();
    }

    protected override void Place()
    {
        var p = ScreenGrab.Cursor();
        var a = SystemParameters.WorkArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double x = p.X / dpi.DpiScaleX, y = p.Y / dpi.DpiScaleY;
        Left = Math.Clamp(x - ActualWidth / 2, a.Left, a.Right - ActualWidth);
        Top = Math.Clamp(y - 60, a.Top, a.Bottom - ActualHeight);
    }

    void Render()
    {
        _list.Children.Clear();
        var items = ClipboardHistory.Items;
        if (items.Count == 0)
        {
            var t = Text("Things you copy show up here.", 12.5, dim: true);
            t.Margin = new Thickness(4, 16, 0, 20);
            _list.Children.Add(t);
            return;
        }
        _selected = Math.Clamp(_selected, 0, items.Count - 1);
        for (int i = 0; i < items.Count; i++)
        {
            var e = items[i];
            UIElement content = e.Image != null
                ? new Image { Source = e.Image, MaxHeight = 110, MaxWidth = 360, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left }
                : new TextBlock { Text = e.Preview, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, MaxHeight = 58, TextTrimming = TextTrimming.CharacterEllipsis };
            if (content is TextBlock tb) tb.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var row = new DockPanel();
            if (e.Pinned) { var pin = Text("Pinned", 10.5, dim: true); DockPanel.SetDock(pin, Dock.Right); pin.Margin = new Thickness(8, 0, 0, 0); row.Children.Add(pin); }
            row.Children.Add(content);
            var card = new Border { CornerRadius = Theme.R(12), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 4), Child = row, Cursor = Cursors.Hand, BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BackgroundProperty, i == _selected ? "Selection" : "SurfaceAlt");
            card.SetResourceReference(Border.BorderBrushProperty, i == _selected ? "TextDim" : "Border");
            int index = i;
            card.MouseLeftButtonUp += (_, _) => Paste(index);
            _list.Children.Add(card);
        }
    }

    void Keys(object sender, KeyEventArgs e)
    {
        var items = ClipboardHistory.Items;
        switch (e.Key)
        {
            case Key.Down: _selected = Math.Min(items.Count - 1, _selected + 1); Render(); break;
            case Key.Up: _selected = Math.Max(0, _selected - 1); Render(); break;
            case Key.Enter: Paste(_selected); break;
            case Key.P when items.Count > 0: items[_selected].Pinned = !items[_selected].Pinned; Render(); break;
            case Key.Delete when items.Count > 0: items.RemoveAt(_selected); Render(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Paste(int index)
    {
        if (index < 0 || index >= ClipboardHistory.Items.Count) return;
        var entry = ClipboardHistory.Items[index];
        Dismiss();
        if (App.TestMode) { App.TestLaunches.Add("paste:" + entry.Preview); return; }
        ClipboardHistory.Use(entry);
        // Back to the app you were in, then paste (the Ctrl+V you'd have pressed yourself).
        Dispatcher.BeginInvoke(async () =>
        {
            if (_target != IntPtr.Zero) SetForegroundWindow(_target);
            await Task.Delay(120);
            keybd_event(0x11, 0, 0, IntPtr.Zero);
            keybd_event(0x56, 0, 0, IntPtr.Zero);
            keybd_event(0x56, 0, 2, IntPtr.Zero);
            keybd_event(0x11, 0, 2, IntPtr.Zero);
        });
    }
}
