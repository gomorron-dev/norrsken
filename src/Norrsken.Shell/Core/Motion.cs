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

    // The motion style (Settings › Personalisation › Motion) picks the curves; every animation asks for them by role.
    static IEasingFunction _out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    static IEasingFunction _inOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    static IEasingFunction _spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    static IEasingFunction _softSpring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 });
    static string _style = "";

    public static IEasingFunction Out { get { Sync(); return _out; } }
    public static IEasingFunction InOut { get { Sync(); return _inOut; } }
    public static IEasingFunction Spring { get { Sync(); return _spring; } }
    public static IEasingFunction SoftSpring { get { Sync(); return _softSpring; } }

    /// <summary>Duration multiplier from Settings (0.6 brisk … 1.5 relaxed); "snappy" shortens a little more.</summary>
    public static double Scale => Math.Clamp(App.Settings.AnimationScale, 0.3, 3) * (App.Settings.MotionStyle == "snappy" ? 0.8 : 1);

    public static int Ms(int ms) => (int)Math.Round(ms * Scale);

    static void Sync()
    {
        string style = App.Settings.MotionStyle ?? "natural";
        if (style == _style) return;
        _style = style;
        (_out, _inOut, _spring, _softSpring) = style switch
        {
            // Bouncier: springs overshoot more, so things land with a little wobble.
            "springy" => (Frozen(new CubicEase { EasingMode = EasingMode.EaseOut }), Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut }),
                          Frozen(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 }), Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 })),
            // No overshoot anywhere: long, soft decelerations.
            "smooth" => (Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut }), Frozen(new SineEase { EasingMode = EasingMode.EaseInOut }),
                         Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut }), Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut })),
            // Crisp: fast starts, a hint of spring.
            "snappy" => (Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut }), Frozen(new QuadraticEase { EasingMode = EasingMode.EaseInOut }),
                         Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 }), Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.1 })),
            _ => (Frozen(new CubicEase { EasingMode = EasingMode.EaseOut }), Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut }),
                  Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }), Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 })),
        };
    }

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
            var delay = TimeSpan.FromMilliseconds(Ms(startMs + i * stepMs));
            var shift = el.RenderTransform as TranslateTransform;
            if (shift == null && (el.RenderTransform == null || el.RenderTransform == Transform.Identity))
                el.RenderTransform = shift = new TranslateTransform();
            el.BeginAnimation(UIElement.OpacityProperty, Delayed(0, 1, delay, Ms(ms * 2 / 3)));
            shift?.BeginAnimation(TranslateTransform.YProperty, Delayed(rise, 0, delay, Ms(ms)));
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
        var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(Ms(ms)), EasingFunction = ease ?? Out };
        if (from is double f) a.From = f;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(dp, a);
    }
}
