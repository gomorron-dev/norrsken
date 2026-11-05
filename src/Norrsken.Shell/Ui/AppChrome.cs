using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Shared look and motion for Norrsken's app windows (Files, Notes, Settings, Terminal, Task Manager):
/// rounded DWM corners; opening rises out of a soft depth on a spring; minimising falls toward
/// the dock and restoring springs back; maximising settles the content; closing sinks away.
/// </summary>
public static class AppChrome
{
    sealed class Parts { public required ScaleTransform Scale; public required TranslateTransform Shift; public required FrameworkElement Root; public bool Minimising; }
    static readonly Dictionary<Window, Parts> Windows = new();

    public static void Attach(Window w, FrameworkElement root)
    {
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        root.RenderTransformOrigin = new Point(0.5, 0.55);
        root.RenderTransform = new TransformGroup { Children = { scale, shift } };
        var parts = new Parts { Scale = scale, Shift = shift, Root = root };
        Windows[w] = parts;
        w.Closed += (_, _) => Windows.Remove(w);
        // A Norrsken app opens in front, with focus (Windows would often leave it behind the window you were in).
        if (!App.TestMode) w.ContentRendered += (_, _) => Interop.Native.ForceForeground(new WindowInteropHelper(w).Handle);

        w.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            Interop.Native.RoundCorners(hwnd);
            // Dark title area while the window is being created (no white flash).
            int dark = Theme.CurrentIsLight ? 0 : 1;
            Interop.Native.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, 4);
        };
        w.StateChanged += (_, _) =>
        {
            root.Margin = w.WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            if (w.WindowState == WindowState.Minimized) return;
            if (parts.Minimising) { parts.Minimising = false; Rise(parts, fromMinimise: true); }
            else Settle(parts);
        };
        w.Loaded += (_, _) => Rise(parts, fromMinimise: false);

        bool closing = false;
        w.Closing += (_, e) =>
        {
            if (e.Cancel || closing || Motion.Reduced || App.TestMode) return;
            e.Cancel = true;
            closing = true;
            Motion.To(scale, ScaleTransform.ScaleXProperty, 0.95, 170, Motion.InOut);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 0.95, 170, Motion.InOut);
            Motion.To(shift, TranslateTransform.YProperty, 10, 170, Motion.InOut);
            Motion.To(root, UIElement.OpacityProperty, 0, 160, Motion.InOut, () => w.Dispatcher.BeginInvoke(w.Close));
        };
    }

    /// <summary>Opening (or coming back from the dock): fade in, rise and spring to full size.</summary>
    static void Rise(Parts p, bool fromMinimise)
    {
        double from = fromMinimise ? 0.82 : 0.94, drop = fromMinimise ? 60 : 16;
        Motion.From(p.Root, UIElement.OpacityProperty, 0, 1, fromMinimise ? 200 : 220);
        Motion.From(p.Scale, ScaleTransform.ScaleXProperty, from, 1, 420, Motion.SoftSpring);
        Motion.From(p.Scale, ScaleTransform.ScaleYProperty, from, 1, 420, Motion.SoftSpring);
        Motion.From(p.Shift, TranslateTransform.YProperty, drop, 0, 420, Motion.Out);
    }

    /// <summary>Maximise/restore: the content eases into its new size instead of jumping.</summary>
    static void Settle(Parts p)
    {
        Motion.From(p.Root, UIElement.OpacityProperty, 0.55, 1, 200);
        Motion.From(p.Scale, ScaleTransform.ScaleXProperty, 0.985, 1, 260, Motion.Out);
        Motion.From(p.Scale, ScaleTransform.ScaleYProperty, 0.985, 1, 260, Motion.Out);
    }

    /// <summary>The minimise pebble: the window falls toward the dock, then minimises.</summary>
    public static void Minimise(Window w)
    {
        if (!Windows.TryGetValue(w, out var p) || Motion.Reduced) { w.WindowState = WindowState.Minimized; return; }
        p.Minimising = true;
        Motion.To(p.Scale, ScaleTransform.ScaleXProperty, 0.72, 220, Motion.InOut);
        Motion.To(p.Scale, ScaleTransform.ScaleYProperty, 0.72, 220, Motion.InOut);
        Motion.To(p.Shift, TranslateTransform.YProperty, w.ActualHeight * 0.45, 220, Motion.InOut);
        Motion.To(p.Root, UIElement.OpacityProperty, 0, 200, Motion.InOut, () =>
        {
            w.WindowState = WindowState.Minimized;
            // Reset quietly so the window is whole again the moment Windows shows it.
            p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            p.Shift.BeginAnimation(TranslateTransform.YProperty, null);
            p.Root.BeginAnimation(UIElement.OpacityProperty, null);
        });
    }
}
