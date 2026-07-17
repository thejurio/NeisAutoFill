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

/// <summary>빈 문자열이면 대시(–) 표시 — 미입력 셀 표시용 (표시 전용, 원본 값은 그대로).</summary>
public sealed class EmptyToDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value?.ToString()) ? "–" : value!.ToString()!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == "–" ? "" : value?.ToString() ?? "";
}

/// <summary>bool 반전.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>정수가 0보다 크면 true (체크박스 활성화 등).</summary>
public sealed class GreaterThanZeroConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int n && n > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
