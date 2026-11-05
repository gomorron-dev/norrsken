using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Switches that open already on start in place. Their knob slides by a trigger animation, which also ran
/// whenever a page appeared, so every "on" switch visibly slid across as Settings opened.
/// </summary>
public static class Switches
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(CheckBox), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => Settle((CheckBox)s)));

    /// <summary>Puts every switch under <paramref name="root"/> at rest (offscreen renders never raise Loaded).</summary>
    public static void SettleAll(DependencyObject root)
    {
        if (root is CheckBox c) Settle(c);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) SettleAll(VisualTreeHelper.GetChild(root, i));
    }

    static void Settle(CheckBox box)
    {
        if (box.Style != Application.Current.TryFindResource("Toggle")) return; // only Norrsken switches
        if (box.Template?.FindName("Thumb", box) is not FrameworkElement { RenderTransform: TranslateTransform shift }) return;
        if (box.IsChecked != true) return;
        shift.BeginAnimation(TranslateTransform.XProperty, null); // drop the on-load slide…
        shift.X = 18;                                               // …and rest at the end; clicks animate from here
    }
}