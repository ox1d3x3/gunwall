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

    // The engine pipe (stage 4b, 0.99.208): only when the window uses the service.
    private Remote.EngineServer? _server;
    private readonly List<DetectionResult> _batch = new();
    private long _lastFlushMs, _lastStateMs;

    /// <summary>This session offers the engine pipe, so a window that asks to
    /// attach is told to connect instead of being handed the engine.</summary>
    public static volatile bool ServingRemote;

    public ServiceEngineSession()
    {
        ServingRemote = false;
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

                _lastStrict = _fw.StrictMode;
                _lastEventsWant = _fw.ExperimentalEvents;
                FirewallManager.ServiceStatusProvider = StatusReport;   // runs on this thread, via the pipe
                _fw.ProtectionChanged += OnProtectionChanged;
                _detection.Polled += FollowSettings;
                if (ServiceMode.Requested)
                {
                    _server = new Remote.EngineServer(_fw, _thread, (caller, access) => true, UpdateService.CurrentVersion);
                    _server.Start();
                    _detection.Polled += FlushToWindows;
                    ServingRemote = true;
                    DiagnosticLog.Log("Service engine: the engine pipe is open - a window that uses the service connects to it.");
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
                LoadBlocklist();
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

    /// <summary>The DNS blocklist: the preset can be 100,000 names, so it is read
    /// off the engine thread and published in one reference write. Again whenever
    /// the list changes (a window using the service edits it, 0.99.208).</summary>
    private void LoadBlocklist()
    {
        try
        {
            List<string> user = new();
            if (_thread.IsDisposed) return;
            _thread.Invoke(() => { if (_fw != null) user = _fw.DnsResolverBlocklist.ToList(); });
            var r = new DnsResolver();
            r.SetBlocklist(DnsResolver.MergedBlocklist(user));
            _blocklist = r;
            _blocklistKey = BlocklistKey(user);
            if (r.BlockedDomainCount > 0)
                DiagnosticLog.Log($"Service engine: DNS blocklist loaded, {r.BlockedDomainCount:N0} name(s) enforced by address.");
        }
        catch (Exception) when (_thread.IsDisposed) { /* the session ended meanwhile */ }
        catch (Exception ex) { DiagnosticLog.LogException("ServiceBlocklist", ex); }
    }

    private volatile string _blocklistKey = "";
    private int _blocklistReloading;

    /// <summary>Changes when the user's lines or the preset file change.</summary>
    private static string BlocklistKey(IReadOnlyCollection<string> user)
    {
        long presetLen = 0, presetTicks = 0;
        try
        {
            var fi = new System.IO.FileInfo(ProfilePaths.FileIn(DnsResolver.PresetFileName));
            if (fi.Exists) { presetLen = fi.Length; presetTicks = fi.LastWriteTimeUtc.Ticks; }
        }
        catch { }
        int h = 17;
        foreach (var l in user) h = unchecked(h * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(l));
        return $"{user.Count}:{h}:{presetLen}:{presetTicks}";
    }

    private long _lastFollowMs;
    private bool? _lastStrict;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>The service's live state for a window's diagnostics bundle
    /// (0.99.217) - before, the bundle only said "in the background service".</summary>
    private string StatusReport()
    {
        var sb = new System.Text.StringBuilder();
        var up = DateTime.UtcNow - _startedUtc;
        sb.AppendLine($"Service session: engine up since {_startedUtc.ToLocalTime():HH:mm:ss} ({(int)up.TotalHours}h {up.Minutes:00}m), "
                    + $"{_server?.SessionCount ?? 0} window(s) connected, protection {(_fw?.StrictMode == true ? "ON" : "off")}, "
                    + $"lockdown {_fw?.LockdownEngaged == true}, {(_fw?.IsSnoozed == true ? $"paused until {_fw.SnoozeUntil:HH:mm:ss}" : "not paused")}.");
        sb.AppendLine($"Service detection: kernel events {(_detection?.EventDriven == true ? "on" : "off")} "
                    + $"(setting {(_fw?.ExperimentalEvents == true ? "on" : "off")}{(_detection?.EventsRecovered == true ? ", switched off after repeated crashes" : "")}), "
                    + $"detect errors {_detection?.DetectErrors ?? 0}, tamper watch {(_fw?.TamperWatchEnabled == true ? "on" : "off")}.");
        sb.AppendLine(_dnsObserver == null
            ? $"Service DNS watching: off (setting {(_fw?.DnsObserveSystemLookups == true ? "on" : "off")})."
            : $"Service DNS watching: {(_dnsObserver.SessionActive ? "active" : "NOT active")}, {_dnsObserver.EventsSeen:N0} events, "
              + $"{_dnsObserver.AnswersRecorded:N0} answers, {_dnsObserver.ParseFailures} parse failures, lastError=[{_dnsObserver.LastError}].");
        sb.AppendLine($"Service DNS blocklist: {_blocklist?.BlockedDomainCount ?? 0:N0} name(s) enforced by address; "
                    + $"custom list {_fw?.CustomDomainCount ?? 0:N0} domain(s).");
        return sb.ToString().TrimEnd();
    }
    private bool _lastEventsWant;

    /// <summary>
    /// Settings the window changes through the service while the session runs
    /// (0.99.208): the DNS observer follows its switch, and the blocklist is
    /// rebuilt when it changes. Every five seconds, on the engine thread. Before,
    /// the window applied these to its own engine, and the service read them
    /// once, when its session started.
    /// </summary>
    private void FollowSettings()
    {
        if (_fw == null) return;
        long now = Environment.TickCount64;
        if (now - _lastFollowMs < 5000) return;
        _lastFollowMs = now;
        try
        {
            bool want = _fw.DnsObserveSystemLookups;
            if (want && _dnsObserver == null)
            {
                _dnsObserver = new DnsEventMonitorService();
                if (!_dnsObserver.Start()) DiagnosticLog.Log($"Service: DNS observer could not start ({_dnsObserver.LastError}).");
            }
            else if (!want && _dnsObserver != null) { _dnsObserver.Stop(); _dnsObserver = null; }

            // Kernel event detection switched in Settings (0.99.216). The window's
            // "restart GunWall" does not restart the service, so it follows here;
            // only on a change, so a start that failed is not retried every 5 s.
            bool events = _fw.ExperimentalEvents;
            if (events != _lastEventsWant && _detection != null)
            {
                _lastEventsWant = events;
                if (events) _detection.TryStartEventMonitor(); else _detection.StopEventMonitor();
                DiagnosticLog.Log($"Service engine: kernel event detection {(events ? (_detection.EventDriven ? "on" : "could not start") : "off")} (setting changed).");
            }

            if (BlocklistKey(_fw.DnsResolverBlocklist) != _blocklistKey
                && Interlocked.Exchange(ref _blocklistReloading, 1) == 0)
                _ = Task.Run(() => { try { LoadBlocklist(); } finally { Volatile.Write(ref _blocklistReloading, 0); } });
        }
        catch (Exception ex) { DiagnosticLog.LogException("Service.FollowSettings", ex); }
    }

    /// <summary>Protection switched on or off: everything undecided may be asked
    /// about again, as the window's switch did (SyncFirewallToggle).</summary>
    private void OnProtectionChanged()
    {
        if (_fw == null) return;
        bool strict = _fw.StrictMode;
        if (_lastStrict != null && _lastStrict != strict) _detection?.Detector.ResetPrompts();
        _lastStrict = strict;
        _server?.PushState();
    }

    /// <summary>Every poll, on the engine thread: what detection decided goes to
    /// the windows in one batch, and the state at least every two seconds (rules
    /// pruned, a setting changed by a timer - changes no call reported).</summary>
    private void FlushToWindows()
    {
        var server = _server;
        if (server == null) return;
        long now = Environment.TickCount64;
        if (_batch.Count > 0 && now - _lastFlushMs >= 250)
        {
            // Core Windows processes were already allowed here (OnDetected); the
            // window must not allow them a second time.
            foreach (var b in _batch) b.AutoAllow.Clear();
            bool hasPrompt = _batch.Any(b => b.Prompts.Count > 0);
            server.Push(new Remote.EngineMessage { Kind = "detected", Detected = _batch.ToList() }, droppable: !hasPrompt);
            _batch.Clear();
            _lastFlushMs = now;
        }
        if (now - _lastStateMs >= 2000) { server.PushState(); _lastStateMs = now; }
    }

    private void OnDetected(DetectionResult r, string source)
    {
        if (_fw == null) return;
        // A connected window shows it as it happens: packet log, activity, prompts.
        if (_server != null && _server.SessionCount > 0
            && (r.Packet != null || r.Activity.Count > 0 || r.Prompts.Count > 0 || r.FirstSeen.Count > 0))
        {
            if (_batch.Count < 500) _batch.Add(r);
        }
        foreach (var q in r.AutoAllow)
        {
            _fw.AllowApp(q.ExePath, q.ProcessName);
            DiagnosticLog.Log($"Allowed automatically: {q.ProcessName} (core Windows process, seen in the {source}, by the service).");
        }
        if (r.Prompts.Count == 0) return;
        // A connected window was just sent them; the queue is for when none is.
        if (_server != null && _server.SessionCount > 0) return;
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

        // Logged, and shown by a connected window as a notification.
        var notices = _upkeep.Tick(conns, procs);
        foreach (var n in notices)
            DiagnosticLog.Log($"Service: {n.Title} - {n.Body}");
        if (notices.Count > 0) _server?.Push(new Remote.EngineMessage { Kind = "notice", Notices = notices }, droppable: false);
    }

    public void Dispose()
    {
        if (_thread.IsDisposed) return;
        try
        {
            _thread.Invoke(() =>
            {
                FirewallManager.ServiceStatusProvider = null;
                // The engine pipe first: no window may call into an engine that is
                // being taken apart.
                ServingRemote = false;
                try { _server?.Dispose(); } catch { }
                _server = null;
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
