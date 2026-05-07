// DH.Client.App/Converters/IntToBoolConverter.cs
using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DH.Client.App.Converters;

/// <summary>
/// 整数到布尔值转换器
/// 用于RadioButton绑定到整数属性
/// ConverterParameter指定要比较的值
/// </summary>
public class IntToBoolConverter : IValueConverter
{
    public static readonly IntToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int compareValue))
        {
            return intValue == compareValue;
        }
        return false;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool boolValue && boolValue && parameter is string paramStr && int.TryParse(paramStr, out int intValue))
        {
            return intValue;
        }
        return Avalonia.Data.BindingOperations.DoNothing;
    }
}
