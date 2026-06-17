using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GunWall.Models;

namespace GunWall.Converters;

/// <summary>Maps AppStatus to the pill FOREGROUND colour (bright green/red).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var allowed = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x54));
        var blocked = new SolidColorBrush(Color.FromRgb(0xD6, 0x53, 0x4F));
        return value is AppStatus s && s == AppStatus.Blocked ? blocked : allowed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps AppStatus to the pill soft BACKGROUND fill.</summary>
public sealed class StatusToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var allowedFill = new SolidColorBrush(Color.FromRgb(0xE7, 0xF6, 0xEC));
        var blockedFill = new SolidColorBrush(Color.FromRgb(0xFC, 0xEB, 0xEA));
        return value is AppStatus s && s == AppStatus.Blocked ? blockedFill : allowedFill;
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

/// <summary>Maps AppCategory to a distinct dot color.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var color = value is AppCategory c ? c switch
        {
            AppCategory.Signed => "#3FB868",    // green  - valid, trusted signature
            AppCategory.Unsigned => "#E0A53F",  // amber  - no signature
            AppCategory.System => "#5B8DEF",    // blue   - Windows/system
            AppCategory.Invalid => "#E25C5C",   // red    - invalid/untrusted signature
            _ => "#7A828C"                       // gray   - unknown
        } : "#7A828C";
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps AppCategory to a short label for tooltips/legend.</summary>
public sealed class CategoryToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is AppCategory c ? c switch
        {
            AppCategory.Signed => "Valid signature",
            AppCategory.Unsigned => "Unsigned",
            AppCategory.System => "Windows / system",
            AppCategory.Invalid => "Invalid signature",
            _ => "Unknown"
        } : "Unknown";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
