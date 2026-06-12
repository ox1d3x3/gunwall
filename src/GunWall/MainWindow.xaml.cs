using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.Win32;
using GunWall.Models;
using GunWall.Services;

namespace GunWall;

public partial class MainWindow : Window
{
    private readonly FirewallManager _firewall = new();
    private readonly NetworkMonitor _monitor = new();
    private readonly ProcessService _processes = new();

    private readonly ObservableCollection<AppInfo> _apps = new();
    private readonly ObservableCollection<ConnectionInfo> _connections = new();
    private readonly ObservableCollection<ActivityEvent> _activity = new();

    private const int GraphPoints = 60;
    private readonly double[] _downSeries = new double[GraphPoints];
    private readonly double[] _upSeries = new double[GraphPoints];

    private long _lastRx = -1, _lastTx = -1;
    private DateTime _lastSample = DateTime.MinValue;
    private bool _engineReady;

    // Background sampling
    private CancellationTokenSource? _cts;
    private volatile int _intervalMs = 1000;
    private string _appFilter = "";
    private string _connFilter = "";
    private bool _showAllApps;

    // Activity feed bookkeeping
    private readonly HashSet<string> _seenConnections = new();
    private const int MaxActivity = 300;

    // Connection alerts (simplewall-style popup)
    private readonly Queue<AlertWindow.AlertInfo> _alertQueue = new();
    private bool _alertOpen;
    private bool _knownSeeded;

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
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();
        SetupTray();

        try
        {
            _firewall.Initialize();
            _engineReady = true;
            EngineStatus.Text = "Engine: active";
            SyncLockdownButton();
            AlertsCheck.IsChecked = _firewall.AlertsEnabled;
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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _cts?.Cancel();
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
        DetectNewApps(snap);

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

            _activity.Insert(0, new ActivityEvent
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

    // ================================================================ alerts
    /// <summary>
    /// Shows a simplewall-style popup the first time an executable is ever
    /// observed with a network connection. On the very first run, all apps
    /// currently online are seeded as "known" so the user isn't flooded.
    /// </summary>
    private void DetectNewApps(Snapshot snap)
    {
        if (!_engineReady) return;

        if (!_knownSeeded)
        {
            _knownSeeded = true;
            _firewall.SeedKnownApps(
                snap.Conns.Where(c => !string.IsNullOrEmpty(c.RemoteAddress))
                          .Select(c => snap.Procs.TryGetValue(c.ProcessId, out var p) ? p.Path : "")
                          .Where(path => !string.IsNullOrEmpty(path)));
            return;
        }

        if (!_firewall.AlertsEnabled) return;

        foreach (var c in snap.Conns)
        {
            if (string.IsNullOrEmpty(c.RemoteAddress)) continue;
            if (c.RemoteAddress is "0.0.0.0" or "::" or "127.0.0.1" or "::1") continue;
            if (!snap.Procs.TryGetValue(c.ProcessId, out var proc)) continue;
            if (string.IsNullOrEmpty(proc.Path)) continue;
            if (string.Equals(proc.Path, Environment.ProcessPath,
                              StringComparison.OrdinalIgnoreCase)) continue;
            if (_firewall.IsBlocked(proc.Path)) continue;

            // MarkKnown returns true only the very first time we see this exe.
            if (!_firewall.MarkKnown(proc.Path)) continue;

            _alertQueue.Enqueue(new AlertWindow.AlertInfo(
                proc.Name, proc.Path, c.RemoteAddress, c.RemotePort, c.Protocol, DateTime.Now));
        }

        ShowNextAlert();
    }

    private void ShowNextAlert()
    {
        if (_alertOpen || _alertQueue.Count == 0) return;
        var info = _alertQueue.Dequeue();
        _alertOpen = true;

        var win = new AlertWindow(info, onBlock: () =>
        {
            _firewall.BlockApp(info.ExePath, info.ProcessName);
            RebuildAppsList();
        });
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
            a.Status = _firewall.IsBlocked(a.ExecutablePath) ? AppStatus.Blocked : AppStatus.Allowed;

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
        // GlassWire palette: cool blue for download, signature orange for upload.
        AddSeries(canvas, _downSeries, max, w, h, Color.FromRgb(0x46, 0xB5, 0xE6));
        AddSeries(canvas, _upSeries, max, w, h, Color.FromRgb(0xFF, 0x9D, 0x2E));
    }

    private static void DrawBaseline(Canvas canvas, double w, double h)
    {
        var line = new Line
        {
            X1 = 0, Y1 = h - 1, X2 = w, Y2 = h - 1,
            Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x37, 0x3F)),
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

        // GlassWire-style soft vertical gradient under the line.
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
        PanelActivity.Visibility = tag == "Activity" ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;

        // Populate immediately on switch instead of waiting for the next tick.
        if (tag == "Firewall") RebuildAppsList();
        if (tag == "Connections") RebuildConnList();
    }

    // ================================================================ actions
    private void ToggleApp_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireEngine()) return;
        if (((Button)sender).DataContext is not AppInfo app) return;

        try
        {
            if (_firewall.IsBlocked(app.ExecutablePath))
                _firewall.UnblockApp(app.ExecutablePath);
            else
                _firewall.BlockApp(app.ExecutablePath, app.Name);

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

    private void AlertsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_engineReady) _firewall.SetAlertsEnabled(AlertsCheck.IsChecked == true);
    }

    private void IntervalCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalCombo?.SelectedItem is ComboBoxItem item &&
            int.TryParse((string)item.Tag, out int ms))
        {
            _intervalMs = ms;
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
            menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Close));
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

    // ================================================================ helpers
    private bool RequireEngine()
    {
        if (_engineReady) return true;
        MessageBox.Show("The firewall engine is not available. Run GunWall as administrator.",
            "GunWall", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "GunWall", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} B/s";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} KB/s";
        return $"{bytesPerSec / (1024 * 1024):0.00} MB/s";
    }

    // Dark native title bar (Windows 10 2004+ / Windows 11).
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            int useDark = 1;
            const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
            DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
        }
        catch
        {
            Debug.WriteLine("Dark title bar not supported on this build.");
        }
    }
}
