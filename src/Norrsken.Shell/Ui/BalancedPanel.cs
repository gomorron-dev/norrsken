using System.Windows;
using System.Windows.Controls;

namespace Norrsken.Shell.Ui;

/// <summary>
/// A grid that never leaves one item alone on its last row: as many rows as needed, the same number of
/// items in each (the last one at most one short), and the cells spread across the full width so there's
/// no ragged gap on the right. Eight pins become two rows of four instead of seven and one.
/// </summary>
public sealed class BalancedPanel : Panel
{
    double _cellW, _cellH;
    int _cols;

    protected override Size MeasureOverride(Size available)
    {
        var visible = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();
        double itemW = 0, itemH = 0;
        foreach (var c in visible)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            itemW = Math.Max(itemW, c.DesiredSize.Width);
            itemH = Math.Max(itemH, c.DesiredSize.Height);
        }
        int n = visible.Count;
        if (n == 0 || itemW <= 0) { _cols = 0; return new Size(0, 0); }
        double width = double.IsInfinity(available.Width) ? itemW * n : available.Width;
        int perRow = Math.Max(1, (int)Math.Floor(width / itemW));
        int rows = (int)Math.Ceiling(n / (double)perRow);
        _cols = (int)Math.Ceiling(n / (double)rows);
        // A single short row keeps natural spacing (three pins don't stretch across the whole launcher).
        _cellW = rows == 1 ? itemW : width / _cols;
        _cellH = itemH;
        return new Size(rows == 1 ? itemW * n : width, rows * itemH);
    }

    protected override Size ArrangeOverride(Size final)
    {
        int i = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed || _cols == 0) { c.Arrange(new Rect()); continue; }
            int row = i / _cols, col = i % _cols;
            double x = col * _cellW + (_cellW - c.DesiredSize.Width) / 2;
            c.Arrange(new Rect(x, row * _cellH, c.DesiredSize.Width, _cellH));
            i++;
        }
        return final;
    }
}
