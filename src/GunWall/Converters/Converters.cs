using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        string hex = value is AppCategory c
            ? Services.CategoryPalette.ForCategory(c)
            : Services.CategoryPalette.Get("Unknown");
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush(Color.FromRgb(0x7A, 0x82, 0x8C)); }
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

/// <summary>Maps a rule's Protected flag to the toggle-button label.</summary>
public sealed class ProtectLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? "Unprotect" : "Protect";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Maps an ISO-3166 alpha-2 country code to its embedded flag image (Flags/xx.png),
/// or null when the code is empty/unknown so the row simply shows no flag. Results
/// (including misses) are cached and frozen, so each flag is decoded at most once.
/// </summary>
public sealed class CountryFlagConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string code = (value as string ?? "").Trim().ToLowerInvariant();
        if (code.Length != 2) return null;

        lock (_cache)
        {
            if (_cache.TryGetValue(code, out var cached)) return cached;

            BitmapImage? img = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/Flags/{code}.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // decode now so a miss throws here
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { img = null; } // no flag bundled for this code

            _cache[code] = img;
            return img;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
