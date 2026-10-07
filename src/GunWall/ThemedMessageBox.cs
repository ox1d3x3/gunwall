using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GunWall;

/// <summary>
/// GunWall's message box: the same arguments and results as
/// <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/>,
/// drawn in the app's theme (0.99.180).
///
/// Every confirmation and error in the app used the Win32 message box - grey
/// title bar, system icons, "Yes / No / Cancel" with a legend in the text to say
/// what each meant. Reported from use (the exit prompt) as not matching the app.
/// This keeps the call shape so each call site changes one word, and adds optional
/// button labels so a prompt can say what its buttons do instead of explaining
/// "Yes = ..." in the message.
///
/// Layout follows the Windows 11 dialog: a rounded card with a shadow, icon and
/// title over the message, and a footer band holding the buttons with the default
/// one in the brand colour. Enter presses the default; Esc presses Cancel,
/// or No, or OK - whichever the box has, in that order, as the system box does.
/// Drag anywhere on the card to move it.
///
/// It never loses a prompt: with no application yet, or if the window cannot be
/// shown (e.g. during shutdown), it falls back to the system box with the same
/// arguments. Called from another thread, it runs on the UI thread.
/// The crash handler in App.xaml.cs deliberately keeps the system box: when the
/// UI itself has failed, a WPF window is the wrong thing to depend on.
/// </summary>
public sealed class ThemedMessageBox : Window
{
    private MessageBoxResult _result;
    private readonly MessageBoxResult _escapeResult;

    /// <summary>Shows a themed message box and returns which button was pressed.</summary>
    /// <param name="labels">Optional button captions in button order (OK, Cancel /
    /// Yes, No, Cancel); a missing or empty entry keeps the standard word.</param>
    public static MessageBoxResult Show(string text, string caption = "GunWall",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        params string[] labels)
        => ShowCore(text, caption, button, icon, null, labels);

    /// <summary>As the system box's overload of the same shape: <paramref name="defaultResult"/>
    /// is the button Enter presses, and the one drawn in the brand colour - so a
    /// cautious prompt can make "No" the easy answer.</summary>
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult defaultResult)
        => ShowCore(text, caption, button, icon, defaultResult, Array.Empty<string>());

    private static MessageBoxResult ShowCore(string text, string caption, MessageBoxButton button,
        MessageBoxImage icon, MessageBoxResult? defaultResult, string[] labels)
    {
        var app = Application.Current;
        MessageBoxResult SystemBox() => defaultResult is { } dr
            ? MessageBox.Show(text, caption, button, icon, dr)
            : MessageBox.Show(text, caption, button, icon);
        if (app == null) return SystemBox();
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => ShowCore(text, caption, button, icon, defaultResult, labels));
        try
        {
            var box = new ThemedMessageBox(text, caption, button, icon, defaultResult, labels);
            box.ShowDialog();
            return box._result;
        }
        catch (Exception ex)
        {
            Services.DiagnosticLog.Log($"Themed message box unavailable ({ex.GetType().Name}); used the system one.");
            return SystemBox();
        }
    }

    private ThemedMessageBox(string text, string caption, MessageBoxButton button,
                             MessageBoxImage icon, MessageBoxResult? defaultResult, string[] labels)
    {
        Title = caption;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        SetResourceReference(FontFamilyProperty, "UiFont");
        System.Windows.Automation.AutomationProperties.SetName(this, caption);

        // Owned by the window the user is looking at, so it centres on it and
        // stays above it. From the tray the main window is hidden: then it
        // centres on the screen and stays on top, or it could open behind
        // whatever the user is doing and never be seen.
        // A minimized owner hides the windows it owns, which for a modal box
        // would look like a freeze - so only a visible, restored window qualifies.
        static bool Shown(Window w) => w.IsVisible && w.WindowState != WindowState.Minimized;
        var owner = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.IsActive && w != this && Shown(w))
                    ?? (Application.Current.MainWindow is { } mw && mw != this && Shown(mw) ? mw : null);
        if (owner != null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            ShowInTaskbar = true;      // reachable from the taskbar if it loses focus
        }

        // ---- Buttons: (result, standard word), default first --------------------
        var spec = button switch
        {
            MessageBoxButton.OKCancel => new[] { (MessageBoxResult.OK, "OK"), (MessageBoxResult.Cancel, "Cancel") },
            MessageBoxButton.YesNo => new[] { (MessageBoxResult.Yes, "Yes"), (MessageBoxResult.No, "No") },
            MessageBoxButton.YesNoCancel => new[] { (MessageBoxResult.Yes, "Yes"), (MessageBoxResult.No, "No"),
                                                    (MessageBoxResult.Cancel, "Cancel") },
            _ => new[] { (MessageBoxResult.OK, "OK") },
        };
        _escapeResult = spec.Any(s => s.Item1 == MessageBoxResult.Cancel) ? MessageBoxResult.Cancel
                      : spec.Any(s => s.Item1 == MessageBoxResult.No) ? MessageBoxResult.No
                      : MessageBoxResult.OK;
        _result = _escapeResult;   // closed any other way (Alt+F4) = the escape answer

        // The default is the first button unless the caller named another.
        int defaultIndex = Math.Max(0, Array.FindIndex(spec, x => x.Item1 == (defaultResult ?? spec[0].Item1)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button? defaultButton = null;
        for (int i = 0; i < spec.Length; i++)
        {
            var (result, word) = spec[i];
            string label = labels != null && i < labels.Length && !string.IsNullOrWhiteSpace(labels[i]) ? labels[i] : word;
            var b = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel = result == _escapeResult,
            };
            if (TryFindResource(i == defaultIndex ? "PrimaryButton" : "ActionButton") is Style st) b.Style = st;
            if (i == defaultIndex) defaultButton = b;
            var r = result;
            b.Click += (_, _) => { _result = r; Close(); };
            buttons.Children.Add(b);
        }

        // ---- Body: icon, title, message ---------------------------------------
        var body = new Grid { Margin = new Thickness(24, 22, 24, 22) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = Badge(icon);
        if (badge != null)
        {
            Grid.SetColumn(badge, 0);
            body.Children.Add(badge);
        }

        var texts = new StackPanel { MaxWidth = 440, MinWidth = 300 };
        Grid.SetColumn(texts, 1);
        texts.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextPrimary", Brushes.Black),
            Margin = new Thickness(0, badge != null ? 5 : 0, 0, 10),
        });
        texts.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextSecondary", Brushes.DimGray),
        });
        body.Children.Add(texts);

        // ---- Footer band with the buttons --------------------------------------
        var footer = new Border
        {
            Background = Res("BgCard", Brushes.WhiteSmoke),
            BorderBrush = Res("Divider", Brushes.LightGray),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(0, 0, 8, 8),
            Padding = new Thickness(24, 14, 24, 14),
            Child = buttons,
        };

        var stack = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(footer, Dock.Bottom);
        stack.Children.Add(footer);
        stack.Children.Add(body);

        var card = new Border
        {
            Background = Res("BgElevated", Brushes.White),
            BorderBrush = Res("BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            // Room around the card for its shadow, which a transparent window
            // would otherwise clip.
            Margin = new Thickness(18),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.35, Color = Colors.Black },
        };
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                try { DragMove(); } catch { /* released before the move began */ }
        };
        Content = card;

        Loaded += (_, _) =>
        {
            // Keyboard focus on the default button, so Enter and Space work at once.
            defaultButton?.Focus();
            Activate();
        };
    }

    /// <summary>The round icon for the message type, or null for none. Colours
    /// follow the app's state roles: warning amber, error brand red, question and
    /// information neutral.</summary>
    private FrameworkElement? Badge(MessageBoxImage icon)
    {
        // MessageBoxImage has aliases (Error = Hand = Stop, Warning = Exclamation,
        // Information = Asterisk), so these cover all of them.
        (string glyph, string fg, string bg)? look = icon switch
        {
            MessageBoxImage.Warning => ("", "WarnText", "WarnFill"),
            MessageBoxImage.Error => ("", "BlockText", "BlockFill"),
            MessageBoxImage.Question => ("", "TextPrimary", "InfoFill"),
            MessageBoxImage.Information => ("", "TextPrimary", "InfoFill"),
            _ => null,
        };
        if (look is not { } l) return null;
        return new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(18),
            Background = Res(l.bg, Brushes.Transparent),
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = l.glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = Res(l.fg, Brushes.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Esc is handled by the IsCancel button; this covers the one-button box
        // when focus has left the button.
        if (e.Key == Key.Escape) { _result = _escapeResult; Close(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    private Brush Res(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;
}
