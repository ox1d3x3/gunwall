using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GunWall.Models;

namespace GunWall.Converters;

/// <summary>Maps AppStatus to a status colour (green allowed / red blocked).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var allowed = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C));
        var blocked = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
        return value is AppStatus s && s == AppStatus.Blocked ? blocked : allowed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps AppStatus to the action button label (the opposite action).</summary>
public sealed class StatusToActionTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AppStatus s && s == AppStatus.Blocked ? "Allow" : "Block";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps AppStatus to a readable text label.</summary>
public sealed class StatusToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AppStatus s && s == AppStatus.Blocked ? "Blocked" : "Allowed";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
