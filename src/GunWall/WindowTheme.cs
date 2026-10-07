using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace GunWall;

/// <summary>
/// Paints the Windows title bar of GunWall's secondary windows in the app's
/// theme (0.99.182).
///
/// The main window draws its own title bar (WPF UI's FluentWindow), and the
/// message box and connection prompt have none. Every other window - app
/// properties, device note, blocklist domains, access rules, kernel layer check,
/// error log - kept the standard Windows title bar, which stays white under the
/// app's dark theme: a modern window under an old-looking strip. Reported from
/// use as popups that "don't look like our app".
///
/// Rather than rebuild six windows around a custom title bar, this asks Windows
/// to draw the existing one in the window's own colours: the caption takes the
/// window's background, the title the theme's text colour, the edge the theme's
/// border colour, with rounded corners and dark-mode caption buttons where the
/// theme is dark. On Windows 10, which has no caption colour, only the dark-mode
/// switch applies.
///
/// Applied twice for each window: on SourceInitialized for windows that opt in
/// with <see cref="Attach"/> (so the bar is right before the window first
/// appears), and from a class handler on every Window's Loaded as the catch-all
/// for any window that does not. <see cref="ApplyAll"/> repaints the open
/// windows when the theme changes.
/// </summary>
public static class WindowTheme
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;   // Windows 10 1809-1909
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_ROUND = 2;

    private static bool _registered;

    /// <summary>Installs the catch-all once, at startup.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) Apply(w); }));
    }

    /// <summary>Paints the title bar as soon as the window has a handle - before
    /// it is first shown - and again whenever it loads.</summary>
    public static void Attach(Window w) => w.SourceInitialized += (_, _) => Apply(w);

    /// <summary>Repaints every open window, after a theme change.</summary>
    public static void ApplyAll()
    {
        try
        {
            foreach (Window w in Application.Current.Windows) Apply(w);
        }
        catch { /* cosmetic */ }
    }

    /// <summary>True for a window this should paint: one with a standard title
    /// bar. The main window draws its own; borderless windows have none.</summary>
    internal static bool Applies(Window w) =>
        w is not Wpf.Ui.Controls.FluentWindow && w.WindowStyle != WindowStyle.None;

    public static void Apply(Window w)
    {
        try
        {
            if (!Applies(w)) return;
            IntPtr h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;

            // The caption is the window's own background, so bar and body read as
            // one surface - the same effect the main window gets from drawing its
            // title bar itself.
            Color caption = (w.Background as SolidColorBrush)?.Color
                            ?? ResColor("BgPrimary", Colors.White);
            if (caption.A < 255) caption = ResColor("BgPrimary", Colors.White);
            Color text = ResColor("TextPrimary", Colors.Black);
            Color border = ResColor("BorderBrush", Colors.Gray);

            int dark = Luminance(caption) < 0.5 ? 1 : 0;
            if (DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int c = ColorRef(caption), t = ColorRef(text), b = ColorRef(border);
            DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref t, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref b, sizeof(int));
        }
        catch { /* older Windows or no compositor: the standard bar stays */ }
    }

    private static Color ResColor(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}
