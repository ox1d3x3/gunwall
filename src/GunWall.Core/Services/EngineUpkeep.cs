using GunWall.Models;

namespace GunWall.Services;

/// <summary>What the upkeep needs from the firewall. FirewallManager implements
/// it; the bench tests a fake.</summary>
public interface IUpkeepPolicy
{
    int ReactiveGeneration { get; }
    bool TamperWatchEnabled { get; }
    FirewallManager.TamperReport CheckIntegrity(bool repair);
    IReadOnlyList<string> P2pAppPaths { get; }
    bool AddP2pReactiveBlock(string exePath, string remoteIp);
    bool BypassesBlocklists(string exePath);
    bool AddAppDomainBlock(string exePath, string domain, string remoteIp);
    bool AddDomainReactiveBlock(string domain, string remoteIp, out string declined);
    IReadOnlyCollection<AppAccessPolicy> ActiveAccessPolicies { get; }
    AppAccessPolicy? GetAccessPolicy(string exePath);
    bool AddAccessReactiveBlock(string exePath, string remoteIp);
}

/// <summary>Something the user should be told. The window shows it as a
/// notification; the background service writes it to the log.</summary>
public sealed record UpkeepNotice(string Level, string Title, string Body, string Category = "security");

/// <summary>
/// The enforcement that runs on live connections, beside the approval prompts
/// (0.99.203): direct-connection (P2P) blocks, blocked-domain blocks, per-app
/// access rules, and the tamper watch. Moved out of MainWindow unchanged so the
/// background service runs it too - until now all four stopped whenever the
/// window was closed.
///
/// Not thread-safe: call from the engine thread, like ConnectionDetector.
/// </summary>
public sealed class EngineUpkeep
{
    /// <summary>How often the tamper watch asks the kernel for every filter.
    /// Every tick would query the kernel once per filter per second for no
    /// benefit; every half-minute catches tampering while it still matters.</summary>
    public static readonly TimeSpan TamperCheckEvery = TimeSpan.FromSeconds(30);

    private readonly IUpkeepPolicy _fw;
    private readonly Func<int> _blockedDomainCount;
    private readonly Func<string, bool> _isDomainBlocked;
    private readonly Action<ConnectionInfo> _closeTcp;

    /// <summary>"exePath|address" pairs already handled by the P2P enforcer.</summary>
    private readonly HashSet<string> _p2pHandled = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Addresses already filtered globally for a blocked domain.</summary>
    private readonly HashSet<string> _domainBlocked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"exePath|address" pairs already filtered at the app layer.</summary>
    private readonly HashSet<string> _appDomainBlocked = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"exePath|address" pairs already handled by the access-rule enforcer.</summary>
    private readonly HashSet<string> _accessHandled = new(StringComparer.OrdinalIgnoreCase);

    private int _reactiveGenerationSeen;
    /// <summary>Milliseconds on a clock that only moves forward (TickCount64). A
    /// wall clock pulled back by a time sync stopped the tamper watch until it
    /// caught up again.</summary>
    private long _lastTamperCheckMs;
    /// <summary>"exePath|address" pairs already open at the first pass. Their DNS
    /// lookups happened before this engine was watching - in the window before a
    /// hand-over to the service, or before GunWall started - so "never resolved"
    /// proves nothing about them and they are not judged direct. A pair leaves the
    /// set as soon as it is no longer open: the next connection to that address is
    /// judged like any other.</summary>
    private HashSet<string>? _openAtStart;

    /// <param name="blockedDomainCount">How many names are on the DNS blocklist.</param>
    /// <param name="isDomainBlocked">Whether a name is on the DNS blocklist.</param>
    /// <param name="closeTcp">Tears a TCP session down; the bench passes a recorder.</param>
    public EngineUpkeep(IUpkeepPolicy firewall, Func<int> blockedDomainCount, Func<string, bool> isDomainBlocked,
                        Action<ConnectionInfo>? closeTcp = null)
    {
        _fw = firewall;
        _blockedDomainCount = blockedDomainCount;
        _isDomainBlocked = isDomainBlocked;
        _closeTcp = closeTcp ?? (c => ConnectionService.CloseTcpConnection(
            c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort));
    }

    /// <summary>
    /// One pass over the current connections. <paramref name="conns"/> must carry
    /// ExePath and ProcessName; access rules on countries, continents and ASNs
    /// need Country and Asn filled in as well (the window's EnrichGeo, the
    /// service's lookup).
    /// </summary>
    public List<UpkeepNotice> Tick(List<ConnectionInfo> conns,
                                   Dictionary<int, (string Name, string Path)> procs)
        => TickAt(conns, procs, Environment.TickCount64);

    /// <summary><see cref="Tick"/> with the clock supplied (milliseconds,
    /// forward-only) - for the bench.</summary>
    public List<UpkeepNotice> TickAt(List<ConnectionInfo> conns,
                                   Dictionary<int, (string Name, string Path)> procs, long nowMs)
    {
        var notices = new List<UpkeepNotice>();
        if (_openAtStart == null)
        {
            // The first tamper check comes one interval after the first pass:
            // startup has just reconciled every filter.
            _lastTamperCheckMs = nowMs;
            _openAtStart = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in conns)
                if (!string.IsNullOrEmpty(c.RemoteAddress) && procs.TryGetValue(c.ProcessId, out var p) && p.Path.Length > 0)
                    _openAtStart.Add(p.Path + "|" + c.RemoteAddress);
        }
        else if (_openAtStart.Count > 0)
        {
            var open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in conns)
                if (!string.IsNullOrEmpty(c.RemoteAddress) && procs.TryGetValue(c.ProcessId, out var p) && p.Path.Length > 0)
                    open.Add(p.Path + "|" + c.RemoteAddress);
            _openAtStart.IntersectWith(open);
        }
        SyncReactiveMemory();
        EnforceP2pBlocks(conns, procs, notices);
        EnforceBlockedDomains(conns, notices);
        TamperWatchTick(nowMs, notices);
        EnforceAccessPolicies(conns, procs, notices);
        return notices;
    }

    /// <summary>The DNS blocklist changed and its filters were cleared: blocked
    /// domains re-form as their connections are next seen.</summary>
    public void ResetDomainMemory()
    {
        _domainBlocked.Clear();
        _appDomainBlocked.Clear();
    }

    /// <summary>An access policy was edited: re-evaluate against the new rules.</summary>
    public void ResetAccessMemory() => _accessHandled.Clear();

    /// <summary>
    /// Clears the reactive enforcers' "already handled" memory when the firewall
    /// says reactive blocks were dropped or removed wholesale - after a repair
    /// pruned ids the kernel had lost, or protection came back on.
    ///
    /// Without it a lost block stayed lost for the session: the memory said
    /// "handled", and once the lost id was pruned nothing counted it missing
    /// either (trap 2.43).
    /// </summary>
    private void SyncReactiveMemory()
    {
        int gen = _fw.ReactiveGeneration;
        if (gen == _reactiveGenerationSeen) return;
        _reactiveGenerationSeen = gen;
        _p2pHandled.Clear();
        _domainBlocked.Clear();
        _appDomainBlocked.Clear();
        _accessHandled.Clear();
        DiagnosticLog.Log("Reactive blocks reset: P2P, access-policy and blocked-domain "
                        + "blocks re-form as their connections are next seen.");
    }

    /// <summary>
    /// Periodically confirms GunWall's filters are still installed, and puts
    /// them back if not.
    /// </summary>
    private void TamperWatchTick(long nowMs, List<UpkeepNotice> notices)
    {
        using var _perf = PerfMonitor.Measure("TamperWatchTick");
        try
        {
            if (!_fw.TamperWatchEnabled) return;
            if (nowMs - _lastTamperCheckMs < (long)TamperCheckEvery.TotalMilliseconds) return;
            _lastTamperCheckMs = nowMs;

            var report = _fw.CheckIntegrity(repair: true);
            if (report.Intact || report.Expected == 0) return;

            notices.Add(new UpkeepNotice("warn", "Firewall filters were removed",
                $"{report.Missing} of {report.Expected} filters had been deleted from the kernel "
                + "by something outside GunWall. They have been re-applied. If this repeats, "
                + "another program is interfering with the firewall."));
        }
        catch (Exception ex) { DiagnosticLog.LogException("TamperWatchTick", ex); }
    }

    /// <summary>
    /// Enforces the DNS blocklist against live connections.
    ///
    /// The blocklist used to work only for lookups GunWall itself answered,
    /// which meant it did nothing at all once the machine stopped sending its
    /// DNS here. The passive watcher knows which addresses each name resolved
    /// to, so a connection to an address belonging to a blocked name is blocked
    /// in the kernel instead - which also holds for applications that bring
    /// their own resolver and never ask Windows at all.
    /// </summary>
    private void EnforceBlockedDomains(List<ConnectionInfo> conns, List<UpkeepNotice> notices)
    {
        using var _perf = PerfMonitor.Measure("EnforceBlockedDomains");
        try
        {
            if (_blockedDomainCount() == 0) return;
            if (!DnsObservations.HasData) return;

            foreach (var c in conns)
            {
                string remote = c.RemoteAddress;
                if (string.IsNullOrEmpty(remote) || remote is "0.0.0.0" or "::") continue;
                // v4 AND v6. Skipping v6 here meant a site on a CDN with AAAA
                // records was never even considered for blocking, while the log
                // reported the v4 half as "enforced".
                if (!System.Net.IPAddress.TryParse(remote, out var ip)) continue;
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
                    ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) continue;

                // Only ever block addresses that are actually out on the
                // Internet. A global filter on loopback, a LAN address or a
                // link-local one would cut the machine off from itself or its
                // own network - and a blocklist can name such an address, since
                // every hosts file maps its entries to 127.0.0.1. Nothing
                // downstream should have to rely on the list being sensible.
                if (!IpScopeClassifier.IsPublicUnicast(remote)) continue;

                string domain = DnsObservations.DomainForIp(remote);
                if (domain.Length == 0) continue;
                if (!_isDomainBlocked(domain)) continue;

                // BEFORE any filter is built, not after. Building one and then
                // removing it would leave a window in which the exempt
                // application is blocked, and would churn the kernel for a
                // decision already made.
                //
                // Only the app-scoped path is exempted. The global fallback below
                // fires precisely when the process cannot be identified, so it
                // cannot know whether an exemption applies - extending it there
                // would mean guessing, and guessing wrong means an exemption
                // silently widening to every application on the machine.
                if (!string.IsNullOrEmpty(c.ExePath) && _fw.BypassesBlocklists(c.ExePath))
                    continue;

                // APP LAYER FIRST. A filter naming one executable has no collateral
                // at all, so a shared CDN address is safe to block here - which is
                // the whole difficulty the global path has to reason about and
                // sometimes decline over.
                //
                // Keyed by app+address rather than address alone, so a second
                // application reaching the same blocked name gets its own filter
                // instead of being silently covered by the first one's.
                if (!string.IsNullOrEmpty(c.ExePath))
                {
                    if (!_appDomainBlocked.Add($"{c.ExePath}|{remote}")) continue;
                    if (_fw.AddAppDomainBlock(c.ExePath, domain, remote))
                    {
                        CloseIfTcp(c);
                        notices.Add(new UpkeepNotice("warn", $"Blocked domain reached: {domain}",
                            $"{c.ProcessName} was blocked from reaching {remote}, which belongs "
                            + $"to {domain} on your DNS blocklist. Only {c.ProcessName} is "
                            + "affected - nothing else on this PC is cut off."));
                        continue;
                    }
                    // Fall through to the global path only if the app-scoped filter
                    // could not be built - a program that has already exited, for
                    // instance.
                }

                if (!_domainBlocked.Add(remote)) continue;    // one filter per address

                if (!_fw.AddDomainReactiveBlock(domain, remote, out string declined))
                {
                    // Told, not just logged. Someone who blocks a domain and then
                    // watches traffic to it continue is owed the reason - and the
                    // reason is a deliberate decision GunWall made on their behalf,
                    // which is exactly the kind of thing that must not be silent.
                    if (declined.Length > 0)
                        notices.Add(new UpkeepNotice("info", $"{domain} was not blocked by address", declined));
                }
                else
                {
                    // Tear the session down too: a new filter stops the next
                    // connection but not one already established.
                    CloseIfTcp(c);
                    notices.Add(new UpkeepNotice("warn", $"Blocked domain reached: {domain}",
                        $"{remote} belongs to {domain}, which is on your DNS blocklist. "
                        + "Connections to it are now blocked."));
                }
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("EnforceBlockedDomains", ex); }
    }

    private void EnforceP2pBlocks(List<ConnectionInfo> conns,
                                  Dictionary<int, (string Name, string Path)> procs,
                                  List<UpkeepNotice> notices)
    {
        using var _perf = PerfMonitor.Measure("EnforceP2pBlocks");
        try
        {
            // Observations now come from the passive watcher as well as the
            // resolver, so this no longer depends on the resolver running - but
            // with no observations at all, every address would look "direct".
            if (!DnsObservations.HasData) return;
            var flagged = _fw.P2pAppPaths;
            if (flagged.Count == 0) return;

            foreach (var c in conns)
            {
                if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
                if (!procs.TryGetValue(c.ProcessId, out var pi) || pi.Path.Length == 0) continue;
                if (!flagged.Contains(pi.Path.ToLowerInvariant())) continue;

                // Public addresses of EITHER family, through the shared classifier.
                // A hand-rolled private-range test here was IPv4-only, which let an
                // application under the "block direct connections" scope reach any
                // IPv6 address it liked, unresolved and unblocked.
                if (!IpScopeClassifier.IsPublicUnicast(c.RemoteAddress)) continue;
                if (DnsObservations.WasResolved(c.RemoteAddress)) continue;

                string dedupe = pi.Path + "|" + c.RemoteAddress;
                if (_openAtStart != null && _openAtStart.Contains(dedupe)) continue;
                if (!_p2pHandled.Add(dedupe)) continue;

                bool added = _fw.AddP2pReactiveBlock(pi.Path, c.RemoteAddress);
                CloseIfTcp(c);
                if (added)
                    notices.Add(new UpkeepNotice("warn", $"Direct connection blocked: {pi.Name}",
                        $"{c.RemoteAddress}:{c.RemotePort} was never resolved via DNS - blocked as P2P/direct."));
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("EnforceP2p", ex); }
    }

    /// <summary>
    /// Evaluates each connection of policy-bearing apps against its ordered
    /// access policy (first-match-wins). Connections that evaluate to Block get
    /// a persistent per-IP reactive filter + a TCP RST, deduped per (app, ip).
    /// A wrong rule can only mis-block, never crash (roadmap risk).
    /// </summary>
    private void EnforceAccessPolicies(List<ConnectionInfo> conns,
                                       Dictionary<int, (string Name, string Path)> procs,
                                       List<UpkeepNotice> notices)
    {
        using var _perf = PerfMonitor.Measure("EnforceAccessPolicies");
        try
        {
            if (_fw.ActiveAccessPolicies.Count == 0) return;

            foreach (var c in conns)
            {
                if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
                if (!procs.TryGetValue(c.ProcessId, out var pi) || pi.Path.Length == 0) continue;

                var policy = _fw.GetAccessPolicy(pi.Path);
                if (policy is not { IsActive: true }) continue;

                string scope = IpScopeClassifier.Classify(c.RemoteAddress);
                string continent = c.Country.Length > 0 ? GeoData.Continent(c.Country) : "";
                // The connection carries only an address. The shared observation
                // memory knows the name it came from - whether GunWall's own
                // resolver answered the lookup or the system observer merely
                // watched it happen.
                string domain = DnsObservations.DomainForIp(c.RemoteAddress);
                var facts = new ConnFacts(c.RemoteAddress, scope, c.Country, continent, c.Asn, domain);

                if (AppRuleEngine.Evaluate(policy, facts) != RuleVerdict.Block) continue;

                string dedupe = pi.Path + "|" + c.RemoteAddress;
                if (!_accessHandled.Add(dedupe)) continue;

                bool added = _fw.AddAccessReactiveBlock(pi.Path, c.RemoteAddress);
                CloseIfTcp(c);
                if (added)
                    notices.Add(new UpkeepNotice("warn", $"Access rule blocked: {pi.Name}",
                        (domain.Length > 0 ? domain + " — " : "") +
                        $"{c.RemoteAddress}:{c.RemotePort}" +
                        (c.Country.Length > 0 ? $" ({GeoData.CountryName(c.Country)})" : "") +
                        " matched a block rule."));
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("EnforceAccess", ex); }
    }

    private void CloseIfTcp(ConnectionInfo c)
    {
        if (c.Protocol != "TCP") return;
        try { _closeTcp(c); }
        catch { /* the filter already stops the next connection */ }
    }
}
