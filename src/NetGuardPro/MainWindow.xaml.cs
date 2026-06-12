using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NetGuardPro.Models;
using NetGuardPro.Services;

namespace NetGuardPro;

public partial class MainWindow : Window
{
    private readonly FirewallManager _firewall = new();
    private readonly NetworkMonitor _monitor = new();
    private readonly ProcessService _processes = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly ObservableCollection<AppInfo> _apps = new();
    private readonly ObservableCollection<ConnectionInfo> _connections = new();

    private const int GraphPoints = 60;
    private readonly double[] _downSeries = new double[GraphPoints];
    private readonly double[] _upSeries = new double[GraphPoints];

    private long _lastRx = -1, _lastTx = -1;
    private DateTime _lastSample = DateTime.MinValue;
    private bool _engineReady;

    public MainWindow()
    {
        InitializeComponent();
        AppsList.ItemsSource = _apps;
        ConnList.ItemsSource = _connections;
        Loaded += OnLoaded;
        Closed += (_, _) => _firewall.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();

        try
        {
            _firewall.Initialize();
            _engineReady = true;
            EngineStatus.Text = "Engine: active";
            SyncLockdownButton();
        }
        catch (Exception ex)
        {
            _engineReady = false;
            EngineStatus.Text = "Engine: unavailable";
            MessageBox.Show(
                "NetGuard Pro could not initialise the Windows Filtering Platform.\n\n" +
                "Make sure you are running as administrator.\n\nDetails: " + ex.Message,
                "NetGuard Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(this, EventArgs.Empty); // immediate first paint
    }

    // ----------------------------------------------------------- live update
    private void OnTick(object? sender, EventArgs e)
    {
        SampleBandwidth();

        var conns = _monitor.GetTcpConnections();
        var procMap = _processes.SnapshotProcesses();

        foreach (var c in conns)
            c.ProcessName = procMap.TryGetValue(c.ProcessId, out var p) ? p.Name : $"PID {c.ProcessId}";

        ConnCount.Text = conns.Count.ToString();

        // Only rebuild the visible heavy list to avoid unnecessary churn.
        if (PanelConnections.Visibility == Visibility.Visible)
        {
            _connections.Clear();
            foreach (var c in conns.OrderBy(c => c.ProcessName)) _connections.Add(c);
        }

        if (PanelFirewall.Visibility == Visibility.Visible)
            RebuildAppsList(conns, procMap);

        RedrawGraph();
    }

    private void SampleBandwidth()
    {
        var (rx, tx) = _monitor.GetCumulativeBytes();
        var now = DateTime.UtcNow;

        if (_lastRx < 0)
        {
            _lastRx = rx; _lastTx = tx; _lastSample = now;
            return;
        }

        double secs = Math.Max(0.001, (now - _lastSample).TotalSeconds);
        double downRate = Math.Max(0, (rx - _lastRx) / secs);
        double upRate = Math.Max(0, (tx - _lastTx) / secs);

        _lastRx = rx; _lastTx = tx; _lastSample = now;

        Shift(_downSeries, downRate);
        Shift(_upSeries, upRate);

        DownSpeed.Text = FormatRate(downRate);
        UpSpeed.Text = FormatRate(upRate);
    }

    private static void Shift(double[] series, double newest)
    {
        Array.Copy(series, 1, series, 0, series.Length - 1);
        series[^1] = newest;
    }

    private void RebuildAppsList(List<ConnectionInfo> conns, Dictionary<int, (string Name, string Path)> procMap)
    {
        var networked = _processes.GetNetworkedApps(conns, procMap);

        // Merge persisted blocked apps that may not currently have connections.
        var known = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in networked) known[a.ExecutablePath] = a;

        foreach (var rule in _firewall.GetRules())
        {
            if (!known.TryGetValue(rule.ExecutablePath, out var app))
            {
                app = new AppInfo
                {
                    Name = rule.DisplayName,
                    ExecutablePath = rule.ExecutablePath
                };
                known[rule.ExecutablePath] = app;
            }
            app.Status = rule.Status;
        }

        // Apply blocked status for any app present in the rules.
        foreach (var a in known.Values)
            a.Status = _firewall.IsBlocked(a.ExecutablePath) ? AppStatus.Blocked : AppStatus.Allowed;

        _apps.Clear();
        foreach (var a in known.Values
                     .OrderByDescending(a => a.ActiveConnections)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            _apps.Add(a);
    }

    // ----------------------------------------------------------- graph
    private void RedrawGraph()
    {
        var canvas = GraphCanvas;
        canvas.Children.Clear();
        double w = canvas.ActualWidth, h = canvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        // Scale to the largest value across both series, with a 64 KB/s floor so
        // an idle connection doesn't produce a jumpy, exaggerated baseline.
        double max = 64 * 1024;
        for (int i = 0; i < GraphPoints; i++)
        {
            max = Math.Max(max, _downSeries[i]);
            max = Math.Max(max, _upSeries[i]);
        }

        DrawBaseline(canvas, w, h);
        AddSeries(canvas, _downSeries, max, w, h, Color.FromRgb(0x4F, 0xC3, 0xF7));
        AddSeries(canvas, _upSeries, max, w, h, Color.FromRgb(0x81, 0xC7, 0x84));
    }

    private static void DrawBaseline(Canvas canvas, double w, double h)
    {
        var line = new Line
        {
            X1 = 0, Y1 = h - 1, X2 = w, Y2 = h - 1,
            Stroke = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)),
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

        // Soft fill under the line.
        var fill = new Polygon
        {
            Fill = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B))
        };
        foreach (var p in poly.Points) fill.Points.Add(p);
        fill.Points.Add(new Point(w, h));
        fill.Points.Add(new Point(0, h));
        canvas.Children.Insert(0, fill);
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawGraph();

    // ----------------------------------------------------------- nav
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelDashboard == null) return; // during init
        string tag = (string)((RadioButton)sender).Tag;
        PanelDashboard.Visibility = tag == "Dashboard" ? Visibility.Visible : Visibility.Collapsed;
        PanelFirewall.Visibility = tag == "Firewall" ? Visibility.Visible : Visibility.Collapsed;
        PanelConnections.Visibility = tag == "Connections" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----------------------------------------------------------- actions
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

            app.Status = _firewall.IsBlocked(app.ExecutablePath) ? AppStatus.Blocked : AppStatus.Allowed;
            // Refresh the row binding by rebuilding the list snapshot.
            CollectionRefresh();
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
            CollectionRefresh();
            MessageBox.Show($"Blocked: {name}", "NetGuard Pro",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    private void RefreshApps_Click(object sender, RoutedEventArgs e) => OnTick(this, EventArgs.Empty);

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

    private void CollectionRefresh()
    {
        var conns = _monitor.GetTcpConnections();
        var procMap = _processes.SnapshotProcesses();
        RebuildAppsList(conns, procMap);
    }

    // ----------------------------------------------------------- helpers
    private bool RequireEngine()
    {
        if (_engineReady) return true;
        MessageBox.Show("The firewall engine is not available. Run NetGuard Pro as administrator.",
            "NetGuard Pro", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "NetGuard Pro", MessageBoxButton.OK, MessageBoxImage.Error);

    private static string FormatRate(double bytesPerSec)
    {
        double bits = bytesPerSec;
        if (bits < 1024) return $"{bits:0} B/s";
        if (bits < 1024 * 1024) return $"{bits / 1024:0.0} KB/s";
        return $"{bits / (1024 * 1024):0.00} MB/s";
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
            // Older Windows builds: ignore, the app still works.
            Debug.WriteLine("Dark title bar not supported on this build.");
        }
    }
}
