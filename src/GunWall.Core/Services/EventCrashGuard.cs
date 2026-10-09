using System.Globalization;
using System.IO;

namespace GunWall.Services;

/// <summary>
/// Decides whether kernel event detection must be switched off after an unclean
/// exit (0.99.194).
///
/// The kernel event callback reads a native struct whose layout could, on some
/// Windows build, be wrong - and then the process dies inside the callback. A
/// marker file is written while events run and deleted on a clean exit, so a
/// marker found at startup means the last run did not exit cleanly.
///
/// Until 0.99.193 ONE such marker switched detection off permanently. But almost
/// every unclean exit is not a crash: the installer force-closes GunWall on every
/// upgrade and uninstall, Windows ends it at restart, power fails. So detection was
/// quietly turned off on ordinary machines - and without it, a new app whose first
/// connection is blocked before it reaches the connection table is never prompted
/// for (reported: Telegram and UniGetUI, 2026-10-09).
///
/// A crash in the callback happens soon after events start, and again on the next
/// start. So: a run that ended unclean within <see cref="ShortRun"/> of starting is
/// a strike; a longer run, or a clean exit, clears the strikes; detection is
/// switched off only after <see cref="StrikesToDisable"/> short unclean runs in a row.
/// The marker carries the start time, a heartbeat refreshed while running, and the
/// strike count.
/// </summary>
public static class EventCrashGuard
{
    public static readonly TimeSpan ShortRun = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(60);
    public const int StrikesToDisable = 2;

    public sealed record Marker(DateTime StartUtc, DateTime AliveUtc, int Strikes);

    public sealed record Verdict(bool Disable, int Strikes, string Reason);

    public static string Format(Marker m) =>
        string.Join("\n",
            m.StartUtc.ToString("o", CultureInfo.InvariantCulture),
            m.AliveUtc.ToString("o", CultureInfo.InvariantCulture),
            m.Strikes.ToString(CultureInfo.InvariantCulture));

    /// <summary>Null when the text is not this format (including the single
    /// timestamp written by 0.99.193 and earlier).</summary>
    public static Marker? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length != 3) return null;
        if (!DateTime.TryParse(lines[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var start)) return null;
        if (!DateTime.TryParse(lines[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var alive)) return null;
        if (!int.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out int strikes)) return null;
        if (alive < start || strikes < 0 || strikes > 100) return null;
        return new Marker(start.ToUniversalTime(), alive.ToUniversalTime(), strikes);
    }

    /// <summary>The decision at startup. <paramref name="markerText"/> is null when
    /// no marker exists (the last run exited cleanly, or events were off).</summary>
    public static Verdict Decide(string? markerText, bool markerExists)
    {
        if (!markerExists) return new Verdict(false, 0, "");

        var m = Parse(markerText);
        int strikes;
        string ran;
        if (m == null)
        {
            // Unreadable or the old format: no start time to judge by. Counts as a
            // strike but never alone switches detection off.
            strikes = 1;
            ran = "an unknown time";
        }
        else
        {
            var run = m.AliveUtc - m.StartUtc;
            bool shortRun = run < ShortRun;
            strikes = shortRun ? m.Strikes + 1 : 0;
            ran = run.TotalMinutes < 1 ? "under a minute" : $"at least {(int)run.TotalMinutes} min";
        }

        bool disable = strikes >= StrikesToDisable;
        string reason = disable
            ? $"the last {strikes} runs each ended without a clean exit within {ShortRun.TotalMinutes:0} minutes of starting kernel events - treated as a crash in the event callback"
            : $"the last run ended without a clean exit after {ran} (force-closed, restart or power loss); strike {strikes} of {StrikesToDisable}, detection stays on";
        return new Verdict(disable, disable ? 0 : strikes, reason);
    }
}
