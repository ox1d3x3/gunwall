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
}
