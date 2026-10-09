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
    private readonly string _pendingPath = ProfilePaths.FileIn(PendingPrompts.FileName);

    public ServiceEngineSession()
    {
        try
        {
            _thread.Invoke(() =>
            {
                FirewallManager.ServiceHost = true;
                _fw = new FirewallManager();
                _fw.EnsureSettingsLoaded();
                _fw.Initialize();
                // No launch backup: the service starts a session every time the
                // window closes, and backups are never pruned.
                EngineStartup.AfterInitialize(_fw, launchBackup: false);
                int pruned = EngineStartup.RestoreAndReconcile(_fw);
                DiagnosticLog.Log($"Service engine: up - protection {(_fw.StrictMode ? "ON" : "off (monitoring)")}, "
                                + $"{_fw.GetRules().Count} rule(s), {pruned} pruned.");

                _detection = new DetectionHost(_fw, new NetworkMonitor(), new ProcessService(), _thread,
                                               () => _fw != null, Environment.ProcessPath, Environment.SystemDirectory);
                _detection.Detected += OnDetected;
                _detection.TryStartEventMonitor();
                _detection.StartPolling();
                DiagnosticLog.Log($"Service engine: detection running (kernel events {(_detection.EventDriven ? "on" : "off")}).");
            });

            // Country and ASN rules need the GeoIP tables, as in the window - loaded
            // off the engine thread, after protection is already up.
            var fw = _fw;
            _ = Task.Run(() =>
            {
                try { fw?.LoadGeoIp(); }
                catch (Exception ex) { DiagnosticLog.LogException("ServiceGeoIp", ex); }
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

    public void Dispose()
    {
        if (_thread.IsDisposed) return;
        try
        {
            _thread.Invoke(() =>
            {
                _detection?.Dispose();   // clean stop: clears the crash-guard marker
                _detection = null;
                _fw?.Dispose();          // closes the WFP session; filters stay in the kernel
                _fw = null;
            });
        }
        finally { _thread.Dispose(); }
    }
}
