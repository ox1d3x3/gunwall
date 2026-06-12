using GunWall.Models;
using GunWall.Services.Wfp;

namespace GunWall.Services;

/// <summary>
/// The central firewall service used by the UI. Coordinates the WFP engine with
/// the persistent rule store, so that:
///  - Blocking/unblocking an app creates/removes the right WFP filters and saves
///    the change.
///  - Lockdown can be toggled and survives restarts.
///  - On startup, persisted state is reconciled (filters already live in WFP).
///
/// Privacy note: this class only ever acts on explicit user instructions. It
/// never blocks or allows anything on its own and never sends data anywhere.
/// </summary>
public sealed class FirewallManager : IDisposable
{
    private readonly WfpEngine _engine = new();
    private readonly RuleStore _store = new();
    private StoreData _data = new();

    public bool LockdownEngaged => _data.LockdownEngaged;
    public bool AlertsEnabled => _data.AlertsEnabled;
    public bool StrictMode => _data.StrictMode;

    private HashSet<string>? _knownSet;
    private HashSet<string> KnownSet =>
        _knownSet ??= new HashSet<string>(_data.KnownApps, StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens the WFP engine and loads persisted rules. Call once at startup.</summary>
    public void Initialize()
    {
        _engine.Initialize();
        _data = _store.Load();
        // Filters created in previous sessions are PERSISTENT, so they are already
        // active in WFP. We simply trust the saved record as the source of truth.
    }

    public IReadOnlyList<FirewallRule> GetRules() => _data.Rules.AsReadOnly();

    public bool IsBlocked(string exePath) =>
        _data.Rules.Any(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase) &&
            r.Status == AppStatus.Blocked);

    public bool IsAllowed(string exePath) =>
        _data.Rules.Any(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase) &&
            r.Status == AppStatus.Allowed);

    /// <summary>
    /// The user-facing status of an app under the current mode:
    /// explicit block always wins; in strict mode anything not allowed is blocked.
    /// </summary>
    public AppStatus EffectiveStatus(string exePath)
    {
        if (IsBlocked(exePath)) return AppStatus.Blocked;
        if (_data.StrictMode && !IsAllowed(exePath)) return AppStatus.Blocked;
        return AppStatus.Allowed;
    }

    /// <summary>
    /// Allows an application: removes any explicit block; in strict mode also
    /// creates persistent PERMIT filters and records the allow rule.
    /// </summary>
    public void AllowApp(string exePath, string displayName)
    {
        UnblockApp(exePath);
        if (!_data.StrictMode || IsAllowed(exePath)) return;

        var ids = _engine.PermitApplication(exePath);
        _data.Rules.Add(new FirewallRule
        {
            ExecutablePath = exePath,
            DisplayName = displayName,
            Status = AppStatus.Allowed,
            FilterIds = ids
        });
        _store.Save(_data);
    }

    private void RemoveAllowRule(string exePath)
    {
        var rule = _data.Rules.FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase) &&
            r.Status == AppStatus.Allowed);
        if (rule is null) return;
        _engine.RemoveFilters(rule.FilterIds);
        _data.Rules.Remove(rule);
        _store.Save(_data);
    }

    /// <summary>Blocks an application and persists the rule. Idempotent.</summary>
    public void BlockApp(string exePath, string displayName)
    {
        if (IsBlocked(exePath)) return;
        RemoveAllowRule(exePath); // an explicit block supersedes an allow

        var ids = _engine.BlockApplication(exePath);
        _data.Rules.RemoveAll(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
        _data.Rules.Add(new FirewallRule
        {
            ExecutablePath = exePath,
            DisplayName = displayName,
            Status = AppStatus.Blocked,
            FilterIds = ids
        });
        _store.Save(_data);
    }

    /// <summary>Removes the block for an application and persists. Idempotent.</summary>
    public void UnblockApp(string exePath)
    {
        var rule = _data.Rules.FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return;

        _engine.RemoveFilters(rule.FilterIds);
        _data.Rules.Remove(rule);
        _store.Save(_data);
    }

    /// <summary>Engages or releases global lockdown (block-all). Persisted.</summary>
    public void SetLockdown(bool engaged)
    {
        if (engaged == _data.LockdownEngaged) return;

        if (engaged)
        {
            _data.LockdownFilterIds = _engine.EngageLockdown();
            _data.LockdownEngaged = true;
        }
        else
        {
            _engine.RemoveFilters(_data.LockdownFilterIds);
            _data.LockdownFilterIds.Clear();
            _data.LockdownEngaged = false;
        }
        _store.Save(_data);
    }

    /// <summary>
    /// Removes every GunWall filter from the system and clears all saved
    /// rules. Use before uninstalling or to fully reset.
    /// </summary>
    public void RemoveAllFiltering()
    {
        _engine.RemoveAllFiltering();
        _data = new StoreData();
        _store.Save(_data);
    }

    // ------------------------------------------------ alerts / known apps

    public bool IsKnownApp(string exePath) => KnownSet.Contains(exePath);

    /// <summary>Marks one app as seen and persists. Returns true if it was new.</summary>
    public bool MarkKnown(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !KnownSet.Add(exePath)) return false;
        _data.KnownApps.Add(exePath);
        _store.Save(_data);
        return true;
    }

    /// <summary>Bulk-seeds known apps (first run) with a single save.</summary>
    public void SeedKnownApps(IEnumerable<string> exePaths)
    {
        bool changed = false;
        foreach (var p in exePaths)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (KnownSet.Add(p)) { _data.KnownApps.Add(p); changed = true; }
        }
        if (changed) _store.Save(_data);
    }

    /// <summary>
    /// Enables or disables strict (whitelist) mode. When enabling, core
    /// Windows networking services are auto-allowed so DNS/DHCP keep working —
    /// without this, strict mode would appear to "break the internet".
    /// </summary>
    public void SetStrictMode(bool enabled)
    {
        if (enabled == _data.StrictMode) return;

        if (enabled)
        {
            // 1) Base block + loopback keep-alive.
            _data.StrictFilterIds = _engine.EngageStrictMode();
            _data.StrictMode = true;
            _store.Save(_data);

            // 2) Re-create permits for previously allowed apps.
            foreach (var rule in _data.Rules.Where(r => r.Status == AppStatus.Allowed))
            {
                try { rule.FilterIds = _engine.PermitApplication(rule.ExecutablePath); }
                catch { /* exe may be gone; rule stays recorded */ }
            }
            _store.Save(_data);

            // 3) Safety net: keep core Windows networking alive.
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            foreach (var name in new[] { "svchost.exe", "services.exe", "lsass.exe" })
            {
                string path = System.IO.Path.Combine(sys32, name);
                try { if (System.IO.File.Exists(path)) AllowApp(path, name); }
                catch { /* best effort */ }
            }
        }
        else
        {
            _engine.RemoveFilters(_data.StrictFilterIds);
            _data.StrictFilterIds.Clear();
            foreach (var rule in _data.Rules.Where(r => r.Status == AppStatus.Allowed))
            {
                try { _engine.RemoveFilters(rule.FilterIds); } catch { }
                rule.FilterIds.Clear();
            }
            _data.StrictMode = false;
            _store.Save(_data);
        }
    }

    public void SetAlertsEnabled(bool enabled)
    {
        if (_data.AlertsEnabled == enabled) return;
        _data.AlertsEnabled = enabled;
        _store.Save(_data);
    }

    public void Dispose()
    {
        _engine.Dispose();
    }
}
