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
    private readonly int _timeoutSeconds;   // 0 = never auto-decide
    private readonly bool _defaultAllow;     // what happens on timeout
    private bool _decided;                   // guards against deciding twice
    private System.Windows.Threading.DispatcherTimer? _countdown;
    private int _secondsLeft;

    public AlertWindow(AlertInfo info, Action onBlock, Action? onAllow = null, bool strictMode = false,
                       int timeoutSeconds = 0, bool defaultAllow = false)
    {
        InitializeComponent();
        _info = info;
        _onBlock = onBlock;
        _onAllow = onAllow;
        _strictMode = strictMode;
        _timeoutSeconds = timeoutSeconds;
        _defaultAllow = defaultAllow;
        _secondsLeft = timeoutSeconds;

        NameText.Text = info.ProcessName;
        AddressText.Text = string.IsNullOrEmpty(info.RemoteAddress)
            ? "\u2014 (no remote yet)"
            : $"{info.Protocol.ToLowerInvariant()}://{info.RemoteAddress}";
        PortText.Text = info.RemotePort == 0 ? "\u2014" : PortLabel(info.RemotePort);
        PathText.Text = info.ExePath;
        DateText.Text = info.Time.ToString("g");
        SignatureText.Text = "Info";
        SignatureText.ToolTip = "Checking the digital signature...";
        HostText.Text = "Resolving...";
        UpdateSummary();

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

        var sig = await Task.Run(() => SignatureService.Verify(path));
        // One word in the chip, the whole verdict on hover: the prompt has room
        // for a label, not a sentence, and the sentence is what matters only
        // once someone is actually weighing it up.
        SignatureText.Text = sig.Status switch
        {
            SignatureStatus.Valid    => "Signed",
            SignatureStatus.Unsigned => "Unsigned",
            SignatureStatus.Invalid  => "Invalid",
            _                        => "Info"
        };
        SignatureText.ToolTip = sig.Status switch
        {
            SignatureStatus.Valid    => $"Verified publisher: {sig.Signer}",
            SignatureStatus.Unsigned => "No digital signature. Anyone could have produced this file.",
            SignatureStatus.Invalid  => $"INVALID signature - {sig.Detail}",
            _                        => "The digital signature could not be checked."
        };
        SignatureText.Foreground = new SolidColorBrush(sig.Status switch
        {
            SignatureStatus.Valid    => Color.FromRgb(0x3F, 0xB8, 0x68), // green
            SignatureStatus.Unsigned => Color.FromRgb(0xE0, 0xA5, 0x3F), // amber
            SignatureStatus.Invalid  => Color.FromRgb(0xE2, 0x5C, 0x5C), // red
            _                        => Color.FromRgb(0x7A, 0x82, 0x8C)  // gray
        });

        if (string.IsNullOrEmpty(ip)) { HostText.Text = "\u2014"; return; }
        string host = await NetInfoService.ResolveHostAsync(ip);
        HostText.Text = string.IsNullOrEmpty(host) ? "\u2014" : host;
        UpdateSummary();   // the name is better than the address, so re-state it
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
        if (_decided) return;
        _decided = true;
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
        if (_decided) return;
        _decided = true;
        StopCountdown();
        try { _onBlock(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not block: {ex.Message}", "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    // Dismissing the prompt is not "no answer" - the connection is pending a
    // decision, so the safe answer is deny. Closing by accident must never
    // grant network access.
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Folds the endpoint detail out and back. The prompt opens compact
    /// deliberately: it interrupts whatever the person was doing, so it should
    /// ask one short question. Everything needed to answer it properly is one
    /// click away rather than absent - and the countdown, if one is running, is
    /// stopped on the way, since someone reading the detail is deciding rather
    /// than ignoring it.
    /// </summary>
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (DetailsPanel == null) return;
        bool showing = DetailsPanel.Visibility == Visibility.Visible;

        DetailsPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;
        if (DetailsLabel != null) DetailsLabel.Text = showing ? "Details" : "Hide details";
        if (DetailsChevron != null) DetailsChevron.Text = showing ? "\uE70D" : "\uE70E";
        if (!showing)
        {
            StopCountdown();
            if (CountdownHint != null)
                CountdownHint.Text = "Waiting for your choice \u2014 closing this window blocks the app";
        }
        SizeToContent = SizeToContent.Height;
    }

    /// <summary>
    /// Keeps the endpoint line reading as a destination rather than an address.
    /// A hostname is something a person can recognise or distrust; a bare
    /// address is not, so the name replaces it as soon as one resolves, with
    /// the port appended because "which service" is part of the question.
    /// </summary>
    private void UpdateSummary()
    {
        if (HostText == null) return;
        string host = HostText.Text ?? "";
        if (host.Length == 0 || host == "\u2014" || host.StartsWith("Resolving"))
        {
            string addr = AddressText?.Text ?? "";
            if (addr.Length > 0 && addr != "\u2014") host = addr;
        }
        if (host.Length == 0 || host == "\u2014") return;

        string port = PortText?.Text ?? "";
        int colon = port.IndexOf(' ');
        if (colon > 0) port = port[..colon];          // "443 (HTTPS)" -> "443"
        HostText.Text = port.Length > 0 && port != "\u2014" && !host.Contains(':')
            ? $"{host}:{port}" : host;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopCountdown();
        if (!_decided)
        {
            _decided = true;
            try { _onBlock(); }
            catch (Exception ex)
            {
                GunWall.Services.DiagnosticLog.LogException("AlertWindow.CloseBlock", ex);
            }
        }
        base.OnClosed(e);
    }

    private void StartCountdown()
    {
        // "Never" (0): no auto-decision — the popup stays until the user chooses.
        if (_timeoutSeconds <= 0)
        {
            if (CountdownHint != null)
                CountdownHint.Text = "Waiting for your choice \u2014 closing this window blocks the app";
            return;
        }

        // Otherwise count down and, on expiry, apply the user's chosen default
        // action (Allow or Block).
        _countdown = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            string verb = _defaultAllow ? "Allow" : "Block";
            if (CountdownHint != null)
                CountdownHint.Text = $"{verb}s automatically in {_secondsLeft}s";
            if (_secondsLeft <= 0)
            {
                _countdown?.Stop();
                if (_defaultAllow) Allow_Click(this, new RoutedEventArgs());
                else Block_Click(this, new RoutedEventArgs());
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
