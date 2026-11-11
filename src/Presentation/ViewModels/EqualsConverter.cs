using System;
using System.Globalization;
using System.Windows.Data;

namespace Mskpy.Presentation.ViewModels;

/// <summary>
/// Binds a string value to a control's IsChecked when it equals the converter
/// parameter, and writes the parameter back on a user toggle. Used for the
/// list-scope chips ("My pending" / "All Maharashtra").
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && parameter is string p && string.Equals(s, p, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => parameter?.ToString() ?? "";
}
