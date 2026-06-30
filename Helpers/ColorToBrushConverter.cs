using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PureAudio.Helpers;

/// <summary>
/// Converts a hex color string (e.g. "#C9A84C") to a SolidColorBrush.
/// Used for binding string color properties to Foreground/BorderBrush in XAML.
/// </summary>
public class ColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrEmpty(hex))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                return new SolidColorBrush(color);
            }
            catch
            {
                // Fallback to gray on parse failure
                return new SolidColorBrush(Colors.Gray);
            }
        }
        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
