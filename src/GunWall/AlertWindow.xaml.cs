using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using GunWall.Services;

namespace GunWall;

/// <summary>
/// a connection connection alert. Shown when an executable GunWall has
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

    public AlertWindow(AlertInfo info, Action onBlock, Action? onAllow = null)
    {
        InitializeComponent();
        _info = info;
        _onBlock = onBlock;
        _onAllow = onAllow;

        NameText.Text = info.ProcessName;
        AddressText.Text = string.IsNullOrEmpty(info.RemoteAddress)
            ? "\u2014 (no remote yet)"
            : $"{info.Protocol.ToLowerInvariant()}://{info.RemoteAddress}";
        PortText.Text = info.RemotePort == 0 ? "\u2014" : PortLabel(info.RemotePort);
        PathText.Text = info.ExePath;
        DateText.Text = info.Time.ToString("g");
        SignatureText.Text = "Checking signature...";
        HostText.Text = "Resolving...";

        Loaded += OnLoaded;
        PositionBottomRight();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryEnableDarkTitleBar();

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
        try { _onBlock(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not block: {ex.Message}", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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
