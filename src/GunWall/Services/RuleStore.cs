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
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GunWall");
        _file = Path.Combine(_dir, "rules.json");
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
        string legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetGuardPro", "rules.json");
        if (!File.Exists(legacy)) return;
        Directory.CreateDirectory(_dir);
        File.Copy(legacy, _file, overwrite: false);
    }

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
}
