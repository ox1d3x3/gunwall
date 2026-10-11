using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GunWall.Controls;

/// <summary>
/// Live numbers that count to their new value instead of snapping to it
/// (0.99.222). Mahabub: the Overview's figures jumped once a second; he wanted
/// them to roll, like a speedometer.
///
/// One frame hook for every rolling number on screen, unhooked when none is
/// moving. Each change eases from the value currently SHOWN - so a new value
/// arriving mid-roll continues from where the digits are, never jumping back.
/// Off (plain text) when Windows' animation effects are off, when the TextBlock
/// is not visible, and for the first value (nothing to roll from).
/// </summary>
public static class RollingNumber
{
    /// <summary>How long a roll takes - under the one-second refresh, so a value
    /// settles before the next one arrives.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(550);

    private sealed class Roll
    {
        public double From, To, Shown;
        public bool HasValue;
        public DateTime StartUtc;
        public Func<double, string> Format = v => v.ToString("0");
    }

    private static readonly ConditionalWeakTable<TextBlock, Roll> Rolls = new();
    private static readonly List<(WeakReference<TextBlock> Block, Roll Roll)> Moving = new();
    private static bool _hooked;

    /// <summary>Shows <paramref name="value"/> in <paramref name="block"/>, rolling
    /// from what it shows now. Call on the UI thread.</summary>
    public static void Set(TextBlock? block, double value, Func<double, string> format)
    {
        if (block == null) return;
        var r = Rolls.GetValue(block, _ => new Roll());
        r.Format = format;
        bool animate = r.HasValue && SystemParameters.ClientAreaAnimation && block.IsVisible
                       && !double.IsNaN(value) && !double.IsInfinity(value)
                       && format(value) != format(r.Shown);
        if (!animate)
        {
            r.From = r.To = r.Shown = value;
            r.HasValue = true;
            Moving.RemoveAll(m => m.Roll == r);
            block.Text = format(value);
            return;
        }
        r.From = r.Shown;
        r.To = value;
        r.StartUtc = DateTime.UtcNow;
        if (!Moving.Any(m => m.Roll == r)) Moving.Add((new WeakReference<TextBlock>(block), r));
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        for (int i = Moving.Count - 1; i >= 0; i--)
        {
            var (wr, r) = Moving[i];
            if (!wr.TryGetTarget(out var block)) { Moving.RemoveAt(i); continue; }
            double t = Math.Clamp((now - r.StartUtc).TotalMilliseconds / Duration.TotalMilliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);               // ease out: quick start, gentle landing
            r.Shown = r.From + (r.To - r.From) * eased;
            string text = r.Format(t >= 1 ? r.To : r.Shown);
            if (block.Text != text) block.Text = text;
            if (t >= 1) { r.Shown = r.To; Moving.RemoveAt(i); }
        }
        if (Moving.Count == 0 && _hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }
}
