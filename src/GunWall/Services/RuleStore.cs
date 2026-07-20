using System.IO;
using System.Text.Json;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Persists firewall rules and lockdown state to a JSON file under
/// %ProgramData%\GunWall. State is machine-wide because WFP filters are
/// machine-wide. No data is transmitted anywhere — this is a local file only.
/// </summary>
public sealed class RuleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true
    };

    private readonly string _dir;
    private readonly string _file;
    private readonly object _gate = new();

    public RuleStore()
    {
        // Save the profile in the application's own folder (portable) so the
        // user's allow/block choices live alongside GunWall. If that folder
        // isn't writable (e.g. installed read-only), fall back to ProgramData.
        string appDir = AppContext.BaseDirectory;
        string portableDir = Path.Combine(appDir, "GunWallData");

        if (IsWritable(appDir))
        {
            _dir = portableDir;
        }
        else
        {
            _dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GunWall");
        }
        _file = Path.Combine(_dir, "rules.json");
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".gw_write_test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public StoreData Load()
    {
        lock (_gate)
        {
            try
            {
                MigrateFromLegacyLocation();
                if (!File.Exists(_file)) return new StoreData();
                string json = File.ReadAllText(_file);
                return JsonSerializer.Deserialize<StoreData>(json) ?? new StoreData();
            }
            catch
            {
                // Corrupt or unreadable store: start clean rather than crashing.
                return new StoreData();
            }
        }
    }

    /// <summary>
    /// One-time migration: earlier alphas stored rules under
    /// %ProgramData%\NetGuardPro. If that file exists and ours doesn't yet,
    /// adopt it so users keep their rules across the rename.
    /// </summary>
    private void MigrateFromLegacyLocation()
    {
        if (File.Exists(_file)) return;
        Directory.CreateDirectory(_dir);

        // Prefer a prior GunWall profile in ProgramData (previous releases),
        // then fall back to the original NetGuardPro location.
        string[] legacies =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "GunWall", "rules.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "NetGuardPro", "rules.json"),
        };
        foreach (var legacy in legacies)
        {
            if (legacy.Equals(_file, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(legacy))
            {
                try { File.Copy(legacy, _file, overwrite: false); } catch { }
                return;
            }
        }
    }

    /// <summary>The folder where the user's profile is stored (shown in Settings).</summary>
    public string ProfileFolder => _dir;

    public void Save(StoreData data)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            string json = JsonSerializer.Serialize(data, JsonOpts);
            // Write to a temp file then move, so a crash mid-write can't corrupt state.
            string tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Copy(tmp, _file, overwrite: true);
            File.Delete(tmp);
        }
    }

    /// <summary>Writes the full profile (rules + settings) to a chosen file.</summary>
    public void Export(StoreData data, string filePath)
    {
        string json = JsonSerializer.Serialize(data, JsonOpts);
        File.WriteAllText(filePath, json);
    }

    /// <summary>Reads a profile from a chosen file. Throws on malformed input.</summary>
    public StoreData Import(string filePath)
    {
        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<StoreData>(json)
               ?? throw new InvalidDataException("Profile file is empty or invalid.");
    }
}

/// <summary>Everything GunWall persists between runs.</summary>
public sealed class StoreData
{
    public List<FirewallRule> Rules { get; set; } = new();
    public bool LockdownEngaged { get; set; }
    public List<ulong> LockdownFilterIds { get; set; } = new();

    /// <summary>Executables already seen once — no alert popup for these.</summary>
    public List<string> KnownApps { get; set; } = new();

    /// <summary>Whether the new-connection alert popup is enabled.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>Strict (whitelist) mode: block everything except allowed apps.</summary>
    public bool StrictMode { get; set; }
    public List<ulong> StrictFilterIds { get; set; } = new();

    /// <summary>Filter IDs of the always-on permit for GunWall's own executable,
    /// so its update/list/VirusTotal traffic isn't blocked by its own block-all.</summary>
    public List<ulong> SelfFilterIds { get; set; } = new();

    /// <summary>User-customised category dot colors (key -> hex). Empty = defaults.</summary>
    public Dictionary<string, string> CategoryColors { get; set; } = new();

    /// <summary>Free-text notes per executable path (key -> note).</summary>
    public Dictionary<string, string> AppNotes { get; set; } = new();

    /// <summary>UI/behaviour preferences.</summary>
    public bool StartMinimized { get; set; }
    public bool AlwaysOnTop { get; set; }
    public bool HashesEnabled { get; set; } = true;

    /// <summary>
    /// Kernel event-driven detection. ON by default so GunWall detects every
    /// connection (including blocked/ICMP) like a mature firewall. Protected by
    /// a crash-loop guard that auto-disables it if a session ends uncleanly.
    /// </summary>
    public bool ExperimentalEvents { get; set; } = true;

    /// <summary>User-defined address/port/protocol rules.</summary>
    public List<CustomRule> CustomRules { get; set; } = new();

    /// <summary>Blocked IPs (simple address blocklist).</summary>
    public List<string> Blocklist { get; set; } = new();
    public List<ulong> BlocklistFilterIds { get; set; } = new();

    /// <summary>Run GunWall when Windows starts.</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>UI theme: true = dark, false = light.</summary>
    public bool ThemeDark { get; set; } = true;

    /// <summary>VirusTotal API key (optional, user-provided).</summary>
    public string VirusTotalApiKey { get; set; } = "";

    /// <summary>System hardening rules: key -> filter IDs (presence = enabled).</summary>
    public Dictionary<string, List<ulong>> SystemRules { get; set; } = new();

    /// <summary>Send firewall events to the Windows Event Log.</summary>
    public bool EventLogEnabled { get; set; }

    /// <summary>Temporary blocks: exe path (lowercase) -> UTC expiry time.</summary>
    public Dictionary<string, DateTime> TempBlocks { get; set; } = new();

    /// <summary>Write each packet-log entry to a CSV file in the profile folder.</summary>
    public bool PacketFileLogging { get; set; }

    /// <summary>Precise per-app byte metering via an ETW kernel-network
    /// session (experimental; approximation is the fallback). Off by default.</summary>
    public bool EtwMeterEnabled { get; set; }

    /// <summary>Apps with the reactive "Block P2P / direct connections" scope
    /// enabled (lower-cased executable paths).</summary>
    public List<string> P2pApps { get; set; } = new();

    /// <summary>Play a sound when a notification popup appears.</summary>
    public bool NotificationSound { get; set; }

    /// <summary>Show a tray balloon when a new app is detected.</summary>
    public bool TrayNotifications { get; set; }

    /// <summary>Seconds before a popup auto-decides; 0 = never (stays open).</summary>
    public int PopupTimeoutSeconds { get; set; } = 20;

    /// <summary>On popup timeout: true = allow, false = block.</summary>
    public bool PopupDefaultAllow { get; set; } = true;

    /// <summary>Suppress new-app approval popups while a fullscreen app/game is foreground.</summary>
    public bool FullscreenSilent { get; set; }

    /// <summary>Ask for confirmation before clearing the Activity / Packets logs.</summary>
    public bool ConfirmClearLogs { get; set; } = true;

    /// <summary>Always confirm before exiting, even when the firewall is not active.</summary>
    public bool AlwaysConfirmExit { get; set; }

    /// <summary>Cap on in-memory Activity / Packets rows (0 = unlimited).</summary>
    public int MaxLogEntries { get; set; } = 1000;

    /// <summary>Packets CSV file rotation threshold, in megabytes.</summary>
    public int MaxLogFileMB { get; set; } = 5;

    /// <summary>Keep apps with no rule and no active connections in the Apps list.</summary>
    public bool KeepUnusedApps { get; set; } = true;

    /// <summary>Per-app network-scope blocks. Key is "{lowercased exe path}|{scope}"
    /// (scope = local | lan | incoming); value is the installed WFP filter ids.</summary>
    public Dictionary<string, List<ulong>> ScopeFilters { get; set; } = new();

    /// <summary>Automatically save a timestamped backup of the profile on changes.</summary>
    public bool AutoBackup { get; set; }

    /// <summary>Curated blocklist categories that are on: key -> filter IDs. (Legacy v0.24, migrated on load.)</summary>
    public Dictionary<string, List<ulong>> Blocklists { get; set; } = new();

    /// <summary>Curated blocklist category keys that are enabled (hosts-file based).</summary>
    public List<string> EnabledBlocklists { get; set; } = new();

    /// <summary>Categories enforced via WFP IP filters instead of the hosts file
    /// (used automatically when security software blocks the hosts write):
    /// key -> filter IDs.</summary>
    public Dictionary<string, List<ulong>> BlocklistWfpFilters { get; set; } = new();

    /// <summary>Selected filtering-DNS provider key ("auto" = network default).</summary>
    public string DnsProvider { get; set; } = "auto";

    // §5 filter-list engine: a user-pointed local domain list (one domain per line,
    // or hosts-style "0.0.0.0 domain"); GunWall watches + folds it into the block.
    public string CustomBlocklistPath { get; set; } = "";

    // §1 entity rule engine: GeoIP-keyed block rules (country / continent / ASN),
    // matched reactively per connection. EntityReactiveFilters holds the WFP filter
    // IDs of the per-app remote-IP blocks they spawned this session, so they can be
    // torn down deterministically (cleared on startup for a clean slate).
    public List<EntityRule> EntityRules { get; set; } = new();
    public List<ulong> EntityReactiveFilters { get; set; } = new();

    // GeoIP data source: "local" downloads the IPv4 table into GunWall; "api" queries
    // a self-hosted iptoasn-webservice (no download, always fresh, resolves IPv6).
    // Default "local" preserves existing behaviour for upgrades.
    public string GeoIpMode { get; set; } = "local";
    public string GeoIpApiUrl { get; set; } = "";

    // §3 local DNS resolver: GunWall's OWN resolver, bound to 127.0.0.1. Logs every
    // lookup, blocks the listed domains, caches by TTL. It never changes the system
    // DNS by itself (a guided redirect / "Gaming Session" toggle is the next phase),
    // so these are just its saved settings — it only runs when the user starts it.
    // §VT automatic reputation checks: cached VirusTotal verdicts keyed by SHA-256,
    // so each unique file is looked up once and results survive restarts.
    public Dictionary<string, VtCacheEntry> VtCache { get; set; } = new();

    // §10 rule profiles: named snapshots of per-app allow/block rules
    // (profile name -> exePath -> "Status|DisplayName").
    public Dictionary<string, Dictionary<string, string>> RuleProfiles { get; set; } = new();
    public string ActiveProfile { get; set; } = "";

    public int DnsResolverPort { get; set; } = 53;
    public string DnsResolverUpstream { get; set; } = "1.1.1.1";
    public List<string> DnsResolverBlocklist { get; set; } = new();

    // §3 Phase 2: system-DNS routing state. DnsRedirectActive is the user's saved
    // intent (re-applied on launch); DnsGamingSession bypasses the redirect without
    // losing it; DnsSavedAdapters is what to put back (captured before we touch DNS).
    public bool DnsRedirectActive { get; set; }
    public bool DnsGamingSession { get; set; }
    public List<SavedAdapterDns> DnsSavedAdapters { get; set; } = new();
}

/// <summary>One adapter's pre-redirect IPv4 DNS setting, so it can be restored
/// exactly: DHCP-assigned, or its original static server list.</summary>
public sealed class SavedAdapterDns
{
    public string Name { get; set; } = "";
    public bool WasDhcp { get; set; }
    public List<string> Servers { get; set; } = new();
}

/// <summary>One cached VirusTotal verdict for a file hash. Found=false means the
/// hash is unknown to VirusTotal (also cached, so we don't ask again).</summary>
public sealed class VtCacheEntry
{
    public bool Found { get; set; }
    public int Flagged { get; set; }
    public int Total { get; set; }
    public DateTime CheckedUtc { get; set; }
}
