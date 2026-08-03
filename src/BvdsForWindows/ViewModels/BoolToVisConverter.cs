using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BvdsForWindows.ViewModels;

/// <summary>bool → Visibility 转换</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public static readonly BoolToVisConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility 反向转换（true → Collapsed）</summary>
public sealed class InverseBoolToVisConverter : IValueConverter
{
    public static readonly InverseBoolToVisConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public static class BoolToVis
{
    public static BoolToVisConverter Instance => BoolToVisConverter.Instance;
}

public static class InverseBoolToVis
{
    public static InverseBoolToVisConverter Instance => InverseBoolToVisConverter.Instance;
}
