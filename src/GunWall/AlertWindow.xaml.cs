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

    private readonly GunWall.Services.GeoIpService? _firewallGeo;

    public AlertWindow(AlertInfo info, Action onBlock, Action? onAllow = null, bool strictMode = false,
                       int timeoutSeconds = 0, bool defaultAllow = false,
                       GunWall.Services.GeoIpService? geo = null)
    {
        _firewallGeo = geo;
        InitializeComponent();
        _info = info;
        _onBlock = onBlock;
        _onAllow = onAllow;
        _strictMode = strictMode;
        _timeoutSeconds = timeoutSeconds;
        _defaultAllow = defaultAllow;
        _secondsLeft = timeoutSeconds;

        NameText.Text = info.ProcessName;

        // The app's own icon. Recognised before the name is read, which on a
        // prompt answered in two seconds is most of the decision.
        try { AppIcon.Source = Services.IconService.GetIcon(info.ExePath); }
        catch { /* no icon is a cosmetic loss, never a reason to fail the prompt */ }
        AddressText.Text = string.IsNullOrEmpty(info.RemoteAddress)
            ? "\u2014 (no remote yet)"
            : $"{info.Protocol.ToLowerInvariant()}://{info.RemoteAddress}";
        PortText.Text = info.RemotePort == 0 ? "\u2014" : PortLabel(info.RemotePort);
        PathText.Text = info.ExePath;
        DateText.Text = info.Time.ToString("g");
        SignatureText.Text = "Checking...";
        SignatureText.ToolTip = "Checking the digital signature...";
        HostText.Text = "Resolving...";

        // Country flag beside the destination. Deliberately best-effort: the
        // lookup is local and instant when the database is loaded, empty for a
        // LAN or IPv6 address, and a missing flag simply leaves the row reading
        // as it did before. A prompt must never wait on GeoIP to appear.
        try
        {
            string code = _firewallGeo?.Lookup(info.RemoteAddress).Country ?? "";
            if (code.Length > 0)
            {
                FlagIcon.Source = (System.Windows.Media.ImageSource?)
                    new GunWall.Converters.CountryFlagConverter()
                        .Convert(code, typeof(System.Windows.Media.ImageSource),
                                 null!, System.Globalization.CultureInfo.InvariantCulture);
                // The flag is recognised; the name is what someone reaches for when
                // it is not. Hovering answers that without spending a row on it.
                FlagIcon.ToolTip = GunWall.Services.GeoData.CountryName(code);
            }
        }
        catch { }
        UpdateSummary();

        // In Zero Trust (strict) mode the app is currently BLOCKED and stays
        // blocked unless approved; reflect that in the header.
        if (_strictMode)
        {
            // HeaderText is the SUBTITLE under the question, not a kicker - the
            // state strip that held the kicker was retired in 0.99.71 and this
            // comment described it for one release after it stopped existing.
            // SummaryText above is the question and keeps naming the app.
            HeaderText.Text = "Blocked - awaiting approval";
            SetSubjectRole("Block");
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
        // Checked BEFORE the WFP call, not after it throws. A prompt can outlive
        // the process it is about - GitHub Desktop's updater copies itself into a
        // temp folder, connects, and deletes itself - and asking the kernel to
        // build a rule for a file that has gone produces an error code where a
        // sentence belongs.
        if (!GunWall.Services.FirewallManager.IsApplicablePath(_info.ExePath))
        {
            GunWall.Services.DiagnosticLog.Log(
                $"Allow declined: {_info.ProcessName} no longer exists at {_info.ExePath}");
            MessageBox.Show(
                $"{_info.ProcessName} has already closed and its file is gone, so there is "
                + "nothing left to write a rule for.\n\nThis is normal for updaters, which "
                + "copy themselves to a temporary folder and delete it when they finish. "
                + "Nothing was blocked that would not have been blocked anyway - the program "
                + "had already stopped.",
                "GunWall", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
            return;
        }

        try { _onAllow?.Invoke(); }
        catch (Exception ex)
        {
            // LOGGED, not only shown. This dialog appeared on a machine whose
            // diagnostics bundle then read "Errors this session: 0 distinct, 0
            // total" - a failure the user watched happen and the log denied. An
            // error worth interrupting someone for is worth recording.
            Services.DiagnosticLog.LogException("AlertWindow/Allow", ex);
            MessageBox.Show(ExplainRuleFailure("allow", ex), "GunWall",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    /// <summary>Turns a WFP failure into something the reader can act on.
    ///
    /// ERROR_FILE_NOT_FOUND out of FwpmGetAppIdFromFileName0 means precisely one
    /// thing: the rule points at an executable that is no longer on disk. That
    /// happens every time an application updates into a versioned folder -
    /// Kaspersky 21.25 becoming 21.26, GitHub Desktop app-3.5.12 becoming
    /// app-3.6.3 - and the raw message named an API nobody outside this project
    /// has heard of.</summary>
    private static string ExplainRuleFailure(string verb, Exception ex)
    {
        if (ex.Message.Contains("FwpmGetAppIdFromFileName0", StringComparison.Ordinal)
            && ex.Message.Contains("0x00000002", StringComparison.Ordinal))
            return $"Could not {verb} this program: the file it points at no longer "
                 + "exists.\n\nThis usually means the application updated into a new "
                 + "versioned folder, leaving the old rule behind. Open Applications, "
                 + "find the entry with the old path, and remove it - then allow the "
                 + "current one.";
        return $"Could not {verb}: {ex.Message}";
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
            SetHint(FailClosedHint);
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

    // ---------------------------------------------------------------- hint text
    //
    // The hint sits in the middle column of the actions row, between the chevron
    // and the buttons, and that column's width is not a choice - it is whatever
    // the other two leave behind:
    //
    //     368  row width (430 window - 14 margin - 1 border - 16 padding, twice)
    //   -  30  chevron (PromptChevron Width)
    //   - 192  Block 92 + gap 8 + Allow 92 (PromptSecondary MinWidth)
    //   -  20  the hint's own 10px margins
    //   = 126px, and JetBrainsMono is 0.600em per glyph flat, so at 11.5px
    //     every character is 6.9px and the budget is 18 CHARACTERS.
    //
    // "Blocks automatically in 18s" was 27 and ran under the Block button. The
    // word "automatically" carried none of the meaning - the sentence says the
    // same thing without it.
    //
    // This limit is checked by hint-width in tools/checks/check_theme.py rather
    // than trusted to this comment, because trap 2.11 is exactly a documented
    // limit that nothing enforced: the tracking helper's "proportional fonts
    // only" sat at the top of its file for three releases and then the default
    // font became monospace.
    private const int HintBudgetChars = 18;

    /// <summary>Shown when no timer is running: closing the prompt without
    /// answering blocks the app. Fail-closed is the guarantee, so it is stated
    /// rather than left to be discovered.</summary>
    private const string FailClosedHint = "Closing blocks";

    private string CountdownText() =>
        $"{(_defaultAllow ? "Allow" : "Block")}s in {_secondsLeft}s";

    /// <summary>The only place the hint is written, so the budget is enforced
    /// rather than merely declared. The check in tools/checks catches this
    /// before a build; the assert catches anything the check's regexes cannot
    /// see, such as a string built at runtime. Release builds drop it.</summary>
    private void SetHint(string text)
    {
        System.Diagnostics.Debug.Assert(
            text.Length <= HintBudgetChars,
            $"Prompt hint \"{text}\" is {text.Length} chars against a "
            + $"{HintBudgetChars}-char column. It will ellipsise.");
        if (CountdownHint != null) CountdownHint.Text = text;
    }

    private void StartCountdown()
    {
        // "Never" (0): no auto-decision — the popup stays until the user chooses.
        if (_timeoutSeconds <= 0)
        {
            SetHint(FailClosedHint);
            return;
        }

        // Stated before the timer starts. Previously the first Tick was the
        // first thing to write the hint, so it was blank for a second and then
        // opened one short - a 20s timeout appeared as "19s". The countdown is
        // the one number here with a deadline attached; it should be right from
        // the first frame.
        SetHint(CountdownText());

        // Otherwise count down and, on expiry, apply the user's chosen default
        // action (Allow or Block).
        _countdown = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _countdown.Tick += (_, _) =>
        {
            _secondsLeft--;
            SetHint(CountdownText());
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

    /// <summary>Tints the subject tile by role, so a blocked-app prompt arrives
    /// visibly different from a first-connection one before a word is read.</summary>
    private void SetSubjectRole(string role)
    {
        try
        {
            SubjectTile.Background = (Brush)System.Windows.Application.Current.FindResource(role + "Fill");
            SubjectIcon.Stroke = (Brush)System.Windows.Application.Current.FindResource(role + "Text");
        }
        catch { }
    }

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
