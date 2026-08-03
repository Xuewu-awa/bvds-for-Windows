using System.Globalization;
using System.Windows.Data;

namespace BvdsForWindows.ViewModels;

/// <summary>字符串相等 → bool 转换（用于 RadioButton 绑定，参数传目标值）</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter as string ?? "" : Binding.DoNothing;
}
