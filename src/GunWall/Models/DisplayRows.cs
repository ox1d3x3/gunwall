namespace GunWall.Models;

// Display-only rows: each resolves theme brushes, so they live with the window
// rather than in GunWall.Core (which has no WPF dependency).

/// <summary>One row of the Traffic Breakdown card (Phase 5). BarWidth is the
/// pre-computed pixel width of the mini bar, relative to the column's max.
/// Dominant follows the same 60%-of-largest rule as the dashboard's top
/// talkers, so the accent marks the outlier in whatever is actually happening
/// rather than decorating every row.</summary>
public sealed record BreakRow(string Name, string Value, double BarWidth, string Tip, bool Dominant = false)
{
    public System.Windows.Media.Brush BarBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(
            Dominant ? "BlockText" : "TextTertiary");
}

/// <summary>A single connection event for the Packets Log (allowed or blocked).</summary>
public sealed class PacketLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string AppName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Direction { get; set; } = "";
    public string RemoteEndpoint { get; set; } = "";
    public bool Blocked { get; set; }
    public string Action => Blocked ? "Blocked" : "Allowed";
    public string TimeText => Time.ToString("HH:mm:ss");

    /// <summary>§8: which rule produced this verdict (e.g. "App rule — Block").</summary>
    public string Reason { get; set; } = "";

    /// <summary>Green for allowed, red for blocked — bound by the action pill.</summary>
    /// <summary>Pill background behind the verdict. Paired with ActionBrush so
    /// the fill and the text always come from the same verdict.</summary>
    public System.Windows.Media.Brush ActionFill =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(
            Blocked ? "BlockFill" : "AllowFill");

    // Resolved from the theme rather than constructed from literals: these were
    // hardcoded before the palette existed, so they did not follow a theme change
    // and had drifted from the verdict colours the rest of the interface uses.
    public System.Windows.Media.Brush ActionBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(
            Blocked ? "BlockText" : "AllowText");
}

/// <summary>
/// One row of the dashboard's top-talkers list.
///
/// The bar's colour is decided against the largest row rather than a fixed
/// threshold, so it marks the outlier in whatever is actually happening: on a
/// quiet machine nothing is red, and on a busy one only the genuinely dominant
/// application is. A fixed threshold would either shout constantly or never.
/// </summary>
public sealed class TopTalker
{
    public string Name { get; set; } = "";
    public long Bytes { get; set; }
    public double BarWidth { get; set; }
    public string SizeText => FormatBytes(Bytes);
    public bool Dominant { get; set; }

    public System.Windows.Media.Brush BarBrush =>
        (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource(
            Dominant ? "BlockText" : "TextTertiary");

    private static string FormatBytes(long b) =>
        b >= 1073741824 ? $"{b / 1073741824.0:0.0} GB"
        : b >= 1048576  ? $"{b / 1048576.0:0.0} MB"
        : b >= 1024     ? $"{b / 1024.0:0.0} KB"
        : $"{b} B";
}
