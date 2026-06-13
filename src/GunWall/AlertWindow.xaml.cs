using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GunWall.Services;

namespace GunWall;

/// <summary>
/// A connection alert. Shown when an executable GunWall has
/// never seen before makes its first network connection.
///
/// Honest note on semantics: GunWall is allow-by-default, so this alert is
/// "a new app just connected — keep allowing it?" rather than the
/// "a connection was blocked — allow it?". The Block button creates real,
/// persistent WFP filters immediately.
/// </summary>
public partial class AlertWindow : Window
{
    /// <summary>Everything the popup displays.</summary>
    public sealed record AlertInfo(
        string ProcessName,
        string ExePath,
        string RemoteAddress,
        int RemotePort,
        string Protocol,
        DateTime Time);

    private readonly AlertInfo _info;
    private readonly Action _onBlock;
    private readonly Action? _onAllow;
    private readonly bool _strictMode;
    private System.Windows.Threading.DispatcherTimer? _countdown;
    private int _secondsLeft = 20;

    public AlertWindow(AlertInfo info, Action onBlock, Action? onAllow = null, bool strictMode = false)
    {
        InitializeComponent();
        _info = info;
        _onBlock = onBlock;
        _onAllow = onAllow;
        _strictMode = strictMode;

        NameText.Text = info.ProcessName;
        AddressText.Text = string.IsNullOrEmpty(info.RemoteAddress)
            ? "\u2014 (no remote yet)"
            : $"{info.Protocol.ToLowerInvariant()}://{info.RemoteAddress}";
        PortText.Text = info.RemotePort == 0 ? "\u2014" : PortLabel(info.RemotePort);
        PathText.Text = info.ExePath;
        DateText.Text = info.Time.ToString("g");
        SignatureText.Text = "Checking signature...";
        HostText.Text = "Resolving...";

        // In Zero Trust (strict) mode the app is currently BLOCKED and stays
        // blocked unless approved; reflect that in the header.
        if (_strictMode)
            HeaderText.Text = "App is blocked - approve to allow network access";

        Loaded += OnLoaded;
        PositionBottomRight();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();
        StartCountdown();

        // Enrich asynchronously so the popup appears instantly.
        var path = _info.ExePath;
        var ip = _info.RemoteAddress;

        string publisher = await Task.Run(() => NetInfoService.GetPublisher(path));
        SignatureText.Text = publisher;

        if (string.IsNullOrEmpty(ip)) { HostText.Text = "\u2014"; return; }
        string host = await NetInfoService.ResolveHostAsync(ip);
        HostText.Text = string.IsNullOrEmpty(host) ? "\u2014" : host;
    }

    private static string PortLabel(int port) => port switch
    {
        443 => "443 (https)",
        80 => "80 (http)",
        53 => "53 (dns)",
        21 => "21 (ftp)",
        22 => "22 (ssh)",
        25 => "25 (smtp)",
        _ => port.ToString()
    };

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        StopCountdown();
        // In alert mode this is a no-op (app already allowed by default);
        // in strict mode the callback creates persistent PERMIT filters.
        try { _onAllow?.Invoke(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not allow: {ex.Message}", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        StopCountdown();
        try { _onBlock(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not block: {ex.Message}", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { StopCountdown(); Close(); }

    private void StartCountdown()
    {
        // The grace timer's default action depends on mode:
        //  - Strict (Zero Trust): timeout = stay BLOCKED (just dismiss). The
        //    app is denied by default, so doing nothing keeps it denied.
        //  - Monitoring: timeout = keep ALLOWED (dismiss; allowed by default).
        // Either way the popup never traps the user indefinitely, and the safe
        // default for the active mode is what happens on timeout.
        _countdown = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            string label = _strictMode ? $"Dismiss ({_secondsLeft}s)" : $"Allow ({_secondsLeft}s)";
            if (_strictMode && CountdownHint != null)
                CountdownHint.Text = $"Stays blocked in {_secondsLeft}s if no choice";
            else if (AllowButton != null)
                AllowButton.Content = BuildAllowContent(label);
            if (_secondsLeft <= 0)
            {
                _countdown?.Stop();
                if (_strictMode) { Close(); }       // stay blocked
                else { Allow_Click(this, new RoutedEventArgs()); }
            }
        };
        _countdown.Start();
    }

    private static object BuildAllowContent(string text)
    {
        var sp = new System.Windows.Controls.StackPanel
        { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 10, Height = 10,
            Fill = new SolidColorBrush(Color.FromRgb(0x3D, 0xD6, 0x8C)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        sp.Children.Add(new System.Windows.Controls.TextBlock
        { Text = text, FontWeight = FontWeights.SemiBold });
        return sp;
    }

    private void StopCountdown() => _countdown?.Stop();

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void PositionBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - 380; // approximate height; SizeToContent finalizes
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

    private void TryEnableDarkTitleBar()
    {
        try
        {
            var helper = new WindowInteropHelper(this);
            helper.EnsureHandle();
            int useDark = 1;
            DwmSetWindowAttribute(helper.Handle, 20, ref useDark, sizeof(int));
        }
        catch { /* older builds */ }
    }
}
