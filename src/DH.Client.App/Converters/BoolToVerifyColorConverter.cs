using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace DH.Client.App.Converters
{
    /// <summary>
    /// 布尔值 → 验证结果颜色：true(通过) → 绿色, false(失败) → 红色
    /// </summary>
    public class BoolToVerifyColorConverter : IValueConverter
    {
        public static readonly BoolToVerifyColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool passed)
            {
                return passed
                    ? new SolidColorBrush(Color.Parse("#28a745")) // 绿色 - 验证通过
                    : new SolidColorBrush(Color.Parse("#dc3545")); // 红色 - 验证失败
            }
            return new SolidColorBrush(Colors.Gray);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
