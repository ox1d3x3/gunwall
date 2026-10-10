using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// The engine as the background service runs it (0.99.199): the same startup as
/// the window (EngineStartup), the same detection (DetectionHost) on a dedicated
/// engine thread, and no window - so a program that needs a decision is queued in
/// PendingPrompts and asked about when the window next opens. Core Windows
/// processes are still allowed automatically when that setting is on.
/// </summary>
public sealed class ServiceEngineSession : IEngineSession
{
    private readonly EngineThread _thread = new("GunWall engine");
    private FirewallManager? _fw;
    private DetectionHost? _detection;
    private EngineUpkeep? _upkeep;
    private DnsEventMonitorService? _dnsObserver;
    /// <summary>The DNS blocklist, for blocked-domain enforcement only - the
    /// service does not answer DNS. Built in the background; until then no name
    /// counts as blocked, exactly as in the window before its list is loaded.</summary>
    private volatile DnsResolver? _blocklist;
    private long _lastUpkeepMs;
    private readonly string _pendingPath = ProfilePaths.FileIn(PendingPrompts.FileName);

    public ServiceEngineSession()
    {
        try
        {
            _thread.Invoke(() =>
            {
                FirewallManager.ServiceHost = true;
                DnsEventMonitorService.ServiceInstance = true;   // its own ETW session name
                _fw = new FirewallManager();
                _fw.TimerContext = _thread;   // timed blocks and pauses end on this thread
                _fw.EnsureSettingsLoaded();
                _fw.Initialize();
                // No launch backup: the service starts a session every time the
                // window closes, and backups are never pruned.
                EngineStartup.AfterInitialize(_fw, launchBackup: false);
                int pruned = EngineStartup.RestoreAndReconcile(_fw);
                // A pause saved by the last run: finish it on time, or put protection
                // back if it ended while nothing ran (0.99.203).
                string snooze = _fw.ReconcileSnooze();
                if (snooze.Length > 0) DiagnosticLog.Log("Service engine: " + snooze);
                DiagnosticLog.Log($"Service engine: up - protection {(_fw.StrictMode ? "ON" : "off (monitoring)")}, "
                                + $"{_fw.GetRules().Count} rule(s), {pruned} pruned.");

                _detection = new DetectionHost(_fw, new NetworkMonitor(), new ProcessService(), _thread,
                                               () => _fw != null, Environment.ProcessPath, Environment.SystemDirectory);
                _detection.Detected += OnDetected;
                _detection.TryStartEventMonitor();

                // The enforcement the window runs on live connections (0.99.203).
                _fw.DomainBlockTest = n => { try { return _blocklist?.IsBlocked(n) ?? false; } catch { return false; } };
                _fw.DomainForceTest = n => { try { return _blocklist?.IsForced(n) ?? false; } catch { return false; } };
                _upkeep = new EngineUpkeep(_fw, () => _blocklist?.BlockedDomainCount ?? 0,
                                           n => { try { return _blocklist?.IsBlocked(n) ?? false; } catch { return false; } });
                _detection.Snapshot += OnSnapshot;

                // Names behind addresses, for P2P detection, blocked domains and
                // access rules on a domain - as the window does when its setting is on.
                bool dnsWatch = false;
                if (_fw.DnsObserveSystemLookups)
                {
                    _dnsObserver = new DnsEventMonitorService();
                    dnsWatch = _dnsObserver.Start();
                    if (!dnsWatch)
                        DiagnosticLog.Log($"Service: DNS observer could not start ({_dnsObserver.LastError}).");
                }

                _detection.StartPolling();
                DiagnosticLog.Log($"Service engine: detection running (kernel events {(_detection.EventDriven ? "on" : "off")}, "
                                + $"DNS watching {(dnsWatch ? "on" : "off")}, tamper watch {(_fw.TamperWatchEnabled ? "on" : "off")}"
                                + $"{(_fw.IsSnoozed ? $", paused until {_fw.SnoozeUntil:HH:mm:ss}" : "")}).");
            });

            // Country and ASN rules need the GeoIP tables, as in the window - loaded
            // off the engine thread, after protection is already up.
            var fw = _fw;
            _ = Task.Run(() =>
            {
                try { fw?.LoadGeoIp(); }
                catch (Exception ex) { DiagnosticLog.LogException("ServiceGeoIp", ex); }
                // The DNS blocklist: the preset can be 100,000 names, so it is read
                // here and published in one reference write.
                try
                {
                    List<string> user = new();
                    if (_thread.IsDisposed) return;
                    _thread.Invoke(() => { if (_fw != null) user = _fw.DnsResolverBlocklist.ToList(); });
                    var r = new DnsResolver();
                    r.SetBlocklist(DnsResolver.MergedBlocklist(user));
                    _blocklist = r;
                    if (r.BlockedDomainCount > 0)
                        DiagnosticLog.Log($"Service engine: DNS blocklist loaded, {r.BlockedDomainCount:N0} name(s) enforced by address.");
                }
                catch (Exception) when (_thread.IsDisposed) { /* the session ended meanwhile */ }
                catch (Exception ex) { DiagnosticLog.LogException("ServiceBlocklist", ex); }
            });
        }
        catch
        {
            // A half-built session must not keep a WFP handle or a thread alive
            // across the 30-second retries.
            Dispose();
            throw;
        }
    }

    private void OnDetected(DetectionResult r, string source)
    {
        if (_fw == null) return;
        foreach (var q in r.AutoAllow)
        {
            _fw.AllowApp(q.ExePath, q.ProcessName);
            DiagnosticLog.Log($"Allowed automatically: {q.ProcessName} (core Windows process, seen in the {source}, by the service).");
        }
        if (r.Prompts.Count == 0) return;
        try
        {
            int added = PendingPrompts.Add(_pendingPath, r.Prompts);
            if (added > 0)
                DiagnosticLog.Log($"Service: {added} application(s) waiting for a decision when the window opens ("
                                + string.Join(", ", r.Prompts.Select(p => p.ProcessName)) + ").");
        }
        catch (Exception ex) { DiagnosticLog.LogException("PendingPrompts.Add", ex); }
    }

    /// <summary>
    /// Enforcement on live connections, once a second like the window's snapshot:
    /// P2P blocks, blocked domains, the tamper watch and access rules (0.99.203).
    /// Before this they stopped whenever the window was closed.
    /// </summary>
    private void OnSnapshot(List<ConnectionInfo> conns, Dictionary<int, (string Name, string Path)> procs)
    {
        if (_fw == null || _upkeep == null) return;
        long now = Environment.TickCount64;      // forward-only: a clock change cannot stall it
        if (now - _lastUpkeepMs < 1000) return;
        _lastUpkeepMs = now;

        // What the window's snapshot adds before its enforcement runs: the program
        // behind each connection, and its location for country / ASN access rules.
        bool geo = _fw.GeoIpActive;
        var memo = new Dictionary<string, GeoIpService.GeoInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conns)
        {
            if (procs.TryGetValue(c.ProcessId, out var p)) { c.ProcessName = p.Name; c.ExePath = p.Path ?? ""; }
            if (!geo || c.Country.Length > 0 || string.IsNullOrEmpty(c.RemoteAddress)) continue;
            if (!memo.TryGetValue(c.RemoteAddress, out var g))
            {
                try { g = _fw.GeoIp.Lookup(c.RemoteAddress); } catch { g = new GeoIpService.GeoInfo("", 0, ""); }
                memo[c.RemoteAddress] = g;
            }
            if (g.HasData) { c.Country = g.Country; c.Asn = g.Asn; c.AsnOwner = g.Owner; }
        }

        // No window to show a notification in: the log has it.
        foreach (var n in _upkeep.Tick(conns, procs))
            DiagnosticLog.Log($"Service: {n.Title} - {n.Body}");
    }

    public void Dispose()
    {
        if (_thread.IsDisposed) return;
        try
        {
            _thread.Invoke(() =>
            {
                // The DNS observer first: the window starts its own as soon as the
                // engine is handed over (a different session name, but no reason to
                // run both).
                try { _dnsObserver?.Stop(); } catch { }
                _dnsObserver = null;
                _detection?.Dispose();   // clean stop: clears the crash-guard marker
                _detection = null;
                _fw?.Dispose();          // closes the WFP session; filters stay in the kernel
                _fw = null;
            });
        }
        finally { _thread.Dispose(); }
    }
}
