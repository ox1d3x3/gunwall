using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace GunWall.Controls;

/// <summary>
/// Letter-spacing for TextBlock, which WPF does not have.
///
/// WHY THIS EXISTS
///
/// The design tracks uppercase micro-labels at 0.09–0.13em — column headers,
/// section labels, the state kicker on the hero. At 10–11px, uppercase text set
/// solid reads as a block; the tracking is most of what makes those labels
/// legible as labels rather than as small shouting.
///
/// WPF has no character-spacing property. <c>CharacterSpacing</c> is WinUI and
/// UWP; <c>Typography</c> exposes OpenType features, not tracking. So it has to
/// be built.
///
/// HOW, AND WHAT IT COSTS
///
/// The text is rebuilt as a run per character with a thin space between, which
/// is an approximation rather than true tracking:
///
///   - It only does POSITIVE spacing. Negative tracking — the design's −0.03em
///     at display sizes — cannot be expressed this way at all, and would need a
///     custom render path. That was a deliberate scope decision, not an
///     oversight: positive spacing covers the short static labels where the
///     effect is visible, and the display sizes carry their weight from size.
///   - Text trimming and wrapping still work, but they now break between the
///     inserted runs rather than at word boundaries, so this must not be used on
///     anything that wraps or ellipsises.
///   - Copying the text yields the spacing characters too.
///
/// All three are acceptable for short, static, uppercase labels and for nothing
/// else, which is exactly where it is applied.
/// </summary>
public static class Tracking
{
    /// <summary>Spacing in ems. 0 leaves the TextBlock untouched.</summary>
    public static readonly DependencyProperty EmProperty =
        DependencyProperty.RegisterAttached(
            "Em", typeof(double), typeof(Tracking),
            new PropertyMetadata(0.0, OnEmChanged));

    public static void SetEm(DependencyObject o, double v) => o.SetValue(EmProperty, v);
    public static double GetEm(DependencyObject o) => (double)o.GetValue(EmProperty);

    private static void OnEmChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is not TextBlock tb) return;
        // Text arrives from a binding or a style setter, either of which can land
        // after this property does, so apply on load as well as on change.
        tb.Loaded -= OnLoaded;
        tb.Loaded += OnLoaded;
        Apply(tb);
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock tb) Apply(tb);
    }

    /// <summary>True when the face gives every glyph the same advance.</summary>
    private static bool IsMonospaced(TextBlock tb)
    {
        try
        {
            double W = Measure(tb, "W"), i = Measure(tb, "i");
            return System.Math.Abs(W - i) < 0.01;
        }
        catch { return false; }
    }

    private static double Measure(TextBlock tb, string s) =>
        new System.Windows.Media.FormattedText(
            s, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, System.Windows.Media.Brushes.Black, 1.0).WidthIncludingTrailingWhitespace;

    private static void Apply(TextBlock tb)
    {
        double em = GetEm(tb);
        string text = tb.Text ?? "";
        if (em <= 0 || text.Length < 2) return;

        // Not under a monospace face. This approximation inserts hair spaces, and
        // a monospace font gives EVERY glyph the same advance - so a hair space is
        // a full character wide, not the 0.045em this assumes. JetBrains Mono does
        // not even contain U+200A, so the character also forces a font fallback
        // mid-string.
        //
        // The visible result once the interface font became monospace: column
        // headers roughly doubled in width and clipped - "DIRECTION" rendered as
        // "DIRECTI(". Tracking is a proportional-type adjustment, and a monospace
        // face is already evenly spaced, so skipping is correct rather than merely
        // safe. It returns automatically if the user picks a proportional font.
        if (IsMonospaced(tb)) return;

        // A hair space is the narrowest fixed-width space Unicode defines, and it
        // does not collapse the way a normal space does. Repeating it is coarse -
        // real tracking is continuous - but at these sizes the granularity is
        // below the threshold where anyone could see the difference.
        int count = (int)System.Math.Round(em / 0.045);
        if (count < 1) count = 1;
        string gap = new string('\u200A', count);

        tb.Inlines.Clear();
        for (int i = 0; i < text.Length; i++)
        {
            tb.Inlines.Add(new Run(text[i].ToString()));
            if (i < text.Length - 1) tb.Inlines.Add(new Run(gap));
        }
    }
}
