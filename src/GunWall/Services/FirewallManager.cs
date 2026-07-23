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
        try
        {
            _engine.Initialize();
            DiagnosticLog.Log($"WFP engine initialised (handle {(EngineHandle != IntPtr.Zero ? "valid" : "NULL")}).");
        }
        catch (Exception ex)
        {
            DiagnosticLog.LogException("WfpEngine.Initialize", ex);
            throw;
        }
        _data = _store.Load();
        LoadGeoIp();
        ReloadCustomList();
        ClearEntityReactiveBlocks(); // §1: drop last session's reactive geo-blocks; they re-form on demand
        // Filters created in previous sessions are PERSISTENT, so they are already
        // active in WFP. We simply trust the saved record as the source of truth.
    }

    public IReadOnlyList<FirewallRule> GetRules() => _data.Rules.AsReadOnly();

    // ============================================================= GeoIP (§4)
    // Read-only enrichment: remote IP -> country / ASN / owner. No enforcement.
    private readonly GeoIpService _geo = new();
    public GeoIpService GeoIp => _geo;
    public bool GeoIpLoaded => _geo.Loaded;
    public int GeoIpRangeCount => _geo.RangeCount;
    private string GeoIpCachePath => System.IO.Path.Combine(_store.ProfileFolder, "geoip-v4.tsv");

    // GeoIP source selection: "local" (downloaded table) or "api" (self-hosted server).
    public string GeoIpMode => _data.GeoIpMode == "api" ? "api" : "local";
    public string GeoIpApiUrl => _data.GeoIpApiUrl ?? "";
    public bool GeoIpApiActive => GeoIpMode == "api" && !string.IsNullOrWhiteSpace(GeoIpApiUrl);
    /// <summary>True when enrichment/matching can produce data (API active, or a local table loaded).</summary>
    public bool GeoIpActive => GeoIpApiActive || _geo.Loaded;

    public void LoadGeoIp()
    {
        if (GeoIpApiActive) { _geo.EnableApi(GeoIpApiUrl); return; }
        _geo.DisableApi();
        try { _geo.LoadFromFile(GeoIpCachePath); } catch { }
    }

    /// <summary>Switch the GeoIP source at runtime and persist the choice.</summary>
    public void SetGeoIpSource(string mode, string url)
    {
        _data.GeoIpMode = mode == "api" ? "api" : "local";
        _data.GeoIpApiUrl = (url ?? "").Trim();
        _store.Save(_data);
        LoadGeoIp(); // (re)configure the service for the new source
    }

    /// <summary>One-shot test of an API server URL (resolves 8.8.8.8). UI awaits this.</summary>
    public System.Threading.Tasks.Task<string> TestGeoIpApiAsync(string url) =>
        GeoIpService.TestApiAsync(url);

    // ===================================================== rule profiles (§10)
    // Named snapshots of the per-app allow/block rules. Applying a profile goes
    // through the same AllowApp/BlockApp paths the UI uses, so WFP filters are
    // torn down and recreated correctly. Apps not in the profile are untouched.
    public IEnumerable<string> RuleProfileNames => _data.RuleProfiles.Keys.OrderBy(k => k);
    public string ActiveRuleProfile => _data.ActiveProfile;

    /// <summary>Snapshot the current Allowed/Blocked app rules under a name.</summary>
    public void SaveRuleProfile(string name)
    {
        var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _data.Rules)
            if (r.Status is AppStatus.Allowed or AppStatus.Blocked)
                snap[r.ExecutablePath] = $"{r.Status}|{r.DisplayName}";
        _data.RuleProfiles[name] = snap;
        _store.Save(_data);
    }

    /// <summary>Apply a saved profile. Returns rules changed, or -1 if unknown.</summary>
    public int ApplyRuleProfile(string name)
    {
        if (!_data.RuleProfiles.TryGetValue(name, out var snap)) return -1;
        int changed = 0;
        foreach (var kv in snap)
        {
            int bar = kv.Value.IndexOf('|');
            if (bar <= 0) continue;
            string status = kv.Value[..bar], disp = kv.Value[(bar + 1)..];
            if (EffectiveStatus(kv.Key).ToString() == status) continue;
            if (status == "Allowed") AllowApp(kv.Key, disp);
            else if (status == "Blocked") BlockApp(kv.Key, disp);
            else continue;
            changed++;
        }
        _data.ActiveProfile = name;
        _store.Save(_data);
        return changed;
    }

    public void DeleteRuleProfile(string name)
    {
        if (_data.RuleProfiles.Remove(name))
        {
            if (_data.ActiveProfile == name) _data.ActiveProfile = "";
            _store.Save(_data);
        }
    }

    // ================================================== verdict reasons (§8)
    /// <summary>Why a connection was blocked or allowed, evaluated in the same
    /// precedence order the engine enforces: lockdown, app blocks (timed blocks
    /// labeled), custom rules, explicit allows, then the mode default. Derived
    /// from the same rule state the engine acts on, so the reason matches
    /// GunWall's own decision.</summary>
    public string ExplainVerdict(string exePath, string remoteAddress, int remotePort,
                                 string protocol, bool outbound, out bool blocked)
    {
        if (_data.LockdownEngaged) { blocked = true; return "Lockdown \u2014 all traffic blocked"; }

        if (IsBlocked(exePath))
        {
            blocked = true;
            if (_data.TempBlocks.TryGetValue(exePath, out var until) && until > DateTime.UtcNow)
                return $"Timed block \u2014 until {until.ToLocalTime():HH:mm}";
            return "App rule \u2014 Block";
        }

        foreach (var r in _data.CustomRules)   // first enabled match, filter order
        {
            if (!r.Enabled || !r.Applied) continue;
            if (r.Outbound != outbound) continue;
            if (r.Protocol is not ("Any" or "") &&
                !string.Equals(r.Protocol, protocol, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.RemotePort != 0 && r.RemotePort != remotePort) continue;
            if (!string.IsNullOrEmpty(r.RemoteAddress) &&
                !AddressMatches(r.RemoteAddress, remoteAddress)) continue;

            blocked = r.Block;
            string label = string.IsNullOrWhiteSpace(r.Name) ? r.TargetText : r.Name;
            return $"Custom rule \u2014 {(r.Block ? "Block" : "Allow")}: {label}";
        }

        if (IsSilent(exePath)) { blocked = false; return "Muted \u2014 allowed, no popups"; }
        if (IsAllowed(exePath)) { blocked = false; return "App rule \u2014 Allow"; }

        if (_data.StrictMode) { blocked = true; return "Zero-Trust \u2014 no allow rule yet"; }

        blocked = false;
        return "Monitor mode \u2014 allowed by default";
    }

    /// <summary>Exact match, or IPv4 CIDR containment when the rule uses "a.b.c.d/n".</summary>
    private static bool AddressMatches(string rule, string address)
    {
        if (string.Equals(rule, address, StringComparison.OrdinalIgnoreCase)) return true;
        int slash = rule.IndexOf('/');
        if (slash <= 0) return false;
        if (!System.Net.IPAddress.TryParse(rule[..slash], out var net) ||
            !int.TryParse(rule[(slash + 1)..], out int bits) || bits is < 0 or > 32 ||
            !System.Net.IPAddress.TryParse(address, out var ip) ||
            net.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        uint n = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(net.GetAddressBytes());
        uint a = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        uint mask = bits == 0 ? 0u : uint.MaxValue << (32 - bits);
        return (n & mask) == (a & mask);
    }

    // ================================================ app-health snapshot (§12)
    // Cheap counters for the live health card in Settings.
    public int AppRuleCount => _data.Rules.Count;
    public int CustomRuleCount => _data.CustomRules.Count;
    public int SystemRuleCount => _data.SystemRules.Count;
    public int VtCacheCount => _data.VtCache.Count;

    // ============================================= VirusTotal verdict cache (§VT)
    // Auto-checks store their result per SHA-256 so each unique file is looked up
    // once (VirusTotal's free tier is rate-limited) and verdicts survive restarts.
    public VtCacheEntry? GetVtCached(string sha256) =>
        !string.IsNullOrEmpty(sha256) && _data.VtCache.TryGetValue(sha256, out var e) ? e : null;

    public void SaveVtResult(string sha256, bool found, int flagged, int total)
    {
        if (string.IsNullOrEmpty(sha256)) return;
        _data.VtCache[sha256] = new VtCacheEntry
        {
            Found = found, Flagged = flagged, Total = total, CheckedUtc = DateTime.UtcNow
        };
        _store.Save(_data);
    }

    // ===================================================== local DNS resolver (§3)
    // GunWall's OWN loopback resolver. These accessors only persist its config; the
    // running resolver itself lives in the UI layer. It binds to 127.0.0.1 and never
    // changes the system DNS on its own — the user points DNS at it (a guided redirect
    // and "Gaming Session" toggle are the next phase).
    public int DnsResolverPort => _data.DnsResolverPort is > 0 and <= 65535 ? _data.DnsResolverPort : 53;
    public string DnsResolverUpstream =>
        string.IsNullOrWhiteSpace(_data.DnsResolverUpstream) ? "1.1.1.1" : _data.DnsResolverUpstream.Trim();
    public IReadOnlyList<string> DnsResolverBlocklist => _data.DnsResolverBlocklist.AsReadOnly();

    /// <summary>Persist the local resolver's port, upstream and blocklist.</summary>
    public void SaveDnsResolverConfig(int port, string upstream, IEnumerable<string> blocklist)
    {
        _data.DnsResolverPort = port is > 0 and <= 65535 ? port : 53;
        _data.DnsResolverUpstream = (upstream ?? "").Trim();
        _data.DnsResolverBlocklist = blocklist?.ToList() ?? new List<string>();
        _store.Save(_data);
    }

    // §3a: secure DNS (DoH) configuration.
    public string DnsDohUrl => _data.DnsDohUrl ?? "";
    public bool DnsDohFallback => _data.DnsDohFallback;
    public bool DnsBlockCloakedCnames => _data.DnsBlockCloakedCnames;
    public void SaveDnsCloakConfig(bool enabled)
    {
        if (_data.DnsBlockCloakedCnames == enabled) return;
        _data.DnsBlockCloakedCnames = enabled;
        _store.Save(_data);
        EventLog(enabled
            ? "CNAME-cloaking defense enabled"
            : "CNAME-cloaking defense disabled");
    }
    public void SaveDnsDohConfig(string url, bool fallback)
    {
        _data.DnsDohUrl = (url ?? "").Trim();
        _data.DnsDohFallback = fallback;
        _store.Save(_data);
        EventLog(_data.DnsDohUrl.Length > 0
            ? $"Secure DNS (DoH) set to {_data.DnsDohUrl}" + (fallback ? " with plaintext fallback" : " (fail closed)")
            : "Secure DNS (DoH) disabled - queries forward in plaintext");
    }

    // §3 Phase 2: system-DNS routing state (intent + captured adapter config).
    public bool DnsRedirectActive => _data.DnsRedirectActive;
    public bool DnsGamingSession => _data.DnsGamingSession;
    public List<SavedAdapterDns> DnsSavedAdapters => _data.DnsSavedAdapters;

    /// <summary>Persist routing intent/bypass; pass a non-null capture to replace
    /// the saved adapter state (null keeps the existing capture).</summary>
    public void SaveDnsRedirectState(bool active, bool gaming, List<SavedAdapterDns>? saved)
    {
        _data.DnsRedirectActive = active;
        _data.DnsGamingSession = gaming;
        if (saved != null) _data.DnsSavedAdapters = saved;
        _store.Save(_data);
    }

    /// <summary>Download the free CC0 database, then load it. Returns ranges loaded.</summary>
    public int DownloadAndLoadGeoIp()
    {
        GeoIpService.DownloadDatabase(GeoIpCachePath);
        _geo.LoadFromFile(GeoIpCachePath);
        return _geo.RangeCount;
    }

    // ============================================ custom domain blocklist (§5)
    private readonly HashSet<string> _customDomains = new(StringComparer.OrdinalIgnoreCase);
    private BloomFilter _customBloom = new(1, 0.01);
    public int CustomDomainCount => _customDomains.Count;
    public string CustomListPath => _data.CustomBlocklistPath ?? "";

    public void SetCustomListPath(string path)
    {
        _data.CustomBlocklistPath = path ?? "";
        _store.Save(_data);
        ReloadCustomList();
    }

    /// <summary>(Re)load the user's custom domain list and rebuild its bloom index.</summary>
    public int ReloadCustomList()
    {
        _customDomains.Clear();
        string path = _data.CustomBlocklistPath ?? "";
        try
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                foreach (var raw in System.IO.File.ReadAllLines(path))
                {
                    string d = CleanDomainLine(raw);
                    if (d.Length > 0) _customDomains.Add(d);
                }
        }
        catch { }
        _customBloom = new BloomFilter(Math.Max(1, _customDomains.Count), 0.01);
        foreach (var d in _customDomains) _customBloom.Add(d);
        return _customDomains.Count;
    }

    /// <summary>Fast membership test (bloom pre-filter + exact confirm).</summary>
    public bool IsCustomDomainBlocked(string domain)
    {
        if (string.IsNullOrEmpty(domain) || _customDomains.Count == 0) return false;
        if (!_customBloom.MightContain(domain)) return false;
        return _customDomains.Contains(domain.TrimEnd('.'));
    }

    private static string CleanDomainLine(string raw)
    {
        string s = raw.Trim();
        if (s.Length == 0 || s[0] == '#') return "";
        // Accept hosts-style "0.0.0.0 domain" / "127.0.0.1 domain" lines.
        int sp = s.IndexOfAny(new[] { ' ', '\t' });
        if (sp > 0)
        {
            string first = s.Substring(0, sp);
            if (first is "0.0.0.0" or "127.0.0.1" or "::1" or "::")
                s = s.Substring(sp + 1).Trim();
        }
        int hash = s.IndexOf('#');
        if (hash >= 0) s = s.Substring(0, hash).Trim();
        return s.TrimEnd('.');
    }

    // ================================================== entity rule engine (§1)
    // Block rules keyed on a remote's GeoIP entity (country / continent / ASN),
    // evaluated reactively in the connect-event handler. On a match GunWall installs
    // a per-app block for that specific remote IP (reusing the proven scope-block WFP
    // path). IPv4-only (the GeoIP table is IPv4); blocks are post-hoc (the first
    // packet to a brand-new remote may complete before the filter lands) - effective
    // for sustained traffic. Enforcement is independent of monitoring/strict mode: an
    // explicit entity rule always applies once the engine is up, like a scope block.
    private readonly HashSet<string> _entityBlocked = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EntityRule> EntityRules => _data.EntityRules.AsReadOnly();
    public int EntityReactiveBlockCount => _data.EntityReactiveFilters.Count;

    public void AddEntityRule(EntityRule rule)
    {
        if (rule == null || string.IsNullOrWhiteSpace(rule.Value)) return;
        rule.Value = rule.Value.Trim();
        _data.EntityRules.Add(rule);
        _store.Save(_data);
        EventLog($"Entity rule added: {rule.TypeLabel} {rule.Value} -> block for {rule.AppLabel}");
    }

    public void RemoveEntityRule(string id)
    {
        int removed = _data.EntityRules.RemoveAll(r => r.Id == id);
        if (removed > 0)
        {
            // A rule changed: tear down the reactive filters it (and others) spawned;
            // still-active rules re-form their blocks on the next matching connection.
            ClearEntityReactiveBlocks();
            _store.Save(_data);
        }
    }

    public void SetEntityRuleEnabled(string id, bool enabled)
    {
        var r = _data.EntityRules.FirstOrDefault(x => x.Id == id);
        if (r == null) return;
        r.Enabled = enabled;
        ClearEntityReactiveBlocks();   // re-evaluate cleanly under the new rule set
        _store.Save(_data);
    }

    /// <summary>Pure-logic match: does any enabled rule block this app from talking to
    /// a remote with the given GeoInfo? Returns the matched rule, else null.</summary>
    public EntityRule? MatchEntityBlock(string appPath, GeoIpService.GeoInfo geo)
    {
        if (_data.EntityRules.Count == 0 || !geo.HasData) return null;
        foreach (var r in _data.EntityRules)
        {
            if (!r.Enabled) continue;
            if (r.AppPath.Length > 0 &&
                !string.Equals(r.AppPath, appPath, StringComparison.OrdinalIgnoreCase)) continue;
            bool hit = r.Type switch
            {
                "country"   => geo.Country.Length > 0 &&
                               string.Equals(geo.Country, r.Value, StringComparison.OrdinalIgnoreCase),
                "continent" => geo.Country.Length > 0 &&
                               string.Equals(GeoData.Continent(geo.Country), r.Value, StringComparison.OrdinalIgnoreCase),
                "asn"       => geo.Asn != 0 && geo.Asn == ParseAsn(r.Value),
                _           => false
            };
            if (hit) return r;
        }
        return null;
    }

    /// <summary>Reactive enforcement: look up the remote, match entity rules, and on a
    /// hit install a per-app block for that IP (deduped per session). Returns a short
    /// reason string for logging (e.g. "country RU"), or null if nothing was blocked.</summary>
    public string? ApplyEntityBlocks(string appPath, string remoteIp)
    {
        if (!_engine.IsInitialized) return null;
        if (string.IsNullOrEmpty(appPath) || string.IsNullOrEmpty(remoteIp)) return null;
        if (_data.EntityRules.Count == 0) return null;

        // Never reactively block our own process - GunWall must keep reaching its API
        // server, update check, list mirrors, and VirusTotal regardless of any rule.
        if (string.Equals(appPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            return null;

        // Enforcement is IPv4-only (the WFP block path is v4). An IPv6 remote can still
        // be *enriched* in the connection list, but we can't install a block for it yet,
        // so bail before claiming one.
        if (!System.Net.IPAddress.TryParse(remoteIp, out var ipAddr) ||
            ipAddr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return null;

        var geo = _geo.Lookup(remoteIp);
        var rule = MatchEntityBlock(appPath, geo);
        if (rule == null) return null;

        string key = appPath.ToLowerInvariant() + "|" + remoteIp;
        if (!_entityBlocked.Add(key)) return null; // already handled this app+remote this session

        List<ulong> ids;
        try { ids = _engine.AddAppRemoteIpBlock(appPath, remoteIp); }
        catch (Exception ex) { DiagnosticLog.LogException("ApplyEntityBlocks", ex); return null; }

        if (ids.Count == 0) return null; // nothing was installed - don't claim a block

        _data.EntityReactiveFilters.AddRange(ids);
        _store.Save(_data);

        string reason = rule.Type switch
        {
            "country"   => $"country {rule.Value}",
            "continent" => $"continent {rule.Value}",
            "asn"       => $"ASN {rule.Value}",
            _           => "entity rule"
        };
        EventLog($"Entity block: {System.IO.Path.GetFileName(appPath)} -> {remoteIp} ({reason})");
        return reason;
    }

    /// <summary>Remove every reactive entity block installed this session and reset the
    /// dedup set. The entity rules themselves are kept.</summary>
    public void ClearEntityReactiveBlocks()
    {
        if (_data.EntityReactiveFilters.Count > 0)
        {
            try { _engine.RemoveFilters(_data.EntityReactiveFilters); } catch { }
            _data.EntityReactiveFilters.Clear();
            _store.Save(_data);
        }
        _entityBlocked.Clear();
    }

    private static int ParseAsn(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return -1;
        v = v.Trim();
        if (v.StartsWith("AS", StringComparison.OrdinalIgnoreCase)) v = v.Substring(2);
        return int.TryParse(v, out int n) ? n : -1;
    }

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

    /// <summary>§12: probe every WFP layer to confirm this Windows build
    /// accepts it. Adds and immediately removes a harmless permit filter on
    /// each; nothing is persisted or left behind.</summary>
    public List<Wfp.WfpEngine.LayerProbe> VerifyKernelLayers() => _engine.VerifyLayers();

    /// <summary>Engages or releases global lockdown (block-all). Persisted.</summary>
    public void SetLockdown(bool engaged)
    {
        if (engaged == _data.LockdownEngaged) return;

        if (engaged)
        {
            _data.LockdownFilterIds = _engine.EngageLockdown();
            _data.LockdownEngaged = true;
            // Logged because releasing lockdown erases every other trace of it:
            // the filters go and the ID list is cleared, so without this line a
            // diagnostics export can't tell lockdown was ever engaged.
            DiagnosticLog.Log($"Lockdown ENGAGED: {_data.LockdownFilterIds.Count} block filters installed.");
        }
        else
        {
            int had = _data.LockdownFilterIds.Count;
            _engine.RemoveFilters(_data.LockdownFilterIds);
            _data.LockdownFilterIds.Clear();
            _data.LockdownEngaged = false;
            DiagnosticLog.Log($"Lockdown RELEASED: {had} block filters removed.");
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
    /// Guarantees GunWall's own executable can reach the network, so its update
    /// check, blocklist downloads and VirusTotal lookups are never blocked by its
    /// own Zero-Trust block-all (which otherwise denies GunWall.exe just like any
    /// other unapproved app). Re-asserted on every launch. A user's *explicit*
    /// block on GunWall still wins, since that filter has higher weight.
    /// </summary>
    public void EnsureSelfConnectivity()
    {
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(self)) return;

            // Drop any stale permit from a previous run, then add a fresh one.
            if (_data.SelfFilterIds.Count > 0)
            {
                try { _engine.RemoveFilters(_data.SelfFilterIds); } catch { }
                _data.SelfFilterIds.Clear();
            }
            _data.SelfFilterIds = _engine.PermitApplication(self);
            _store.Save(_data);
            EventLog("Self-permit re-asserted for GunWall's own executable.");
        }
        catch { /* never let self-permit setup crash startup */ }
    }

    /// <summary>Loads saved category colors into the palette (called at startup).</summary>
    public void LoadCategoryColors() => CategoryPalette.Load(_data.CategoryColors);

    /// <summary>Persists the current palette to the profile.</summary>
    public void SaveCategoryColors()
    {
        _data.CategoryColors = CategoryPalette.ToDict();
        _store.Save(_data);
    }

    /// <summary>The note attached to an executable, or empty.</summary>
    public string GetNote(string exePath) =>
        !string.IsNullOrEmpty(exePath) && _data.AppNotes.TryGetValue(exePath, out var n) ? n : "";

    /// <summary>Sets or clears the note for an executable.</summary>
    public void SetNote(string exePath, string note)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        if (string.IsNullOrWhiteSpace(note)) _data.AppNotes.Remove(exePath);
        else _data.AppNotes[exePath] = note.Trim();
        _store.Save(_data);
    }

    /// <summary>
    /// Removes apps GunWall has merely seen (in the known list) but for which no
    /// allow/block rule exists - housekeeping for "seen but never decided" apps.
    /// They'll prompt again the next time they connect. Returns the count removed.
    /// </summary>
    public int PurgeUnusedApps()
    {
        var ruled = new HashSet<string>(
            _data.Rules.Select(r => r.ExecutablePath), StringComparer.OrdinalIgnoreCase);
        int before = _data.KnownApps.Count;
        _data.KnownApps.RemoveAll(p => !ruled.Contains(p));
        _knownSet = null; // force a rebuild on next access
        int removed = before - _data.KnownApps.Count;
        if (removed > 0) _store.Save(_data);
        return removed;
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

    public bool EtwMeterEnabled => _data.EtwMeterEnabled;
    public void SetEtwMeterEnabled(bool v) { _data.EtwMeterEnabled = v; _store.Save(_data); }

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

    public bool RemoveCustomRule(string id)
    {
        var rule = _data.CustomRules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return false;
        if (rule.Protected) return false; // protected rules cannot be deleted
        try { _engine.RemoveFilters(rule.FilterIds); } catch { }
        _data.CustomRules.Remove(rule);
        _store.Save(_data);
        return true;
    }

    /// <summary>Marks a custom rule protected (non-removable) or removes that protection.</summary>
    public void SetCustomRuleProtected(string id, bool prot)
    {
        var rule = _data.CustomRules.FirstOrDefault(r => r.Id == id);
        if (rule is null) return;
        rule.Protected = prot;
        _store.Save(_data);
    }

    /// <summary>Manual sweep: unblocks and clears any timed blocks already past expiry
    /// (the per-block timer normally does this automatically). Returns the count purged.</summary>
    public int PurgeExpiredTimers()
    {
        if (_data.TempBlocks.Count == 0) return 0;
        var now = DateTime.UtcNow;
        int n = 0;
        foreach (var kv in new Dictionary<string, DateTime>(_data.TempBlocks))
        {
            if (kv.Value > now) continue; // still active
            string key = kv.Key;
            try { UnblockApp(key); } catch { }
            _data.TempBlocks.Remove(key);
            if (_tempTimers.TryGetValue(key, out var t)) { t.Dispose(); _tempTimers.Remove(key); }
            n++;
        }
        if (n > 0) { try { _store.Save(_data); } catch { } }
        return n;
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
            // Filter count matters: 0 means every layer this rule needs was
            // rejected, which otherwise looks identical to success.
            DiagnosticLog.Log($"System rule ON: {key} -> {ids.Count} filter(s) installed."
                              + (ids.Count == 0 ? "  WARNING: no filters were accepted." : ""));
        }
        else
        {
            if (_data.SystemRules.TryGetValue(key, out var ids))
            {
                try { _engine.RemoveFilters(ids); } catch { }
                _data.SystemRules.Remove(key);
                EventLog($"System rule disabled: {key}");
                DiagnosticLog.Log($"System rule OFF: {key} -> {ids.Count} filter(s) removed.");
            }
        }
        _store.Save(_data);
    }

    // ------------------------------------------------ event log
    public bool EventLogEnabled => _data.EventLogEnabled;
    public void SetEventLogEnabled(bool v) { _data.EventLogEnabled = v; _store.Save(_data); }

    public bool FullscreenSilent => _data.FullscreenSilent;
    public void SetFullscreenSilent(bool v) { _data.FullscreenSilent = v; _store.Save(_data); }
    public bool ConfirmClearLogs => _data.ConfirmClearLogs;
    public void SetConfirmClearLogs(bool v) { _data.ConfirmClearLogs = v; _store.Save(_data); }
    public bool AlwaysConfirmExit => _data.AlwaysConfirmExit;
    public void SetAlwaysConfirmExit(bool v) { _data.AlwaysConfirmExit = v; _store.Save(_data); }
    public int MaxLogEntries => _data.MaxLogEntries;
    public void SetMaxLogEntries(int v) { _data.MaxLogEntries = v < 0 ? 0 : v; _store.Save(_data); }
    public int MaxLogFileMB => _data.MaxLogFileMB;
    public void SetMaxLogFileMB(int v) { _data.MaxLogFileMB = v < 1 ? 1 : v; _store.Save(_data); }
    public bool KeepUnusedApps => _data.KeepUnusedApps;
    public void SetKeepUnusedApps(bool v) { _data.KeepUnusedApps = v; _store.Save(_data); }

    /// <summary>Total WFP filters GunWall currently has installed across all layers
    /// (sum of every stored filter id). A live window into the kernel-side footprint.</summary>
    public int ActiveFilterCount =>
        _data.Rules.Sum(r => r.FilterIds.Count)
        + _data.CustomRules.Sum(r => r.FilterIds.Count)
        + _data.SystemRules.Values.Sum(v => v.Count)
        + _data.BlocklistFilterIds.Count
        + _data.BlocklistWfpFilters.Values.Sum(v => v.Count)
        + _data.StrictFilterIds.Count
        + _data.LockdownFilterIds.Count
        + _data.SelfFilterIds.Count
        + _data.ScopeFilters.Values.Sum(v => v.Count);

    private static string ScopeKey(string exePath, string scope) => exePath.ToLowerInvariant() + "|" + scope;

    /// <summary>Is the given network scope (local | lan | incoming) currently blocked for this app?</summary>
    public bool IsScopeBlocked(string exePath, string scope) =>
        _data.ScopeFilters.TryGetValue(ScopeKey(exePath, scope), out var ids) && ids.Count > 0;

    /// <summary>Turns a per-app network-scope block on or off. Filters install through the
    /// engine's fault-tolerant path and are fully removed when turned off.</summary>
    // ===================== §1: per-app ordered access policies =====================

    /// <summary>The app's policy, or null if it has none (allow-all).</summary>
    public AppAccessPolicy? GetAccessPolicy(string exePath) =>
        _data.AccessPolicies.TryGetValue(exePath.ToLowerInvariant(), out var p) ? p : null;

    /// <summary>All apps that currently have a policy (for the sampling loop).</summary>
    public IReadOnlyCollection<AppAccessPolicy> ActiveAccessPolicies =>
        _data.AccessPolicies.Values.Where(p => p.IsActive).ToList();

    /// <summary>Fetch-or-create the app's policy (created empty = allow-all).</summary>
    public AppAccessPolicy GetOrCreateAccessPolicy(string exePath)
    {
        string key = exePath.ToLowerInvariant();
        if (!_data.AccessPolicies.TryGetValue(key, out var p))
        {
            p = new AppAccessPolicy { AppPath = exePath };
            _data.AccessPolicies[key] = p;
        }
        return p;
    }

    /// <summary>Persist edits to an app's policy. If the policy became inert and
    /// empty, it is dropped and its reactive filters cleared.</summary>
    public void SaveAccessPolicy(string exePath)
    {
        string key = exePath.ToLowerInvariant();
        if (_data.AccessPolicies.TryGetValue(key, out var p) &&
            p.Rules.Count == 0 && !p.DefaultBlock)
        {
            _data.AccessPolicies.Remove(key);
        }
        ClearAccessReactiveBlocks(exePath); // re-evaluate cleanly under the new rules
        _store.Save(_data);
        EventLog($"Access policy updated for {System.IO.Path.GetFileName(exePath)}");
    }

    /// <summary>Reactively block one destination for an app under its policy,
    /// stored under the app's "access" scope key so a policy change clears it.
    /// Returns true if a filter was actually added.</summary>
    public bool AddAccessReactiveBlock(string exePath, string remoteIp)
    {
        var ids = _engine.AddAppRemoteIpBlock(exePath, remoteIp);
        if (ids.Count == 0) return false;
        string key = ScopeKey(exePath, "access");
        if (_data.ScopeFilters.TryGetValue(key, out var existing)) existing.AddRange(ids);
        else _data.ScopeFilters[key] = ids;
        _store.Save(_data);
        return true;
    }

    /// <summary>Remove every reactive access-block filter for one app.</summary>
    public void ClearAccessReactiveBlocks(string exePath)
    {
        string key = ScopeKey(exePath, "access");
        if (_data.ScopeFilters.TryGetValue(key, out var ids))
        {
            try { _engine.RemoveFilters(ids); } catch { }
            _data.ScopeFilters.Remove(key);
        }
    }

    public bool IsP2pBlocked(string exePath) =>
        _data.P2pApps.Contains(exePath.ToLowerInvariant());

    public IReadOnlyList<string> P2pAppPaths => _data.P2pApps.AsReadOnly();

    /// <summary>Toggle the reactive P2P/direct scope. Enabling only flags the
    /// app; blocking happens reactively as direct connections are observed.
    /// Disabling also removes every reactive filter accumulated for it.</summary>
    public void SetP2pBlock(string exePath, bool blocked)
    {
        string lower = exePath.ToLowerInvariant();
        string key = ScopeKey(exePath, "p2p");
        if (blocked)
        {
            if (_data.P2pApps.Contains(lower)) return;
            _data.P2pApps.Add(lower);
            EventLog($"P2P/direct blocking enabled for {System.IO.Path.GetFileName(exePath)}");
        }
        else
        {
            _data.P2pApps.Remove(lower);
            if (_data.ScopeFilters.TryGetValue(key, out var ids))
            {
                try { _engine.RemoveFilters(ids); } catch { }
                _data.ScopeFilters.Remove(key);
            }
            EventLog($"P2P/direct blocking disabled for {System.IO.Path.GetFileName(exePath)}");
        }
        _store.Save(_data);
    }

    /// <summary>Reactively block one direct destination for a P2P-flagged app.
    /// The filter IDs are stored under the app's p2p scope key so disabling
    /// the toggle cleans everything up. Returns false if nothing was added.</summary>
    public bool AddP2pReactiveBlock(string exePath, string remoteIp)
    {
        var ids = _engine.AddAppRemoteIpBlock(exePath, remoteIp);
        if (ids.Count == 0) return false;
        string key = ScopeKey(exePath, "p2p");
        if (_data.ScopeFilters.TryGetValue(key, out var existing)) existing.AddRange(ids);
        else _data.ScopeFilters[key] = ids;
        _store.Save(_data);
        EventLog($"P2P direct connection blocked: {System.IO.Path.GetFileName(exePath)} -> {remoteIp}");
        return true;
    }

    public void SetScopeBlock(string exePath, string scope, bool blocked)
    {
        string key = ScopeKey(exePath, scope);
        if (blocked)
        {
            if (IsScopeBlocked(exePath, scope)) return;
            var ids = _engine.AddAppScopeBlock(exePath, scope);
            _data.ScopeFilters[key] = ids;
            EventLog($"Scope block enabled: {scope} for {System.IO.Path.GetFileName(exePath)}");
        }
        else
        {
            if (_data.ScopeFilters.TryGetValue(key, out var ids))
            {
                try { _engine.RemoveFilters(ids); } catch { }
                _data.ScopeFilters.Remove(key);
                EventLog($"Scope block disabled: {scope} for {System.IO.Path.GetFileName(exePath)}");
            }
        }
        _store.Save(_data);
    }

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
        _packetLog.SetMaxMB(_data.MaxLogFileMB);
        _packetLog.Append(time, blocked ? "Blocked" : "Allowed", app, protocol, direction, remote, exePath);
    }

    public string PacketLogPath => System.IO.Path.Combine(_store.ProfileFolder, "packets.csv");

    // ------------------------------------------------ notification options
    public bool NotificationSound => _data.NotificationSound;
    public void SetNotificationSound(bool v) { _data.NotificationSound = v; _store.Save(_data); }
    /// <summary>Alerts-page categories the user silenced (see MainWindow.Notify).</summary>
    public IReadOnlyList<string> MutedAlertCategories => _data.MutedAlertCategories;
    public void SetAlertCategoryMuted(string cat, bool muted)
    {
        bool has = _data.MutedAlertCategories.Contains(cat);
        if (muted == has) return;               // no change - skip the disk write
        if (muted) _data.MutedAlertCategories.Add(cat);
        else _data.MutedAlertCategories.Remove(cat);
        _store.Save(_data);
    }

    public bool TraySingleClick => _data.TraySingleClick;
    public void SetTraySingleClick(bool v) { _data.TraySingleClick = v; _store.Save(_data); }

    public int UiZoomPercent => _data.UiZoomPercent;
    public void SetUiZoomPercent(int v) { _data.UiZoomPercent = Math.Clamp(v, 75, 150); _store.Save(_data); }

    public bool TrayNotifications => _data.TrayNotifications;
    public void SetTrayNotifications(bool v) { _data.TrayNotifications = v; _store.Save(_data); }

    public int PopupTimeoutSeconds => _data.PopupTimeoutSeconds;
    public void SetPopupTimeoutSeconds(int v) { _data.PopupTimeoutSeconds = v < 0 ? 0 : v; _store.Save(_data); }
    public bool PopupDefaultAllow => _data.PopupDefaultAllow;
    public void SetPopupDefaultAllow(bool v) { _data.PopupDefaultAllow = v; _store.Save(_data); }

    /// <summary>Writes to the Windows Event Log if the user enabled it, and always to the diagnostic log.</summary>
    public void EventLog(string message)
    {
        DiagnosticLog.Log(message);
        if (_data.EventLogEnabled) EventLogService.Write("GunWall: " + message);
    }

    // ------------------------------------------------ diagnostics bundle
    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string RunCapture(string exe, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return $"(failed to start {exe})";
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return o;
        }
        catch (Exception ex) { return $"(error running {exe}: {ex.Message})"; }
    }

    private string SanitizedConfigJson()
    {
        string saved = _data.VirusTotalApiKey;
        try
        {
            _data.VirusTotalApiKey = string.IsNullOrEmpty(saved) ? "" : "(redacted)";
            return System.Text.Json.JsonSerializer.Serialize(_data,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { return "(failed to serialize config: " + ex.Message + ")"; }
        finally { _data.VirusTotalApiKey = saved; }
    }

    /// <summary>
    /// Builds a diagnostics zip at <paramref name="destZipPath"/>: app/system info,
    /// the user's settings (secrets redacted), the runtime diagnostic log, recent
    /// packets, the GunWall hosts block, and network/firewall/DNS state. Intended
    /// to be attached to a bug report.
    /// </summary>
    public void ExportDiagnostics(string destZipPath)
    {
        DiagnosticLog.Log("Diagnostics export requested.");
        string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GunWallDiag_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tmp);
        try
        {
            // 1. system + app info
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("GunWall diagnostics");
            sb.AppendLine("Generated:        " + DateTime.Now.ToString("u"));
            sb.AppendLine("App version:      " + UpdateService.CurrentVersion);
            sb.AppendLine("OS:               " + Environment.OSVersion + (Environment.Is64BitOperatingSystem ? " (x64)" : " (x86)"));
            sb.AppendLine(".NET:             " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            sb.AppendLine("Process 64-bit:   " + Environment.Is64BitProcess);
            sb.AppendLine("Elevated (admin): " + IsElevated());
            sb.AppendLine("Engine started:   " + (EngineHandle != IntPtr.Zero));
            sb.AppendLine("Culture:          " + System.Globalization.CultureInfo.CurrentCulture.Name);
            sb.AppendLine("Machine:          " + Environment.MachineName);
            System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "system-info.txt"), sb.ToString());

            // 2. current state summary
            var st = new System.Text.StringBuilder();
            st.AppendLine("Lockdown:    " + LockdownEngaged);
            st.AppendLine("Strict mode: " + StrictMode);
            try { st.AppendLine("Live TCP/UDP rows: " + new NetworkMonitor().GetTcpConnections().Count); }
            catch (Exception cex) { st.AppendLine("Live TCP/UDP rows: ERROR - " + cex.Message); }
            st.AppendLine("DNS provider: " + CurrentDnsProvider);
            st.AppendLine("Enabled blocklists: " + (_data.EnabledBlocklists.Count == 0 ? "(none)" : string.Join(", ", _data.EnabledBlocklists)));
            foreach (var cat in Models.BlocklistCatalog.All)
                st.AppendLine($"  {cat.Key}: on={IsBlocklistOn(cat.Key)}, domains={BlocklistDomainCount(cat.Key)}");
            st.AppendLine("App rules:    " + _data.Rules.Count);
            st.AppendLine("Custom rules: " + _data.CustomRules.Count);
            st.AppendLine("System rules: " + _data.SystemRules.Count);
            System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "state.txt"), st.ToString());

            // 3. settings (secrets redacted)
            System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "config.json"), SanitizedConfigJson());

            // 4. runtime diagnostic log(s)
            try
            {
                if (DiagnosticLog.LogPath != null && System.IO.File.Exists(DiagnosticLog.LogPath))
                    System.IO.File.Copy(DiagnosticLog.LogPath, System.IO.Path.Combine(tmp, "diagnostics.log"), true);
                if (DiagnosticLog.PreviousLogPath != null && System.IO.File.Exists(DiagnosticLog.PreviousLogPath))
                    System.IO.File.Copy(DiagnosticLog.PreviousLogPath, System.IO.Path.Combine(tmp, "diagnostics.previous.log"), true);
            }
            catch { }

            // 5. recent packets (if file logging is on)
            try
            {
                string pkts = System.IO.Path.Combine(_store.ProfileFolder, "packets.csv");
                if (System.IO.File.Exists(pkts))
                    System.IO.File.Copy(pkts, System.IO.Path.Combine(tmp, "packets.csv"), true);
            }
            catch { }

            // 6. GunWall hosts block
            try
            {
                var domains = HostsFileService.GetBlockedDomains();
                var hb = new System.Text.StringBuilder();
                hb.AppendLine($"GunWall is blocking {domains.Count} domains via the hosts file.");
                hb.AppendLine();
                foreach (var d in domains.Take(20000)) hb.AppendLine(d);
                System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "hosts-gunwall-block.txt"), hb.ToString());
            }
            catch { }

            // 7. network / firewall / DNS state
            try
            {
                var net = new System.Text.StringBuilder();
                var wf = WindowsFirewallService.GetState();
                net.AppendLine($"Windows Firewall - Domain: {wf.Domain}, Private: {wf.Private}, Public: {wf.Public}");
                net.AppendLine("GunWall DNS selection: " + CurrentDnsProvider);
                net.AppendLine();
                net.AppendLine("===== ipconfig /all =====");
                net.AppendLine(RunCapture("ipconfig", "/all"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(tmp, "network.txt"), net.ToString());
            }
            catch { }

            // zip it up
            if (System.IO.File.Exists(destZipPath)) System.IO.File.Delete(destZipPath);
            System.IO.Compression.ZipFile.CreateFromDirectory(tmp, destZipPath);
            DiagnosticLog.Log("Diagnostics export written: " + destZipPath);
        }
        finally
        {
            try { System.IO.Directory.Delete(tmp, true); } catch { }
        }
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

    // ------------------------------------------------ curated blocklists (hosts-file based)
    private string ListsFolder
    {
        get
        {
            string d = System.IO.Path.Combine(_store.ProfileFolder, "lists");
            try { System.IO.Directory.CreateDirectory(d); } catch { }
            return d;
        }
    }

    /// <summary>Baked-in + downloaded domains for a category, de-duplicated.</summary>
    private List<string> DomainsFor(Models.BlocklistCategory cat)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in cat.Hosts) set.Add(h);
        try
        {
            string f = System.IO.Path.Combine(ListsFolder, cat.Key + ".txt");
            if (System.IO.File.Exists(f))
                foreach (var line in System.IO.File.ReadAllLines(f))
                {
                    var d = line.Trim();
                    if (d.Length > 0) set.Add(d);
                }
        }
        catch { }
        return set.ToList();
    }

    public bool IsBlocklistOn(string key)
    {
        // Ads & trackers is enforced at the DNS layer (AdGuard), not the hosts
        // file - 85k domains is impractical as hosts entries or WFP filters.
        if (key == "ads") return CurrentDnsProvider == "adguard";
        return _data.EnabledBlocklists.Contains(key);
    }

    /// <summary>True when a category is being enforced via WFP IP filters rather
    /// than the hosts file (because security software blocked the hosts write).</summary>
    public bool IsBlocklistViaWfp(string key) => _data.BlocklistWfpFilters.ContainsKey(key);

    public int BlocklistDomainCount(string key)
    {
        var cat = Models.BlocklistCatalog.All.FirstOrDefault(c => c.Key == key);
        return cat == null ? 0 : DomainsFor(cat).Count;
    }

    // Lists larger than this won't fall back to WFP (one filter per resolved IP
    // doesn't scale to tens of thousands of ad domains — those use hosts/DNS).
    private const int MaxWfpBlocklistDomains = 5000;

    /// <summary>Rewrites the GunWall hosts block from every hosts-enforced category
    /// (WFP-enforced categories are excluded — they're handled by the engine).</summary>
    public bool RebuildHostsBlock()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in Models.BlocklistCatalog.All)
            if (_data.EnabledBlocklists.Contains(cat.Key) && !_data.BlocklistWfpFilters.ContainsKey(cat.Key))
                foreach (var d in DomainsFor(cat)) all.Add(d);
        foreach (var d in _customDomains) all.Add(d); // §5 user custom list
        return HostsFileService.SetBlockedDomains(all, _store.ProfileFolder);
    }

    /// <summary>
    /// Resolves a category's domains to IPv4 addresses (in parallel) and blocks
    /// them outbound via WFP. Defender can't revert WFP filters, so this is the
    /// fallback when the hosts file is locked/reverted. Returns the filter IDs.
    /// </summary>
    private List<ulong> BlockDomainsViaWfp(IReadOnlyList<string> domains)
    {
        var ips = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        using (var sem = new System.Threading.SemaphoreSlim(64))
        {
            var tasks = new List<System.Threading.Tasks.Task>();
            foreach (var d in domains)
            {
                sem.Wait();
                tasks.Add(System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        foreach (var a in System.Net.Dns.GetHostAddresses(d))
                            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                ips.TryAdd(a.ToString(), 0);
                    }
                    catch { /* unresolvable — skip */ }
                    finally { sem.Release(); }
                }));
            }
            try { System.Threading.Tasks.Task.WaitAll(tasks.ToArray()); } catch { }
        }

        var idList = new List<ulong>();
        foreach (var ip in ips.Keys)
        {
            try { idList.AddRange(_engine.AddCustomRule(true, true, "Any", ip, 0)); } // block outbound
            catch { /* skip a bad entry, keep going */ }
        }
        return idList;
    }

    /// <summary>
    /// Turns a category on/off. Tries the hosts file first (fast, scales). If the
    /// hosts write is blocked/reverted by security software, small lists fall back
    /// to WFP IP blocking (which can't be reverted). Only commits the logical state
    /// when blocking actually took effect, so a toggle never falsely shows "on".
    /// </summary>
    public bool SetBlocklistEnabled(string key, bool on)
    {
        // Ads & trackers: block at the DNS layer with AdGuard rather than the
        // hosts file. Fast, Defender-proof, and no list upkeep. (This shares the
        // single system DNS setting with the Filtering DNS card.)
        if (key == "ads")
        {
            SetDnsProvider(on ? "adguard" : "auto");
            EventLog($"Ads & trackers {(on ? "enabled via AdGuard DNS" : "disabled (DNS set to automatic)")}");
            return true;
        }

        bool has = _data.EnabledBlocklists.Contains(key);

        if (!on)
        {
            // Disable: drop any WFP filters and re-assert the hosts block without it.
            if (_data.BlocklistWfpFilters.TryGetValue(key, out var existing))
            {
                try { _engine.RemoveFilters(existing); } catch { }
                _data.BlocklistWfpFilters.Remove(key);
            }
            _data.EnabledBlocklists.Remove(key);
            RebuildHostsBlock(); // best effort for any remaining hosts-enforced lists
            _store.Save(_data);
            EventLog($"Blocklist disabled: {key}");
            return true;
        }

        if (has) return true; // already on

        // 1) Try the hosts file.
        _data.EnabledBlocklists.Add(key);
        if (RebuildHostsBlock())
        {
            _store.Save(_data);
            EventLog($"Blocklist enabled (hosts file): {key}");
            return true;
        }

        // 2) Hosts blocked (e.g. Defender). Fall back to WFP IP blocking for lists
        //    small enough that one-filter-per-IP is practical.
        var cat = Models.BlocklistCatalog.All.FirstOrDefault(c => c.Key == key);
        var domains = cat == null ? new List<string>() : DomainsFor(cat);
        if (cat == null || domains.Count == 0 || domains.Count > MaxWfpBlocklistDomains)
        {
            _data.EnabledBlocklists.Remove(key);
            RebuildHostsBlock(); // leave the file consistent
            EventLog($"Blocklist enable failed (hosts blocked; {domains.Count} domains too large for WFP fallback): {key}");
            return false;
        }

        var ids = BlockDomainsViaWfp(domains);
        if (ids.Count == 0)
        {
            _data.EnabledBlocklists.Remove(key);
            RebuildHostsBlock();
            EventLog($"Blocklist enable failed (hosts blocked; WFP fallback produced no filters): {key}");
            return false;
        }

        _data.BlocklistWfpFilters[key] = ids;       // enforced via WFP now
        RebuildHostsBlock();                         // ensure this one isn't in the hosts file
        _store.Save(_data);
        EventLog($"Blocklist enabled (WFP fallback, {ids.Count} filters): {key}");
        return true;
    }

    /// <summary>
    /// Downloads the latest community lists for every category, caches them, and
    /// re-applies the hosts block. Returns a short per-category summary. Network +
    /// file I/O, so call it off the UI thread.
    /// </summary>
    public async System.Threading.Tasks.Task<string> UpdateBlocklistsOnlineAsync()
    {
        var results = new List<string>();
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.Add("User-Agent", "GunWall");

        foreach (var cat in Models.BlocklistCatalog.All)
        {
            var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var url in cat.SourceUrls)
            {
                try
                {
                    string text = await http.GetStringAsync(url);
                    foreach (var d in ParseHostsDomains(text)) domains.Add(d);
                }
                catch { /* one source failed - keep others */ }
            }

            if (domains.Count > 0)
            {
                try { System.IO.File.WriteAllLines(System.IO.Path.Combine(ListsFolder, cat.Key + ".txt"), domains); }
                catch { }
                results.Add($"{cat.Name}: {domains.Count:n0}");
            }
            else
            {
                results.Add($"{cat.Name}: not updated");
            }
        }

        RebuildHostsBlock();
        return string.Join("   \u2022   ", results);
    }

    private static IEnumerable<string> ParseHostsDomains(string text)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            string domain;
            if (line.StartsWith("0.0.0.0 ", StringComparison.Ordinal) ||
                line.StartsWith("127.0.0.1 ", StringComparison.Ordinal))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                domain = parts[1];
            }
            else if (!line.Contains(' ') && line.Contains('.'))
            {
                domain = line; // bare-domain list
            }
            else continue;

            domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
            if (domain.Length == 0 || domain == "0.0.0.0" || domain == "localhost" || domain.Contains('/'))
                continue;
            yield return domain;
        }
    }

    /// <summary>v0.24 used WFP filters for blocklists; remove those and move to the hosts model.</summary>
    public void MigrateLegacyBlocklists()
    {
        if (_data.Blocklists.Count == 0) return;
        foreach (var kv in _data.Blocklists)
        {
            try { _engine.RemoveFilters(kv.Value); } catch { }
            if (!_data.EnabledBlocklists.Contains(kv.Key)) _data.EnabledBlocklists.Add(kv.Key);
        }
        _data.Blocklists.Clear();
        _store.Save(_data);
        try { RebuildHostsBlock(); } catch { }
    }

    // ------------------------------------------------ filtering DNS
    public string CurrentDnsProvider => string.IsNullOrEmpty(_data.DnsProvider) ? "auto" : _data.DnsProvider;

    public int SetDnsProvider(string key)
    {
        var preset = DnsService.ByKey(key);
        int n = DnsService.Apply(preset);
        _data.DnsProvider = preset.Key;
        _store.Save(_data);
        EventLog($"DNS set to {preset.Name} on {n} adapter(s)");
        return n;
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

    /// <summary>Critical check by process name (with or without the .exe suffix).</summary>
    public static bool IsCriticalProcessName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return CriticalProcesses.Contains(name) || CriticalProcesses.Contains(name + ".exe");
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
