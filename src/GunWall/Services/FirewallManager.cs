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

    /// <summary>WFP engine handle, for the kernel net-event monitor.</summary>
    public IntPtr EngineHandle => _engine.EngineHandle;
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
            FilterIds = ids,
            Hash = _data.HashesEnabled ? HashService.Compute(exePath) : ""
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
            FilterIds = ids,
            Hash = _data.HashesEnabled ? HashService.Compute(exePath) : ""
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
            // 1) Base block + loopback keep-alive (atomic transaction).
            _data.StrictFilterIds = _engine.EngageStrictMode();
            _data.StrictMode = true;

            // When taking full control, forget the "already seen" list so every
            // app must be approved or denied again — the whitelist starts fresh,
            // and the user gets a prompt the next time each app connects.
            _data.KnownApps.Clear();
            _knownSet = null;
            _store.Save(_data);

            // 2) Re-create permits for previously allowed apps.
            foreach (var rule in _data.Rules.Where(r => r.Status == AppStatus.Allowed))
            {
                try { rule.FilterIds = _engine.PermitApplication(rule.ExecutablePath); }
                catch { /* exe may be gone; rule stays recorded */ }
            }
            _store.Save(_data);

            // 3) Safety net: keep core Windows networking alive (DNS/DHCP live
            //    inside these system hosts). Permitting them by app-ID is the
            //    reliable way to keep the connection working in strict mode.
            foreach (var path in WfpEngine.CoreSystemApps())
            {
                try { AllowApp(path, System.IO.Path.GetFileNameWithoutExtension(path)); }
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

    // ------------------------------------------------ silent apps
    public bool IsSilent(string exePath) =>
        _data.Rules.Any(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase) && r.Silent);

    /// <summary>
    /// Marks an allowed app "silent": it stays allowed but never raises a popup.
    /// If the app has no rule yet, an allowed+silent rule is created.
    /// </summary>
    public void SetSilent(string exePath, string displayName, bool silent)
    {
        var rule = _data.Rules.FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
        {
            rule = new FirewallRule
            {
                ExecutablePath = exePath,
                DisplayName = displayName,
                Status = AppStatus.Allowed,
                Hash = _data.HashesEnabled ? HashService.Compute(exePath) : ""
            };
            _data.Rules.Add(rule);
        }
        rule.Silent = silent;
        _store.Save(_data);
    }

    /// <summary>Returns the stored hash for an app, or empty if none.</summary>
    public string GetHash(string exePath) =>
        _data.Rules.FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase))?.Hash ?? "";

    // ------------------------------------------------ settings
    public bool StartMinimized => _data.StartMinimized;
    public bool AlwaysOnTop => _data.AlwaysOnTop;
    public bool HashesEnabled => _data.HashesEnabled;
    public bool ExperimentalEvents => _data.ExperimentalEvents;

    /// <summary>Where the user's profile (rules + settings) is stored on disk.</summary>
    public string ProfileFolder => _store.ProfileFolder;

    public void SetStartMinimized(bool v) { _data.StartMinimized = v; _store.Save(_data); }
    public void SetAlwaysOnTop(bool v) { _data.AlwaysOnTop = v; _store.Save(_data); }
    public void SetHashesEnabled(bool v) { _data.HashesEnabled = v; _store.Save(_data); }
    public void SetExperimentalEvents(bool v) { _data.ExperimentalEvents = v; _store.Save(_data); }

    // ------------------------------------------------ custom rules
    public IReadOnlyList<CustomRule> CustomRules => _data.CustomRules;

    public void AddCustomRule(CustomRule rule)
    {
        if (rule.Enabled)
        {
            rule.FilterIds = _engine.AddCustomRule(
                rule.Block, rule.Outbound, rule.Protocol, rule.RemoteAddress, rule.RemotePort);
            rule.Applied = rule.FilterIds.Count > 0;
        }
        _data.CustomRules.Add(rule);
        _store.Save(_data);
    }

    public void RemoveCustomRule(string id)
    {
        var rule = _data.CustomRules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        try { _engine.RemoveFilters(rule.FilterIds); } catch { }
        _data.CustomRules.Remove(rule);
        _store.Save(_data);
    }

    // ------------------------------------------------ blocklist
    public IReadOnlyList<string> Blocklist => _data.Blocklist;

    /// <summary>Adds IPs to the blocklist and installs block filters for them.</summary>
    public int AddToBlocklist(IEnumerable<string> addresses)
    {
        int added = 0;
        foreach (var raw in addresses)
        {
            string ip = raw.Trim();
            if (string.IsNullOrEmpty(ip) || ip.StartsWith('#')) continue;
            if (_data.Blocklist.Contains(ip, StringComparer.OrdinalIgnoreCase)) continue;
            // Only IPv4 literals are filtered here; non-IP entries are stored but
            // not applied (kept for a future DNS-resolving blocklist).
            if (System.Net.IPAddress.TryParse(ip, out var parsed) &&
                parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var ids = _engine.AddCustomRule(true, true, "Any", ip, 0);
                _data.BlocklistFilterIds.AddRange(ids);
            }
            _data.Blocklist.Add(ip);
            added++;
        }
        _store.Save(_data);
        return added;
    }

    public void ClearBlocklist()
    {
        try { _engine.RemoveFilters(_data.BlocklistFilterIds); } catch { }
        _data.BlocklistFilterIds.Clear();
        _data.Blocklist.Clear();
        _store.Save(_data);
    }

    // ------------------------------------------------ startup
    public bool RunAtStartup => _data.RunAtStartup;
    public bool ThemeDark => _data.ThemeDark;
    public void SetThemeDark(bool v) { _data.ThemeDark = v; _store.Save(_data); }

    public void SetRunAtStartup(bool enabled)
    {
        bool ok = StartupService.SetEnabled(enabled);
        // Persist the user's intent regardless; reflect actual state if it failed.
        _data.RunAtStartup = enabled && ok;
        _store.Save(_data);
        if (!ok && enabled)
            throw new InvalidOperationException(
                "Could not register the startup task. Make sure GunWall is running as administrator.");
    }

    // ------------------------------------------------ profile export / import
    /// <summary>Exports all rules and settings to a portable file.</summary>
    public void ExportProfile(string filePath) => _store.Export(_data, filePath);

    /// <summary>
    /// Replaces current rules/settings with those from a file. Note: this loads
    /// the records; the live WFP filters are reconciled on next strict-mode
    /// toggle. Returns the number of rules imported.
    /// </summary>
    public int ImportProfile(string filePath)
    {
        var imported = _store.Import(filePath);
        _data = imported;
        _knownSet = null;
        _store.Save(_data);
        return _data.Rules.Count;
    }

    public void Dispose()
    {
        _engine.Dispose();
    }
}
