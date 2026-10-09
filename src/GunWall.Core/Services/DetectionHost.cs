using System.IO;
using GunWall.Models;
using GunWall.Services.Wfp;

namespace GunWall.Services;

/// <summary>
/// Runs detection: the 300 ms connection-table poll, the kernel event
/// subscription and its crash guard, feeding <see cref="ConnectionDetector"/>
/// (0.99.198, service split stage 1b). Moved out of MainWindow unchanged, so a
/// background service can host it without a window.
///
/// THREADING - deliberately the same as before the move. The table reads run on
/// the thread pool; every decision runs on <c>engineContext</c>, one thread at a
/// time, because FirewallManager is not thread-safe. The window passes its own
/// UI context, so decisions run exactly where they ran when this code lived in
/// MainWindow. A service will pass a dedicated engine thread instead.
///
/// The host decides nothing and shows nothing: <see cref="Detected"/> carries each
/// pass's result to whoever displays it.
/// </summary>
public sealed class DetectionHost : IDisposable
{
    private readonly FirewallManager _fw;
    private readonly NetworkMonitor _monitor;
    private readonly ProcessService _processes;
    private readonly SynchronizationContext _ctx;
    private readonly Func<bool> _engineReady;
    private CancellationTokenSource? _cts;
    private NetEventMonitor? _netEvents;

    // Crash-guard marker state (see EventCrashGuard).
    private DateTime _eventsStartedUtc, _eventsHeartbeatUtc;
    private int _eventStrikes;

    public ConnectionDetector Detector { get; }

    /// <summary>Kernel events are running (the poll always runs as well).</summary>
    public bool EventDriven { get; private set; }

    /// <summary>The crash guard switched kernel events off at this start.</summary>
    public bool EventsRecovered { get; private set; }

    public int DetectErrors { get; private set; }

    /// <summary>One detection pass and where it came from ("connection table" or
    /// "kernel events"). Raised on the engine context.</summary>
    public event Action<DetectionResult, string>? Detected;

    /// <summary>After every poll, on the engine context, whether or not the engine
    /// is ready - for periodic upkeep that used to ride on the poll.</summary>
    public event Action? Polled;

    public DetectionHost(FirewallManager firewall, NetworkMonitor monitor, ProcessService processes,
                         SynchronizationContext engineContext, Func<bool> engineReady,
                         string? selfPath, string? system32Dir)
    {
        _fw = firewall;
        _monitor = monitor;
        _processes = processes;
        _ctx = engineContext;
        _engineReady = engineReady;
        Detector = new ConnectionDetector(firewall, selfPath, system32Dir);
    }

    /// <summary>Starts the poll. Call once.</summary>
    public void StartPolling()
    {
        _cts ??= new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    // 300 ms is frequent enough to catch brief VPN/handshake connections without
    // measurable CPU cost (the table reads are cheap).
    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (conns, procs) = await Task.Run(() =>
                {
                    var c = _monitor.GetTcpConnections();
                    var pr = _processes.SnapshotProcesses();
                    return (c, pr);
                }, ct).ConfigureAwait(false);

                await OnEngine(() =>
                {
                    // Approval prompts and §1 reactive geo-blocking (polling path).
                    if (_engineReady()) Detected?.Invoke(Detector.OnPoll(conns, procs, DateTime.Now), "connection table");
                    EventMarkerHeartbeat();
                    Polled?.Invoke();
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                DetectErrors++;
                if (DetectErrors == 1 || DetectErrors % 20 == 0)
                    DiagnosticLog.Log($"Detection loop error #{DetectErrors}: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(300, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs <paramref name="work"/> on the engine context and completes
    /// when it has - so one poll's decisions finish before the next poll reads.</summary>
    private Task OnEngine(Action work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ctx.Post(_ =>
        {
            try { work(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }

    // ============================================================ kernel events
    /// <summary>
    /// Starts kernel event detection if it is enabled and the crash guard allows
    /// it. Call on the engine context, after the engine is up.
    ///
    /// CRASH-LOOP GUARD: kernel event interop runs in a callback that, if its
    /// struct layout is wrong on this OS build, can hard-crash the process. A
    /// marker file exists while events run and is deleted on a clean exit.
    /// EventCrashGuard decides what a leftover marker means: since 0.99.194 only
    /// repeated SHORT unclean runs switch detection off. One leftover marker did,
    /// before - and every upgrade force-closes GunWall, so detection was silently
    /// off on ordinary machines.
    /// </summary>
    public void TryStartEventMonitor()
    {
        if (!_engineReady() || _fw.EngineHandle == IntPtr.Zero) return;
        if (!_fw.ExperimentalEvents) { EventDriven = false; return; }

        int strikes = 0;
        try
        {
            string marker = EventMarkerPath();
            bool exists = File.Exists(marker);
            string? text = exists ? File.ReadAllText(marker) : null;
            var verdict = EventCrashGuard.Decide(text, exists);
            if (exists)
            {
                File.Delete(marker);
                DiagnosticLog.Log($"Kernel event detection: {verdict.Reason}.");
            }
            if (verdict.Disable)
            {
                _fw.SetExperimentalEvents(false);
                EventDriven = false;
                EventsRecovered = true; // surfaced in the UI as a notice
                return;
            }
            strikes = verdict.Strikes;
        }
        catch { /* if the guard itself fails, fall through cautiously */ }

        try
        {
            _eventsStartedUtc = DateTime.UtcNow;
            _eventStrikes = strikes;
            WriteEventMarker();
            _netEvents = new NetEventMonitor(_fw.EngineHandle);
            _netEvents.ConnectionEvent += OnKernelConnectionEvent;
            EventDriven = _netEvents.Start();
            if (!EventDriven) ClearEventMarker(); // didn't start, no crash risk
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"event monitor unavailable: {ex.Message}");
            EventDriven = false;
            ClearEventMarker();
        }
    }

    // Called on a kernel thread for every filtered connection. Posted to the
    // engine context, then the same approval logic the poller uses.
    private void OnKernelConnectionEvent(NetEventMonitor.Event e)
    {
        _ctx.Post(_ =>
        {
            if (!_engineReady() || string.IsNullOrEmpty(e.AppPath)) return;
            // Activity, Packet Log verdict (with the kernel-disagreement wording),
            // CSV logging, §1 entity blocks and the approval decision all live in
            // ConnectionDetector; the subscriber only shows the result.
            Detected?.Invoke(Detector.OnKernelEvent(
                e.AppPath, e.RemoteAddress, e.RemotePort, e.Protocol, e.Dropped, DateTime.Now), "kernel events");
        }, null);
    }

    private string EventMarkerPath() => Path.Combine(_fw.ProfileFolder, "events.lock");

    /// <summary>Writes the marker with the start time, now as the heartbeat, and
    /// the strikes carried from earlier short unclean runs.</summary>
    private void WriteEventMarker()
    {
        _eventsHeartbeatUtc = DateTime.UtcNow;
        File.WriteAllText(EventMarkerPath(), EventCrashGuard.Format(
            new EventCrashGuard.Marker(_eventsStartedUtc, _eventsHeartbeatUtc, _eventStrikes)));
    }

    /// <summary>Refreshes the marker's heartbeat while events run, so a later
    /// unclean exit can be told apart from a crash soon after starting.</summary>
    private void EventMarkerHeartbeat()
    {
        if (!EventDriven) return;
        if (DateTime.UtcNow - _eventsHeartbeatUtc < EventCrashGuard.HeartbeatEvery) return;
        try { WriteEventMarker(); }
        catch { _eventsHeartbeatUtc = DateTime.UtcNow; /* retry next interval */ }
    }

    private void ClearEventMarker()
    {
        try { if (File.Exists(EventMarkerPath())) File.Delete(EventMarkerPath()); }
        catch { /* best effort */ }
    }

    /// <summary>A clean stop: the poll ends, kernel events unsubscribe, and the
    /// crash-guard marker is removed - this is not a crash.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        ClearEventMarker(); // clean exit — not a crash
        _netEvents?.Dispose();
        _netEvents = null;
    }
}
