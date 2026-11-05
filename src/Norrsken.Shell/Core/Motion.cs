using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Norrsken.Shell.Core;

/// <summary>
/// One place for Norrsken's motion language. Every animation goes through here so
/// "Reduce motion" (and Windows' own animation setting) can turn them into instant changes.
/// </summary>
public static class Motion
{
    /// <summary>Self-test renders static frames, so it asks for end states immediately.</summary>
    public static bool ForceInstant { get; set; }

    public static bool Reduced => ForceInstant || App.Settings.ReduceMotion || !SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction InOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    public static readonly IEasingFunction SoftSpring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 });

    static IEasingFunction Frozen(EasingFunctionBase e) { e.Freeze(); return e; }

    public static void To(IAnimatable target, DependencyProperty dp, double to, int ms,
        IEasingFunction? ease = null, Action? done = null) => Run(target, dp, null, to, ms, ease, done);

    public static void From(IAnimatable target, DependencyProperty dp, double from, double to, int ms,
        IEasingFunction? ease = null, Action? done = null) => Run(target, dp, from, to, ms, ease, done);

    /// <summary>
    /// Items cascade in one after another: each fades up from a few pixels below, <paramref name="stepMs"/> apart.
    /// Only the first <paramref name="max"/> animate; the rest simply appear (long lists stay snappy).
    /// </summary>
    public static void Stagger(IEnumerable<UIElement> items, int stepMs = 18, int max = 24, double rise = 10, int ms = 320, int startMs = 0)
    {
        int i = 0;
        foreach (var el in items)
        {
            if (i >= max || Reduced) break;
            var delay = TimeSpan.FromMilliseconds(startMs + i * stepMs);
            var shift = el.RenderTransform as TranslateTransform;
            if (shift == null && (el.RenderTransform == null || el.RenderTransform == Transform.Identity))
                el.RenderTransform = shift = new TranslateTransform();
            el.BeginAnimation(UIElement.OpacityProperty, Delayed(0, 1, delay, ms * 2 / 3));
            shift?.BeginAnimation(TranslateTransform.YProperty, Delayed(rise, 0, delay, ms));
            i++;
        }
    }

    /// <summary>Hidden until its delay, then eased in (keyframes, so it's hidden from the very first frame).</summary>
    static DoubleAnimationUsingKeyFrames Delayed(double from, double to, TimeSpan delay, int ms)
    {
        var a = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        a.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(delay)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(delay + TimeSpan.FromMilliseconds(ms)), Out));
        return a;
    }

    /// <summary>A quick spring "pop" (for something that just appeared or changed).</summary>
    public static void Pop(FrameworkElement el, double from = 0.9)
    {
        if (Reduced) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var s = el.RenderTransform as ScaleTransform ?? new ScaleTransform();
        el.RenderTransform = s;
        From(s, ScaleTransform.ScaleXProperty, from, 1, 380, Spring);
        From(s, ScaleTransform.ScaleYProperty, from, 1, 380, Spring);
    }

    static void Run(IAnimatable target, DependencyProperty dp, double? from, double to, int ms,
        IEasingFunction? ease, Action? done)
    {
        if (Reduced || ms <= 0)
        {
            target.BeginAnimation(dp, null);
            ((DependencyObject)target).SetValue(dp, to);
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = ease ?? Out };
        if (from is double f) a.From = f;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(dp, a);
    }
}
