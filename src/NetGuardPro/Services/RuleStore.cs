using System.IO;
using System.Text.Json;
using NetGuardPro.Models;

namespace NetGuardPro.Services;

/// <summary>
/// Persists firewall rules and lockdown state to a JSON file under
/// %ProgramData%\NetGuardPro. State is machine-wide because WFP filters are
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
            "NetGuardPro");
        _file = Path.Combine(_dir, "rules.json");
    }

    public StoreData Load()
    {
        lock (_gate)
        {
            try
            {
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

/// <summary>Everything NetGuard Pro persists between runs.</summary>
public sealed class StoreData
{
    public List<FirewallRule> Rules { get; set; } = new();
    public bool LockdownEngaged { get; set; }
    public List<ulong> LockdownFilterIds { get; set; } = new();
}
