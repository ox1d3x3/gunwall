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
    private readonly NetworkStatsService _stats = new();
    private readonly AppUsageService _usage = new();   // approximate per-app data usage
    private readonly ObservableCollection<CountryStat> _trafficCountries = new();
    private readonly ObservableCollection<AppStat> _trafficApps = new();

    // §3 GunWall's own local DNS resolver (loopback only; never touches system DNS).
    private readonly DnsResolver _dnsResolver = new();
    private readonly ObservableCollection<DnsLogEntry> _dnsLog = new();
    private bool _dnsVisible;
    private const int MaxDnsLog = 500;

    // §VT automatic VirusTotal checks: queue of exe paths awaiting a lookup, a
    // session cache of computed hashes, and a guard so each path queues once.
    private readonly Queue<string> _vtQueue = new();
    private readonly HashSet<string> _vtQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _vtHashByPath = new(StringComparer.OrdinalIgnoreCase);

    // §6 domain heuristics: notify once per suspicious-looking domain.
    private readonly HashSet<string> _dgaSeen = new(StringComparer.OrdinalIgnoreCase);

    // §13 notification center: session-scoped list + unread badge on the sidebar.
    private readonly ObservableCollection<AppNotification> _notifications = new();
    private int _unreadNotifications;
    private bool _alertsVisible;
    private const int MaxNotifications = 200;
    private bool? _lastNotifiedProtection;
    private bool? _lastNotifiedLockdown;

    private const int GraphPoints = 60;
    private readonly double[] _downSeries = new double[GraphPoints];
    private readonly double[] _upSeries = new double[GraphPoints];
    // Live graph sampler: reads NIC byte counters every 250ms (cheap — they're just
    // counters) so the graph's leading edge updates in near-real-time, 4x faster
    // than the 1s stats loop. 240 points x 250ms = the same 60s window.
    private const int GraphFinePoints = 240;
    private readonly double[] _gDown = new double[GraphFinePoints];
    private readonly double[] _gUp = new double[GraphFinePoints];
    private System.Windows.Threading.DispatcherTimer? _graphTimer;
    private long _gLastRx = -1, _gLastTx;
    private DateTime _gLastAt;
    // Between samples a single shared TranslateTransform slides the geometry left
    // (the "train"); the per-sample rebuild keeps the tip alive.
    private readonly TranslateTransform _graphScroll = new();
    private DateTime _lastGraphSample = DateTime.MinValue;
    private double _graphInterval = 250.0;
    private double _graphStepX;

    private long _lastRx = -1, _lastTx = -1;
    private DateTime _lastSample = DateTime.MinValue;
    private bool _engineReady;

    // Background sampling
    private CancellationTokenSource? _cts;
    private NetEventMonitor? _netEvents;
    private bool _eventDriven; // true when kernel events are active (no polling needed for detection)
    private bool _eventsRecovered; // true if we auto-disabled events after a prior crash
    private volatile int _intervalMs = 1000;

    // Sampling-pipeline health (surfaced in diagnostics so a stalled/erroring loop
    // is visible instead of silently emptying the Connections/Traffic panels).
    private long _sampleTicks;
    private int _sampleErrors, _detectErrors;
    private string _lastSampleError = "";
    private readonly Dictionary<string, int> _stepErr = new();
    private string _appFilter = "";
    private string _connFilter = "";
    private bool _showAllApps;
    private int _lastStoreCount = -1;

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
        TrafficCountries.ItemsSource = _trafficCountries;
        TrafficApps.ItemsSource = _trafficApps;
        DnsLog.ItemsSource = _dnsLog;
        _dnsResolver.Query += OnDnsQuery;
        AlertsList.ItemsSource = _notifications;
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
        if (_reallyExit)
        {
            // Never leave system DNS pointing at a resolver that's about to die.
            // Intent stays persisted, so routing resumes on next launch.
            try
            {
                if (_firewall.DnsRedirectActive && !_firewall.DnsGamingSession)
                    DnsService.RestoreAdapters(_firewall.DnsSavedAdapters);
            }
            catch { }
            try { _dnsResolver.Stop(); } catch { }
            return; // genuine exit — let it close
        }

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
            _firewall.EnsureSelfConnectivity(); // GunWall must not block its own update/list/VT traffic
            _firewall.LoadCategoryColors();      // apply any customised category dot colors
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
            if (FullscreenSilentCheck != null) FullscreenSilentCheck.IsChecked = _firewall.FullscreenSilent;
            if (ConfirmClearCheck != null) ConfirmClearCheck.IsChecked = _firewall.ConfirmClearLogs;
            if (ConfirmExitCheck != null) ConfirmExitCheck.IsChecked = _firewall.AlwaysConfirmExit;
            if (KeepUnusedCheck != null) KeepUnusedCheck.IsChecked = _firewall.KeepUnusedApps;
            if (MaxLogEntriesCombo != null)
                MaxLogEntriesCombo.SelectedIndex = _firewall.MaxLogEntries switch
                { 500 => 0, 1000 => 1, 5000 => 2, 0 => 3, _ => 1 };
            if (MaxLogFileCombo != null)
                MaxLogFileCombo.SelectedIndex = _firewall.MaxLogFileMB switch
                { 2 => 0, 5 => 1, 20 => 2, 50 => 3, _ => 1 };
            PopulateColorUi();
            UpdateCustomListStatus(); // §5 reflect any saved custom blocklist file
            RefreshEntityRules();     // §1 render saved country/continent/ASN rules + GeoIP status
            try
            {
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                    TimeSpan.FromMilliseconds(260))
                { EasingFunction = new System.Windows.Media.Animation.CubicEase() };
                BeginAnimation(OpacityProperty, fadeIn);
            }
            catch { }

            System.Windows.Media.CompositionTarget.Rendering += OnGraphFrame; // frame-driven graph
            _graphTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(250) };
            _graphTimer.Tick += GraphTimer_Tick;
            _graphTimer.Start();
            InitGeoSourceUi();        // reflect saved GeoIP source (local / API)
            InitDnsPanel();           // §3 reflect saved local-resolver settings
            RefreshProfileCombo();    // §10 reflect saved rule profiles
            _suppressModeEvent = false;
            SyncFirewallToggle();
            if (ApplyButton != null) ApplyButton.IsEnabled = false;

            // Apply window prefs immediately.
            Topmost = _firewall.AlwaysOnTop;
            if (_firewall.StartMinimized) WindowState = WindowState.Minimized;

            AboutText.Text = $"GunWall v0.65.0 - free, open-source, no telemetry. " +
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
                LogActivity(new NetActivityEvent
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
        _ = VtLoopAsync(_cts.Token);
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
                ApplyEntityBlocksForConns(conns, procs); // §1 reactive geo-blocking (polling path)
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _detectErrors++;
                Debug.WriteLine($"detect error: {ex.Message}");
                if (_detectErrors == 1 || _detectErrors % 20 == 0)
                    Services.DiagnosticLog.Log($"Detection loop error #{_detectErrors}: {ex.GetType().Name}: {ex.Message}");
            }

            try { await Task.Delay(300, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>§1 polling-path enforcement: apply entity blocks to every observed
    /// connection. Runs alongside the event-driven path; the firewall manager dedups
    /// per app+remote, so double-processing is harmless and does no extra WFP work.</summary>
    private void ApplyEntityBlocksForConns(List<ConnectionInfo> conns,
                                           Dictionary<int, (string Name, string Path)> procs)
    {
        if (!_engineReady) return;
        foreach (var c in conns)
        {
            if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
            if (c.RemoteAddress is "127.0.0.1" or "::1" or "0.0.0.0" or "::") continue;
            if (!procs.TryGetValue(c.ProcessId, out var p) || string.IsNullOrEmpty(p.Path)) continue;
            if (string.Equals(p.Path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;
            string? reason = _firewall.ApplyEntityBlocks(p.Path, c.RemoteAddress);
            if (reason != null)
            {
                LogActivity(new NetActivityEvent
                {
                    ProcessName = System.IO.Path.GetFileNameWithoutExtension(p.Path),
                    Detail = $"blocked {c.RemoteAddress} ({reason})"
                });
                while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);
            }
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
                if (_sampleTicks == 0)
                    Services.DiagnosticLog.Log($"First snapshot applied: {snap.Conns.Count} connections.");
                _sampleTicks++;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _sampleErrors++;
                _lastSampleError = $"{ex.GetType().Name}: {ex.Message}";
                Debug.WriteLine($"sample error: {ex.Message}");
                if (_sampleErrors == 1 || _sampleErrors % 20 == 0)
                    Services.DiagnosticLog.Log($"Sample loop error #{_sampleErrors}: {_lastSampleError}");
            }

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
        {
            if (procs.TryGetValue(c.ProcessId, out var p))
            {
                c.ProcessName = p.Name;
                c.ExePath = p.Path ?? "";
            }
            else c.ProcessName = $"PID {c.ProcessId}";
        }

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
        // Set the shared snapshot FIRST so the Connections panel and the inspector
        // always have data even if a later UI step misbehaves.
        _lastConns = snap.Conns;
        _lastProcs = snap.Procs;

        try
        {
            Shift(_downSeries, snap.DownRate);
            Shift(_upSeries, snap.UpRate);
            DownSpeed.Text = FormatRate(snap.DownRate);
            UpSpeed.Text = FormatRate(snap.UpRate);
            ConnCount.Text = snap.Conns.Count.ToString();
        }
        catch (Exception ex) { SampleStepError("dashboard-speeds", ex); }

        try { EnrichGeo(snap.Conns); } catch (Exception ex) { SampleStepError("EnrichGeo", ex); }
        try { RecordActivity(snap.Conns); } catch (Exception ex) { SampleStepError("RecordActivity", ex); }
        try
        {
            foreach (var c in snap.Conns)
                _stats.RecordOne(c.ProcessName.Length > 0 ? c.ProcessName : "PID " + c.ProcessId,
                                 c.RemoteAddress, c.Country);

            // Approximate per-app usage: this tick's measured bytes, attributed by
            // each app's share of active external connections (estimate by design).
            var perApp = snap.Conns
                .Where(cn => !string.IsNullOrEmpty(cn.RemoteAddress) && !cn.RemoteAddress.StartsWith("127."))
                .GroupBy(cn => cn.ProcessName.Length > 0 ? cn.ProcessName : "PID " + cn.ProcessId)
                .Select(g => (g.Key, g.Count()))
                .ToList();
            _usage.Record(snap.DownRate, snap.UpRate, perApp);
        }
        catch (Exception ex) { SampleStepError("stats", ex); }
        try { UpdateSessionTotals(); } catch (Exception ex) { SampleStepError("UpdateSessionTotals", ex); }

        try { if (PanelTraffic.Visibility == Visibility.Visible) RefreshTraffic(); }
        catch (Exception ex) { SampleStepError("RefreshTraffic", ex); }
        try { if (PanelConnections.Visibility == Visibility.Visible) RebuildConnList(); }
        catch (Exception ex) { SampleStepError("RebuildConnList", ex); }
        try { if (PanelFirewall.Visibility == Visibility.Visible) RebuildAppsList(); }
        catch (Exception ex) { SampleStepError("RebuildAppsList", ex); }
        // graph is driven by its own 250ms live sampler (GraphTimer_Tick)
        try { if (PanelSettings.Visibility == Visibility.Visible) UpdateHealthCard(); }
        catch (Exception ex) { SampleStepError("UpdateHealthCard", ex); }
    }

    // Records (and, on first occurrence, logs) a failure in one ApplySnapshot step
    // so a single bad row or panel can't silently blank the whole UI.
    private void SampleStepError(string step, Exception ex)
    {
        int n = _stepErr.TryGetValue(step, out var v) ? v + 1 : 1;
        _stepErr[step] = n;
        _lastSampleError = $"{step}: {ex.GetType().Name}: {ex.Message}";
        Debug.WriteLine($"ApplySnapshot[{step}] error: {ex.Message}");
        if (n == 1 || n % 25 == 0)
            Services.DiagnosticLog.Log($"ApplySnapshot step '{step}' error #{n}: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>Resolve country/ASN for the snapshot's connections (deduped per tick), so the
    /// Traffic history and the connection inspector have location data regardless of which
    /// panel is open. No-op when no GeoIP source is active.</summary>
    private void EnrichGeo(List<ConnectionInfo> conns)
    {
        if (!_firewall.GeoIpActive) return;
        Dictionary<string, GunWall.Services.GeoIpService.GeoInfo> memo = new(StringComparer.OrdinalIgnoreCase);
        foreach (var c in conns)
        {
            if (c.Country.Length > 0 || string.IsNullOrEmpty(c.RemoteAddress)) continue;
            if (!memo.TryGetValue(c.RemoteAddress, out var g))
            {
                g = _firewall.GeoIp.Lookup(c.RemoteAddress);
                memo[c.RemoteAddress] = g;
            }
            if (g.HasData) { c.Country = g.Country; c.Asn = g.Asn; c.AsnOwner = g.Owner; }
        }
    }

    /// <summary>Repopulate the Traffic panel's top-countries / top-apps tables.</summary>
    private void RefreshTraffic()
    {
        if (TrafficCountries == null) return;
        _trafficCountries.Clear();
        foreach (var (code, count) in _stats.TopCountries(25))
            _trafficCountries.Add(new CountryStat(code, GunWall.Services.GeoData.CountryName(code), count));
        _trafficApps.Clear();
        foreach (var (app, count, countries) in _stats.TopApps(25))
            _trafficApps.Add(new AppStat(app, count, countries));
        if (TrafficSubtitle != null)
            TrafficSubtitle.Text = _stats.TotalDestinations == 0
                ? "No external destinations seen yet - traffic will appear here as apps connect out."
                : $"{_stats.TotalDestinations:N0} distinct destinations since startup, across {_stats.CountryCount} countries and {_stats.AppCount} apps.";

        RefreshMapMarkers();
        RefreshUsage();
    }

    /// <summary>Plot a green dot per active country on the world map, sized by
    /// destination count. Uses the same TopCountries data as the list, so map and
    /// list always agree. Cheap: a couple dozen ellipses, only on Traffic refresh.</summary>
    private void RefreshMapMarkers()
    {
        if (MapMarkers == null) return;
        MapMarkers.Children.Clear();
        var top = _stats.TopCountries(200);
        int max = 1;
        foreach (var (_, count) in top) if (count > max) max = count;

        foreach (var (code, count) in top)
        {
            if (!WorldMapData.CountryPoints.TryGetValue(code.ToUpperInvariant(), out var pt)) continue;

            // radius 3..9 by share of the busiest country (sqrt keeps small ones visible)
            double r = 3.0 + 6.0 * Math.Sqrt(count / (double)max);
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = new SolidColorBrush(Color.FromArgb(0xE0, 0x23, 0xC0, 0x5C)),
                Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x23, 0xC0, 0x5C)),
                StrokeThickness = 2,
                ToolTip = $"{GunWall.Services.GeoData.CountryName(code)} · {count} destinations"
            };
            Canvas.SetLeft(dot, pt.X - r);
            Canvas.SetTop(dot, pt.Y - r);
            MapMarkers.Children.Add(dot);
        }
    }

    private static void Shift(double[] series, double newest)
    {
        Array.Copy(series, 1, series, 0, series.Length - 1);
        series[^1] = newest;
    }

    // ============================================================ DNS resolver (§3)
    // GunWall's own local resolver lives here in the UI layer; FirewallManager only
    // persists its configuration. It binds to 127.0.0.1 and never changes the system
    // DNS by itself — the user points DNS at it. A guided redirect and "Gaming
    // Session" toggle are the next phase.

    private void InitDnsPanel()
    {
        if (DnsPortBox == null) return;
        DnsPortBox.Text = _firewall.DnsResolverPort.ToString();
        DnsUpstreamBox.Text = _firewall.DnsResolverUpstream;
        DnsBlockBox.Text = string.Join(Environment.NewLine, _firewall.DnsResolverBlocklist);
        ApplyDnsBlocklists();            // preset file + manual lines
        RefreshDnsPresetStatus();
        ReapplyDnsRedirectOnStartup();   // §3 Phase 2: honor saved routing intent (fail-safe inside)
        UpdateDnsUi();
        UpdateDnsRedirectUi();
    }

    private static List<string> DnsBlockLines(string text) =>
        (text ?? "")
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private void DnsStart_Click(object sender, RoutedEventArgs e)
    {
        if (_dnsResolver.Running)
        {
            // Never stop the resolver while system DNS points at it - restore first.
            if (_firewall.DnsRedirectActive)
            {
                DnsService.RestoreAdapters(_firewall.DnsSavedAdapters);
                _firewall.SaveDnsRedirectState(false, false, null);
                Services.DiagnosticLog.Log("DNS routing OFF (resolver stopped): adapters restored.");
            }
            try { _dnsResolver.Stop(); } catch (Exception ex) { ShowError(ex); }
            UpdateDnsUi();
            UpdateDnsRedirectUi();
            return;
        }

        if (!int.TryParse(DnsPortBox.Text.Trim(), out int port) || port < 1 || port > 65535)
        {
            MessageBox.Show("Enter a valid port (1-65535). The standard DNS port is 53.",
                            "DNS resolver", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        string upstream = DnsUpstreamBox.Text.Trim();

        // Apply + persist the blocklist before the resolver starts serving.
        var block = DnsBlockLines(DnsBlockBox.Text);
        ApplyDnsBlocklists();   // preset file + manual lines
        _firewall.SaveDnsResolverConfig(port, upstream, block);

        try
        {
            _dnsResolver.Start(port, upstream);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Couldn't start the resolver on 127.0.0.1:{port}.\n\n{ex.Message}\n\n" +
                "Port 53 may already be in use by another DNS service. Try a different " +
                "port (e.g. 5335) and point your test there with:\n" +
                $"    nslookup -port={(port == 53 ? 5335 : port)} example.com 127.0.0.1",
                "DNS resolver", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateDnsUi();
    }

    private void DnsApplyBlock_Click(object sender, RoutedEventArgs e)
    {
        var block = DnsBlockLines(DnsBlockBox.Text);
        ApplyDnsBlocklists();   // preset file + manual lines
        int port = int.TryParse(DnsPortBox.Text.Trim(), out int p) && p > 0 ? p : 53;
        _firewall.SaveDnsResolverConfig(port, DnsUpstreamBox.Text.Trim(), block);
        RefreshDnsPresetStatus();
        UpdateDnsUi();
    }

    private void OnDnsQuery(DnsLogEntry entry)
    {
        // §6: score every lookup for DGA-style names, even when the panel is
        // closed. Cheap pure math; notify once per domain via the UI thread.
        if (entry.Action != DnsAction.Error)
        {
            var h = DomainHeuristics.Score(entry.Domain);
            bool firstSight;
            lock (_dgaSeen) firstSight = h.Suspicious && _dgaSeen.Add(entry.Domain);
            if (firstSight)
            {
                Services.DiagnosticLog.Log(
                    $"Domain heuristics: {entry.Domain} looks machine-generated (score {h.Score}: {h.Reason}).");
                Dispatcher.BeginInvoke(new Action(() =>
                    Notify("warn", "Suspicious domain pattern",
                           $"{entry.Domain} looks machine-generated (score {h.Score}: {h.Reason}). " +
                           "If unfamiliar, consider adding it to the DNS blocklist.")));
            }
        }

        if (!_dnsVisible) return; // counters keep ticking on the resolver; UI catches up on open
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_dnsVisible) return;
            _dnsLog.Insert(0, entry);
            while (_dnsLog.Count > MaxDnsLog) _dnsLog.RemoveAt(_dnsLog.Count - 1);
            UpdateDnsStats();
        }));
    }

    private void UpdateDnsUi()
    {
        if (DnsResStatus == null) return;
        bool on = _dnsResolver.Running;
        DnsStartBtn.Content = on ? "Stop resolver" : "Start resolver";
        DnsResStatus.Text = on
            ? $"Listening on 127.0.0.1:{_dnsResolver.Port}, forwarding to {_dnsResolver.Upstream}. "
              + $"{_dnsResolver.BlockedDomainCount} domains blocked. Point this PC's DNS at 127.0.0.1 to use it."
            : "Stopped. Start the resolver, then point a DNS query (or this PC's DNS) at 127.0.0.1 to use it.";
        DnsPortBox.IsEnabled = !on;
        DnsUpstreamBox.IsEnabled = !on;
        UpdateDnsStats();
    }

    private void UpdateDnsStats()
    {
        if (DnsStatQueries == null) return;
        DnsStatQueries.Text = _dnsResolver.Total.ToString("N0");
        DnsStatForwarded.Text = _dnsResolver.Forwarded.ToString("N0");
        DnsStatCached.Text = _dnsResolver.Cached.ToString("N0");
        DnsStatBlocked.Text = _dnsResolver.Blocked.ToString("N0");
    }

    // ======================================================== rule profiles (§10)
    private void RefreshProfileCombo()
    {
        if (ProfileCombo == null) return;
        ProfileCombo.Items.Clear();
        foreach (var n in _firewall.RuleProfileNames) ProfileCombo.Items.Add(n);
        string ap = _firewall.ActiveRuleProfile;
        if (ProfileCombo.Items.Count > 0)
            ProfileCombo.SelectedItem = ap.Length > 0 && ProfileCombo.Items.Contains(ap)
                ? ap : ProfileCombo.Items[0];
        RuleProfileStatus.Text = ap.Length > 0
            ? $"Active profile: {ap}"
            : "No profile applied yet. Save your current rules to create one.";
    }

    private void ProfileSave_Click(object sender, RoutedEventArgs e)
    {
        string name = ProfileNameBox.Text.Trim();
        if (name.Length == 0) { RuleProfileStatus.Text = "Enter a profile name first."; return; }
        _firewall.SaveRuleProfile(name);
        ProfileNameBox.Text = "";
        RefreshProfileCombo();
        ProfileCombo.SelectedItem = name;
        RuleProfileStatus.Text = $"Saved current app rules as '{name}'.";
        Notify("info", $"Profile saved: {name}", "Current app allow/block rules captured.");
    }

    private void ProfileApply_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem is not string name)
        { RuleProfileStatus.Text = "Choose a profile to apply."; return; }
        int changed = _firewall.ApplyRuleProfile(name);
        if (changed < 0) { RuleProfileStatus.Text = "Profile not found."; return; }
        RuleProfileStatus.Text = $"Applied '{name}' — {changed} app rule(s) changed.";
        Notify("info", $"Profile applied: {name}", $"{changed} app rule(s) changed.");
        RebuildAppsList();
    }

    // =============================================== DNS blocklist preset (§5+§3)
    // Curated list for GunWall's own resolver: StevenBlack unified hosts (ads +
    // malware; credited data source). Kept in a plain file beside the app so the
    // JSON config stays small; the user's manual lines remain separate.
    private const string DnsPresetUrl =
        "https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts";
    private static string DnsPresetPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "dns-blocklist-preset.txt");
    private static readonly System.Net.Http.HttpClient _dnsPresetHttp =
        new() { Timeout = TimeSpan.FromSeconds(90) };

    private static List<string> LoadDnsPresetLines()
    {
        try
        {
            if (System.IO.File.Exists(DnsPresetPath))
                return System.IO.File.ReadAllLines(DnsPresetPath).ToList();
        }
        catch { }
        return new List<string>();
    }

    /// <summary>Feed the resolver the preset file plus the user's manual lines.</summary>
    private void ApplyDnsBlocklists()
    {
        var merged = LoadDnsPresetLines();
        merged.AddRange(DnsBlockLines(DnsBlockBox.Text));
        _dnsResolver.SetBlocklist(merged);
    }

    private void RefreshDnsPresetStatus()
    {
        if (DnsPresetStatus == null) return;
        DnsPresetStatus.Text = System.IO.File.Exists(DnsPresetPath)
            ? $"Preset active · {_dnsResolver.BlockedDomainCount:N0} domains in the resolver's blocklist. Delete dns-blocklist-preset.txt to remove."
            : "No preset loaded - the resolver only blocks the lines you add below.";
    }

    private async void DnsPreset_Click(object sender, RoutedEventArgs e)
    {
        DnsPresetBtn.IsEnabled = false;
        DnsPresetStatus.Text = "Downloading preset… (a few MB)";
        try
        {
            string body = await _dnsPresetHttp.GetStringAsync(DnsPresetUrl);
            if (body.Length < 10000) throw new Exception("Response too small - unexpected content.");
            System.IO.File.WriteAllText(DnsPresetPath, body);
            ApplyDnsBlocklists();
            RefreshDnsPresetStatus();
            Notify("good", "DNS preset loaded",
                   $"{_dnsResolver.BlockedDomainCount:N0} domains now blocked by GunWall's resolver.");
        }
        catch (Exception ex)
        {
            DnsPresetStatus.Text = $"Download failed: {ex.Message}";
        }
        finally { DnsPresetBtn.IsEnabled = true; }
    }

    // ====================================================== captive portal (§9)
    private async void CheckPortal_Click(object sender, RoutedEventArgs e)
    {
        CheckPortalBtn.IsEnabled = false;
        PortalStatus.Text = "Checking connectivity\u2026";
        try
        {
            var r = await CaptivePortalService.CheckAsync();
            PortalStatus.Text = r.Detail;
            if (r.Captive)
                Notify("warn", "Captive portal detected",
                       "A login page is intercepting traffic. Use Portal mode to reach it.");
        }
        finally { CheckPortalBtn.IsEnabled = true; }
    }

    private void PortalMode_Click(object sender, RoutedEventArgs e)
    {
        var until = _firewall.SnoozeProtection(TimeSpan.FromMinutes(5));
        PortalStatus.Text = $"Portal mode: filtering paused until {until.ToLocalTime():HH:mm:ss}. " +
                            "Open a browser, complete the portal login, and protection resumes on its own.";
        UpdateStatusBanner();
    }

    // ===================================================== notification center (§13)
    /// <summary>Record a notable event in the Alerts page and bump the sidebar
    /// badge when the page isn't open. Safe to call from any UI-thread path.</summary>
    private void Notify(string kind, string title, string detail)
    {
        try
        {
            if (AlertsList == null) return;
            _notifications.Insert(0, new AppNotification { Kind = kind, Title = title, Detail = detail });
            while (_notifications.Count > MaxNotifications)
                _notifications.RemoveAt(_notifications.Count - 1);
            if (!_alertsVisible) _unreadNotifications++;
            RefreshAlertsBadge();
        }
        catch { /* notifications must never break the caller */ }
    }

    private void RefreshAlertsBadge()
    {
        if (AlertsNavLabel == null) return;
        AlertsNavLabel.Text = _unreadNotifications > 0 ? $"Alerts ({_unreadNotifications})" : "Alerts";
        if (AlertsEmpty != null)
            AlertsEmpty.Visibility = _notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ======================================================== data usage (§7b)
    private void RefreshUsage()
    {
        if (UsageList == null || UsageWindowCombo == null) return;
        var window = UsageWindowCombo.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(5),
            1 => TimeSpan.FromHours(1),
            _ => TimeSpan.FromHours(24)
        };
        UsageList.ItemsSource = _usage.Totals(window);
    }

    private void UsageWindow_Changed(object sender, SelectionChangedEventArgs e) => RefreshUsage();

    // ============================================================ app health (§12)
    /// <summary>Live self-diagnostics card in Settings: the same counters the
    /// diagnostics export records, refreshed each tick while the page is open.</summary>
    private void UpdateHealthCard()
    {
        if (HealthPipeline == null) return;

        HealthPipeline.Text =
            $"Monitoring: {_sampleTicks} ticks, {_sampleErrors} sample / {_detectErrors} detection errors"
            + (string.IsNullOrEmpty(_lastSampleError) ? "" : $"  \u2014  last: {_lastSampleError}");

        var geo = _firewall.GeoIp;
        HealthGeo.Text = _firewall.GeoIpApiActive
            ? $"GeoIP: API server \u00B7 {geo.ApiOkCount} ok / {geo.ApiFailCount} failed"
            : "GeoIP: local database";

        HealthDns.Text = _dnsResolver.Running
            ? $"DNS resolver: listening on 127.0.0.1:{_dnsResolver.Port} \u00B7 {_dnsResolver.Total} queries, {_dnsResolver.Blocked} blocked"
            : "DNS resolver: stopped";

        HealthVt.Text = string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey)
            ? "VirusTotal: no API key set"
            : $"VirusTotal: {_firewall.VtCacheCount} verdicts cached \u00B7 {_vtQueue.Count} queued";

        HealthRules.Text =
            $"Rules: {_firewall.AppRuleCount} app \u00B7 {_firewall.CustomRuleCount} custom \u00B7 " +
            $"{_firewall.SystemRuleCount} system  \u2014  engine {(_engineReady ? "active" : "unavailable")}";
    }

    // ---------------------------------------------- §3 Phase 2: system routing
    private bool _dnsToggleGuard; // suppress handler recursion when we set toggles in code

    /// <summary>Reflect persisted routing state in the toggles + status line.</summary>
    private void UpdateDnsRedirectUi()
    {
        if (DnsRedirectToggle == null) return;
        _dnsToggleGuard = true;
        DnsRedirectToggle.IsChecked = _firewall.DnsRedirectActive;
        DnsGamingToggle.IsChecked = _firewall.DnsGamingSession;
        DnsGamingToggle.IsEnabled = _firewall.DnsRedirectActive;
        _dnsToggleGuard = false;

        if (!_firewall.DnsRedirectActive)
            DnsRedirectStatus.Text =
                "System DNS untouched. Turn on routing to send every app's lookups through GunWall " +
                "(needs the resolver on port 53). Your current DNS is captured first and restored on exit.";
        else if (_firewall.DnsGamingSession)
            DnsRedirectStatus.Text =
                "Gaming Session: system DNS is back on your normal servers for zero-overhead play. " +
                "The resolver stays warm - flip the toggle off to route through GunWall again.";
        else
            DnsRedirectStatus.Text =
                $"System DNS routed to GunWall ({_firewall.DnsSavedAdapters.Count} adapter(s) captured for restore; " +
                "auto-restored on exit)." +
                (DnsService.TunnelAdapterUp()
                    ? " VPN detected: while it's connected its in-tunnel DNS may take priority. GunWall " +
                      "only changes physical adapters, so nothing is broken or leaked either way."
                    : "");
    }

    private void SetToggleSilently(CheckBox box, bool value)
    {
        _dnsToggleGuard = true; box.IsChecked = value; _dnsToggleGuard = false;
    }

    private void DnsRedirect_Toggled(object sender, RoutedEventArgs e)
    {
        if (_dnsToggleGuard || DnsRedirectToggle == null) return;

        if (DnsRedirectToggle.IsChecked == true)
        {
            // Routing needs the resolver live on the standard DNS port.
            if (!int.TryParse(DnsPortBox.Text.Trim(), out int port)) port = 53;
            if (port != 53)
            {
                MessageBox.Show(
                    "Windows can only send system DNS to port 53, so routing needs the resolver " +
                    "on port 53. Set the listen port to 53 (stop whatever is using it) and try again.",
                    "DNS routing", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetToggleSilently(DnsRedirectToggle, false);
                return;
            }
            if (!_dnsResolver.Running)
            {
                DnsStart_Click(this, new RoutedEventArgs()); // shows its own message on failure
                if (!_dnsResolver.Running) { SetToggleSilently(DnsRedirectToggle, false); return; }
            }

            // Capture what to restore - but never let a crash-leftover 127.0.0.1
            // state overwrite the genuine saved configuration.
            var captured = DnsService.CaptureAdapterDns();
            bool poisoned = DnsService.LooksLikeOurRedirect(captured);

            int changed = DnsService.RedirectToLocal();
            if (changed == 0)
            {
                MessageBox.Show("No adapter accepted the DNS change (netsh failed). Nothing was modified.",
                    "DNS routing", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetToggleSilently(DnsRedirectToggle, false);
                return;
            }
            _firewall.SaveDnsRedirectState(true, false, poisoned ? null : captured);
            Services.DiagnosticLog.Log($"DNS routing ON: {changed} adapter(s) -> 127.0.0.1.");
        }
        else
        {
            int n = DnsService.RestoreAdapters(_firewall.DnsSavedAdapters);
            _firewall.SaveDnsRedirectState(false, false, null);
            Services.DiagnosticLog.Log($"DNS routing OFF: {n} adapter(s) restored.");
        }
        UpdateDnsRedirectUi();
    }

    private void DnsGaming_Toggled(object sender, RoutedEventArgs e)
    {
        if (_dnsToggleGuard || DnsGamingToggle == null) return;
        if (!_firewall.DnsRedirectActive) { SetToggleSilently(DnsGamingToggle, false); return; }

        if (DnsGamingToggle.IsChecked == true)
        {
            // Bypass: put the system back on its normal DNS; the resolver stays
            // warm and routing intent is remembered.
            int n = DnsService.RestoreAdapters(_firewall.DnsSavedAdapters);
            _firewall.SaveDnsRedirectState(true, true, null);
            Services.DiagnosticLog.Log($"Gaming Session ON: {n} adapter(s) back to normal DNS.");
        }
        else
        {
            DnsService.RedirectToLocal();
            _firewall.SaveDnsRedirectState(true, false, null);
            Services.DiagnosticLog.Log("Gaming Session OFF: system DNS routed to GunWall again.");
        }
        UpdateDnsRedirectUi();
    }

    /// <summary>On launch, honor last session's routing intent - with a fail-safe:
    /// if the resolver can't start, adapters are restored, never left pointing at
    /// a dead 127.0.0.1.</summary>
    private void ReapplyDnsRedirectOnStartup()
    {
        if (!_firewall.DnsRedirectActive) return;

        bool resolverOk = _dnsResolver.Running;
        if (!resolverOk && _firewall.DnsResolverPort == 53)
        {
            try { _dnsResolver.Start(53, _firewall.DnsResolverUpstream); resolverOk = true; }
            catch { resolverOk = false; }
        }

        if (!resolverOk)
        {
            DnsService.RestoreAdapters(_firewall.DnsSavedAdapters);
            _firewall.SaveDnsRedirectState(false, false, null);
            Services.DiagnosticLog.Log("DNS routing intent dropped: resolver couldn't start on port 53; adapters restored.");
            return;
        }

        if (!_firewall.DnsGamingSession)
        {
            var captured = DnsService.CaptureAdapterDns();
            bool poisoned = DnsService.LooksLikeOurRedirect(captured);
            DnsService.RedirectToLocal();
            _firewall.SaveDnsRedirectState(true, false, poisoned ? null : captured);
            Services.DiagnosticLog.Log("DNS routing re-applied from saved intent.");
        }
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

            LogActivity(new NetActivityEvent
            {
                ProcessName = c.ProcessName,
                Detail = $"connected to {c.RemoteEndpoint} ({c.Protocol})"
            });
            while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);
        }
        // Keep the dedupe set bounded.
        if (_seenConnections.Count > 8000) _seenConnections.Clear();
    }

    /// <summary>Inserts an activity row and trims the in-memory log to the configured cap.</summary>
    private void LogActivity(NetActivityEvent ev)
    {
        _activity.Insert(0, ev);
        int max = _firewall.MaxLogEntries;
        if (max > 0) while (_activity.Count > max) _activity.RemoveAt(_activity.Count - 1);
    }

    /// <summary>Inserts a packet row and trims the in-memory log to the configured cap.</summary>
    private void LogPacket(PacketLogEntry ev)
    {
        _packets.Insert(0, ev);
        int max = _firewall.MaxLogEntries;
        if (max > 0) while (_packets.Count > max) _packets.RemoveAt(_packets.Count - 1);
    }

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        if (_firewall.ConfirmClearLogs && MessageBox.Show(
                "Clear the activity log?", "GunWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _activity.Clear();
    }

    private void ClearPackets_Click(object sender, RoutedEventArgs e)
    {
        if (_firewall.ConfirmClearLogs && MessageBox.Show(
                "Clear the packets log?", "GunWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _packets.Clear();
    }

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
            try
            {
                if (!_firewall.RemoveCustomRule(id))
                    MessageBox.Show("This rule is protected. Unprotect it first to delete it.",
                        "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshRulesList();
            }
            catch (Exception ex) { ShowError(ex); }
        }
    }

    private void ProtectRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string id)
        {
            var rule = _firewall.CustomRules.FirstOrDefault(r => r.Id == id);
            if (rule == null) return;
            _firewall.SetCustomRuleProtected(id, !rule.Protected);
            RefreshRulesList();
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
            LogActivity(new NetActivityEvent
            {
                ProcessName = System.IO.Path.GetFileNameWithoutExtension(e.AppPath),
                Detail = $"{verb}{remote} ({e.Protocol})"
            });
            while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);

            // Packets Log: verdict + reason from GunWall's own rule state (§8) —
            // the same precedence the engine enforces, so they always agree.
            // This also fixes lockdown events previously showing as allowed.
            string appName = System.IO.Path.GetFileNameWithoutExtension(e.AppPath);
            string reason = _firewall.ExplainVerdict(e.AppPath, e.RemoteAddress ?? "",
                e.RemotePort, e.Protocol, outbound: true, out bool blocked);
            LogPacket(new PacketLogEntry
            {
                AppName = appName,
                ExePath = e.AppPath,
                Protocol = e.Protocol,
                Direction = "Out",
                RemoteEndpoint = string.IsNullOrEmpty(e.RemoteAddress)
                    ? "\u2014" : $"{e.RemoteAddress}:{e.RemotePort}",
                Blocked = blocked,
                Reason = reason
            });
            while (_packets.Count > MaxPackets) _packets.RemoveAt(_packets.Count - 1);

            // Optional: persist this entry to the CSV log file.
            _firewall.LogPacketToFile(DateTime.Now, blocked, appName, e.Protocol, "Out",
                string.IsNullOrEmpty(e.RemoteAddress) ? "" : $"{e.RemoteAddress}:{e.RemotePort}",
                e.AppPath);

            // §1 entity rules: reactively block this remote if a country / continent /
            // ASN rule matches it. Independent of monitoring/strict mode (an explicit
            // rule always applies). Post-hoc by design - blocks sustained traffic.
            string? entReason = _firewall.ApplyEntityBlocks(e.AppPath, e.RemoteAddress ?? "");
            if (entReason != null)
            {
                LogActivity(new NetActivityEvent
                {
                    ProcessName = appName,
                    Detail = $"blocked {e.RemoteAddress} ({entReason})"
                });
                while (_activity.Count > MaxActivity) _activity.RemoveAt(_activity.Count - 1);
            }

            // Approval pipeline: prompt once for any undecided app.
            string path = e.AppPath;
            if (_firewall.IsBlocked(path) || _firewall.IsAllowed(path) || _firewall.IsSilent(path))
                return;
            if (!_firewall.AlertsEnabled) return;
            // Only interrupt with approval popups once the firewall is enabled.
            // In monitoring mode GunWall observes silently - no prompts - so the
            // user can open and explore the app without being interrupted.
            if (!_firewall.StrictMode) return;
            if (!_promptedThisSession.Add(path)) return;

            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            _alertQueue.Enqueue(new AlertWindow.AlertInfo(
                name, path, e.RemoteAddress ?? "", e.RemotePort, e.Protocol, DateTime.Now));
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
                // Monitoring: observe silently. Mark the app as seen so the Apps
                // list and logs stay populated, but never prompt. Approval popups
                // only start once the firewall is enabled (Zero-Trust), so the
                // user can open and check the app without interruption.
                if (!string.IsNullOrEmpty(c.RemoteAddress) && c.RemoteAddress is not ("0.0.0.0" or "::"))
                    _firewall.MarkKnown(proc.Path);
                continue;
            }

            string remote = c.RemoteAddress;
            if (remote is "0.0.0.0" or "::") remote = ""; // unbound -> pending
            _alertQueue.Enqueue(new AlertWindow.AlertInfo(
                proc.Name, proc.Path, remote ?? "", c.RemotePort, c.Protocol, DateTime.Now));
        }

        ShowNextAlert();
    }

    private void ShowNextAlert()
    {
        if (_alertOpen || _alertQueue.Count == 0) return;
        // Fullscreen-silent: hold approval popups while a game/fullscreen app is
        // foreground. The alert stays queued and appears once fullscreen ends.
        if (_firewall.FullscreenSilent && Services.FullscreenDetector.IsFullscreenAppActive()) return;
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
            a.Note = _firewall.GetNote(a.ExecutablePath);
            ApplyVtStatus(a);   // §VT cached verdict + auto-queue on first sight

            var store = Services.StoreAppService.Resolve(a.ExecutablePath);
            a.IsStoreApp = store.IsStore;
            a.StoreName = store.DisplayName;
            a.PackageFamily = store.PackageFamily;
            // Show the friendly Store name in the Publisher column when we have one
            // and the signature didn't already give a clear publisher.
            if (store.IsStore && !string.IsNullOrWhiteSpace(store.DisplayName) &&
                (string.IsNullOrWhiteSpace(a.Publisher) || a.Publisher is "Unsigned" or "Unknown"))
                a.Publisher = $"Store: {store.DisplayName}";
        }

        int storeCount = known.Values.Count(a => a.IsStoreApp);
        if (storeCount != _lastStoreCount)
        {
            _lastStoreCount = storeCount;
            Services.DiagnosticLog.Log($"Apps list: {known.Count} apps, {storeCount} detected as Store/UWP.");
        }

        IEnumerable<AppInfo> view = known.Values;
        if (!string.IsNullOrWhiteSpace(_appFilter))
            view = view.Where(a =>
                a.Name.Contains(_appFilter, StringComparison.OrdinalIgnoreCase) ||
                a.ExecutablePath.Contains(_appFilter, StringComparison.OrdinalIgnoreCase));

        // Keep-unused toggle: when off, hide apps with no rule and no live connections.
        if (!_firewall.KeepUnusedApps)
            view = view.Where(a => a.ActiveConnections > 0
                || _firewall.IsBlocked(a.ExecutablePath)
                || _firewall.IsAllowed(a.ExecutablePath)
                || _firewall.IsSilent(a.ExecutablePath));

        // Preserve selection across the periodic rebuild: the timer replaces every
        // AppInfo object, which would otherwise null out SelectedItem mid-interaction
        // (e.g. while a right-click context menu is open).
        string? keepPath = (AppsList.SelectedItem as AppInfo)?.ExecutablePath;

        _apps.Clear();
        foreach (var a in view
                     .OrderByDescending(a => a.Status == AppStatus.Blocked) // blocked pinned on top
                     .ThenByDescending(a => a.ActiveConnections)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            _apps.Add(a);

        if (!string.IsNullOrEmpty(keepPath))
        {
            var reselect = _apps.FirstOrDefault(a =>
                string.Equals(a.ExecutablePath, keepPath, StringComparison.OrdinalIgnoreCase));
            if (reselect != null) AppsList.SelectedItem = reselect;
        }
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

        string? keepConn = (ConnList.SelectedItem as ConnectionInfo) is { } sc
            ? $"{sc.ProcessId}|{sc.Protocol}|{sc.LocalAddress}:{sc.LocalPort}|{sc.RemoteAddress}:{sc.RemotePort}"
            : null;

        if (!_geoDownloading && GeoStatus != null)
            GeoStatus.Text = _firewall.GeoIpApiActive
                ? "GeoIP: API server"
                : _firewall.GeoIpLoaded
                    ? $"GeoIP: {_firewall.GeoIpRangeCount:N0} ranges"
                    : "GeoIP: not downloaded";
        if (!_geoDownloading && GeoDownloadBtn != null)
            GeoDownloadBtn.IsEnabled = !_firewall.GeoIpApiActive; // download is for local mode only

        _connections.Clear();
        bool geo = _firewall.GeoIpActive;
        // Dedupe GeoIP lookups within this refresh: many sockets often share one remote
        // IP (e.g. a browser holding dozens of connections to the same CDN), so resolve
        // each distinct address once instead of once per row.
        Dictionary<string, GunWall.Services.GeoIpService.GeoInfo>? geoMemo =
            geo ? new(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var c in view.OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            if (geo && c.Country.Length == 0 && !string.IsNullOrEmpty(c.RemoteAddress))
            {
                if (!geoMemo!.TryGetValue(c.RemoteAddress, out var g))
                {
                    g = _firewall.GeoIp.Lookup(c.RemoteAddress);
                    geoMemo[c.RemoteAddress] = g;
                }
                if (g.HasData) { c.Country = g.Country; c.Asn = g.Asn; c.AsnOwner = g.Owner; }
            }
            _connections.Add(c);
        }

        if (keepConn != null)
        {
            var re = _connections.FirstOrDefault(c =>
                $"{c.ProcessId}|{c.Protocol}|{c.LocalAddress}:{c.LocalPort}|{c.RemoteAddress}:{c.RemotePort}" == keepConn);
            if (re != null) ConnList.SelectedItem = re;
        }
    }

    // ===== §11 connection inspector (read-only detail pane) =====
    private void ConnSelected(object sender, SelectionChangedEventArgs e)
    {
        // Ignore the transient deselection that happens while the list refreshes
        // (Clear + re-add); keep the last view until a real row is chosen.
        if (ConnList.SelectedItem is not ConnectionInfo c) return;

        InspPlaceholder.Visibility = Visibility.Collapsed;
        InspContent.Visibility = Visibility.Visible;

        InspProcess.Text = string.IsNullOrEmpty(c.ProcessName) ? "(unknown)" : c.ProcessName;
        InspSub.Text = $"PID {c.ProcessId}  \u00B7  {c.Protocol}";

        bool listening = c.State.IndexOf("listen", StringComparison.OrdinalIgnoreCase) >= 0;
        InspType.Text = listening ? "Listening (inbound)" : "Active connection";
        InspLocal.Text = c.LocalEndpoint;
        InspRemote.Text = c.RemoteEndpoint;
        InspState.Text = string.IsNullOrEmpty(c.State) ? "\u2014" : c.State;

        // Location
        if (c.Country.Length > 0 || c.Asn != 0)
        {
            var flag = GunWall.Converters.CountryFlagConverter.Load(c.Country);
            InspFlag.Source = flag;
            InspFlag.Visibility = flag != null ? Visibility.Visible : Visibility.Collapsed;
            InspCountry.Text = c.Country.Length > 0
                ? GunWall.Services.GeoData.CountryName(c.Country)
                : "Unknown country";
            InspAsnRow.Visibility = c.Asn != 0 ? Visibility.Visible : Visibility.Collapsed;
            InspAsn.Text = c.Asn != 0 ? $"AS{c.Asn}" : "\u2014";
            InspOwnerRow.Visibility = c.AsnOwner.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            InspOwner.Text = c.AsnOwner;
        }
        else
        {
            InspFlag.Source = null;
            InspFlag.Visibility = Visibility.Collapsed;
            InspCountry.Text = "Local / private address";
            InspAsnRow.Visibility = Visibility.Collapsed;
            InspOwnerRow.Visibility = Visibility.Collapsed;
        }

        // Application policy (best-effort, read-only)
        if (!string.IsNullOrEmpty(c.ExePath))
        {
            InspPath.Text = c.ExePath;
            var status = _firewall.EffectiveStatus(c.ExePath);
            bool blocked = status == AppStatus.Blocked;
            InspStatus.Text = blocked ? "Blocked" : "Allowed";
            InspStatus.Foreground = (Brush)FindResource(blocked ? "BlockText" : "AllowText");

            InspScopeRow.Visibility = Visibility.Visible;
            if (_firewall.IsKnownApp(c.ExePath))
            {
                var blocks = new System.Collections.Generic.List<string>();
                if (_firewall.IsScopeBlocked(c.ExePath, "local")) blocks.Add("localhost");
                if (_firewall.IsScopeBlocked(c.ExePath, "lan")) blocks.Add("LAN");
                if (_firewall.IsScopeBlocked(c.ExePath, "incoming")) blocks.Add("incoming");
                InspScope.Text = blocks.Count == 0 ? "No scope restrictions" : "Blocked: " + string.Join(", ", blocks);
            }
            else InspScope.Text = "Not individually managed";

            // §8 part two: the same verdict reason the Packets Log shows.
            InspVerdict.Text = _firewall.ExplainVerdict(c.ExePath, c.RemoteAddress ?? "",
                c.RemotePort, c.Protocol ?? "", outbound: true, out _);
        }
        else
        {
            InspPath.Text = "\u2014";
            InspStatus.Text = "No owning process";
            InspStatus.Foreground = (Brush)FindResource("TextSecondary");
            InspScopeRow.Visibility = Visibility.Collapsed;
            InspVerdict.Text = "\u2014";
        }
    }

    private bool _geoDownloading;

    private async void GeoDownload_Click(object sender, RoutedEventArgs e)
    {
        _geoDownloading = true;
        GeoDownloadBtn.IsEnabled = false;
        GeoStatus.Text = "Downloading GeoIP data\u2026";
        try
        {
            int n = await System.Threading.Tasks.Task.Run(() => _firewall.DownloadAndLoadGeoIp());
            GeoStatus.Text = $"GeoIP: {n:N0} ranges loaded";
        }
        catch (Exception ex)
        {
            GeoStatus.Text = "GeoIP: download failed";
            MessageBox.Show("Could not download the GeoIP database:\n\n" + ex.Message +
                "\n\nData source: iptoasn.com (free, public domain).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _geoDownloading = false;
            GeoDownloadBtn.IsEnabled = true;
            RebuildConnList();
        }
    }

    // ===== GeoIP data source (local download vs self-hosted API) =====
    private void GeoSourceApply_Click(object sender, RoutedEventArgs e)
    {
        string mode = (GeoSourceCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "local";
        string url = (GeoApiUrlBox?.Text ?? "").Trim();
        if (mode == "api" && url.Length == 0)
        {
            if (GeoSourceStatus != null) GeoSourceStatus.Text = "Enter the API server URL first.";
            return;
        }
        _firewall.SetGeoIpSource(mode, url);
        if (GeoSourceStatus != null) GeoSourceStatus.Text = GeoSourceSummary();
        RebuildConnList();     // re-enrich under the new source
        UpdateEntityStatus();  // §1 status reflects the new source
    }

    private async void GeoApiTest_Click(object sender, RoutedEventArgs e)
    {
        string url = (GeoApiUrlBox?.Text ?? "").Trim();
        if (GeoSourceStatus != null) GeoSourceStatus.Text = "Testing\u2026";
        string result = await _firewall.TestGeoIpApiAsync(url);
        if (GeoSourceStatus != null) GeoSourceStatus.Text = result;
    }

    private string GeoSourceSummary() => _firewall.GeoIpApiActive
        ? $"Using API server: {_firewall.GeoIpApiUrl}"
        : _firewall.GeoIpLoaded
            ? $"Using local database ({_firewall.GeoIpRangeCount:N0} ranges)."
            : "Using local database - not downloaded yet (Connections tab).";

    private void InitGeoSourceUi()
    {
        if (GeoSourceCombo != null) GeoSourceCombo.SelectedIndex = _firewall.GeoIpMode == "api" ? 1 : 0;
        if (GeoApiUrlBox != null) GeoApiUrlBox.Text = _firewall.GeoIpApiUrl;
        if (GeoSourceStatus != null) GeoSourceStatus.Text = GeoSourceSummary();
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
    // Frame-driven smoothing: the sampler updates the target series once a second;
    // this eases the displayed values toward the target every rendered frame (at
    // the monitor's refresh rate) and draws smooth Catmull-Rom curves. That's what
    // turns the old 1-fps snap into a fluid, continuously-eased graph. Runs only while
    // the dashboard is visible, and stops drawing once values have settled.
    // 250ms live sampler: instantaneous rates from NIC counter deltas feed the
    // fine series, then the geometry rebuilds so the tip reflects traffic that
    // happened a quarter-second ago — real-time, not a delayed recording.
    private void GraphTimer_Tick(object? sender, EventArgs e)
    {
        if (PanelDashboard == null || PanelDashboard.Visibility != Visibility.Visible) return;
        try
        {
            var (rx, tx) = _monitor.GetCumulativeBytes();
            var now = DateTime.UtcNow;
            if (_gLastRx >= 0)
            {
                double secs = Math.Max(0.05, (now - _gLastAt).TotalSeconds);
                double down = Math.Max(0, (rx - _gLastRx) / secs);
                double up = Math.Max(0, (tx - _gLastTx) / secs);
                Array.Copy(_gDown, 1, _gDown, 0, GraphFinePoints - 1); _gDown[^1] = down;
                Array.Copy(_gUp, 1, _gUp, 0, GraphFinePoints - 1); _gUp[^1] = up;
                RedrawGraph();
            }
            _gLastRx = rx; _gLastTx = tx; _gLastAt = now;
        }
        catch { /* counters are best-effort; graph must never throw */ }
    }

    // Per frame (monitor refresh rate): slide the already-built waveform left by
    // the fraction of the way to the next sample. No geometry work here — just a
    // transform offset — so it glides continuously instead of snapping/refreshing.
    private void OnGraphFrame(object? sender, EventArgs e)
    {
        if (PanelDashboard == null || PanelDashboard.Visibility != Visibility.Visible) return;
        if (_graphStepX <= 0 || _lastGraphSample == DateTime.MinValue) return;
        double elapsed = (DateTime.UtcNow - _lastGraphSample).TotalMilliseconds;
        double frac = Math.Clamp(elapsed / _graphInterval, 0.0, 1.0);
        _graphScroll.X = -frac * _graphStepX;
    }

    // Rebuilt once per sample (~1/sec). Lays out points so the newest sits just off
    // the right edge and slides in over the next second; the oldest slides off the
    // clipped left edge. That right-to-left march is the continuous "train" motion.
    private void RedrawGraph()
    {
        var canvas = GraphCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 1 || h <= 1) { _graphStepX = 0; return; }

        double max = 64 * 1024;
        for (int i = 0; i < GraphFinePoints; i++)
        {
            max = Math.Max(max, _gDown[i]);
            max = Math.Max(max, _gUp[i]);
        }

        _graphStepX = w / (GraphFinePoints - 1);
        DrawBaseline(canvas, w, h); // static, not scrolled
        // Download: blue gradient area. Upload: thin magenta line.
        AddSmoothSeries(canvas, _gDown, max, w, h, Color.FromRgb(0x3D, 0xA9, 0xFC), fillArea: true, _graphScroll);
        AddSmoothSeries(canvas, _gUp, max, w, h, Color.FromRgb(0xE0, 0x66, 0xA6), fillArea: false, _graphScroll);

        var now = DateTime.UtcNow;
        if (_lastGraphSample != DateTime.MinValue)
        {
            double dt = (now - _lastGraphSample).TotalMilliseconds;
            if (dt is > 100 and < 5000) _graphInterval = dt; // adapt to the real cadence
        }
        _lastGraphSample = now;
        _graphScroll.X = 0; // new data appended at the right; restart the slide
    }

    private static void DrawBaseline(Canvas canvas, double w, double h)
    {
        canvas.Children.Add(new Line
        {
            X1 = 0, Y1 = h - 1, X2 = w, Y2 = h - 1,
            Stroke = new SolidColorBrush(Color.FromRgb(0x26, 0x28, 0x2B)),
            StrokeThickness = 1
        });
    }

    // Screen points for one series, laid out so the newest point (index n-1) sits
    // one step BEYOND the right edge, ready to slide in.
    private static Point[] SeriesPoints(double[] series, double max, double w, double h)
    {
        int n = series.Length;
        var pts = new Point[n];
        double stepX = w / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double x = (i - n + 2) * stepX + w;
            double y = h - (series[i] / max * (h - 6)) - 3;
            pts[i] = new Point(x, y);
        }
        return pts;
    }

    // Catmull-Rom spline through pts, expressed as cubic Bezier segments.
    private static (Point C1, Point C2, Point End)[] SplineBeziers(Point[] pts)
    {
        int n = pts.Length;
        var segs = new (Point, Point, Point)[n - 1];
        for (int i = 0; i < n - 1; i++)
        {
            Point p0 = i > 0 ? pts[i - 1] : pts[i];
            Point p1 = pts[i];
            Point p2 = pts[i + 1];
            Point p3 = i + 2 < n ? pts[i + 2] : pts[i + 1];
            var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
            var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
            segs[i] = (c1, c2, p2);
        }
        return segs;
    }

    private static void AddSmoothSeries(Canvas canvas, double[] series, double max,
                                        double w, double h, Color color, bool fillArea,
                                        Transform scroll)
    {
        var pts = SeriesPoints(series, max, w, h);

        if (fillArea)
        {
            var fillGeo = new StreamGeometry();
            using (var ctx = fillGeo.Open())
            {
                ctx.BeginFigure(new Point(pts[0].X, h), isFilled: true, isClosed: true);
                ctx.LineTo(pts[0], isStroked: false, isSmoothJoin: false);
                foreach (var (c1, c2, end) in SplineBeziers(pts))
                    ctx.BezierTo(c1, c2, end, isStroked: false, isSmoothJoin: true);
                ctx.LineTo(new Point(pts[^1].X, h), isStroked: false, isSmoothJoin: false);
            }
            fillGeo.Freeze();

            var fillBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x96, color.R, color.G, color.B), 0.0));
            fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, color.R, color.G, color.B), 0.5));
            fillBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1.0));
            fillBrush.Freeze();
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = fillGeo, Fill = fillBrush, IsHitTestVisible = false, RenderTransform = scroll });
        }

        var lineGeo = new StreamGeometry();
        using (var ctx = lineGeo.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
            foreach (var (c1, c2, end) in SplineBeziers(pts))
                ctx.BezierTo(c1, c2, end, isStroked: true, isSmoothJoin: true);
        }
        lineGeo.Freeze();

        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = lineGeo,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = fillArea ? 2.2 : 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            RenderTransform = scroll
        });
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
        PanelTraffic.Visibility = tag == "Traffic" ? Visibility.Visible : Visibility.Collapsed;
        PanelDns.Visibility = tag == "Dns" ? Visibility.Visible : Visibility.Collapsed;
        _dnsVisible = tag == "Dns";
        PanelAlerts.Visibility = tag == "Alerts" ? Visibility.Visible : Visibility.Collapsed;
        _alertsVisible = tag == "Alerts";
        if (_alertsVisible) { _unreadNotifications = 0; RefreshAlertsBadge(); }
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
        if (tag == "Traffic") RefreshTraffic();
        if (tag == "Dns") UpdateDnsUi();
        if (tag == "Settings") UpdateHealthCard();
        if (tag == "Rules") RefreshRulesList();
        if (tag == "Services" && _services.Count == 0) LoadServices();
        if (tag == "System") BuildSystemRulesUi();
        if (tag == "Security") { BuildBlocklistCatUi(); RefreshDnsCombo(); }
        if (tag == "Settings") { RefreshProfilesCombo(); RefreshProfileCombo(); RefreshBackupsCombo(); RefreshWinFwStatus(); }

        if (tag == "Dashboard") RedrawGraph();
        AnimatePanelIn(); // choreographed entrance: fade + rise, 200ms ease-out
    }

    /// <summary>Panel-switch motion: the content host fades in and rises 12px
    /// with an ease-out curve. Small and fast (~200ms) so it reads as polish,
    /// never as delay; failures are swallowed so navigation can never break.</summary>
    private void AnimatePanelIn()
    {
        try
        {
            if (PanelHost == null || PanelHostShift == null) return;
            var ease = new System.Windows.Media.Animation.CubicEase
            { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

            var fade = new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            var rise = new System.Windows.Media.Animation.DoubleAnimation(12.0, 0.0,
                TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };

            PanelHost.BeginAnimation(OpacityProperty, fade);
            PanelHostShift.BeginAnimation(TranslateTransform.YProperty, rise);
        }
        catch { /* motion is cosmetic - never let it break navigation */ }
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
        var app = AppsList.SelectedItem as AppInfo;
        Services.DiagnosticLog.Log($"CopyPath_Click fired; path='{app?.ExecutablePath ?? "<null>"}'");
        if (app == null || string.IsNullOrEmpty(app.ExecutablePath)) return;
        try
        {
            Clipboard.SetDataObject(app.ExecutablePath, true); // more robust than SetText
            MessageBox.Show($"Copied to clipboard:\n{app.ExecutablePath}",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Services.DiagnosticLog.LogException("CopyPath", ex); ShowError(ex); }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var app = AppsList.SelectedItem as AppInfo;
        Services.DiagnosticLog.Log($"OpenLocation_Click fired; path='{app?.ExecutablePath ?? "<null>"}'");
        if (app == null || string.IsNullOrEmpty(app.ExecutablePath)) return;
        if (!System.IO.File.Exists(app.ExecutablePath))
        {
            MessageBox.Show($"The file no longer exists at:\n{app.ExecutablePath}",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Services.ShellHelper.RevealInExplorer(app.ExecutablePath))
            MessageBox.Show($"Could not open the location in Explorer.\nThe file is at:\n{app.ExecutablePath}",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>Selects the row under the cursor on right-click, so context-menu
    /// actions (which read SelectedItem) operate on the row you clicked. WPF does
    /// not select on right-click by default. Handled at the ListView level and
    /// walking up to the row container, which is more reliable than a per-item
    /// EventSetter.</summary>
    private void Row_RightClickSelect(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindAncestor<System.Windows.Controls.ListViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    /// <summary>Walks up the visual (and, for text, logical) tree to the first
    /// ancestor of type T.</summary>
    private static T? FindAncestor<T>(DependencyObject? start) where T : DependencyObject
    {
        DependencyObject? d = start;
        while (d != null)
        {
            if (d is T match) return match;
            DependencyObject? parent =
                d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : null;
            parent ??= System.Windows.LogicalTreeHelper.GetParent(d);
            d = parent;
        }
        return null;
    }

    private void Properties_Click(object sender, RoutedEventArgs e)
    {
        var app = AppsList.SelectedItem as AppInfo;
        Services.DiagnosticLog.Log($"Properties_Click fired; path='{app?.ExecutablePath ?? "<null>"}'");
        if (app == null) return;
        try
        {
            Services.DiagnosticLog.Log("Properties: creating window...");
            var win = new AppPropertiesWindow(app, _firewall) { Owner = this };
            Services.DiagnosticLog.Log("Properties: showing dialog...");
            bool? changed = win.ShowDialog();
            Services.DiagnosticLog.Log($"Properties: dialog closed (result={changed}).");
            if (changed == true) RebuildAppsList(); // a rule was applied from the dialog
        }
        catch (Exception ex) { Services.DiagnosticLog.LogException("Properties", ex); ShowError(ex); }
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

    private void PurgeExpiredTimers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int purged = _firewall.PurgeExpiredTimers();
            RebuildAppsList();
            MessageBox.Show(
                purged == 0 ? "No expired timed blocks to clear." : $"Cleared {purged} expired timed block(s).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // ---- category color customization ----
    private void PopulateColorUi()
    {
        try
        {
            ColorSignedBox.Text = Services.CategoryPalette.Get("Signed");
            ColorUnsignedBox.Text = Services.CategoryPalette.Get("Unsigned");
            ColorSystemBox.Text = Services.CategoryPalette.Get("System");
            ColorInvalidBox.Text = Services.CategoryPalette.Get("Invalid");
            ColorUnknownBox.Text = Services.CategoryPalette.Get("Unknown");
            RefreshSwatches();
        }
        catch { }
    }

    private void RefreshSwatches()
    {
        SetSwatch(ColorSignedSwatch, ColorSignedBox.Text);
        SetSwatch(ColorUnsignedSwatch, ColorUnsignedBox.Text);
        SetSwatch(ColorSystemSwatch, ColorSystemBox.Text);
        SetSwatch(ColorInvalidSwatch, ColorInvalidBox.Text);
        SetSwatch(ColorUnknownSwatch, ColorUnknownBox.Text);
    }

    private static void SetSwatch(System.Windows.Controls.Border swatch, string hex)
    {
        try
        {
            swatch.Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        }
        catch { swatch.Background = System.Windows.Media.Brushes.Gray; }
    }

    private void ApplyColors_Click(object sender, RoutedEventArgs e)
    {
        var entries = new (string key, System.Windows.Controls.TextBox box)[]
        {
            ("Signed", ColorSignedBox), ("Unsigned", ColorUnsignedBox),
            ("System", ColorSystemBox), ("Invalid", ColorInvalidBox), ("Unknown", ColorUnknownBox)
        };
        foreach (var (key, box) in entries)
        {
            string hex = (box.Text ?? "").Trim();
            if (!Services.CategoryPalette.IsValidHex(hex))
            {
                if (ColorStatus != null) ColorStatus.Text = $"\u201c{hex}\u201d isn't a valid hex color (use #RRGGBB).";
                return;
            }
            Services.CategoryPalette.Set(key, hex);
        }
        _firewall.SaveCategoryColors();
        RefreshSwatches();
        RebuildAppsList(); // re-evaluate the dot brushes with the new colors
        if (ColorStatus != null) ColorStatus.Text = "Colors applied.";
    }

    private void ResetColors_Click(object sender, RoutedEventArgs e)
    {
        Services.CategoryPalette.Reset();
        _firewall.SaveCategoryColors();
        PopulateColorUi();
        RebuildAppsList();
        if (ColorStatus != null) ColorStatus.Text = "Reset to defaults.";
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
        UpdateStatusBanner(); // keep the shield (protected/paused) in sync
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
        // Record the UI snapshot pipeline's health so the bundle shows whether the
        // sampling loop is actually feeding the Connections/Traffic panels.
        Services.DiagnosticLog.Log(
            $"UI pipeline: sampleTicks={_sampleTicks}, sampleErrors={_sampleErrors}, " +
            $"detectErrors={_detectErrors}, lastConns={_lastConns.Count}, " +
            $"statsDestinations={_stats.TotalDestinations}, statsCountries={_stats.CountryCount}, " +
            $"geoApiOk={_firewall.GeoIp.ApiOkCount}, geoApiFail={_firewall.GeoIp.ApiFailCount}, " +
            $"dnsRunning={_dnsResolver.Running}, lastSampleError=[{_lastSampleError}]");
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

    /// <summary>Fill an app row's VirusTotal verdict from the cache, queueing an
    /// automatic background lookup the first time we see a file. Costs only
    /// dictionary lookups, so it's safe to run on every list rebuild.</summary>
    private void ApplyVtStatus(AppInfo a)
    {
        a.VtText = ""; a.VtLevel = "";
        if (string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey)) return;
        if (string.IsNullOrEmpty(a.ExecutablePath) || a.IsStoreApp) return;

        // Hash: prefer the rule's stored hash, else this session's computed one.
        string hash = a.Hash;
        if (string.IsNullOrEmpty(hash))
            _vtHashByPath.TryGetValue(a.ExecutablePath, out hash!);

        if (hash == "")   // we tried to hash this file and couldn't (locked/protected)
            return;

        var cached = hash != null ? _firewall.GetVtCached(hash) : null;
        if (cached != null)
        {
            if (cached.Found && cached.Flagged > 0)
            { a.VtText = $"{cached.Flagged}/{cached.Total} flagged"; a.VtLevel = "flagged"; }
            else if (cached.Found)
            { a.VtText = $"Clean \u00B7 0/{cached.Total}"; a.VtLevel = "clean"; }
            else
            { a.VtText = "Not on VirusTotal"; a.VtLevel = "none"; }

            // Refresh very old verdicts in the background (result keeps showing).
            if ((DateTime.UtcNow - cached.CheckedUtc).TotalDays > 30 &&
                _vtQueued.Add(a.ExecutablePath))
                _vtQueue.Enqueue(a.ExecutablePath);
            return;
        }

        // Not cached yet: queue one automatic check for this path.
        if (_vtQueued.Add(a.ExecutablePath)) _vtQueue.Enqueue(a.ExecutablePath);
        a.VtText = "Checking\u2026"; a.VtLevel = "pending";
    }

    /// <summary>Background worker for automatic VirusTotal checks. Processes the
    /// queue one file at a time with a ~16s gap (safely under the free tier's
    /// 4 requests/minute), hashing on a worker thread when needed, and caching
    /// every verdict so a file is never looked up twice.</summary>
    private async Task VtLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_firewall.VirusTotalApiKey) || _vtQueue.Count == 0)
                {
                    await Task.Delay(3000, ct);
                    continue;
                }

                string path = _vtQueue.Dequeue();

                // Resolve the hash (stored rule hash, else compute off-thread once).
                string hash = _firewall.GetHash(path);
                if (string.IsNullOrEmpty(hash) && !_vtHashByPath.TryGetValue(path, out hash!))
                {
                    hash = await Task.Run(() => HashService.Compute(path), ct) ?? "";
                    _vtHashByPath[path] = hash; // "" is remembered too: unhashable
                }
                if (string.IsNullOrEmpty(hash)) continue;

                if (_firewall.GetVtCached(hash) is { } fresh &&
                    (DateTime.UtcNow - fresh.CheckedUtc).TotalDays <= 30)
                    continue; // another path with the same file already resolved it

                var result = await VirusTotalService.LookupAsync(hash, _firewall.VirusTotalApiKey);

                if (result.Ok)
                {
                    _firewall.SaveVtResult(hash, true, result.Malicious, result.Total);
                    if (result.Malicious > 0)
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(path);
                        Services.DiagnosticLog.Log(
                            $"VirusTotal: {name} flagged by {result.Malicious}/{result.Total} engines.");
                        LogActivity(new NetActivityEvent
                        {
                            ProcessName = name,
                            Detail = $"VirusTotal: {result.Malicious}/{result.Total} engines flagged this file"
                        });
                        Notify("warn", $"VirusTotal flagged {name}",
                               $"{result.Malicious}/{result.Total} engines flagged this file.");
                        try
                        {
                            if (_tray != null)
                            {
                                _tray.BalloonTipTitle = "VirusTotal alert";
                                _tray.BalloonTipText =
                                    $"{name}: {result.Malicious}/{result.Total} engines flagged this file.";
                                _tray.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Warning;
                                _tray.ShowBalloonTip(4000);
                            }
                        }
                        catch { /* notification is best-effort */ }
                    }
                }
                else if (result.Message.Contains("Not found", StringComparison.OrdinalIgnoreCase))
                {
                    _firewall.SaveVtResult(hash, false, 0, 0);
                }
                else if (result.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                {
                    // Put it back and let the quota window pass.
                    if (_vtQueued.Contains(path)) _vtQueue.Enqueue(path);
                    await Task.Delay(65_000, ct);
                    continue;
                }
                // Other failures (network, bad key): leave uncached; it can retry
                // next session. _vtQueued keeps it from hot-looping this session.

                await Task.Delay(16_000, ct); // ~3.75 requests/min, under the free cap
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Debug.WriteLine($"vt loop error: {ex.Message}");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { return; }
            }
        }
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
        if (result.Ok)
            _firewall.SaveVtResult(hash, true, result.Malicious, result.Total);
        else if (result.Message.Contains("Not found", StringComparison.OrdinalIgnoreCase))
            _firewall.SaveVtResult(hash, false, 0, 0);
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
        if (WfpCoverageText != null)
        {
            int n = _firewall.ActiveFilterCount;
            WfpCoverageText.Text = $"Kernel coverage: {n} active WFP filter{(n == 1 ? "" : "s")} installed by GunWall " +
                                   "across the connect, accept, transport and ICMP-error layers (plus listen, if you apply that rule below).";
        }
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

    // Reflect each app's current scope-block state in the context-menu checkmarks.
    private void AppsContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (AppsList.SelectedItem is AppInfo app)
        {
            if (ScopeLocalItem != null) ScopeLocalItem.IsChecked = _firewall.IsScopeBlocked(app.ExecutablePath, "local");
            if (ScopeLanItem != null) ScopeLanItem.IsChecked = _firewall.IsScopeBlocked(app.ExecutablePath, "lan");
            if (ScopeIncomingItem != null) ScopeIncomingItem.IsChecked = _firewall.IsScopeBlocked(app.ExecutablePath, "incoming");
        }
        else
        {
            if (ScopeLocalItem != null) ScopeLocalItem.IsChecked = false;
            if (ScopeLanItem != null) ScopeLanItem.IsChecked = false;
            if (ScopeIncomingItem != null) ScopeIncomingItem.IsChecked = false;
        }
    }

    private void ScopeBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi || mi.Tag is not string scope) return;
        if (AppsList.SelectedItem is not AppInfo app) { mi.IsChecked = !mi.IsChecked; return; }
        if (!RequireEngine()) { mi.IsChecked = !mi.IsChecked; return; }
        try
        {
            // The checkable item has already toggled to the desired state.
            _firewall.SetScopeBlock(app.ExecutablePath, scope, mi.IsChecked);
            RebuildAppsList();
        }
        catch (Exception ex)
        {
            mi.IsChecked = !mi.IsChecked; // revert the visual on failure
            ShowError(ex);
        }
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

    // ===== §5 custom blocklist file =====
    private void ChooseCustomList_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select a custom blocklist file",
            Filter = "Blocklist / hosts files (*.txt;*.hosts)|*.txt;*.hosts|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _firewall.SetCustomListPath(dlg.FileName);
            _firewall.RebuildHostsBlock(); // fold the new domains into the active hosts block
            UpdateCustomListStatus();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ClearCustomList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _firewall.SetCustomListPath("");
            _firewall.RebuildHostsBlock(); // drop the custom domains from the hosts block
            UpdateCustomListStatus();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void UpdateCustomListStatus()
    {
        if (CustomListStatus == null) return;
        string path = _firewall.CustomListPath;
        if (string.IsNullOrEmpty(path))
        {
            CustomListStatus.Text = "No custom list selected.";
        }
        else
        {
            int n = _firewall.CustomDomainCount;
            CustomListStatus.Text = $"{n:N0} domain(s) loaded from {System.IO.Path.GetFileName(path)}  -  {path}";
        }
    }

    // ===== §1 entity rules (country / continent / ASN blocking) =====
    private readonly System.Collections.ObjectModel.ObservableCollection<EntityRule> _entityRules = new();
    private string _entityAppPath = "";

    private void RefreshEntityRules()
    {
        if (EntityRuleList == null) return;
        if (EntityRuleList.ItemsSource != _entityRules) EntityRuleList.ItemsSource = _entityRules;
        _entityRules.Clear();
        foreach (var r in _firewall.EntityRules) _entityRules.Add(r);
        UpdateEntityStatus();
    }

    private void UpdateEntityStatus()
    {
        if (EntityStatus == null) return;
        string geo = _firewall.GeoIpApiActive
            ? "GeoIP via API server."
            : _firewall.GeoIpLoaded
                ? $"GeoIP ready ({_firewall.GeoIpRangeCount:N0} ranges)."
                : "GeoIP not loaded - choose a source in Settings to enable matching.";
        int active = _firewall.EntityReactiveBlockCount;
        EntityStatus.Text = active > 0 ? $"{geo}  {active} active block(s)." : geo;
    }

    private void EntityChooseApp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Limit this rule to an application",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _entityAppPath = dlg.FileName;
        if (EntityAppLabel != null)
            EntityAppLabel.Text = "Applies to: " + System.IO.Path.GetFileName(_entityAppPath);
    }

    private void EntityAllApps_Click(object sender, RoutedEventArgs e)
    {
        _entityAppPath = "";
        if (EntityAppLabel != null) EntityAppLabel.Text = "Applies to: All apps";
    }

    private void EntityAddRule_Click(object sender, RoutedEventArgs e)
    {
        string type = (EntityTypeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "country";
        string value = (EntityValueBox?.Text ?? "").Trim();
        if (value.Length == 0)
        {
            MessageBox.Show("Enter a value to block (e.g. RU, EU, or AS13335).",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Normalise: country / continent codes are upper-case; ASN keeps the user's text.
        if (type is "country" or "continent") value = value.ToUpperInvariant();

        _firewall.AddEntityRule(new EntityRule
        {
            AppPath = _entityAppPath,
            Type = type,
            Value = value
        });
        if (EntityValueBox != null) EntityValueBox.Text = "";
        RefreshEntityRules();
    }

    private void EntityRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id && id.Length > 0)
        {
            _firewall.RemoveEntityRule(id);
            RefreshEntityRules();
        }
    }

    private void EntityClearBlocks_Click(object sender, RoutedEventArgs e)
    {
        _firewall.ClearEntityReactiveBlocks();
        UpdateEntityStatus();
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
        _firewall.SetFullscreenSilent(FullscreenSilentCheck?.IsChecked == true);
        _firewall.SetConfirmClearLogs(ConfirmClearCheck?.IsChecked == true);
        _firewall.SetAlwaysConfirmExit(ConfirmExitCheck?.IsChecked == true);
        _firewall.SetKeepUnusedApps(KeepUnusedCheck?.IsChecked == true);
        if (MaxLogEntriesCombo?.SelectedItem is ComboBoxItem mle &&
            int.TryParse(mle.Tag?.ToString(), out int mlev)) _firewall.SetMaxLogEntries(mlev);
        if (MaxLogFileCombo?.SelectedItem is ComboBoxItem mlf &&
            int.TryParse(mlf.Tag?.ToString(), out int mlfv)) _firewall.SetMaxLogFileMB(mlfv);
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
        if (ShieldPath == null) return;

        bool protectedNow;
        string title, sub;
        if (!_engineReady)
        {
            protectedNow = false;
            title = "Not Protected";
            sub = "Run GunWall as administrator to enable filtering";
        }
        else if (_firewall.IsSnoozed)
        {
            protectedNow = false;
            title = "Paused";
            sub = "Filtering is snoozed - resume to protect your network";
        }
        else if (_firewall.LockdownEngaged)
        {
            protectedNow = true;
            title = "Locked Down";
            sub = "All network traffic is blocked";
        }
        else if (_firewall.StrictMode)
        {
            protectedNow = true;
            title = "Protected";
            sub = "Full control - only the apps you allow can connect";
        }
        else
        {
            protectedNow = false;
            title = "Monitoring Only";
            sub = "GunWall is watching quietly - no blocking, no prompts. Click Enable Firewall to actively protect.";
        }

        var fill = (Brush)FindResource(protectedNow ? "AllowBrush" : "BlockBrush");
        var glow = (Brush)FindResource(protectedNow ? "AllowFill" : "BlockFill");
        ShieldPath.Fill = fill;
        if (ShieldGlow != null) ShieldGlow.Fill = glow;
        if (ShieldCheck != null) ShieldCheck.Visibility = protectedNow ? Visibility.Visible : Visibility.Collapsed;
        if (ShieldAlert != null) ShieldAlert.Visibility = protectedNow ? Visibility.Collapsed : Visibility.Visible;
        StatusTitle.Text = title;
        StatusSub.Text = sub;

        // Always-visible status in the sidebar (mirrors the dashboard shield).
        string sideSub = !_engineReady ? "Run as administrator"
            : _firewall.IsSnoozed ? "Filtering paused"
            : _firewall.LockdownEngaged ? "All traffic blocked"
            : _firewall.StrictMode ? "Zero-Trust active"
            : "Watching, not blocking";
        if (SideStatusIcon != null) SideStatusIcon.Foreground = fill;
        if (SideStatusTitle != null) SideStatusTitle.Text = title;
        if (SideStatusSub != null) SideStatusSub.Text = sideSub;

        // §13: notify on real state transitions (never on the initial paint).
        bool protNow = _engineReady && _firewall.StrictMode && !_firewall.IsSnoozed;
        if (_lastNotifiedProtection != null && _lastNotifiedProtection != protNow)
            Notify(protNow ? "good" : "warn",
                   protNow ? "Protection active" : "Protection off",
                   protNow ? "Zero-Trust filtering is enforcing again."
                           : "Firewall filtering is disabled or snoozed.");
        _lastNotifiedProtection = protNow;

        bool ldNow = _firewall.LockdownEngaged;
        if (_lastNotifiedLockdown != null && _lastNotifiedLockdown != ldNow)
            Notify(ldNow ? "warn" : "good",
                   ldNow ? "Lockdown engaged" : "Lockdown lifted",
                   ldNow ? "All network traffic is being blocked."
                         : "Normal filtering restored.");
        _lastNotifiedLockdown = ldNow;

        UpdateTrayIcon(); // tray dot mirrors protection state (green active / red off)
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
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private System.Drawing.Icon? _trayIconOn, _trayIconOff;

    /// <summary>Renders the app icon with a glowing status dot bottom-right:
    /// green when protection is active, red when the firewall is disabled.
    /// Fully qualified System.Drawing throughout (WPF types share names).</summary>
    private System.Drawing.Icon? MakeTrayIcon(bool active)
    {
        try
        {
            var dot = active
                ? System.Drawing.Color.FromArgb(48, 209, 88)    // iOS green
                : System.Drawing.Color.FromArgb(255, 69, 58);   // iOS red
            using var bmp = new System.Drawing.Bitmap(32, 32);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                string? exe = Environment.ProcessPath;
                var baseIcon = exe != null ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null;
                if (baseIcon != null)
                    using (baseIcon) g.DrawIcon(baseIcon, new System.Drawing.Rectangle(0, 0, 32, 32));

                int cx = 23, cy = 23, r = 6;
                for (int i = 3; i >= 1; i--)   // soft glow rings
                {
                    using var glow = new System.Drawing.SolidBrush(
                        System.Drawing.Color.FromArgb(26 * i, dot));
                    g.FillEllipse(glow, cx - r - i * 2, cy - r - i * 2, (r + i * 2) * 2, (r + i * 2) * 2);
                }
                using (var ring = new System.Drawing.SolidBrush(
                           System.Drawing.Color.FromArgb(235, 10, 10, 12)))
                    g.FillEllipse(ring, cx - r - 1, cy - r - 1, (r + 1) * 2, (r + 1) * 2);
                using (var fill = new System.Drawing.SolidBrush(dot))
                    g.FillEllipse(fill, cx - r, cy - r, r * 2, r * 2);
            }

            IntPtr h = bmp.GetHicon();
            try
            {
                using var tmp = System.Drawing.Icon.FromHandle(h);
                return (System.Drawing.Icon)tmp.Clone(); // owns its own handle
            }
            finally { DestroyIcon(h); }                  // release the GDI handle
        }
        catch { return null; }
    }

    /// <summary>Swap the tray icon + tooltip to match the current protection state.</summary>
    private void UpdateTrayIcon()
    {
        if (_tray == null) return;
        try
        {
            bool active = _engineReady && _firewall.StrictMode && !_firewall.IsSnoozed;
            var icon = active ? (_trayIconOn ??= MakeTrayIcon(true))
                              : (_trayIconOff ??= MakeTrayIcon(false));
            if (icon != null) _tray.Icon = icon;
            _tray.Text = active ? "GunWall - protection active" : "GunWall - firewall disabled";
        }
        catch { }
    }

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
            UpdateTrayIcon(); // status-colored icon (green active / red disabled)

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
        // Optional: confirm exit even when the firewall is not active.
        if (!_firewall.StrictMode && !_firewall.LockdownEngaged && _firewall.AlwaysConfirmExit &&
            MessageBox.Show("Exit GunWall?", "Exit GunWall",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

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
