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
        // These were #2E9E54 and #D6534F - fixed values that cannot be right in
        // both themes, on the pills that state whether an application is allowed
        // to reach the network. Resolved at conversion time so they follow the
        // palette in force.
        return (Brush)System.Windows.Application.Current.FindResource(
            value is AppStatus s && s == AppStatus.Blocked ? "BlockText" : "AllowText");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps AppStatus to the pill soft BACKGROUND fill.</summary>
public sealed class StatusToFillConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // #E7F6EC and #FCEBEA are near-white tints - light-theme values, hard
        // coded. The palette's 'ok-bg' and 'brand-bg' are alpha tints of the
        // role colour, so they sit correctly on either ground.
        return (Brush)System.Windows.Application.Current.FindResource(
            value is AppStatus s && s == AppStatus.Blocked ? "BlockFill" : "AllowFill");
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
/// <summary>"Exempt" when an application is exempt from kernel domain blocking,
/// and an empty string otherwise - so the column is blank for almost every row
/// and costs nothing to read until it has something to say.</summary>
public sealed class BoolToBypassTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "Exempt" : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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
        catch { return (Brush)System.Windows.Application.Current.FindResource("TextTertiary"); }
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
/// <summary>Visible when the bound string has content, Collapsed when empty -
/// used for badges that should simply not exist when there's nothing to say.</summary>
public sealed class NotEmptyToVisConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        throw new NotSupportedException();
}

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

    /// <summary>Load the embedded flag for an ISO alpha-2 code (cached, frozen), or null.</summary>
    public static BitmapImage? Load(string? code)
    {
        string c = (code ?? "").Trim().ToLowerInvariant();
        if (c.Length != 2) return null;

        lock (_cache)
        {
            if (_cache.TryGetValue(c, out var cached)) return cached;

            BitmapImage? img = null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri($"pack://application:,,,/Flags/{c}.png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad; // decode now so a miss throws here
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch { img = null; } // no flag bundled for this code

            _cache[c] = img;
            return img;
        }
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Load(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>App name -> stable, pleasant avatar color (hash into a curated palette).</summary>
public sealed class NameToBrushConverter : System.Windows.Data.IValueConverter
{
    private static readonly string[] Palette =
    {
        "#E05D5D","#E08A3C","#D4B13F","#57A85C","#3BA8A0",
        "#4A90D9","#6F6FD6","#9B59B6","#C2559C","#5D8AA8"
    };
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
    {
        string s = value as string ?? "";
        int h = 0; foreach (var ch in s) h = (h * 31 + ch) & 0x7FFFFFFF;
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter
            .ConvertFromString(Palette[h % Palette.Length]);
        var b = new System.Windows.Media.SolidColorBrush(color); b.Freeze(); return b;
    }
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>App name -> single uppercase initial for the monogram avatar.</summary>
public sealed class NameInitialConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
    {
        string s = (value as string ?? "").Trim();
        foreach (var ch in s) if (char.IsLetterOrDigit(ch)) return char.ToUpperInvariant(ch).ToString();
        return "?";
    }
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>Null -> Visible, non-null -> Collapsed (monogram only when no icon).</summary>
public sealed class NullToVisibleConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
        value == null ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) =>
        throw new NotSupportedException();
}
