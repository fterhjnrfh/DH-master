using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace DH.Client.App.Converters
{
    public class BoolToTextColorConverter : IValueConverter
    {
        public static readonly BoolToTextColorConverter Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool isOnline)
            {
                return isOnline
                    ? new SolidColorBrush(Color.Parse("#28a745"))
                    : new SolidColorBrush(Color.Parse("#555555"));
            }
            return new SolidColorBrush(Color.Parse("#555555"));
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}