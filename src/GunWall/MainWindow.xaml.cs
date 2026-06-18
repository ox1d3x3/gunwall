using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using GunWall.Models;
using GunWall.Services;
using GunWall.Services.Wfp;

namespace GunWall;

public partial class MainWindow : Window
{
    private readonly FirewallManager _firewall = new();
    private readonly NetworkMonitor _monitor = new();
    private readonly ProcessService _processes = new();

    private readonly ObservableCollection<AppInfo> _apps = new();
    private readonly ObservableCollection<ConnectionInfo> _connections = new();
    private readonly ObservableCollection<NetActivityEvent> _activity = new();
    private readonly ObservableCollection<PacketLogEntry> _packets = new();
    private readonly ObservableCollection<ServicesService.ServiceItem> _services = new();
    private readonly ObservableCollection<NetworkScanner.Device> _devices = new();

    private const int GraphPoints = 60;
    private readonly double[] _downSeries = new double[GraphPoints];
    private readonly double[] _upSeries = new double[GraphPoints];

    private long _lastRx = -1, _lastTx = -1;
    private DateTime _lastSample = DateTime.MinValue;
    private bool _engineReady;

    // Background sampling
    private CancellationTokenSource? _cts;
    private NetEventMonitor? _netEvents;
    private bool _eventDriven; // true when kernel events are active (no polling needed for detection)
    private bool _eventsRecovered; // true if we auto-disabled events after a prior crash
    private volatile int _intervalMs = 1000;
    private string _appFilter = "";
    private string _connFilter = "";
    private bool _showAllApps;

    // Activity feed bookkeeping
    private readonly HashSet<string> _seenConnections = new();
    private const int MaxActivity = 300;
    private const int MaxPackets = 1000;

    // Connection alerts (a connection popup)
    private readonly Queue<AlertWindow.AlertInfo> _alertQueue = new();
    private bool _alertOpen;
    private bool _knownSeeded;
    // Apps we've already raised a Zero-Trust prompt for this session (prevents
    // re-prompting every 300ms while the user hasn't decided). Cleared when
    // strict mode is toggled so a fresh takeover re-prompts everything.
    private readonly HashSet<string> _promptedThisSession =
        new(StringComparer.OrdinalIgnoreCase);

    // Session data totals
    private long _sessionStartRx = -1, _sessionStartTx = -1;

    // Tray
    private System.Windows.Forms.NotifyIcon? _tray;

    // Latest snapshot kept for instant re-filtering when search text changes.
    private List<ConnectionInfo> _lastConns = new();
    private Dictionary<int, (string Name, string Path)> _lastProcs = new();

    public MainWindow()
    {
        InitializeComponent();
        AppsList.ItemsSource = _apps;
        ConnList.ItemsSource = _connections;
        ActivityList.ItemsSource = _activity;
        PacketsList.ItemsSource = _packets;
        ServicesList.ItemsSource = _services;
        DevicesList.ItemsSource = _devices;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    // True only when the user explicitly chooses Exit (tray menu / dialog), so
    // the X button can be redirected to "hide to tray" instead of quitting.
    private bool _reallyExit;

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return; // genuine exit — let it close

        // The X button hides to tray instead of exiting. This keeps the UI one
        // click away while the firewall stays active, so the user is never left
        // with traffic blocked and no way to manage it. A balloon explains it
        // the first time.
        e.Cancel = true;
        Hide();
        if (_tray != null && !_trayHintShown)
        {
            _trayHintShown = true;
            try
            {
                _tray.BalloonTipTitle = "GunWall is still running";
                _tray.BalloonTipText = _firewall.StrictMode
                    ? "The firewall stays active here. Right-click the tray icon to open or exit."
                    : "GunWall keeps running in the tray. Right-click to open or exit.";
                _tray.ShowBalloonTip(3000);
            }
            catch { /* balloon is best-effort */ }
        }
    }

    private bool _trayHintShown;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Apply the saved theme first so the whole window paints correctly.
        bool dark = _firewall.ThemeDark;
        if (ThemeToggle != null) ThemeToggle.IsChecked = !dark; // checked = light
        ApplyTheme(dark);

        SetupTray();

        try
        {
            _firewall.Initialize();
            _engineReady = true;
            _firewall.ReconcileTempBlocks(); // re-arm or expire timed blocks after a restart
            _firewall.AutoBackupIfEnabled(); // snapshot the profile on launch (if enabled)
            _firewall.MigrateLegacyBlocklists(); // move v0.24 IP-filter blocklists to the hosts model
            Services.DiagnosticLog.Init(_firewall.ProfileFolder); // point the log at the real data folder
            Services.DiagnosticLog.Log($"GunWall {Services.UpdateService.CurrentVersion} loaded. Engine started: {_firewall.EngineHandle != IntPtr.Zero}.");

            // Auto-refresh views when the network changes (Wi-Fi switch, VPN up/down).
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) =>
                Dispatcher.Invoke(() => { try { RebuildConnList(); RebuildAppsList(); } catch { } });
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, _) =>
                Dispatcher.Invoke(() => { try { RebuildConnList(); } catch { } });
            EngineStatus.Text = "Engine: active";
            SyncLockdownButton();
            _suppressModeEvent = true;
            AlertsCheck.IsChecked = _firewall.AlertsEnabled;
            StartMinimizedCheck.IsChecked = _firewall.StartMinimized;
            RunAtStartupCheck.IsChecked = _firewall.RunAtStartup;
            if (EventLogCheck != null) EventLogCheck.IsChecked = _firewall.EventLogEnabled;
            if (NotifSoundCheck != null) NotifSoundCheck.IsChecked = _firewall.NotificationSound;
            if (TrayNotifCheck != null) TrayNotifCheck.IsChecked = _firewall.TrayNotifications;
            if (PacketLogFileCheck != null) PacketLogFileCheck.IsChecked = _firewall.PacketFileLogging;
            if (PopupTimeoutCombo != null)
                PopupTimeoutCombo.SelectedIndex = _firewall.PopupTimeoutSeconds switch
                {
                    15 => 0, 30 => 1, 60 => 2, 0 => 3, _ => 1
                };
            if (PopupDefaultCombo != null)
                PopupDefaultCombo.SelectedIndex = _firewall.PopupDefaultAllow ? 0 : 1;
            if (AutoBackupCheck != null) AutoBackupCheck.IsChecked = _firewall.AutoBackup;
            if (VtKeyStatus != null)
                VtKeyStatus.Text = string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey)
                    ? "No key set." : "A key is saved.";
            AlwaysOnTopCheck.IsChecked = _firewall.AlwaysOnTop;
            HashesCheck.IsChecked = _firewall.HashesEnabled;
            ExperimentalEventsCheck.IsChecked = _firewall.ExperimentalEvents;
            _suppressModeEvent = false;
            SyncFirewallToggle();
            if (ApplyButton != null) ApplyButton.IsEnabled = false;

            // Apply window prefs immediately.
            Topmost = _firewall.AlwaysOnTop;
            if (_firewall.StartMinimized) WindowState = WindowState.Minimized;

            AboutText.Text = $"GunWall v0.33.0 - free, open-source, no telemetry. " +
                             $"Your profile is saved at: {_firewall.ProfileFolder}";

            // Try event-driven detection (kernel net events). If it starts, it
            // becomes the primary detector and we stop relying on polling for
            // new-app prompts. If it fails on this machine, we silently keep the
            // poll-based detector — no regression.
            TryStartEventMonitor();

            if (PacketsSubtitle != null)
            {
                PacketsSubtitle.Text = _eventDriven
                    ? "Live connection events from the kernel - allowed and blocked, system services included."
                    : "Kernel events are off; this log fills from polling. Enable event detection in Settings for full coverage.";
            }

            if (_eventsRecovered)
            {
                _activity.Insert(0, new NetActivityEvent
                {
                    ProcessName = "GunWall",
                    Detail = "Kernel event detection was auto-disabled after an unclean exit; " +
                             "polling is active. Re-enable in Settings if you want to retry."
                });
            }
        }
        catch (Exception ex)
        {
            _engineReady = false;
            EngineStatus.Text = "Engine: unavailable";
            MessageBox.Show(
                "GunWall could not initialise the Windows Filtering Platform.\n\n" +
                "Make sure you are running as administrator.\n\nDetails: " + ex.Message,
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _cts = new CancellationTokenSource();
        _ = SampleLoopAsync(_cts.Token);
        _ = DetectionLoopAsync(_cts.Token);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
        ClearEventMarker(); // clean exit — not a crash
        _netEvents?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        _firewall.Dispose();
    }

    // ================================================================ sampling
    /// <summary>
    /// The heart of the performance fix: ALL expensive work (process snapshot,
    /// TCP/UDP tables, interface statistics) runs on a background thread via
    /// Task.Run. Only the cheap UI application happens on the dispatcher, so
    /// the window stays perfectly responsive regardless of system load.
    /// </summary>
    private async Task DetectionLoopAsync(CancellationToken ct)
    {
        // 300ms is frequent enough to catch brief VPN/handshake connections
        // without measurable CPU cost (the table reads are cheap).
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (conns, procs) = await Task.Run(() =>
                {
                    var c = _monitor.GetTcpConnections();
                    var pr = _processes.SnapshotProcesses();
                    return (c, pr);
                }, ct);
                DetectNewApps(conns, procs);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Debug.WriteLine($"detect error: {ex.Message}"); }

            try { await Task.Delay(300, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SampleLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await Task.Run(() => CollectSnapshot(), ct);
                ApplySnapshot(snap); // continuation resumes on the UI thread
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Debug.WriteLine($"sample error: {ex.Message}"); }

            try { await Task.Delay(_intervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed record Snapshot(
        List<ConnectionInfo> Conns,
        Dictionary<int, (string Name, string Path)> Procs,
        double DownRate, double UpRate);

    private Snapshot CollectSnapshot()
    {
        var conns = _monitor.GetTcpConnections();
        var procs = _processes.SnapshotProcesses();

        foreach (var c in conns)
            c.ProcessName = procs.TryGetValue(c.ProcessId, out var p) ? p.Name : $"PID {c.ProcessId}";

        var (rx, tx) = _monitor.GetCumulativeBytes();
        var now = DateTime.UtcNow;
        double downRate = 0, upRate = 0;
        if (_lastRx >= 0)
        {
            double secs = Math.Max(0.001, (now - _lastSample).TotalSeconds);
            downRate = Math.Max(0, (rx - _lastRx) / secs);
            upRate = Math.Max(0, (tx - _lastTx) / secs);
        }
        _lastRx = rx; _lastTx = tx; _lastSample = now;

        return new Snapshot(conns, procs, downRate, upRate);
    }

    private void ApplySnapshot(Snapshot snap)
    {
        _lastConns = snap.Conns;
        _lastProcs = snap.Procs;

        Shift(_downSeries, snap.DownRate);
        Shift(_upSeries, snap.UpRate);
        DownSpeed.Text = FormatRate(snap.DownRate);
        UpSpeed.Text = FormatRate(snap.UpRate);
        ConnCount.Text = snap.Conns.Count.ToString();

        RecordActivity(snap.Conns);
        UpdateSessionTotals();

        if (PanelConnections.Visibility == Visibility.Visible) RebuildConnList();
        if (PanelFirewall.Visibility == Visibility.Visible) RebuildAppsList();
        if (PanelDashboard.Visibility == Visibility.Visible) RedrawGraph();
    }

    private static void Shift(double[] series, double newest)
    {
        Array.Copy(series, 1, series, 0, series.Length - 1);
        series[^1] = newest;
    }

    // ================================================================ activity
    private void RecordActivity(List<ConnectionInfo> conns)
    {
        foreach (var c in conns)
        {
            if (string.IsNullOrEmpty(c.RemoteAddress)) continue;          // UDP listeners
            if (c.RemoteAddress is "0.0.0.0" or "::") continue;            // unbound
            string key = $"{c.ProcessId}|{c.RemoteAddress}:{c.RemotePort}";
            if (!_seenConnections.Add(key)) continue;

            _activity.Insert(0, new NetActivityEvent
            {
                ProcessName = c.ProcessName,
                Detail = $"connected to {c.RemoteEndpoint} ({c.Protocol})"
            });
            while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);
        }
        // Keep the dedupe set bounded.
        if (_seenConnections.Count > 8000) _seenConnections.Clear();
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e) => _activity.Clear();

    private void ClearPackets_Click(object sender, RoutedEventArgs e) => _packets.Clear();

    // ================================================================ custom rules
    private void RefreshRulesList()
    {
        if (RulesList == null) return;
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _firewall.CustomRules;
        if (BlocklistCount != null)
            BlocklistCount.Text = $"{_firewall.Blocklist.Count} address(es) blocked. " +
                                  "Paste IPv4 addresses (one per line) to add more.";
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        try
        {
            bool block = (RuleAction.SelectedIndex == 0);
            bool outbound = (RuleDirection.SelectedIndex == 0);
            string protocol = ((ComboBoxItem)RuleProtocol.SelectedItem)?.Content?.ToString() ?? "Any";
            string addr = RuleAddress.Text?.Trim() ?? "";
            int port = 0;
            if (!string.IsNullOrWhiteSpace(RulePort.Text) && !int.TryParse(RulePort.Text.Trim(), out port))
            {
                MessageBox.Show("Port must be a number (or blank for any).", "GunWall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            int localPort = 0;
            if (!string.IsNullOrWhiteSpace(RuleLocalPort.Text) && !int.TryParse(RuleLocalPort.Text.Trim(), out localPort))
            {
                MessageBox.Show("Local port must be a number (or blank for any).", "GunWall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!string.IsNullOrEmpty(addr) && !IsValidIpOrCidr(addr))
            {
                MessageBox.Show("Address must be a valid IPv4 (e.g. 1.2.3.4) or subnet " +
                    "(e.g. 192.168.1.0/24), or blank for any.", "GunWall",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var rule = new CustomRule
            {
                Block = block,
                Outbound = outbound,
                Protocol = protocol,
                RemoteAddress = addr,
                RemotePort = port,
                LocalPort = localPort,
                Enabled = true
            };
            _firewall.AddCustomRule(rule);
            RefreshRulesList();

            if (!rule.Applied)
                MessageBox.Show(
                    "The rule was saved but the firewall filter could not be applied on this " +
                    "system (the condition may be unsupported). It shows as 'Not applied'.",
                    "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);

            RuleAddress.Clear();
            RulePort.Clear();
            RuleLocalPort.Clear();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            try { _firewall.RemoveCustomRule(id); RefreshRulesList(); }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    /// <summary>Accepts a bare IPv4 or IPv4/prefix CIDR (e.g. 10.0.0.0/8).</summary>
    private static bool IsValidIpOrCidr(string s)
    {
        int slash = s.IndexOf('/');
        string ipPart = slash >= 0 ? s[..slash] : s;
        if (!System.Net.IPAddress.TryParse(ipPart, out var ip) ||
            ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
        if (slash >= 0)
        {
            if (!int.TryParse(s[(slash + 1)..], out int prefix) || prefix is < 0 or > 32)
                return false;
        }
        return true;
    }

    private void AddBlocklist_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        try
        {
            var lines = (BlocklistInput.Text ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int n = _firewall.AddToBlocklist(lines);
            BlocklistInput.Clear();
            RefreshRulesList();
            MessageBox.Show($"Added {n} address(es) to the blocklist.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearBlocklist_Click(object sender, RoutedEventArgs e)
    {
        try { _firewall.ClearBlocklist(); RefreshRulesList(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PacketsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = PacketsSearch.Text?.Trim() ?? "";
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_packets);
        if (string.IsNullOrEmpty(q))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = o =>
            {
                if (o is not PacketLogEntry p) return false;
                return p.AppName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.RemoteEndpoint.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.Protocol.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || p.Action.Contains(q, StringComparison.OrdinalIgnoreCase);
            };
        }
    }

    // ================================================================ alerts
    /// <summary>
    /// Shows a popup the first time an executable is ever observed using the
    /// network. Detection is PER-PROCESS, not per-remote: any app with a TCP
    /// connection OR a UDP socket (so VPNs that only use outbound UDP, like
    /// WireGuard/OpenVPN tunnels, are caught) triggers exactly one alert. On
    /// the very first run, everything currently networked is seeded as known
    /// so the user isn't flooded. Runs on the fast detection loop.
    /// </summary>
    // ================================================================ event-driven detection
    private void TryStartEventMonitor()
    {
        if (!_engineReady || _firewall.EngineHandle == IntPtr.Zero) return;
        if (!_firewall.ExperimentalEvents) { _eventDriven = false; return; }

        // CRASH-LOOP GUARD: kernel event interop runs in a callback that, if its
        // struct layout is wrong on this OS build, can hard-crash the process.
        // We write a marker file before subscribing and delete it on clean exit.
        // If we find the marker at startup, the previous run did NOT exit cleanly
        // while events were on — so we disable events this run and recover
        // automatically, instead of crash-looping. The user can re-enable in
        // Settings once they understand it's unstable on their machine.
        try
        {
            string marker = EventMarkerPath();
            if (File.Exists(marker))
            {
                File.Delete(marker);
                _firewall.SetExperimentalEvents(false);
                _eventDriven = false;
                _eventsRecovered = true; // surfaced in the UI as a notice
                return;
            }
        }
        catch { /* if the guard itself fails, fall through cautiously */ }

        try
        {
            File.WriteAllText(EventMarkerPath(), DateTime.UtcNow.ToString("o"));
            _netEvents = new NetEventMonitor(_firewall.EngineHandle);
            _netEvents.ConnectionEvent += OnKernelConnectionEvent;
            _eventDriven = _netEvents.Start();
            if (!_eventDriven) ClearEventMarker(); // didn't start, no crash risk
            System.Diagnostics.Debug.WriteLine($"event-driven detection: {_eventDriven}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"event monitor unavailable: {ex.Message}");
            _eventDriven = false;
            ClearEventMarker();
        }
    }

    private string EventMarkerPath() =>
        System.IO.Path.Combine(_firewall.ProfileFolder, "events.lock");

    private void ClearEventMarker()
    {
        try { if (File.Exists(EventMarkerPath())) File.Delete(EventMarkerPath()); }
        catch { /* best effort */ }
    }

    // Called on a kernel thread for every filtered connection. Marshal to the UI
    // thread, then run the same approval logic the poller uses.
    private void OnKernelConnectionEvent(NetEventMonitor.Event e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_engineReady || string.IsNullOrEmpty(e.AppPath)) return;
            if (string.Equals(e.AppPath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                return;

            // Log into the activity feed (every event, drop or allow).
            string verb = e.Dropped ? "blocked" : "connected to";
            string remote = string.IsNullOrEmpty(e.RemoteAddress)
                ? "" : $" {e.RemoteAddress}:{e.RemotePort}";
            _activity.Insert(0, new NetActivityEvent
            {
                ProcessName = System.IO.Path.GetFileNameWithoutExtension(e.AppPath),
                Detail = $"{verb}{remote} ({e.Protocol})"
            });
            while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);

            // Packets Log: derive the action from GunWall's own decision, which
            // we know for certain — no risky extra kernel reads. In strict mode
            // an app that isn't explicitly allowed is being blocked by default.
            string appName = System.IO.Path.GetFileNameWithoutExtension(e.AppPath);
            bool blocked = _firewall.IsBlocked(e.AppPath) ||
                           (_firewall.StrictMode &&
                            !_firewall.IsAllowed(e.AppPath) &&
                            !_firewall.IsSilent(e.AppPath));
            _packets.Insert(0, new PacketLogEntry
            {
                AppName = appName,
                ExePath = e.AppPath,
                Protocol = e.Protocol,
                Direction = "Out",
                RemoteEndpoint = string.IsNullOrEmpty(e.RemoteAddress)
                    ? "\u2014" : $"{e.RemoteAddress}:{e.RemotePort}",
                Blocked = blocked
            });
            while (_packets.Count > MaxPackets) _packets.RemoveAt(_packets.Count - 1);

            // Optional: persist this entry to the CSV log file.
            _firewall.LogPacketToFile(DateTime.Now, blocked, appName, e.Protocol, "Out",
                string.IsNullOrEmpty(e.RemoteAddress) ? "" : $"{e.RemoteAddress}:{e.RemotePort}",
                e.AppPath);

            // Approval pipeline: prompt once for any undecided app.
            string path = e.AppPath;
            if (_firewall.IsBlocked(path) || _firewall.IsAllowed(path) || _firewall.IsSilent(path))
                return;
            if (!_firewall.AlertsEnabled) return;
            if (!_promptedThisSession.Add(path)) return;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            _alertQueue.Enqueue(new AlertWindow.AlertInfo(
                name, path, e.RemoteAddress, e.RemotePort, e.Protocol, DateTime.Now));
            ShowNextAlert();
        });
    }

    private void DetectNewApps(List<ConnectionInfo> conns,
                              Dictionary<int, (string Name, string Path)> procs)
    {
        // Polling runs ALONGSIDE kernel events (when active) as a redundant
        // detector. Both paths share _promptedThisSession, so an app already
        // prompted by an event won't be prompted again by the poller. This
        // gives the union of both: events catch blocked/ICMP/short-lived
        // connections; polling catches anything events might miss.
        if (!_engineReady) return;

        bool strict = _firewall.StrictMode;

        // Seeding only applies to monitoring mode (allow-by-default), where we
        // don't want to notify for everything already running. In strict mode
        // (Zero Trust) we WANT to prompt for every undecided app, so we skip
        // seeding entirely.
        if (!strict && !_knownSeeded)
        {
            _knownSeeded = true;
            var seed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in conns)
            {
                if (c.RemoteAddress is "127.0.0.1" or "::1") continue;
                if (procs.TryGetValue(c.ProcessId, out var p) && !string.IsNullOrEmpty(p.Path))
                    seed.Add(p.Path);
            }
            _firewall.SeedKnownApps(seed);
            return;
        }

        if (!_firewall.AlertsEnabled) return;

        foreach (var c in conns)
        {
            // Skip pure loopback (always permitted).
            if (c.RemoteAddress is "127.0.0.1" or "::1") continue;
            if (!procs.TryGetValue(c.ProcessId, out var proc)) continue;
            if (string.IsNullOrEmpty(proc.Path)) continue;
            if (string.Equals(proc.Path, Environment.ProcessPath,
                              StringComparison.OrdinalIgnoreCase)) continue;

            // A decided app never prompts again (approval/denial persists).
            if (_firewall.IsBlocked(proc.Path)) continue;
            if (_firewall.IsAllowed(proc.Path)) continue;
            if (_firewall.IsSilent(proc.Path)) continue; // allowed + muted

            if (strict)
            {
                // Zero Trust: ANY socket-owning, undecided app must be approved.
                // It is already blocked by the default-deny rule; prompt once
                // per session (the session set prevents 300ms re-spam). If the
                // user ignores it, it stays blocked.
                if (!_promptedThisSession.Add(proc.Path)) continue;
            }
            else
            {
                // Monitoring: notify once ever, only for real outbound contact.
                if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
                if (c.RemoteAddress is "0.0.0.0" or "::") continue;
                if (!_firewall.MarkKnown(proc.Path)) continue;
            }

            string remote = c.RemoteAddress;
            if (remote is "0.0.0.0" or "::") remote = ""; // unbound -> pending
            _alertQueue.Enqueue(new AlertWindow.AlertInfo(
                proc.Name, proc.Path, remote, c.RemotePort, c.Protocol, DateTime.Now));
        }

        ShowNextAlert();
    }

    private void ShowNextAlert()
    {
        if (_alertOpen || _alertQueue.Count == 0) return;
        var info = _alertQueue.Dequeue();
        _alertOpen = true;

        // Optional notification sound.
        if (_firewall.NotificationSound)
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }

        // Optional tray balloon for the newly detected app.
        if (_firewall.TrayNotifications && _tray != null)
        {
            try
            {
                _tray.BalloonTipTitle = "New network access";
                _tray.BalloonTipText = $"{info.ProcessName} is trying to connect.";
                _tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        var win = new AlertWindow(info,
            onBlock: () =>
            {
                _firewall.BlockApp(info.ExePath, info.ProcessName);
                RebuildAppsList();
            },
            onAllow: () =>
            {
                _firewall.AllowApp(info.ExePath, info.ProcessName); // permits in strict mode
                RebuildAppsList();
            },
            strictMode: _firewall.StrictMode,
            timeoutSeconds: _firewall.PopupTimeoutSeconds,
            defaultAllow: _firewall.PopupDefaultAllow);
        win.Owner = this;
        win.Closed += (_, _) => { _alertOpen = false; ShowNextAlert(); };
        win.Show();
    }

    private void UpdateSessionTotals()
    {
        if (_sessionStartRx < 0) { _sessionStartRx = _lastRx; _sessionStartTx = _lastTx; }
        SessionDown.Text = $"Session: {FormatBytes(Math.Max(0, _lastRx - _sessionStartRx))}";
        SessionUp.Text = $"Session: {FormatBytes(Math.Max(0, _lastTx - _sessionStartTx))}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    // ================================================================ lists
    private void RebuildAppsList()
    {
        var source = _showAllApps
            ? _processes.GetAllApps(_lastConns, _lastProcs)
            : _processes.GetNetworkedApps(_lastConns, _lastProcs);

        var known = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in source) known[a.ExecutablePath] = a;

        foreach (var rule in _firewall.GetRules())
        {
            if (!known.TryGetValue(rule.ExecutablePath, out var app))
            {
                app = new AppInfo { Name = rule.DisplayName, ExecutablePath = rule.ExecutablePath };
                known[rule.ExecutablePath] = app;
            }
        }

        foreach (var a in known.Values)
        {
            a.Status = _firewall.EffectiveStatus(a.ExecutablePath);
            a.Silent = _firewall.IsSilent(a.ExecutablePath);
            a.Hash = _firewall.GetHash(a.ExecutablePath);
            a.Category = ComputeCategory(a.ExecutablePath);
            a.Publisher = a.Category == AppCategory.System
                ? "Windows / system"
                : Services.SignatureService.PublisherLabel(a.ExecutablePath);
            a.Icon = Services.IconService.GetIcon(a.ExecutablePath);
        }

        IEnumerable<AppInfo> view = known.Values;
        if (!string.IsNullOrWhiteSpace(_appFilter))
            view = view.Where(a =>
                a.Name.Contains(_appFilter, StringComparison.OrdinalIgnoreCase) ||
                a.ExecutablePath.Contains(_appFilter, StringComparison.OrdinalIgnoreCase));

        _apps.Clear();
        foreach (var a in view
                     .OrderByDescending(a => a.Status == AppStatus.Blocked) // blocked pinned on top
                     .ThenByDescending(a => a.ActiveConnections)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            _apps.Add(a);
    }

    /// <summary>Cheap visual category: missing file, system path, signed, or unsigned.</summary>
    private static AppCategory ComputeCategory(string path)
    {
        if (string.IsNullOrEmpty(path)) return AppCategory.System;
        try
        {
            if (!System.IO.File.Exists(path)) return AppCategory.Unknown;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) &&
                path.StartsWith(win, StringComparison.OrdinalIgnoreCase))
                return AppCategory.System;

            // Real Authenticode validation (cached), not just a name read: a file
            // with a broken, expired, untrusted or forged signature is flagged
            // Invalid rather than trusted.
            return SignatureService.Verify(path).Status switch
            {
                SignatureStatus.Valid    => AppCategory.Signed,
                SignatureStatus.Unsigned => AppCategory.Unsigned,
                SignatureStatus.Invalid  => AppCategory.Invalid,
                _                        => AppCategory.Unknown
            };
        }
        catch { return AppCategory.Unknown; }
    }

    private void RebuildConnList()
    {
        IEnumerable<ConnectionInfo> view = _lastConns;
        if (!string.IsNullOrWhiteSpace(_connFilter))
            view = view.Where(c =>
                c.ProcessName.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
                c.RemoteAddress.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
                c.LocalAddress.Contains(_connFilter, StringComparison.OrdinalIgnoreCase) ||
                c.State.Contains(_connFilter, StringComparison.OrdinalIgnoreCase));

        _connections.Clear();
        foreach (var c in view.OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
            _connections.Add(c);
    }

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _appFilter = AppSearch.Text;
        RebuildAppsList();
    }

    // ---------------------------------------------- connection context menu
    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (ConnList.SelectedItem is not ConnectionInfo c) return;
        if (c.ProcessId <= 0)
        {
            MessageBox.Show("No process is associated with this row.",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (FirewallManager.IsCriticalProcessName(c.ProcessName))
        {
            var crit = MessageBox.Show(
                $"{c.ProcessName} is a core Windows process. Ending it can crash Windows or force a restart.\n\nEnd it anyway?",
                "Caution: system process", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (crit != MessageBoxResult.Yes) return;
        }
        var ask = MessageBox.Show(
            $"End process {c.ProcessName} (PID {c.ProcessId})?\n\nUnsaved work in that program will be lost.",
            "End process", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;

        if (ProcessService.KillProcess(c.ProcessId))
        {
            _firewall.EventLog($"Ended process {c.ProcessName} (PID {c.ProcessId})");
            RebuildConnList();
        }
        else
        {
            MessageBox.Show("Couldn't end that process (it may have already exited, or it needs higher privileges).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CloseConn_Click(object sender, RoutedEventArgs e)
    {
        if (ConnList.SelectedItem is not ConnectionInfo c) return;
        if (!c.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Only TCP connections can be closed (UDP is connectionless).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        bool ok = ConnectionService.CloseTcpConnection(c.LocalAddress, c.LocalPort, c.RemoteAddress, c.RemotePort);
        if (ok)
        {
            _firewall.EventLog($"Closed connection {c.RemoteEndpoint} ({c.ProcessName})");
            RebuildConnList();
        }
        else
        {
            MessageBox.Show("Couldn't close that connection. It may have already closed, or it's " +
                "owned by a protected system process.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private string ResolveConnPath(ConnectionInfo c)
    {
        try
        {
            var procs = _processes.SnapshotProcesses();
            if (procs.TryGetValue(c.ProcessId, out var info)) return info.Path;
        }
        catch { }
        return "";
    }

    private void BlockConnApp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (ConnList.SelectedItem is not ConnectionInfo c) return;
        string path = ResolveConnPath(c);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            MessageBox.Show("Couldn't resolve this connection's program (it may be a protected " +
                "system process).", "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _firewall.BlockApp(path, c.ProcessName);
            MessageBox.Show($"Blocked {c.ProcessName}.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BlockAndCloseConn_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (ConnList.SelectedItem is not ConnectionInfo c) return;
        string path = ResolveConnPath(c);
        try
        {
            if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                _firewall.BlockApp(path, c.ProcessName);

            // Close every current TCP connection belonging to that process.
            int closed = 0;
            foreach (var other in _lastConns.Where(x => x.ProcessId == c.ProcessId &&
                                                        x.Protocol.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)))
            {
                if (ConnectionService.CloseTcpConnection(other.LocalAddress, other.LocalPort,
                        other.RemoteAddress, other.RemotePort)) closed++;
            }
            _firewall.EventLog($"Blocked {c.ProcessName} and closed {closed} connection(s)");
            RebuildConnList();
            MessageBox.Show($"Blocked {c.ProcessName} and closed {closed} active connection(s).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyConnRemote_Click(object sender, RoutedEventArgs e)
    {
        if (ConnList.SelectedItem is not ConnectionInfo c) return;
        try { Clipboard.SetText(c.RemoteAddress); } catch { /* clipboard busy */ }
    }

    private void ConnSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _connFilter = ConnSearch.Text;
        RebuildConnList();
    }

    private void ShowAllApps_Changed(object sender, RoutedEventArgs e)
    {
        _showAllApps = ShowAllAppsCheck.IsChecked == true;
        RebuildAppsList();
    }

    // ================================================================ graph
    private void RedrawGraph()
    {
        var canvas = GraphCanvas;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        double max = 64 * 1024;
        for (int i = 0; i < GraphPoints; i++)
        {
            max = Math.Max(max, _downSeries[i]);
            max = Math.Max(max, _upSeries[i]);
        }

        DrawBaseline(canvas, w, h);
        // Palette: cool blue for download, signature orange for upload.
        AddSeries(canvas, _downSeries, max, w, h, Color.FromRgb(0x3D, 0xA9, 0xFC));
        AddSeries(canvas, _upSeries, max, w, h, Color.FromRgb(0x7C, 0x5C, 0xFF));
    }

    private static void DrawBaseline(Canvas canvas, double w, double h)
    {
        var line = new Line
        {
            X1 = 0, Y1 = h - 1, X2 = w, Y2 = h - 1,
            Stroke = new SolidColorBrush(Color.FromRgb(0x2A, 0x34, 0x46)),
            StrokeThickness = 1
        };
        canvas.Children.Add(line);
    }

    private static void AddSeries(Canvas canvas, double[] series, double max,
                                  double w, double h, Color color)
    {
        var poly = new Polyline
        {
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round
        };
        double stepX = w / (GraphPoints - 1);
        for (int i = 0; i < series.Length; i++)
        {
            double x = i * stepX;
            double y = h - (series[i] / max * (h - 4)) - 2;
            poly.Points.Add(new Point(x, y));
        }
        canvas.Children.Add(poly);

        // Soft vertical gradient fill under the line.
        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, color.R, color.G, color.B), 0.0));
        fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(8, color.R, color.G, color.B), 1.0));

        var fill = new Polygon { Fill = fillBrush };
        foreach (var p in poly.Points) fill.Points.Add(p);
        fill.Points.Add(new Point(w, h));
        fill.Points.Add(new Point(0, h));
        canvas.Children.Insert(0, fill);
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawGraph();

    // ================================================================ nav
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDashboard == null) return; // during init
        string tag = (string)((RadioButton)sender).Tag;
        PanelDashboard.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        PanelFirewall.Visibility = tag == "Firewall" ? Visibility.Visible : Visibility.Collapsed;
        PanelConnections.Visibility = tag == "Connections" ? Visibility.Visible : Visibility.Collapsed;
        PanelServices.Visibility = tag == "Services" ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = tag == "Network" ? Visibility.Visible : Visibility.Collapsed;
        PanelPackets.Visibility = tag == "Packets" ? Visibility.Visible : Visibility.Collapsed;
        PanelRules.Visibility = tag == "Rules" ? Visibility.Visible : Visibility.Collapsed;
        PanelSystem.Visibility = tag == "System" ? Visibility.Visible : Visibility.Collapsed;
        PanelSecurity.Visibility = tag == "Security" ? Visibility.Visible : Visibility.Collapsed;
        PanelActivity.Visibility = tag == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        // Populate immediately on switch instead of waiting for the next tick.
        if (tag == "Dashboard") RefreshDashboardStats();
        if (tag == "Firewall") RebuildAppsList();
        if (tag == "Connections") RebuildConnList();
        if (tag == "Rules") RefreshRulesList();
        if (tag == "Services" && _services.Count == 0) LoadServices();
        if (tag == "System") BuildSystemRulesUi();
        if (tag == "Security") { BuildBlocklistCatUi(); RefreshDnsCombo(); }
        if (tag == "Settings") { RefreshProfilesCombo(); RefreshBackupsCombo(); RefreshWinFwStatus(); }
    }

    // ================================================================ actions
    private void ToggleApp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (((Button)sender).DataContext is not AppInfo app) return;

        try
        {
            if (_firewall.EffectiveStatus(app.ExecutablePath) == AppStatus.Blocked)
                _firewall.AllowApp(app.ExecutablePath, app.Name);
            else
            {
                if (FirewallManager.IsCriticalProcess(app.ExecutablePath))
                {
                    var ask = MessageBox.Show(
                        $"{app.Name} is a core Windows process. Blocking it can break networking, " +
                        "updates, or sign-in.\n\nBlock it anyway?",
                        "Caution: system process", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (ask != MessageBoxResult.Yes) return;
                }
                _firewall.BlockApp(app.ExecutablePath, app.Name);
            }

            RebuildAppsList();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void BrowseBlock_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Select an application to block",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
            _firewall.BlockApp(dlg.FileName, name);
            RebuildAppsList();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => RebuildAppsList();

    private void MuteApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        try { _firewall.SetSilent(app.ExecutablePath, app.Name, true); RebuildAppsList(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UnmuteApp_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        try { _firewall.SetSilent(app.ExecutablePath, app.Name, false); RebuildAppsList(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        try { Clipboard.SetText(app.ExecutablePath); } catch { /* clipboard busy */ }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        try
        {
            if (System.IO.File.Exists(app.ExecutablePath))
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{app.ExecutablePath}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        try
        {
            var win = new AppPropertiesWindow(app, _firewall) { Owner = this };
            bool? changed = win.ShowDialog();
            if (changed == true) RebuildAppsList(); // a rule was applied from the dialog
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void PurgeUnused_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int removed = _firewall.PurgeUnusedApps();
            RebuildAppsList();
            MessageBox.Show(
                removed == 0 ? "No unused apps to remove." : $"Removed {removed} unused app(s).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ================================================================ dashboard / snooze / updates
    /// <summary>Dashboard stat cards are clickable: jump to the relevant tab.</summary>
    private void Stat_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string target) return;
        RadioButton? nav = target switch
        {
            "Firewall" => NavFirewall,
            "Rules" => NavRules,
            "System" => NavSystem,
            "Connections" => NavConnections,
            _ => null
        };
        if (nav != null) nav.IsChecked = true; // fires Nav_Checked -> switches panel
    }

    private void RefreshDashboardStats()
    {
        if (StatApps == null) return;
        try
        {
            StatApps.Text = _apps.Count.ToString();
            StatBlocked.Text = _apps.Count(a => a.Status == AppStatus.Blocked).ToString();
            StatAllowed.Text = _apps.Count(a => a.Status == AppStatus.Allowed).ToString();
            StatRules.Text = _firewall.CustomRules.Count.ToString();
            StatBlocklist.Text = _firewall.Blocklist.Count.ToString();
            int sys = 0;
            foreach (var k in new[] { "block_inbound", "block_ipv6", "block_smb", "block_netbios", "block_rdp_in", "block_telnet" })
                if (_firewall.IsSystemRuleOn(k)) sys++;
            StatSystem.Text = sys.ToString();
            UpdateSnoozeUi();
        }
        catch { /* stats are cosmetic */ }
    }

    private void UpdateSnoozeUi()
    {
        if (SnoozeStatus == null) return;
        if (_firewall.IsSnoozed)
        {
            SnoozeStatus.Text = $"Protection paused until {_firewall.SnoozeUntil:t}";
            if (ResumeButton != null) ResumeButton.Visibility = Visibility.Visible;
        }
        else
        {
            SnoozeStatus.Text = "";
            if (ResumeButton != null) ResumeButton.Visibility = Visibility.Collapsed;
        }
    }

    private void Snooze_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string mins) return;
        if (!int.TryParse(mins, out int minutes)) return;
        try
        {
            var until = _firewall.SnoozeProtection(TimeSpan.FromMinutes(minutes));
            UpdateSnoozeUi();
            SyncLockdownButton();
            MessageBox.Show($"Protection paused until {until:t}. It resumes automatically " +
                "(and always comes back if you restart GunWall).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        try { _firewall.EndSnooze(); UpdateSnoozeUi(); SyncLockdownButton(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string folder = _firewall.ProfileFolder;
            System.IO.Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ExportDiag_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save GunWall diagnostics",
            Filter = "Zip archive (*.zip)|*.zip",
            FileName = "GunWall-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        string path = dlg.FileName;
        if (ExportDiagBtn != null) ExportDiagBtn.IsEnabled = false;
        if (DiagStatus != null) DiagStatus.Text = "Collecting diagnostics\u2026 this takes a few seconds.";
        try
        {
            await System.Threading.Tasks.Task.Run(() => _firewall.ExportDiagnostics(path));
            if (DiagStatus != null) DiagStatus.Text = "Saved to: " + path;
            // Reveal the file in Explorer so it's easy to attach.
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex)
        {
            ShowError(ex);
            if (DiagStatus != null) DiagStatus.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            if (ExportDiagBtn != null) ExportDiagBtn.IsEnabled = true;
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateStatus == null) return;
        UpdateStatus.Text = "Checking for updates...";
        try
        {
            var r = await UpdateService.CheckAsync();
            UpdateStatus.Text = r.Message;
            if (r.Ok && r.UpdateAvailable)
            {
                var ask = MessageBox.Show($"{r.Message}\n\nOpen the downloads page?",
                    "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
                if (ask == MessageBoxResult.Yes)
                    try { Process.Start(new ProcessStartInfo(r.Url) { UseShellExecute = true }); } catch { }
            }
        }
        catch (Exception ex) { UpdateStatus.Text = "Update check failed."; Debug.WriteLine(ex.Message); }
    }

    // ================================================================ VirusTotal
    private void SaveVtKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _firewall.SetVirusTotalApiKey(VtApiKeyBox.Password);
            if (VtKeyStatus != null)
                VtKeyStatus.Text = string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey)
                    ? "Key cleared."
                    : "Key saved. Right-click an app in the Apps tab to scan it.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void ScanVt_Click(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is not AppInfo app) return;
        if (string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey))
        {
            MessageBox.Show("Add your VirusTotal API key in Settings first.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string hash = _firewall.GetHash(app.ExecutablePath);
        if (string.IsNullOrEmpty(hash))
            hash = HashService.Compute(app.ExecutablePath);
        if (string.IsNullOrEmpty(hash))
        {
            MessageBox.Show("Could not read this file to hash it (it may be protected).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = await VirusTotalService.LookupAsync(hash, _firewall.VirusTotalApiKey);
        var icon = result.Ok && result.Malicious == 0 ? MessageBoxImage.Information
                 : result.Ok ? MessageBoxImage.Warning
                 : MessageBoxImage.Error;
        MessageBox.Show($"{app.Name}\n\n{result.Message}", "VirusTotal scan",
            MessageBoxButton.OK, icon);
    }

    // ================================================================ system rules
    private string _systemFilter = "";

    // ---------------------------------------------- curated blocklists UI
    private readonly List<CheckBox> _blocklistToggles = new();
    private bool _blocklistBusy;

    private void BuildBlocklistCatUi()
    {
        if (BlocklistCatList == null) return;
        _blocklistToggles.Clear();
        BlocklistCatList.Children.Clear();
        foreach (var cat in Models.BlocklistCatalog.All)
            BlocklistCatList.Children.Add(BuildBlocklistCard(cat));
    }

    private Border BuildBlocklistCard(Models.BlocklistCategory cat)
    {
        bool on = _firewall.IsBlocklistOn(cat.Key);
        int count = _firewall.BlocklistDomainCount(cat.Key);

        var name = new TextBlock { Text = cat.Name, FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock
        {
            Text = cat.Key == "ads"
                ? $"{cat.Description}  (via AdGuard DNS)"
                : $"{cat.Description}  ({count:n0} domains)",
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var left = new StackPanel { MaxWidth = 620 };
        left.Children.Add(name);
        left.Children.Add(desc);
        DockPanel.SetDock(left, Dock.Left);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("SlideToggle"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = cat.Key,
            IsChecked = on
        };
        toggle.Click += Blocklist_Click;
        _blocklistToggles.Add(toggle);
        DockPanel.SetDock(toggle, Dock.Right);

        var dock = new DockPanel { LastChildFill = false };
        dock.Children.Add(toggle);
        dock.Children.Add(left);
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 8),
            Child = dock
        };
    }

    private async void Blocklist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || cb.Tag is not string key) return;
        var cat = Models.BlocklistCatalog.All.FirstOrDefault(c => c.Key == key);
        if (cat == null) return;

        // Serialize: a hosts-file rewrite must not overlap another. Ignore clicks
        // while one is running (and revert this checkbox so it doesn't desync).
        if (_blocklistBusy) { cb.IsChecked = _firewall.IsBlocklistOn(key); return; }

        bool on = cb.IsChecked == true;
        _blocklistBusy = true;
        foreach (var t in _blocklistToggles) t.IsEnabled = false;   // freeze all toggles
        if (UpdateListsBtn != null) UpdateListsBtn.IsEnabled = false;
        if (BlocklistProgress != null) BlocklistProgress.Visibility = Visibility.Visible;
        if (BlocklistCatStatus != null)
            BlocklistCatStatus.Text = on
                ? $"Applying \u201c{cat.Name}\u201d\u2026 this can take a few seconds."
                : $"Removing \u201c{cat.Name}\u201d\u2026";
        try
        {
            // Run the work, but keep the loading state visible for a moment so the
            // toggle reads as a deliberate action rather than an instant flicker.
            var op = System.Threading.Tasks.Task.Run(() => _firewall.SetBlocklistEnabled(key, on));
            await System.Threading.Tasks.Task.WhenAll(op, System.Threading.Tasks.Task.Delay(600));
            bool ok = op.Result;

            if (!ok) cb.IsChecked = !on; // revert the visual to match reality (nothing was applied)
            if (BlocklistCatStatus != null)
            {
                if (!ok)
                    BlocklistCatStatus.Text =
                        $"Couldn't apply \u201c{cat.Name}\u201d \u2014 it's too large to block without the hosts file, which Windows Defender is blocking here. Use the Filtering DNS option below for ads/trackers.";
                else if (key == "ads")
                    BlocklistCatStatus.Text = on
                        ? "Ads & trackers is on \u2014 blocking at the DNS layer with AdGuard. (Changing the Filtering DNS provider below overrides this.)"
                        : "Ads & trackers is off \u2014 DNS set back to automatic.";
                else if (on && _firewall.IsBlocklistViaWfp(key))
                    BlocklistCatStatus.Text =
                        $"\u201c{cat.Name}\u201d is on \u2014 enforced via firewall rules (Windows Defender blocked the hosts-file method, so GunWall blocked the addresses directly).";
                else
                    BlocklistCatStatus.Text = $"\u201c{cat.Name}\u201d is {(on ? "on" : "off")}.";
            }
            if (key == "ads") RefreshDnsCombo(); // keep the Filtering DNS card in sync
        }
        catch (Exception ex)
        {
            cb.IsChecked = !on; // revert on error
            ShowError(ex);
        }
        finally
        {
            _blocklistBusy = false;
            foreach (var t in _blocklistToggles) t.IsEnabled = true;
            if (UpdateListsBtn != null) UpdateListsBtn.IsEnabled = true;
            if (BlocklistProgress != null) BlocklistProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void UpdateLists_Click(object sender, RoutedEventArgs e)
    {
        if (_blocklistBusy) return;
        _blocklistBusy = true;
        foreach (var t in _blocklistToggles) t.IsEnabled = false;
        if (UpdateListsBtn != null) UpdateListsBtn.IsEnabled = false;
        if (BlocklistProgress != null) BlocklistProgress.Visibility = Visibility.Visible;
        if (BlocklistCatStatus != null)
            BlocklistCatStatus.Text = "Downloading the latest community blocklists\u2026 this can take up to a minute.";
        try
        {
            string summary = await _firewall.UpdateBlocklistsOnlineAsync();
            if (BlocklistCatStatus != null) BlocklistCatStatus.Text = "Updated \u2014 " + summary;
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _blocklistBusy = false;
            if (UpdateListsBtn != null) UpdateListsBtn.IsEnabled = true;
            if (BlocklistProgress != null) BlocklistProgress.Visibility = Visibility.Collapsed;
            BuildBlocklistCatUi(); // domain counts changed; rebuild reflects them (toggles re-enabled here)
        }
    }


    /// <summary>Builds the System Rules cards from the catalog (filtered by search).</summary>
    private void BuildSystemRulesUi()
    {
        if (SystemBlockList == null || SystemAllowList == null) return;
        SystemBlockList.Children.Clear();
        SystemAllowList.Children.Clear();

        foreach (var preset in Models.SystemRuleCatalog.All)
        {
            if (_systemFilter.Length > 0 &&
                !preset.Name.Contains(_systemFilter, StringComparison.OrdinalIgnoreCase) &&
                !preset.Description.Contains(_systemFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = preset.Category == "allow" ? SystemAllowList : SystemBlockList;
            target.Children.Add(BuildSystemRuleCard(preset));
        }
    }

    private Border BuildSystemRuleCard(Models.SystemRulePreset preset)
    {
        var name = new TextBlock { Text = preset.Name, FontWeight = FontWeights.SemiBold };
        var desc = new TextBlock
        {
            Text = preset.Description,
            Style = (Style)FindResource("Muted"),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        var left = new StackPanel { MaxWidth = 640 };
        left.Children.Add(name);
        left.Children.Add(desc);
        DockPanel.SetDock(left, Dock.Left);

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("SlideToggle"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = preset.Key,
            IsChecked = _firewall.IsSystemRuleOn(preset.Key)
        };
        toggle.Click += SystemRule_Click;
        DockPanel.SetDock(toggle, Dock.Right);

        var dock = new DockPanel { LastChildFill = false };
        dock.Children.Add(toggle);
        dock.Children.Add(left);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Child = dock
        };
    }

    private void SystemSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _systemFilter = SystemSearch.Text?.Trim() ?? "";
        BuildSystemRulesUi();
    }

    private void SecureBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        var ask = MessageBox.Show(
            "Turn on a recommended hardening baseline?\n\n" +
            "This enables: block inbound RDP, block SMB, block NetBIOS, and block Telnet. " +
            "It won't touch your other rules, and you can turn any of them back off.",
            "Secure baseline", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;
        try
        {
            foreach (var key in new[] { "block_rdp_in", "block_smb", "block_netbios", "block_telnet" })
                if (!_firewall.IsSystemRuleOn(key)) _firewall.SetSystemRule(key, true);
            BuildSystemRulesUi();
            MessageBox.Show("Secure baseline applied.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SystemRule_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) { BuildSystemRulesUi(); return; }
        if (sender is not CheckBox cb || cb.Tag is not string key) return;
        try
        {
            bool on = cb.IsChecked == true;
            _firewall.SetSystemRule(key, on);
            // Reflect the real applied state (a rule may not apply on some systems).
            bool actual = _firewall.IsSystemRuleOn(key);
            if (actual != on)
            {
                cb.IsChecked = actual;
                if (on)
                    MessageBox.Show("This rule couldn't be applied on this system (the filter " +
                        "condition may be unsupported).", "GunWall",
                        MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { ShowError(ex); BuildSystemRulesUi(); }
    }

    // ================================================================ temporary rules
    private void BlockDirection_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (AppsList.SelectedItem is not AppInfo app) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string dir) return;
        try
        {
            bool outbound = dir == "out";
            _firewall.BlockAppDirection(app.ExecutablePath, app.Name, outbound);
            RebuildAppsList();
            MessageBox.Show($"{app.Name} is now blocked for {(outbound ? "outbound" : "inbound")} " +
                "connections only.", "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BlockTemp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (AppsList.SelectedItem is not AppInfo app) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string mins) return;
        if (!int.TryParse(mins, out int minutes)) return;
        try
        {
            var until = _firewall.BlockAppTemporarily(
                app.ExecutablePath, app.Name, TimeSpan.FromMinutes(minutes));
            RebuildAppsList();
            MessageBox.Show($"{app.Name} is blocked until {until:t}. It will be unblocked " +
                "automatically (or stays blocked if you close GunWall before then).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ================================================================ services
    private void LoadServices()
    {
        try
        {
            _services.Clear();
            foreach (var s in ServicesService.GetServices()) _services.Add(s);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshServices_Click(object sender, RoutedEventArgs e) => LoadServices();

    private void ServicesSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        string q = ServicesSearch.Text?.Trim() ?? "";
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(_services);
        view.Filter = string.IsNullOrEmpty(q) ? null : o =>
        {
            if (o is not ServicesService.ServiceItem s) return false;
            return s.Display.Contains(q, StringComparison.OrdinalIgnoreCase)
                || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        };
    }

    private void BlockService_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string svcName) return;
        try
        {
            string exe = ServicesService.GetBinaryPath(svcName);
            if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
            {
                MessageBox.Show(
                    "Could not resolve this service's host program (it may be a shared svchost " +
                    "service, which can't be blocked individually by image).",
                    "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _firewall.BlockApp(exe, System.IO.Path.GetFileName(exe));
            MessageBox.Show($"Blocked the host program for '{svcName}':\n{exe}",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BlockServiceHost_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string exe) return;
        if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe))
        {
            MessageBox.Show(
                "This service's host program couldn't be resolved (often a shared svchost " +
                "service, which can't be blocked individually by image).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            _firewall.BlockApp(exe, System.IO.Path.GetFileName(exe));
            MessageBox.Show($"Blocked host program:\n{exe}\n\n(Note: shared host programs like " +
                "svchost.exe carry many services — blocking affects all of them.)",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ================================================================ network scanner
    private async void ScanNetwork_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ScanBtn.IsEnabled = false;
            ScanBtn.Content = "Scanning...";
            _devices.Clear();
            if (NetworkSubtitle != null) NetworkSubtitle.Text = "Scanning your local network...";

            var found = await NetworkScanner.ScanAsync(pct =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (NetworkSubtitle != null) NetworkSubtitle.Text = $"Scanning... {pct}%";
                }));

            foreach (var d in found) _devices.Add(d);
            if (NetworkSubtitle != null)
                NetworkSubtitle.Text = $"Found {found.Count} device(s) on your local network.";
        }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            ScanBtn.IsEnabled = true;
            ScanBtn.Content = "Scan network";
        }
    }

    // ================================================================ profiles
    private void RefreshProfilesCombo()
    {
        if (ProfilesCombo == null) return;
        var sel = ProfilesCombo.SelectedItem as string;
        ProfilesCombo.ItemsSource = null;
        ProfilesCombo.ItemsSource = _firewall.ListProfiles();
        if (sel != null) ProfilesCombo.SelectedItem = sel;
    }

    // ---------------------------------------------- Windows Firewall integration
    private void RefreshWinFwStatus()
    {
        if (WinFwStatus == null) return;
        try
        {
            var s = WindowsFirewallService.GetState();
            if (s.Domain == null && s.Private == null && s.Public == null)
            {
                WinFwStatus.Text = "Windows Firewall status: unavailable.";
                return;
            }
            string On(bool? b) => b == null ? "?" : (b.Value ? "On" : "Off");
            WinFwStatus.Text = s.AllOff
                ? "Windows Firewall is OFF on all profiles."
                : $"Windows Firewall - Domain: {On(s.Domain)}, Private: {On(s.Private)}, Public: {On(s.Public)}.";
        }
        catch { WinFwStatus.Text = "Windows Firewall status: unavailable."; }
    }

    private void WinFwRefresh_Click(object sender, RoutedEventArgs e) => RefreshWinFwStatus();

    // ---------------------------------------------- filtering DNS
    private void RefreshDnsCombo()
    {
        if (DnsCombo == null) return;
        if (DnsCombo.Items.Count == 0)
            foreach (var p in Services.DnsService.All)
                DnsCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Key });
        string cur = _firewall.CurrentDnsProvider;
        foreach (ComboBoxItem it in DnsCombo.Items)
            if ((it.Tag as string) == cur) { DnsCombo.SelectedItem = it; break; }
        if (DnsCombo.SelectedItem == null && DnsCombo.Items.Count > 0) DnsCombo.SelectedIndex = 0;
    }

    private void DnsApply_Click(object sender, RoutedEventArgs e)
    {
        if (DnsCombo?.SelectedItem is not ComboBoxItem it || it.Tag is not string key) return;
        var preset = Services.DnsService.ByKey(key);
        if (key != "auto")
        {
            var ask = MessageBox.Show(
                $"Set DNS to {preset.Name} on all active adapters?\n\n" +
                "You can return to your network's automatic DNS here at any time.",
                "Apply filtering DNS", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ask != MessageBoxResult.OK) { RefreshDnsCombo(); return; }
        }
        try
        {
            int n = _firewall.SetDnsProvider(key);
            if (DnsStatus != null)
                DnsStatus.Text = n > 0
                    ? $"{preset.Name} applied to {n} adapter(s)."
                    : "No active adapters were changed (the command may have been blocked).";
            BuildBlocklistCatUi(); // the Ads toggle mirrors the AdGuard DNS selection
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void WinFwOff_Click(object sender, RoutedEventArgs e)
    {
        var ask = MessageBox.Show(
            "Turn off Windows Defender Firewall for all network profiles?\n\n" +
            "GunWall will keep protecting you, but turning the Windows firewall back on " +
            "later is recommended if you stop using GunWall.",
            "Turn off Windows Firewall", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;
        bool ok = WindowsFirewallService.SetEnabled(false);
        _firewall.EventLog("Windows Firewall turned off");
        RefreshWinFwStatus();
        if (!ok)
            MessageBox.Show("Couldn't change Windows Firewall (the command was blocked or failed).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void WinFwOn_Click(object sender, RoutedEventArgs e)
    {
        bool ok = WindowsFirewallService.SetEnabled(true);
        _firewall.EventLog("Windows Firewall turned on");
        RefreshWinFwStatus();
        if (!ok)
            MessageBox.Show("Couldn't change Windows Firewall (the command was blocked or failed).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void WinFwImport_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        var ask = MessageBox.Show(
            "Import the BLOCK rules from Windows Firewall as GunWall blocks?\n\n" +
            "(Allow rules are skipped, since GunWall allows by default.)",
            "Import Windows Firewall rules", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;
        try
        {
            int n = _firewall.ImportWindowsFirewallRules();
            RebuildAppsList();
            if (WinFwImportStatus != null)
                WinFwImportStatus.Text = n > 0
                    ? $"Imported {n} blocked program(s)."
                    : "No new block rules with a program path were found.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---------------------------------------------- versioned backups
    private void RefreshBackupsCombo()
    {
        if (BackupsCombo == null) return;
        BackupsCombo.Items.Clear();
        foreach (var (name, when) in _firewall.ListBackups())
            BackupsCombo.Items.Add(new ComboBoxItem
            {
                Content = when.ToString("ddd dd MMM yyyy, h:mm tt"),
                Tag = name
            });
        if (BackupsCombo.Items.Count > 0) BackupsCombo.SelectedIndex = 0;
    }

    private void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _firewall.CreateBackup();
            RefreshBackupsCombo();
            if (BackupStatus != null) BackupStatus.Text = "Backup created.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        if (BackupsCombo?.SelectedItem is not ComboBoxItem item || item.Tag is not string name)
        {
            if (BackupStatus != null) BackupStatus.Text = "Select a backup to restore.";
            return;
        }
        var ask = MessageBox.Show(
            "Restore this backup? It replaces your current rules and settings.\n\n" +
            "Filters are re-applied when you next toggle strict mode.",
            "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;
        try
        {
            int n = _firewall.RestoreBackup(name);
            RebuildAppsList();
            RefreshRulesList();
            if (BackupStatus != null) BackupStatus.Text = $"Restored ({n} app rules).";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _firewall.SaveProfile(NewProfileName.Text);
            NewProfileName.Clear();
            RefreshProfilesCombo();
            if (ProfileStatus != null) ProfileStatus.Text = "Profile saved.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LoadProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesCombo.SelectedItem is not string name)
        {
            if (ProfileStatus != null) ProfileStatus.Text = "Select a profile to load.";
            return;
        }
        try
        {
            int n = _firewall.LoadProfile(name);
            if (ProfileStatus != null)
                ProfileStatus.Text = $"Loaded '{name}' ({n} rules). Re-toggle the firewall to apply filters.";
            RebuildAppsList();
            RefreshRulesList();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfilesCombo.SelectedItem is not string name) return;
        try
        {
            _firewall.DeleteProfile(name);
            RefreshProfilesCombo();
            if (ProfileStatus != null) ProfileStatus.Text = $"Deleted '{name}'.";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LockdownButton_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        try
        {
            _firewall.SetLockdown(!_firewall.LockdownEngaged);
            SyncLockdownButton();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        var answer = MessageBox.Show(
            "Remove every GunWall filter and clear all saved rules?\n\nThis cannot be undone.",
            "GunWall", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _firewall.RemoveAllFiltering();
            SyncLockdownButton();
            RebuildAppsList();
            MessageBox.Show("All GunWall filtering removed.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private bool _suppressModeEvent;
    private bool _settingsDirty;

    /// <summary>
    /// The prominent "Enable Firewall" action. Toggles full network control
    /// (whitelist takeover): when enabled, every app is blocked except those
    /// you allow. This is the same protective engine the Settings mode selects,
    /// surfaced as a one-click button.
    /// </summary>
    private void EnableFirewall_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        bool turningOn = !_firewall.StrictMode;

        if (turningOn)
        {
            var answer = MessageBox.Show(
                "Enable Firewall takes full control of your network.\n\n" +
                "Every app will be blocked except the ones you allow. Core Windows " +
                "networking (DNS / DHCP) and loopback stay on automatically, but other " +
                "apps - including your browser - will need an Allow (you'll get a popup, " +
                "or use the Firewall tab).\n\nEnable now?",
                "GunWall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        try
        {
            _firewall.SetStrictMode(turningOn);
            SyncFirewallToggle();
            RebuildAppsList();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SyncFirewallToggle()
    {
        if (EnableFirewallButton == null) return;
        EnableFirewallButton.Content = _firewall.StrictMode ? "Disable Firewall" : "Enable Firewall";
        // A mode change resets per-session prompt tracking, so entering Zero
        // Trust re-prompts for every undecided app.
        _promptedThisSession.Clear();
        if (StrictModeRadio != null && AlertModeRadio != null)
        {
            _suppressModeEvent = true;
            StrictModeRadio.IsChecked = _firewall.StrictMode;
            AlertModeRadio.IsChecked = !_firewall.StrictMode;
            _suppressModeEvent = false;
        }
        UpdateStatusBanner();
    }

    // Settings are now STAGED and committed with Apply, so the user can select
    // options and confirm rather than each click taking effect immediately.
    private void Settings_Staged(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;
        MarkSettingsDirty();
    }

    private void Mode_Changed(object sender, RoutedEventArgs e) => Settings_Staged(sender, e);

    private void IntervalCombo_Changed(object sender, SelectionChangedEventArgs e)
        => MarkSettingsDirty();

    private void AlertsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;
        MarkSettingsDirty();
    }

    private void Pref_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressModeEvent) return;
        MarkSettingsDirty();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export GunWall profile",
            Filter = "GunWall profile (*.gwprofile)|*.gwprofile|JSON (*.json)|*.json",
            FileName = "gunwall-profile.gwprofile"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _firewall.ExportProfile(dlg.FileName);
            MessageBox.Show("Profile exported.", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Importing replaces your current rules and settings with the file's. Continue?",
            "GunWall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        var dlg = new OpenFileDialog
        {
            Title = "Import GunWall profile",
            Filter = "GunWall profile (*.gwprofile)|*.gwprofile|JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            int count = _firewall.ImportProfile(dlg.FileName);
            SyncFirewallToggle();
            RebuildAppsList();
            MessageBox.Show($"Imported {count} rule(s).", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void MarkSettingsDirty()
    {
        _settingsDirty = true;
        if (ApplyButton != null) ApplyButton.IsEnabled = true;
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        if (!_engineReady || !_settingsDirty) { _settingsDirty = false; return; }

        // 1) Refresh interval
        if (IntervalCombo?.SelectedItem is ComboBoxItem item &&
            int.TryParse((string)item.Tag, out int ms))
            _intervalMs = ms;

        // 2) Alerts toggle
        _firewall.SetAlertsEnabled(AlertsCheck?.IsChecked == true);

        // Preferences
        _firewall.SetStartMinimized(StartMinimizedCheck?.IsChecked == true);
        try { _firewall.SetRunAtStartup(RunAtStartupCheck?.IsChecked == true); }
        catch (Exception ex)
        {
            ShowError(ex);
            if (RunAtStartupCheck != null) RunAtStartupCheck.IsChecked = _firewall.RunAtStartup;
        }
        _firewall.SetAlwaysOnTop(AlwaysOnTopCheck?.IsChecked == true);
        _firewall.SetEventLogEnabled(EventLogCheck?.IsChecked == true);
        _firewall.SetNotificationSound(NotifSoundCheck?.IsChecked == true);
        _firewall.SetTrayNotifications(TrayNotifCheck?.IsChecked == true);
        _firewall.SetPacketFileLogging(PacketLogFileCheck?.IsChecked == true);
        if (PopupTimeoutCombo?.SelectedItem is ComboBoxItem pti &&
            int.TryParse(pti.Tag?.ToString(), out int secs))
            _firewall.SetPopupTimeoutSeconds(secs);
        if (PopupDefaultCombo?.SelectedItem is ComboBoxItem pdi)
            _firewall.SetPopupDefaultAllow((pdi.Tag?.ToString() ?? "allow") == "allow");
        _firewall.SetAutoBackup(AutoBackupCheck?.IsChecked == true);
        _firewall.SetHashesEnabled(HashesCheck?.IsChecked == true);
        _firewall.SetExperimentalEvents(ExperimentalEventsCheck?.IsChecked == true);
        Topmost = _firewall.AlwaysOnTop;

        // 3) Firewall mode (the heavy one) - confirm before a takeover.
        bool wantStrict = StrictModeRadio?.IsChecked == true;
        if (wantStrict != _firewall.StrictMode)
        {
            if (wantStrict)
            {
                var answer = MessageBox.Show(
                    "Strict mode blocks every app except the ones you allow. " +
                    "Continue?",
                    "GunWall", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    _suppressModeEvent = true;
                    if (AlertModeRadio != null) AlertModeRadio.IsChecked = true;
                    if (StrictModeRadio != null) StrictModeRadio.IsChecked = false;
                    _suppressModeEvent = false;
                }
                else
                {
                    try { _firewall.SetStrictMode(true); } catch (Exception ex) { ShowError(ex); }
                }
            }
            else
            {
                try { _firewall.SetStrictMode(false); } catch (Exception ex) { ShowError(ex); }
            }
        }

        SyncFirewallToggle();
        RebuildAppsList();
        _settingsDirty = false;
        if (ApplyButton != null) ApplyButton.IsEnabled = false;
        if (ApplyStatus != null) ApplyStatus.Text = "Settings applied.";
    }

    private void UpdateStatusBanner()
    {
        if (StatusDot == null) return;
        if (!_engineReady)
        {
            StatusDot.Fill = (Brush)FindResource("BlockBrush");
            StatusTitle.Text = "Engine unavailable";
            StatusSub.Text = "Run GunWall as administrator to enable filtering";
        }
        else if (_firewall.LockdownEngaged)
        {
            StatusDot.Fill = (Brush)FindResource("BlockBrush");
            StatusTitle.Text = "Lockdown engaged";
            StatusSub.Text = "All network traffic is blocked";
        }
        else if (_firewall.StrictMode)
        {
            StatusDot.Fill = (Brush)FindResource("Accent");
            StatusTitle.Text = "Firewall enabled - full control";
            StatusSub.Text = "Everything is blocked except apps you allowed";
        }
        else
        {
            StatusDot.Fill = (Brush)FindResource("AllowBrush");
            StatusTitle.Text = "Monitoring";
            StatusSub.Text = "New apps are allowed and you get notified - enable the firewall for full control";
        }
    }

    private void Repo_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* no browser available */ }
        e.Handled = true;
    }

    private void SyncLockdownButton()
    {
        UpdateStatusBanner();
        if (_firewall.LockdownEngaged)
        {
            LockdownButton.Content = "Release lockdown";
            EngineStatus.Text = "Engine: LOCKDOWN";
        }
        else
        {
            LockdownButton.Content = "Engage lockdown";
            EngineStatus.Text = _engineReady ? "Engine: active" : "Engine: unavailable";
        }
    }

    // ================================================================ tray
    private void SetupTray()
    {
        try
        {
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Text = "GunWall",
                Visible = true
            };
            string? exe = Environment.ProcessPath;
            if (exe != null)
                _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);

            _tray.DoubleClick += (_, _) => RestoreFromTray();

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open GunWall", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Toggle lockdown", null, (_, _) =>
                Dispatcher.Invoke(() => LockdownButton_Click(this, new RoutedEventArgs())));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
            _tray.ContextMenuStrip = menu;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"tray unavailable: {ex.Message}");
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Minimize hides to tray (filters keep enforcing - they live in the OS).
        if (WindowState == WindowState.Minimized && _tray != null)
            Hide();
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    // ================================================================ theme
    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        // Checked = light, unchecked = dark.
        bool dark = !(ThemeToggle.IsChecked == true);
        ApplyTheme(dark);
        _firewall.SetThemeDark(dark);
    }

    private void ApplyTheme(bool dark)
    {
        try
        {
            var dicts = Application.Current.Resources.MergedDictionaries;
            // The palette is always the first merged dictionary (see App.xaml).
            var palette = new ResourceDictionary
            {
                Source = new Uri(dark
                    ? "Themes/Theme.Dark.xaml"
                    : "Themes/Theme.Light.xaml", UriKind.Relative)
            };
            if (dicts.Count > 0) dicts[0] = palette;
            else dicts.Insert(0, palette);

            // Match the OS title bar to the theme.
            TrySetTitleBarTheme(dark);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ApplyTheme failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Genuine exit path. If the firewall is still engaged, the persistent WFP
    /// filters will keep blocking traffic after GunWall closes — and the user
    /// would have no UI to manage them. So we warn and offer to disable the
    /// firewall on the way out, preventing the "stuck with no internet and no
    /// app" trap.
    /// </summary>
    private void ExitFromTray()
    {
        if (_firewall.StrictMode || _firewall.LockdownEngaged)
        {
            var result = MessageBox.Show(
                "The firewall is still active. If you exit now, it will keep " +
                "blocking traffic in the background and you'll need to reopen " +
                "GunWall to change anything.\n\n" +
                "Yes  = Turn the firewall OFF and exit\n" +
                "No   = Keep the firewall ON and exit\n" +
                "Cancel = Stay open",
                "Exit GunWall",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel) return;
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    if (_firewall.LockdownEngaged) _firewall.SetLockdown(false);
                    if (_firewall.StrictMode) _firewall.SetStrictMode(false);
                }
                catch (Exception ex) { ShowError(ex); }
            }
        }

        _reallyExit = true;
        Close();
    }

    // ================================================================ helpers
    private bool RequireEngine()
    {
        if (_engineReady) return true;
        MessageBox.Show("The firewall engine is not available. Run GunWall as administrator.",
            "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void ShowError(Exception ex)
    {
        Services.DiagnosticLog.LogException("ShowError", ex);
        MessageBox.Show(ex.Message, "GunWall", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec / (1024 * 1024):0.00} MB/s";
    }

    // Dark native title bar (Windows 10 2004+ / Windows 11).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private void TryEnableDarkTitleBar() => TrySetTitleBarTheme(true);

    private void TrySetTitleBarTheme(bool dark)
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            int useDark = dark ? 1 : 0;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch
        {
            Debug.WriteLine("Title bar theme not supported on this build.");
        }
    }
}
