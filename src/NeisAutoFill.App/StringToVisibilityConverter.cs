using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NeisAutoFill.App;

/// <summary>문자열이 비어 있으면 Collapsed, 있으면 Visible.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
