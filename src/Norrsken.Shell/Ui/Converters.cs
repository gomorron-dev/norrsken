using System.Globalization;
using System.Windows.Data;

namespace Norrsken.Shell.Ui;

/// <summary>Maps a bool to one of two numbers (dot opacity, dot width, ...).</summary>
public sealed class BoolToDouble : IValueConverter
{
    public double True { get; set; } = 1;
    public double False { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? True : False;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
