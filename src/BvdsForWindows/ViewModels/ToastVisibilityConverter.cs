using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BvdsForWindows.ViewModels;

/// <summary>Toast 可见性转换器</summary>
public sealed class ToastVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
