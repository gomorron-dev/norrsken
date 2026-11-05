using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Norrsken.Shell.Core;

namespace Norrsken.Shell.Ui;

/// <summary>
/// Draws a Norrsken line glyph (from <see cref="IconPack"/>) in the inherited foreground colour,
/// so a glyph inside an inverted (active) tile inverts with it.
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(GlyphIcon),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (IconPack.Named(Glyph) is not { } geometry || ActualWidth <= 0) return;
        double s = Math.Min(ActualWidth, ActualHeight) / 24;
        dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        // Thinner strokes at small sizes would vanish; keep ~1.4 device px minimum.
        var pen = new Pen(Foreground, Math.Max(1.7, 1.4 / s))
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(Filled ? Foreground : null, pen, geometry);
        dc.Pop();
        dc.Pop();
    }
}
