using GunWall.Models;

namespace GunWall.Services;

/// <summary>One decision, yours or GunWall's, for the tray's Recent list and Undo
/// (0.99.241). <see cref="Undo"/> holds each program's rule as it was before, or is
/// null for a decision that cannot be undone (one GunWall made on its own).</summary>
public sealed record RecentDecision(DateTime At, string Name, string ExePath, string What, bool Automatic,
                                    IReadOnlyList<DecisionSnapshot>? Undo)
{
    /// <summary>"msedge - allowed, 09:41" for the tray menu.</summary>
    public string MenuText => $"{Name} - {What}, {At:HH:mm}";
}

/// <summary>
/// The last few decisions this session, newest first (0.99.241, Mahabub
/// 2026-11-17: "recent decisions in the tray"). Undo works on the newest one that
/// can still be undone; undoing it takes it off the list.
/// </summary>
public sealed class RecentDecisions
{
    public const int Max = 10;
    private readonly List<RecentDecision> _items = new();

    public IReadOnlyList<RecentDecision> Items => _items;

    public void Add(RecentDecision d)
    {
        _items.Insert(0, d);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
    }

    /// <summary>The newest decision that can be undone, or null.</summary>
    public RecentDecision? LastUndoable => _items.FirstOrDefault(d => d.Undo is { Count: > 0 });

    /// <summary>Takes <paramref name="d"/> off the list once it has been undone.</summary>
    public bool Remove(RecentDecision d) => _items.Remove(d);
}

/// <summary>
/// "While you were away" (0.99.241): what happened while the window was in the
/// tray, said once when it comes back - if it was away long enough to have missed
/// something and something happened. Counts, not a log: programs blocked from
/// connecting (with how often), decisions GunWall made by itself, popups shown.
/// </summary>
public sealed class AwayTally
{
    /// <summary>Shorter than this and nobody was really away.</summary>
    public static readonly TimeSpan MinAway = TimeSpan.FromMinutes(10);

    private DateTime? _since;
    private readonly Dictionary<string, int> _blocked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _decided = new(StringComparer.Ordinal);
    private int _asked;

    public bool Away => _since != null;

    /// <summary>The window went to the tray. A second call while away changes nothing.</summary>
    public void Begin(DateTime now)
    {
        if (_since != null) return;
        _since = now;
        _blocked.Clear(); _decided.Clear(); _asked = 0;
    }

    /// <summary>A connection GunWall blocked, by program name.</summary>
    public void Blocked(string app)
    {
        if (_since == null || string.IsNullOrWhiteSpace(app)) return;
        _blocked[app] = _blocked.TryGetValue(app, out int n) ? n + 1 : 1;
    }

    /// <summary>A decision GunWall made without asking, by what it was.</summary>
    public void Decided(string what)
    {
        if (_since == null || string.IsNullOrWhiteSpace(what)) return;
        _decided[what] = _decided.TryGetValue(what, out int n) ? n + 1 : 1;
    }

    /// <summary>A popup was shown.</summary>
    public void Asked() { if (_since != null) _asked++; }

    /// <summary>The window is back. The summary, or null when it was not away for
    /// <see cref="MinAway"/> or nothing happened. Either way the tally ends.</summary>
    public (string Title, string Body)? End(DateTime now)
    {
        if (_since is not DateTime since) return null;
        _since = null;
        var away = now - since;
        if (away < MinAway) return null;
        var lines = new List<string>();
        if (_blocked.Count > 0)
        {
            var top = _blocked.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
            string names = string.Join(", ", top.Take(3).Select(kv => $"{kv.Key} ({kv.Value:N0})"));
            if (top.Count > 3) names += $" and {top.Count - 3} more";
            lines.Add($"Blocked {Plural(top.Count, "program")} from connecting: {names}.");
        }
        foreach (var kv in _decided.OrderByDescending(kv => kv.Value))
            lines.Add($"{Plural(kv.Value, "program")} {kv.Key}.");
        if (_asked > 0) lines.Add($"{Plural(_asked, "popup")} asked for a decision.");
        if (lines.Count == 0) return null;
        return ($"While you were away ({Duration(away)})", string.Join("\n", lines));
    }

    public static string Duration(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min"
        : t.TotalHours < 24 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{(int)t.TotalDays} d {t.Hours} h";

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n:N0} {word}s";
}
