using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Norrsken.Shell.Ui;

public partial class WindowPebbles : UserControl
{
    Window? _window;

    public WindowPebbles()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _window = Window.GetWindow(this);
            if (_window == null) return;
            if (_window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) MaxButton.Visibility = Visibility.Collapsed;
            _window.StateChanged += (_, _) => UpdateMaxGlyph();
            UpdateMaxGlyph();
        };
    }

    void UpdateMaxGlyph()
    {
        bool max = _window?.WindowState == WindowState.Maximized;
        // Restore: two inward ticks; maximise: plus.
        MaxGlyph.Data = Geometry.Parse(max ? "M0,0 L2.5,2.5 M6,6 L3.5,3.5" : "M0,3 L6,3 M3,0 L3,6");
        MaxButton.ToolTip = max ? "Restore" : "Maximise";
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, max ? "Restore" : "Maximise");
    }

    void Min_Click(object sender, RoutedEventArgs e) { if (_window != null) AppChrome.Minimise(_window); }

    void Max_Click(object sender, RoutedEventArgs e)
    {
        if (_window != null)
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void Close_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
