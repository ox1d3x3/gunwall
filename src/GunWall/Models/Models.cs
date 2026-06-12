namespace GunWall.Models;

/// <summary>A single live network connection observed on the machine.</summary>
public sealed class ConnectionInfo
{
    public int ProcessId { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string LocalAddress { get; set; } = "";
    public int LocalPort { get; set; }
    public string RemoteAddress { get; set; } = "";
    public int RemotePort { get; set; }
    public string State { get; set; } = "";

    // Filled in by the UI layer once the PID is resolved to a process.
    public string ProcessName { get; set; } = "";

    // Convenience strings for binding in the connections grid.
    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);
    public string RemoteEndpoint => FormatEndpoint(RemoteAddress, RemotePort);

    private static string FormatEndpoint(string addr, int port)
    {
        if (string.IsNullOrEmpty(addr)) return "\u2014"; // UDP listeners have no remote
        // Bracket IPv6 literals for readability: [::1]:443
        return addr.Contains(':') ? $"[{addr}]:{port}" : $"{addr}:{port}";
    }
}

/// <summary>Whether an application is currently allowed or blocked.</summary>
public enum AppStatus
{
    Allowed,
    Blocked
}

/// <summary>An application known to GunWall, with its current policy.</summary>
public sealed class AppInfo
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public AppStatus Status { get; set; } = AppStatus.Allowed;

    /// <summary>Number of live connections currently attributed to this app.</summary>
    public int ActiveConnections { get; set; }

    public override bool Equals(object? obj) =>
        obj is AppInfo other &&
        string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        ExecutablePath.ToLowerInvariant().GetHashCode();
}

/// <summary>
/// A persisted firewall rule. Holds the WFP filter IDs created for the rule so
/// it can be cleanly removed later, even across application restarts.
/// </summary>
public sealed class FirewallRule
{
    public string ExecutablePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public AppStatus Status { get; set; } = AppStatus.Blocked;

    /// <summary>WFP filter IDs created for this rule (connect/recv, v4/v6).</summary>
    public List<ulong> FilterIds { get; set; } = new();

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One entry in the live activity feed: a newly observed connection.
/// Inspired by a live activity timeline — local-only, never uploaded.
/// </summary>
public sealed class NetActivityEvent
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string ProcessName { get; set; } = "";
    public string Detail { get; set; } = "";
    public string TimeText => Time.ToString("HH:mm:ss");
}
