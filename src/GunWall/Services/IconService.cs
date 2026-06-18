using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GunWall.Services;

/// <summary>
/// Extracts an executable's icon as a frozen (thread-safe, bindable) ImageSource,
/// cached per path. The native HICON is released immediately after the bitmap is
/// copied, so there is no handle leak. Failure-tolerant: returns null when an icon
/// can't be read, and the UI simply shows no icon.
/// </summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        return Cache.GetOrAdd(exePath, path =>
        {
            try
            {
                if (!File.Exists(path)) return null;

                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;

                // CreateBitmapSourceFromHIcon copies the pixels, so the icon handle
                // (disposed by the using above) does not need to outlive this call.
                var src = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch
            {
                return null;
            }
        });
    }
}
