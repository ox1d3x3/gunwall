using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using GunWall.Services;

namespace GunWall;

/// <summary>
/// The UI size for every window GunWall opens, not only the main one (0.99.227).
/// Mahabub: the popups - the connection prompt, Review apps, the dialogs - stayed
/// at the old full size while the main window followed "UI size", so at the default
/// (which draws at 80% since 0.99.219) every popup looked oversized beside it.
///
/// A window calls <see cref="Apply"/> in its constructor. Its content is scaled the
/// way the main window's RootGrid is, and the sizes it was designed with (Width,
/// Height, minimum and maximum) are scaled with it - before the window first
/// appears, so it never opens big and then shrinks.
/// </summary>
public static class UiZoom
{
    /// <summary>The factor the main window uses now. Set by MainWindow.ApplyUiZoom.</summary>
    public static double Factor { get; private set; } = UiScale.Factor(UiScale.DefaultPercent);

    public static void Set(int percent) => Factor = UiScale.Factor(percent);

    private static readonly ConditionalWeakTable<Window, object> Done = new();

    /// <summary>Scales <paramref name="w"/> to the UI size when its window is created
    /// (content is set by then, also for windows built in code).</summary>
    public static void Apply(Window w)
    {
        w.SourceInitialized += (_, _) => ScaleNow(w);
    }

    private static void ScaleNow(Window w)
    {
        if (Done.TryGetValue(w, out _)) return;
        Done.Add(w, new object());
        double f = Factor;
        if (Math.Abs(f - 1.0) < 0.001 || w.Content is not FrameworkElement content) return;
        try
        {
            content.LayoutTransform = new ScaleTransform(f, f);
            bool autoW = w.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
            bool autoH = w.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
            if (!autoW && !double.IsNaN(w.Width)) w.Width = Math.Round(w.Width * f);
            if (!autoH && !double.IsNaN(w.Height)) w.Height = Math.Round(w.Height * f);
            if (w.MinWidth > 0) w.MinWidth = Math.Round(w.MinWidth * f);
            if (w.MinHeight > 0) w.MinHeight = Math.Round(w.MinHeight * f);
            if (!double.IsPositiveInfinity(w.MaxWidth)) w.MaxWidth = Math.Round(w.MaxWidth * f);
            if (!double.IsPositiveInfinity(w.MaxHeight)) w.MaxHeight = Math.Round(w.MaxHeight * f);
        }
        catch (Exception ex) { DiagnosticLog.LogException("UiZoom.Apply", ex); }
    }
}
