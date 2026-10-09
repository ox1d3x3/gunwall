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

    /// <summary>Windows services hosted by this PID, e.g. "Windows Update, BITS".
    /// Empty for ordinary programs. Turns a row of identical "svchost" entries
    /// into something a rule can be reasoned about.</summary>
    public string Services { get; set; } = "";

    /// <summary>Full hosted-service list for the tooltip / inspector.</summary>
    public string ServicesDetail { get; set; } = "";

    /// <summary>Process name with its services appended when it hosts any.</summary>
    public string ProcessDisplay =>
        Services.Length > 0 ? $"{ProcessName}  ({Services})" : ProcessName;

    // Full executable path of the owning process (filled by the UI layer; "" if the
    // socket has no resolvable owner, e.g. PID 0 TIME_WAIT). Used by the inspector to
    // surface the app's current policy.
    public string ExePath { get; set; } = "";

    // GeoIP enrichment (filled by the UI layer from the remote address; read-only).
    public string Country { get; set; } = "";
    public int Asn { get; set; }
    public string AsnOwner { get; set; } = "";
    /// <summary>Why a remote has no country, in plain words ("Local network",
    /// "Not in GeoIP data", ...). Set by the UI layer when the lookup finds nothing,
    /// and shown in place of the blank Location cell that used to make a LAN
    /// printer and an unplaced public server look the same.</summary>
    public string LocationNote { get; set; } = "";

    public string Location
    {
        get
        {
            if (Country.Length == 0 && Asn == 0) return LocationNote;
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
/// <summary>
/// One row of the Applications list.
///
/// Three values change every second for an active application - the connection
/// count, and the sparkline with its tooltip - and those three announce their
/// changes. The list is kept in sync row by row rather than cleared and refilled,
/// so an unchanged row keeps its visual container, its selection and its place,
/// and a change to one of these three redraws only that cell. Everything else on
/// the row is compared by value and, when it differs, the row object is replaced.
/// </summary>
public sealed class AppInfo : System.ComponentModel.INotifyPropertyChanged
{
    // Every displayed value announces its own changes (0.99.177). Rows are
    // updated in place rather than replaced, because Equals below compares by
    // path: a replacement row is "equal" to the one it replaces, and WPF treats
    // an equal value as no change - so a row replaced after Block or Allow kept
    // showing its old status until the list was rebuilt another way (reported
    // from use: the filtered Applications list never updated after a click).
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Changed(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));

    public string Name
    {
        get => _name;
        set { if (!Equals(_name, value)) { _name = value; Changed(nameof(Name)); } }
    }
    private string _name = "";
    public string ExecutablePath { get; set; } = "";
    public AppStatus Status
    {
        get => _status;
        set { if (!Equals(_status, value)) { _status = value; Changed(nameof(Status)); } }
    }
    private AppStatus _status = AppStatus.Allowed;

    /// <summary>Signed / unsigned / system / invalid — drives the colored dot.</summary>
    public AppCategory Category
    {
        get => _category;
        set { if (!Equals(_category, value)) { _category = value; Changed(nameof(Category)); } }
    }
    private AppCategory _category = AppCategory.Unknown;

    /// <summary>Verified signing publisher (or "Unsigned" / "Invalid signature").</summary>
    public string Publisher
    {
        get => _publisher;
        set { if (!Equals(_publisher, value)) { _publisher = value; Changed(nameof(Publisher)); } }
    }
    private string _publisher = "";

    /// <summary>Cached executable icon for the list (null if unavailable). An
    /// ImageSource, typed object so this library carries no WPF dependency; the
    /// window sets and binds it.</summary>
    public object? Icon
    {
        get => _icon;
        set { if (!Equals(_icon, value)) { _icon = value; Changed(nameof(Icon)); } }
    }
    private object? _icon;

    /// <summary>User's free-text note for this app.</summary>
    public string Note
    {
        get => _note;
        set { if (!Equals(_note, value)) { _note = value; Changed(nameof(Note)); } }
    }
    private string _note = "";

    /// <summary>Exempt from kernel domain blocking. Surfaced in the list because
    /// an exemption nobody can see is one they set once and forget - and this one
    /// quietly weakens a protection they asked for.</summary>
    public bool BypassBlocklists
    {
        get => _bypassBlocklists;
        set { if (!Equals(_bypassBlocklists, value)) { _bypassBlocklists = value; Changed(nameof(BypassBlocklists)); } }
    }
    private bool _bypassBlocklists;

    /// <summary>True when this is a Microsoft Store / UWP (AppContainer) app.</summary>
    public bool IsStoreApp
    {
        get => _isStoreApp;
        set { if (!Equals(_isStoreApp, value)) { _isStoreApp = value; Changed(nameof(IsStoreApp)); } }
    }
    private bool _isStoreApp;

    /// <summary>Friendly Store display name (e.g. "Spotify"); empty for non-Store apps.</summary>
    public string StoreName
    {
        get => _storeName;
        set { if (!Equals(_storeName, value)) { _storeName = value; Changed(nameof(StoreName)); } }
    }
    private string _storeName = "";

    /// <summary>Package family name (Name_PublisherId); empty for non-Store apps.</summary>
    public string PackageFamily
    {
        get => _packageFamily;
        set { if (!Equals(_packageFamily, value)) { _packageFamily = value; Changed(nameof(PackageFamily)); } }
    }
    private string _packageFamily = "";

    /// <summary>Number of live connections currently attributed to this app.</summary>
    public int ActiveConnections
    {
        get => _activeConnections;
        set { if (_activeConnections != value) { _activeConnections = value; Changed(nameof(ActiveConnections)); } }
    }
    private int _activeConnections;

    /// <summary>SHA-256 of the executable (for display / tamper awareness).</summary>
    public string Hash
    {
        get => _hash;
        set { if (!Equals(_hash, value)) { _hash = value; Changed(nameof(Hash)); } }
    }
    private string _hash = "";

    /// <summary>VirusTotal verdict text for the list ("Clean · 0/72", "3/72 flagged",
    /// "Checking…", "Not on VirusTotal"); empty when no API key / no hash.</summary>
    public string VtText
    {
        get => _vtText;
        set { if (!Equals(_vtText, value)) { _vtText = value; Changed(nameof(VtText)); } }
    }
    private string _vtText = "";

    /// <summary>How many services this executable currently hosts across all of
    /// its processes, e.g. "hosts 14 services". Empty for ordinary programs.</summary>
    public string ServicesSummary
    {
        get => _servicesSummary;
        set { if (!Equals(_servicesSummary, value)) { _servicesSummary = value; Changed(nameof(ServicesSummary)); } }
    }
    private string _servicesSummary = "";

    /// <summary>Full hosted-service list for the tooltip.</summary>
    public string ServicesDetail
    {
        get => _servicesDetail;
        set { if (!Equals(_servicesDetail, value)) { _servicesDetail = value; Changed(nameof(ServicesDetail)); } }
    }
    private string _servicesDetail = "";

    /// <summary>Pre-scaled sparkline of this app's last-30-minutes traffic
    /// (points in a 90x20 box); recomputed on every list rebuild. A PointCollection,
    /// typed object for the same reason as Icon.</summary>
    public object? Spark
    {
        get => _spark;
        set { if (!object.ReferenceEquals(_spark, value)) { _spark = value; Changed(nameof(Spark)); } }
    }
    private object? _spark;

    /// <summary>Tooltip for the sparkline ("2.4 MB in the last 30 min").</summary>
    public string SparkTip
    {
        get => _sparkTip;
        set { if (_sparkTip != value) { _sparkTip = value; Changed(nameof(SparkTip)); } }
    }
    private string _sparkTip = "";

    /// <summary>Coloring level for the verdict: "clean", "flagged", "pending", "none".</summary>
    public string VtLevel
    {
        get => _vtLevel;
        set { if (!Equals(_vtLevel, value)) { _vtLevel = value; Changed(nameof(VtLevel)); } }
    }
    private string _vtLevel = "";

    /// <summary>Allowed but not notified about.</summary>
    public bool Silent
    {
        get => _silent;
        set { if (!Equals(_silent, value)) { _silent = value; Changed(nameof(Silent)); } }
    }
    private bool _silent;

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

    /// <summary>Exempt this application from KERNEL-level domain blocking.
    ///
    /// Deliberately narrow, and the narrowness is not a shortcut. Blocklists
    /// enforce on three surfaces and only one can know which application is
    /// asking:
    ///
    ///  - kernel address blocks, built from a connection that carries its own
    ///    executable path - the one that can be exempted;
    ///  - the DNS resolver's NXDOMAIN, which sees a UDP packet from 127.0.0.1.
    ///    Attributing that socket to a process yields svchost.exe, the DNS Client
    ///    service, for every application on the machine;
    ///  - the hosts file, which is machine-wide by construction.
    ///
    /// So an exempt application still receives NXDOMAIN for a blocked name. What
    /// it no longer gets is the address block that severs the connection, which
    /// is the half that actually breaks things.
    ///
    /// Defaults false, so every profile written before this existed behaves
    /// exactly as it did.</summary>
    public bool BypassBlocklists { get; set; }

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

/// <summary>§1: one rule in an app's ordered access policy. Action is "allow"
/// or "block"; EntityType is any | ip | cidr | scope | country | continent |
/// asn; Value is the entity value (e.g. "RU", "10.0.0.0/8", "internet").</summary>
public sealed class AppAccessRule
{
    public string Action { get; set; } = "block";      // allow | block
    public string EntityType { get; set; } = "any";    // any|ip|cidr|scope|country|continent|asn
    public string Value { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public string EntityLabel => EntityType switch
    {
        "any" => "any destination",
        "ip" => "IP " + Value,
        "cidr" => "range " + Value,
        "scope" => Value switch { "internet" => "the Internet", "lan" => "the LAN",
                                  "local" => "device-local", _ => "scope " + Value },
        "country" => "country " + Value.ToUpperInvariant(),
        "continent" => "continent " + Value.ToUpperInvariant(),
        "asn" => "AS" + AppRuleEngineValue,
        "domain" => "domain " + Value + " (and subdomains)",
        _ => EntityType + " " + Value
    };

    // AS-number without a duplicated "AS" prefix, for the label.
    private string AppRuleEngineValue =>
        Value.StartsWith("AS", StringComparison.OrdinalIgnoreCase) ? Value[2..] : Value;

    public string Summary => $"{(Enabled ? "" : "(off) ")}" +
        $"{(Action == "block" ? "Block" : "Allow")} {EntityLabel}";
}

/// <summary>§1: an app's ordered access policy — rules evaluated first-match-
/// wins, with a default action when nothing matches. DefaultBlock=false keeps
/// GunWall's existing "allowed app may reach anything" behaviour.</summary>
public sealed class AppAccessPolicy
{
    public string AppPath { get; set; } = "";
    public List<AppAccessRule> Rules { get; set; } = new();
    public bool DefaultBlock { get; set; }

    /// <summary>True if this policy could ever block something — i.e. it has at
    /// least one enabled block rule, or a default-block with any allow rule.
    /// Used to skip evaluation entirely for inert policies.</summary>
    public bool IsActive =>
        DefaultBlock || Rules.Any(r => r.Enabled && r.Action == "block");
}

/// <summary>§13: one entry in the in-app notification center. Kind drives the
/// title color: "warn" (red), "good" (green), or "info" (default).</summary>
public sealed class AppNotification
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } = "info";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string TimeText => Time.ToString("HH:mm:ss");
}

