using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace PureAudio.Helpers;

/// <summary>
/// Converts a boolean (IsActive) to a Color for CUE segment rectangles.
/// True = Gold (#C9A84C), False = Grey (#555555).
/// </summary>
public class BoolToSegmentColorConverter : IValueConverter
{
    private static readonly Color GoldColor = Color.FromRgb(0xC9, 0xA8, 0x4C);
    private static readonly Color GreyColor = Color.FromRgb(0x55, 0x55, 0x55);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool isActive && isActive)
            return GoldColor;
        return GreyColor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
