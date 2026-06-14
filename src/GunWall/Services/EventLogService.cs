using System.Diagnostics;

namespace GunWall.Services;

/// <summary>
/// Writes GunWall firewall events to the Windows Event Log (Application log,
/// source "GunWall"), mirroring how mature firewalls surface activity to the
/// OS audit trail. Creating the event source needs administrator rights (which
/// GunWall already has). Everything here is best-effort and never throws into
/// the app — if the log is unavailable, events are simply skipped.
/// </summary>
public static class EventLogService
{
    private const string Source = "GunWall";
    private const string LogName = "Application";
    private static bool _ready;
    private static bool _checked;

    private static bool EnsureSource()
    {
        if (_checked) return _ready;
        _checked = true;
        try
        {
            if (!EventLog.SourceExists(Source))
                EventLog.CreateEventSource(new EventSourceCreationData(Source, LogName));
            _ready = true;
        }
        catch
        {
            _ready = false; // e.g. insufficient rights or policy — skip silently
        }
        return _ready;
    }

    public static void Write(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        try
        {
            if (!EnsureSource()) return;
            using var log = new EventLog(LogName) { Source = Source };
            // Keep messages compact; event IDs group by type.
            int id = type switch
            {
                EventLogEntryType.Warning => 200,
                EventLogEntryType.Error => 300,
                _ => 100
            };
            log.WriteEntry(message, type, id);
        }
        catch { /* never let logging break the app */ }
    }
}
