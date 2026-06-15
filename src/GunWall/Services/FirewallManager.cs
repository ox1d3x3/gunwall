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

    /// <summary>
    /// Blocks an app in one direction only (outbound or inbound), leaving the
    /// other direction untouched. Recorded as a normal blocked rule so it shows
    /// in the Apps list and persists.
    /// </summary>
    public void BlockAppDirection(string exePath, string displayName, bool outbound)
    {
        RemoveAllowRule(exePath);
        // Remove any existing full block first so directions don't stack oddly.
        var existing = _data.Rules.FirstOrDefault(r =>
            string.Equals(r.ExecutablePath, exePath, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { try { _engine.RemoveFilters(existing.FilterIds); } catch { } _data.Rules.Remove(existing); }

        var ids = _engine.BlockApplicationDirectional(exePath, outbound);
        _data.Rules.Add(new FirewallRule
        {
            ExecutablePath = exePath,
            DisplayName = displayName + (outbound ? " (outbound blocked)" : " (inbound blocked)"),
            Status = AppStatus.Blocked,
            FilterIds = ids,
            Hash = _data.HashesEnabled ? HashService.Compute(exePath) : ""
        });
        EventLog($"Blocked {(outbound ? "outbound" : "inbound")}: {displayName}");
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
                rule.Block, rule.Outbound, rule.Protocol, rule.RemoteAddress, rule.RemotePort, rule.LocalPort);
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

            // Accept a single IPv4 or an IPv4/prefix subnet. Both apply via the
            // conditioned-filter address+mask path. Non-IP entries are stored
            // but not filtered (kept for a future DNS-resolving blocklist).
            string ipPart = ip.Contains('/') ? ip[..ip.IndexOf('/')] : ip;
            if (System.Net.IPAddress.TryParse(ipPart, out var parsed) &&
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

    public string VirusTotalApiKey => _data.VirusTotalApiKey;
    public void SetVirusTotalApiKey(string v) { _data.VirusTotalApiKey = v?.Trim() ?? ""; _store.Save(_data); }

    // ------------------------------------------------ system rules
    public bool IsSystemRuleOn(string key) =>
        _data.SystemRules.TryGetValue(key, out var ids) && ids.Count > 0;

    public void SetSystemRule(string key, bool enabled)
    {
        if (enabled)
        {
            if (IsSystemRuleOn(key)) return;
            var preset = Models.SystemRuleCatalog.All.FirstOrDefault(p => p.Key == key);
            List<ulong> ids;
            if (preset == null || preset.Special)
                ids = _engine.AddSystemRule(key);   // special handling (block-all / IPv6)
            else
                ids = _engine.AddServiceRule(preset.Block, preset.Direction, preset.Protocol, preset.Ports, preset.Name);
            _data.SystemRules[key] = ids;
            EventLog($"System rule enabled: {key}");
        }
        else
        {
            if (_data.SystemRules.TryGetValue(key, out var ids))
            {
                try { _engine.RemoveFilters(ids); } catch { }
                _data.SystemRules.Remove(key);
                EventLog($"System rule disabled: {key}");
            }
        }
        _store.Save(_data);
    }

    // ------------------------------------------------ event log
    public bool EventLogEnabled => _data.EventLogEnabled;
    public void SetEventLogEnabled(bool v) { _data.EventLogEnabled = v; _store.Save(_data); }

    // ------------------------------------------------ packet file logging
    private PacketLogFile? _packetLog;
    public bool PacketFileLogging => _data.PacketFileLogging;
    public void SetPacketFileLogging(bool v) { _data.PacketFileLogging = v; _store.Save(_data); }

    /// <summary>Writes one packet entry to the CSV log if file logging is on.</summary>
    public void LogPacketToFile(DateTime time, bool blocked, string app, string protocol,
                                string direction, string remote, string exePath)
    {
        if (!_data.PacketFileLogging) return;
        _packetLog ??= new PacketLogFile(_store.ProfileFolder);
        _packetLog.Append(time, blocked ? "Blocked" : "Allowed", app, protocol, direction, remote, exePath);
    }

    public string PacketLogPath => System.IO.Path.Combine(_store.ProfileFolder, "packets.csv");

    // ------------------------------------------------ notification options
    public bool NotificationSound => _data.NotificationSound;
    public void SetNotificationSound(bool v) { _data.NotificationSound = v; _store.Save(_data); }
    public bool TrayNotifications => _data.TrayNotifications;
    public void SetTrayNotifications(bool v) { _data.TrayNotifications = v; _store.Save(_data); }

    public int PopupTimeoutSeconds => _data.PopupTimeoutSeconds;
    public void SetPopupTimeoutSeconds(int v) { _data.PopupTimeoutSeconds = v < 0 ? 0 : v; _store.Save(_data); }
    public bool PopupDefaultAllow => _data.PopupDefaultAllow;
    public void SetPopupDefaultAllow(bool v) { _data.PopupDefaultAllow = v; _store.Save(_data); }

    /// <summary>Writes to the Windows Event Log if the user enabled it.</summary>
    public void EventLog(string message)
    {
        if (_data.EventLogEnabled) EventLogService.Write("GunWall: " + message);
    }

    // ------------------------------------------------ temporary (timed) rules
    private readonly Dictionary<string, System.Threading.Timer> _tempTimers = new();

    /// <summary>
    /// Blocks an app now and automatically unblocks it after the given duration.
    /// In-memory only — a restart cancels pending reverts (the block persists
    /// until manually changed). Returns the revert time.
    /// </summary>
    public DateTime BlockAppTemporarily(string exePath, string displayName, TimeSpan duration)
    {
        BlockApp(exePath, displayName);
        EventLog($"Temporary block for {duration.TotalMinutes:0} min: {displayName}");

        string key = exePath.ToLowerInvariant();
        DateTime expiryUtc = DateTime.UtcNow.Add(duration);
        _data.TempBlocks[key] = expiryUtc;   // persist so it survives a restart
        _store.Save(_data);

        ArmTempTimer(key, exePath, displayName, duration);
        return DateTime.Now.Add(duration);
    }

    private void ArmTempTimer(string key, string exePath, string displayName, TimeSpan duration)
    {
        if (_tempTimers.TryGetValue(key, out var existing)) { existing.Dispose(); _tempTimers.Remove(key); }
        // Cap the timer to a sane max; if duration is negative it fires immediately.
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        var timer = new System.Threading.Timer(_ =>
        {
            try { UnblockApp(exePath); EventLog($"Temporary block expired: {displayName}"); }
            catch { }
            finally
            {
                _data.TempBlocks.Remove(key);
                try { _store.Save(_data); } catch { }
                if (_tempTimers.TryGetValue(key, out var t)) { t.Dispose(); _tempTimers.Remove(key); }
            }
        }, null, duration, System.Threading.Timeout.InfiniteTimeSpan);
        _tempTimers[key] = timer;
    }

    /// <summary>
    /// On startup, reconcile persisted temporary blocks: any that have already
    /// expired are unblocked now; the rest get their timers re-armed for the
    /// remaining time. This makes timed rules survive a restart.
    /// </summary>
    public void ReconcileTempBlocks()
    {
        if (_data.TempBlocks.Count == 0) return;
        var now = DateTime.UtcNow;
        foreach (var kv in new Dictionary<string, DateTime>(_data.TempBlocks))
        {
            string key = kv.Key;
            DateTime expiry = kv.Value;
            // Find the matching rule's display name/path if we still have it.
            var rule = _data.Rules.FirstOrDefault(r =>
                r.ExecutablePath.Equals(key, StringComparison.OrdinalIgnoreCase));
            string path = rule?.ExecutablePath ?? key;
            string name = rule?.DisplayName ?? System.IO.Path.GetFileName(key);

            if (expiry <= now)
            {
                try { UnblockApp(path); } catch { }
                _data.TempBlocks.Remove(key);
            }
            else
            {
                ArmTempTimer(key, path, name, expiry - now);
            }
        }
        _store.Save(_data);
    }

    // ------------------------------------------------ snooze (pause protection)
    private System.Threading.Timer? _snoozeTimer;
    private bool _wasStrictBeforeSnooze;
    public bool IsSnoozed { get; private set; }
    public DateTime SnoozeUntil { get; private set; }

    /// <summary>
    /// Temporarily lifts strict-mode blocking for the given duration, then
    /// automatically restores it. In-memory only — closing GunWall ends the
    /// snooze and protection comes back on next launch (the safe default).
    /// Returns the time protection resumes.
    /// </summary>
    public DateTime SnoozeProtection(TimeSpan duration)
    {
        if (!IsSnoozed)
        {
            _wasStrictBeforeSnooze = StrictMode;
            if (StrictMode) SetStrictMode(false);
        }
        IsSnoozed = true;
        SnoozeUntil = DateTime.Now.Add(duration);
        EventLog($"Protection snoozed for {duration.TotalMinutes:0} min");

        _snoozeTimer?.Dispose();
        _snoozeTimer = new System.Threading.Timer(_ => EndSnooze(), null, duration,
            System.Threading.Timeout.InfiniteTimeSpan);
        return SnoozeUntil;
    }

    /// <summary>Ends a snooze early and restores the prior protection state.</summary>
    public void EndSnooze()
    {
        if (!IsSnoozed) return;
        try { if (_wasStrictBeforeSnooze && !StrictMode) SetStrictMode(true); }
        catch { }
        IsSnoozed = false;
        _snoozeTimer?.Dispose();
        _snoozeTimer = null;
        EventLog("Protection resumed");
    }

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

    // ------------------------------------------------ named profiles
    // Profiles are named snapshots of the whole rule set, stored as JSON files
    // in a "profiles" subfolder. Switching a profile imports it as the active
    // configuration. This builds on the same serialization as export/import.

    private string ProfilesFolder
    {
        get
        {
            string dir = System.IO.Path.Combine(_store.ProfileFolder, "profiles");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    public List<string> ListProfiles()
    {
        var names = new List<string>();
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(ProfilesFolder, "*.json"))
                names.Add(System.IO.Path.GetFileNameWithoutExtension(f));
        }
        catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>Saves the current configuration as a named profile.</summary>
    public void SaveProfile(string name)
    {
        string safe = SanitizeName(name);
        if (string.IsNullOrEmpty(safe)) throw new ArgumentException("Enter a valid profile name.");
        _store.Export(_data, System.IO.Path.Combine(ProfilesFolder, safe + ".json"));
    }

    /// <summary>Loads a named profile as the active configuration.</summary>
    public int LoadProfile(string name)
    {
        string path = System.IO.Path.Combine(ProfilesFolder, SanitizeName(name) + ".json");
        if (!System.IO.File.Exists(path))
            throw new System.IO.FileNotFoundException("That profile no longer exists.");
        return ImportProfile(path);
    }

    public void DeleteProfile(string name)
    {
        try
        {
            string path = System.IO.Path.Combine(ProfilesFolder, SanitizeName(name) + ".json");
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch { }
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    // ------------------------------------------------ auto-backup / versioned profiles
    private const int MaxBackups = 15;

    public bool AutoBackup => _data.AutoBackup;
    public void SetAutoBackup(bool v) { _data.AutoBackup = v; _store.Save(_data); }

    // ------------------------------------------------ curated blocklists (telemetry/update/ads)
    public bool IsBlocklistOn(string key) =>
        _data.Blocklists.TryGetValue(key, out var ids) && ids.Count > 0;

    public int BlocklistFilterCount(string key) =>
        _data.Blocklists.TryGetValue(key, out var ids) ? ids.Count : 0;

    /// <summary>
    /// Resolves a category's hostnames to current IPv4 addresses and blocks them
    /// outbound (plus any literal CIDRs). Persistent filters, so they survive a
    /// reboot. Runs DNS + filter creation, so call it off the UI thread. Returns
    /// the number of distinct addresses blocked.
    /// </summary>
    public int EnableBlocklist(Models.BlocklistCategory cat)
    {
        if (IsBlocklistOn(cat.Key)) return BlocklistFilterCount(cat.Key);

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cidr in cat.Cidrs) targets.Add(cidr);
        foreach (var host in cat.Hosts)
        {
            try
            {
                foreach (var addr in System.Net.Dns.GetHostAddresses(host))
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        targets.Add(addr.ToString());
            }
            catch { /* unresolvable host — skip it */ }
        }

        var ids = new List<ulong>();
        foreach (var ip in targets)
        {
            try { ids.AddRange(_engine.AddCustomRule(true, true, "Any", ip, 0)); } // block outbound
            catch { /* skip a bad entry, keep going */ }
        }

        _data.Blocklists[cat.Key] = ids;
        _store.Save(_data);
        EventLog($"Blocklist enabled: {cat.Name} ({targets.Count} addresses)");
        return targets.Count;
    }

    public void DisableBlocklist(string key)
    {
        if (_data.Blocklists.TryGetValue(key, out var ids))
        {
            try { _engine.RemoveFilters(ids); } catch { }
            _data.Blocklists.Remove(key);
            _store.Save(_data);
            EventLog($"Blocklist disabled: {key}");
        }
    }

    // ------------------------------------------------ Windows Firewall import
    /// <summary>
    /// Imports BLOCK rules from Windows Defender Firewall as GunWall blocks
    /// (allow rules are skipped — GunWall allows by default). Returns the count
    /// of programs newly blocked.
    /// </summary>
    public int ImportWindowsFirewallRules()
    {
        int added = 0;
        foreach (var r in WindowsFirewallService.GetAppRules())
        {
            if (!r.Block) continue;                       // only import blocks
            if (string.IsNullOrEmpty(r.AppPath)) continue;
            string path = Environment.ExpandEnvironmentVariables(r.AppPath);
            if (!System.IO.File.Exists(path)) continue;
            if (IsBlocked(path)) continue;                // already blocked here
            try
            {
                BlockApp(path, System.IO.Path.GetFileName(path));
                added++;
            }
            catch { /* skip a problematic rule, keep importing */ }
        }
        if (added > 0) { EventLog($"Imported {added} block rule(s) from Windows Firewall"); AutoBackupIfEnabled(); }
        return added;
    }

    // ------------------------------------------------ critical-process guard
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "svchost.exe", "fontdrvhost.exe",
        "dwm.exe", "spoolsv.exe", "explorer.exe", "ntoskrnl.exe",
        "lsm.exe", "conhost.exe", "RuntimeBroker.exe", "sihost.exe"
    };

    /// <summary>True if blocking this executable could destabilise Windows.</summary>
    public static bool IsCriticalProcess(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        string name = System.IO.Path.GetFileName(exePath);
        return CriticalProcesses.Contains(name);
    }

    private string BackupsFolder
    {
        get
        {
            string dir = System.IO.Path.Combine(_store.ProfileFolder, "backups");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            return dir;
        }
    }

    /// <summary>Writes a timestamped backup of the current profile and prunes old ones.</summary>
    public string CreateBackup()
    {
        string name = "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = System.IO.Path.Combine(BackupsFolder, name + ".json");
        _store.Export(_data, path);
        PruneBackups();
        return name;
    }

    /// <summary>Called after meaningful changes when auto-backup is on (best-effort).</summary>
    public void AutoBackupIfEnabled()
    {
        if (!_data.AutoBackup) return;
        try { CreateBackup(); } catch { }
    }

    /// <summary>Backups newest-first, as (name, timestamp) pairs.</summary>
    public List<(string Name, DateTime When)> ListBackups()
    {
        var list = new List<(string, DateTime)>();
        try
        {
            foreach (var f in System.IO.Directory.GetFiles(BackupsFolder, "backup_*.json"))
            {
                var fi = new System.IO.FileInfo(f);
                list.Add((System.IO.Path.GetFileNameWithoutExtension(f), fi.LastWriteTime));
            }
        }
        catch { }
        list.Sort((a, b) => b.Item2.CompareTo(a.Item2)); // newest first
        return list;
    }

    /// <summary>Restores a backup as the active configuration.</summary>
    public int RestoreBackup(string name)
    {
        string path = System.IO.Path.Combine(BackupsFolder, SanitizeName(name) + ".json");
        if (!System.IO.File.Exists(path))
            throw new System.IO.FileNotFoundException("That backup no longer exists.");
        return ImportProfile(path);
    }

    private void PruneBackups()
    {
        try
        {
            var files = new List<System.IO.FileInfo>();
            foreach (var f in System.IO.Directory.GetFiles(BackupsFolder, "backup_*.json"))
                files.Add(new System.IO.FileInfo(f));
            files.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
            for (int i = MaxBackups; i < files.Count; i++)
                try { files[i].Delete(); } catch { }
        }
        catch { }
    }

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
