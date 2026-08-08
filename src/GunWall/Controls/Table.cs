using System.Windows;
using System.Windows.Controls;

namespace GunWall.Controls;

/// <summary>Which lifecycle state a table is in. Section 10.</summary>
public enum TablePhase
{
    /// <summary>Data is current. What shows depends on whether there are rows,
    /// and whether a filter is narrowing them — see <see cref="Table"/>.</summary>
    Ready,
    /// <summary>A fetch is in flight and has never completed. Skeleton rows.</summary>
    Loading,
    /// <summary>The last attempt failed. Error block with a retry.</summary>
    Error,
}

/// <summary>
/// Table lifecycle, as attached properties on the ListView.
///
/// WHY A STATE MACHINE AND NOT A TRIGGER
///
/// "No rows" is three different situations wearing one appearance:
///
///   - nothing has loaded yet          -> skeleton, because the table is working
///   - the load failed                 -> error, because something needs doing
///   - there genuinely is nothing      -> empty, and on several of these screens
///                                        that is the GOOD outcome
///
/// and a fourth once a filter exists: rows are there, the query hides them.
///
/// A trigger on HasItems cannot tell these apart, and guessing wrong is not a
/// cosmetic mistake. Showing "No alerts. That is the good outcome." over a table
/// that is still reading the kernel buffer tells someone their machine is quiet
/// when nobody has looked yet. On a firewall that is the wrong answer to give
/// confidently.
///
/// So the phase is stated explicitly by whoever owns the data, and the two
/// derived cases — empty versus no-results — come from whether a query is set.
///
/// DELIBERATELY DEFAULTING TO Ready
///
/// A table nobody has wired up behaves exactly as it did before: rows, or the
/// empty message. Wiring Loading and Error is opt-in per screen, because a
/// screen that populates in one synchronous pass has no loading state to show
/// and pretending otherwise would add a flicker for nothing.
/// </summary>
public static class Table
{
    /// <summary>Lifecycle phase. Set by whatever owns the fetch.</summary>
    public static readonly DependencyProperty PhaseProperty =
        DependencyProperty.RegisterAttached(
            "Phase", typeof(TablePhase), typeof(Table),
            new PropertyMetadata(TablePhase.Ready));

    public static void SetPhase(DependencyObject o, TablePhase v) => o.SetValue(PhaseProperty, v);
    public static TablePhase GetPhase(DependencyObject o) => (TablePhase)o.GetValue(PhaseProperty);

    /// <summary>The active filter text, or empty. Distinguishes "nothing here"
    /// from "nothing matches", which need different words and different
    /// remedies — one is a state of the world, the other is undone by clearing
    /// a box.</summary>
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(Table),
            new PropertyMetadata(""));

    public static void SetQuery(DependencyObject o, string v) => o.SetValue(QueryProperty, v ?? "");
    public static string GetQuery(DependencyObject o) => (string)o.GetValue(QueryProperty) ?? "";

    /// <summary>Message for the empty state. What this table means by nothing.</summary>
    public static readonly DependencyProperty EmptyTextProperty =
        DependencyProperty.RegisterAttached(
            "EmptyText", typeof(string), typeof(Table), new PropertyMetadata(""));

    public static void SetEmptyText(DependencyObject o, string v) => o.SetValue(EmptyTextProperty, v);
    public static string GetEmptyText(DependencyObject o) => (string)o.GetValue(EmptyTextProperty);

    /// <summary>Short heading above the empty message.</summary>
    public static readonly DependencyProperty EmptyTitleProperty =
        DependencyProperty.RegisterAttached(
            "EmptyTitle", typeof(string), typeof(Table), new PropertyMetadata("Nothing here yet"));

    public static void SetEmptyTitle(DependencyObject o, string v) => o.SetValue(EmptyTitleProperty, v);
    public static string GetEmptyTitle(DependencyObject o) => (string)o.GetValue(EmptyTitleProperty);

    /// <summary>Caption beside the spinner while loading.</summary>
    public static readonly DependencyProperty LoadingTextProperty =
        DependencyProperty.RegisterAttached(
            "LoadingText", typeof(string), typeof(Table), new PropertyMetadata("Loading..."));

    public static void SetLoadingText(DependencyObject o, string v) => o.SetValue(LoadingTextProperty, v);
    public static string GetLoadingText(DependencyObject o) => (string)o.GetValue(LoadingTextProperty);

    /// <summary>Error body. Section 10 requires this to state what is STILL TRUE
    /// before what is broken — "filters stay loaded in the kernel, you are still
    /// protected" — because the first thing someone reads during a failure
    /// should tell them whether they are exposed.</summary>
    public static readonly DependencyProperty ErrorTextProperty =
        DependencyProperty.RegisterAttached(
            "ErrorText", typeof(string), typeof(Table), new PropertyMetadata(""));

    public static void SetErrorText(DependencyObject o, string v) => o.SetValue(ErrorTextProperty, v);
    public static string GetErrorText(DependencyObject o) => (string)o.GetValue(ErrorTextProperty);

    /// <summary>Machine-readable code and timestamp, for a support paste. Never
    /// a raw exception - section 10 is explicit about that.</summary>
    public static readonly DependencyProperty ErrorCodeProperty =
        DependencyProperty.RegisterAttached(
            "ErrorCode", typeof(string), typeof(Table), new PropertyMetadata(""));

    public static void SetErrorCode(DependencyObject o, string v) => o.SetValue(ErrorCodeProperty, v);
    public static string GetErrorCode(DependencyObject o) => (string)o.GetValue(ErrorCodeProperty);
}
