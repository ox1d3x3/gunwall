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

    // Full executable path of the owning process (filled by the UI layer; "" if the
    // socket has no resolvable owner, e.g. PID 0 TIME_WAIT). Used by the inspector to
    // surface the app's current policy.
    public string ExePath { get; set; } = "";

    // GeoIP enrichment (filled by the UI layer from the remote address; read-only).
    public string Country { get; set; } = "";
    public int Asn { get; set; }
    public string AsnOwner { get; set; } = "";
    public string Location
    {
        get
        {
            if (Country.Length == 0 && Asn == 0) return "";
            var parts = new System.Collections.Generic.List<string>();
            if (Country.Length > 0) parts.Add(GunWall.Services.GeoData.CountryName(Country));
            if (Asn != 0) parts.Add("AS" + Asn + (AsnOwner.Length > 0 ? " " + AsnOwner : ""));
            return string.Join("  \u00B7  ", parts);
        }
    }

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

/// <summary>Visual category for color-coding apps in the list.</summary>
public enum AppCategory { Unknown, Signed, Unsigned, System, Invalid }

/// <summary>An application known to GunWall, with its current policy.</summary>
public sealed class AppInfo
{
    public string Name { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public AppStatus Status { get; set; } = AppStatus.Allowed;

    /// <summary>Signed / unsigned / system / invalid — drives the colored dot.</summary>
    public AppCategory Category { get; set; } = AppCategory.Unknown;

    /// <summary>Verified signing publisher (or "Unsigned" / "Invalid signature").</summary>
    public string Publisher { get; set; } = "";

    /// <summary>Cached executable icon for the list (null if unavailable).</summary>
    public System.Windows.Media.ImageSource? Icon { get; set; }

    /// <summary>User's free-text note for this app.</summary>
    public string Note { get; set; } = "";

    /// <summary>True when this is a Microsoft Store / UWP (AppContainer) app.</summary>
    public bool IsStoreApp { get; set; }

    /// <summary>Friendly Store display name (e.g. "Spotify"); empty for non-Store apps.</summary>
    public string StoreName { get; set; } = "";

    /// <summary>Package family name (Name_PublisherId); empty for non-Store apps.</summary>
    public string PackageFamily { get; set; } = "";

    /// <summary>Number of live connections currently attributed to this app.</summary>
    public int ActiveConnections { get; set; }

    /// <summary>SHA-256 of the executable (for display / tamper awareness).</summary>
    public string Hash { get; set; } = "";

    /// <summary>Allowed but not notified about.</summary>
    public bool Silent { get; set; }

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

    /// <summary>SHA-256 of the executable when the rule was created (tamper check).</summary>
    public string Hash { get; set; } = "";

    /// <summary>Allowed but suppressed from raising notification popups.</summary>
    public bool Silent { get; set; }

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

    /// <summary>Green for allowed, red for blocked — bound by the action pill.</summary>
    public System.Windows.Media.Brush ActionBrush => Blocked
        ? new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x6B))
        : new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x3D, 0xD6, 0x8C));
}

/// <summary>
/// A user-defined rule that matches by remote address / port / protocol /
/// direction (independent of which app makes the connection). Stored in the
/// profile and applied as WFP filters.
/// </summary>
public sealed class CustomRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public bool Block { get; set; } = true;            // true = block, false = allow
    public bool Outbound { get; set; } = true;         // true = outbound, false = inbound
    public string Protocol { get; set; } = "Any";      // Any / TCP / UDP
    public string RemoteAddress { get; set; } = "";    // empty = any
    public int RemotePort { get; set; }                // 0 = any
    public int LocalPort { get; set; }                 // 0 = any (port on this PC)
    public bool Enabled { get; set; } = true;
    public bool Protected { get; set; }                // user-marked non-removable
    public bool Applied { get; set; }                  // did the WFP filter actually install?
    public List<ulong> FilterIds { get; set; } = new();

    public string ActionText => Block ? "Block" : "Allow";
    public string DirectionText => Outbound ? "Outbound" : "Inbound";
    public string TargetText
    {
        get
        {
            string a = string.IsNullOrEmpty(RemoteAddress) ? "any address" : RemoteAddress;
            string p = RemotePort == 0 ? "any port" : $"port {RemotePort}";
            string lp = LocalPort == 0 ? "" : $", local {LocalPort}";
            return $"{Protocol} \u2192 {a}, {p}{lp}";
        }
    }
    public string StatusText => (!Enabled ? "Disabled" : Applied ? "Active" : "Not applied")
        + (Protected ? " \u00B7 protected" : "");
}

/// <summary>
/// §1 entity rule: a block keyed on a GeoIP-derived entity (country / continent /
/// ASN) rather than a literal IP. Matched reactively against each observed remote
/// in the connect-event handler; on a match GunWall installs a per-app block for
/// that remote IP. AppPath empty = applies to every app (a global rule).
/// </summary>
public sealed class EntityRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string AppPath { get; set; } = "";        // "" = all apps (global)
    public string Type { get; set; } = "country";    // country | continent | asn
    public string Value { get; set; } = "";          // e.g. "RU", "EU", "AS13335"
    public bool Enabled { get; set; } = true;

    public string AppLabel => string.IsNullOrEmpty(AppPath)
        ? "All apps"
        : System.IO.Path.GetFileName(AppPath);

    public string TypeLabel => Type switch
    {
        "country" => "Country",
        "continent" => "Continent",
        "asn" => "ASN",
        _ => Type
    };

    public string Label => $"{(Enabled ? "" : "(off) ")}{TypeLabel} {Value}  \u2192  block for {AppLabel}";
}
