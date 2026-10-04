using System;
using System.Globalization;
using System.Windows.Data;

namespace Paster.UI;

/// <summary>
/// Turns a zero-based row index into the digit that pastes it: 1-9 then 0 for
/// the tenth, blank after that. Fed by ListBox alternation indices, which is
/// the cheapest way to get a row's position into its own template.
/// </summary>
public sealed class IndexKeyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int index || index < 0) return "";
        if (index < 9) return (index + 1).ToString(CultureInfo.InvariantCulture);
        return index == 9 ? "0" : "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
