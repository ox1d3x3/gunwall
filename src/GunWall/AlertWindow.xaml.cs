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
        SignatureText.Text = "Checking...";
        SignatureText.ToolTip = "Checking the digital signature...";
        HostText.Text = "Resolving...";
        UpdateSummary();

        // In Zero Trust (strict) mode the app is currently BLOCKED and stays
        // blocked unless approved; reflect that in the header.
        if (_strictMode)
            {
            // Short enough to fit the title line without truncating; the
            // subtitle carries the rest.
            // HeaderText is the state strip now, so it is the uppercase kicker;
            // SummaryText is the 22px question and keeps naming the app.
            HeaderText.Text = "BLOCKED - AWAITING APPROVAL";
        }

        Loaded += OnLoaded;
        PositionBottomRight();   // provisional, before layout knows the height

        // Once the window has actually been laid out its height is real, so
        // place it properly; and re-place it on every size change, because
        // opening Details makes it taller and it should grow up from the corner
        // rather than walk down past the bottom of the screen.
        ContentRendered += (_, _) => PositionBottomRight();
        SizeChanged += (_, _) => PositionBottomRight();
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
        // Four more raw literals, same family as the footer dot in 0.99.42:
        // built with new SolidColorBrush, so they never followed a theme, and
        // none of them was a palette value - the "green" was #3FB868, which is
        // not --ok in either theme. Role tokens now, resolved at use.
        SignatureText.Foreground = (Brush)System.Windows.Application.Current.FindResource(
            sig.Status switch
            {
                SignatureStatus.Valid    => "AllowText",
                SignatureStatus.Unsigned => "WarnText",
                SignatureStatus.Invalid  => "BlockText",
                _                        => "TextTertiary"
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

    /// <summary>Expands the detail panel. The chevron flips to point up, and a
    /// running countdown is stopped on the way: someone reading the detail is
    /// deciding rather than ignoring it, and the fail-closed timer should not
    /// answer the question out from under them.</summary>
    private void Details_Click(object sender, RoutedEventArgs e)
    {
        if (DetailsPanel == null) return;
        bool showing = DetailsPanel.Visibility == Visibility.Visible;

        DetailsPanel.Visibility = showing ? Visibility.Collapsed : Visibility.Visible;

        // A Path carries geometry, not a glyph. The previous version set Symbol
        // on a SymbolIcon, which was correct for that control; this one has to
        // swap Data, and setting Text on either would compile and quietly do
        // nothing.
        if (DetailsChevron != null)
            DetailsChevron.Data = (System.Windows.Media.Geometry)
                System.Windows.Application.Current.FindResource(
                    showing ? "IconChevronDown" : "IconChevronUp");

        if (!showing)
        {
            StopCountdown();
            if (CountdownHint != null)
                CountdownHint.Text = "Closing blocks the app";
        }
        SizeToContent = SizeToContent.Height;
    }

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
                CountdownHint.Text = "Closing blocks the app";
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
            Fill = (Brush)System.Windows.Application.Current.FindResource("AllowText"),
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

    // The card is inset from the window by its shadow margin, so the window has
    // to overhang the corner by that much for the card to sit the right distance
    // from the screen edge.
    private const double ShadowMargin = 12;
    private const double EdgeGap = 12;

    /// <summary>
    /// Parks the prompt in the corner of the work area, above the taskbar.
    ///
    /// This used to subtract a hardcoded 380 pixels for the height, which was a
    /// guess made when the window was that tall; the prompt is now around half
    /// that, so it floated well above where it belonged. Height is measured
    /// rather than assumed, which means positioning has to happen once the
    /// window has actually been laid out - and again whenever it changes size,
    /// since opening Details makes it taller and it should grow upward from the
    /// corner rather than walking down off the screen.
    /// </summary>
    private void PositionBottomRight()
    {
        var area = WorkAreaForCursor();

        double h = ActualHeight > 0 ? ActualHeight : (Height > 0 ? Height : 220);
        double w = ActualWidth > 0 ? ActualWidth : Width;

        Left = area.Right - w + ShadowMargin - EdgeGap;
        Top = area.Bottom - h + ShadowMargin - EdgeGap;

        // Never let it leave the work area, however small the screen.
        if (Left < area.Left) Left = area.Left;
        if (Top < area.Top) Top = area.Top;
    }

    /// <summary>
    /// The work area of the display the pointer is on, so on a multi-monitor
    /// desk the prompt appears where the person is looking rather than always
    /// on the primary screen. Falls back to the primary work area.
    /// </summary>
    private static Rect WorkAreaForCursor()
    {
        try
        {
            var p = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(p);
            var wa = screen.WorkingArea;

            // Screen coordinates are physical pixels; WPF positions in device
            // independent units, so scale by the current DPI.
            var src = PresentationSource.FromVisual(Application.Current?.MainWindow ?? (Visual)new Window());
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            if (sx <= 0) sx = 1.0;
            if (sy <= 0) sy = 1.0;

            return new Rect(wa.Left / sx, wa.Top / sy, wa.Width / sx, wa.Height / sy);
        }
        catch { return SystemParameters.WorkArea; }
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
