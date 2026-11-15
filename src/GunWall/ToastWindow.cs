using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GunWall;

/// <summary>
/// GunWall's own notification, in the corner above the taskbar (0.99.235).
///
/// The tray balloons ("GunWall is still running", "First network activity") were
/// drawn by Windows: its notification style, its colours, its Action Centre entry -
/// nothing a program can restyle. Mahabub, 2026-11-15: "the GUI is windows default
/// not our software theme". So GunWall shows these itself: the prompt's card, the
/// app's theme and font, the UI size, a short fade and rise.
///
/// It never takes the keyboard: a notice that steals focus types the next key into
/// itself. One at a time; a new one replaces the old. Clicking it opens GunWall.
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    private readonly System.Windows.Threading.DispatcherTimer _life;
    private readonly Action? _onClick;
    private readonly TranslateTransform _shift = new();
    private readonly Grid _root;
    private bool _closing;

    private const double ShadowMargin = 12, EdgeGap = 12, Rise = 14;

    /// <summary>Shows a notice for <paramref name="milliseconds"/>. <paramref name="kind"/>
    /// is "info" or "warn". Safe to call from the UI thread only; never throws.</summary>
    public static void Show(string title, string text, string kind = "info", int milliseconds = 4000, Action? onClick = null)
    {
        try
        {
            _current?.CloseNow();
            var t = new ToastWindow(title, text, kind, milliseconds, onClick);
            _current = t;
            t.Show();
        }
        catch (Exception ex) { Services.DiagnosticLog.LogException("Toast", ex); }
    }

    private ToastWindow(string title, string text, string kind, int milliseconds, Action? onClick)
    {
        _onClick = onClick;
        Title = "GunWall";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        if (TryFindResource("UiFont") is FontFamily font) FontFamily = font;

        bool warn = kind == "warn";
        _root = new Grid { Margin = new Thickness(ShadowMargin + 2), RenderTransform = _shift };
        // The shadow on its own empty layer, as in the prompt: it renders once.
        _root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Res("BgElevated", Brushes.White),
            IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { BlurRadius = 30, ShadowDepth = 6, Direction = 270, Opacity = 0.40, Color = Colors.Black },
        });

        var row = new Grid { Margin = new Thickness(14, 13, 12, 13) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var tile = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(9),
            Background = Res(warn ? "WarnFill" : "AccentSoft", Brushes.Transparent),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new System.Windows.Shapes.Path
            {
                Width = 17, Height = 17, Stretch = Stretch.Uniform,
                Stroke = Res(warn ? "WarnText" : "BlockText", Brushes.OrangeRed),
                StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Data = TryFindResource(warn ? "IconAlerts" : "IconSecurity") as Geometry,
            },
        };
        row.Children.Add(tile);

        var words = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimary", Brushes.Black), TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(text))
            words.Children.Add(new TextBlock
            {
                Text = text, FontSize = 12, Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap, MaxHeight = 70, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Res("TextSecondary", Brushes.Gray),
            });
        Grid.SetColumn(words, 1);
        row.Children.Add(words);

        var close = new TextBlock
        {
            Text = "✕", FontSize = 11, Padding = new Thickness(6, 2, 6, 4),
            Foreground = Res("TextTertiary", Brushes.Gray), Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, ToolTip = "Dismiss",
        };
        close.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); };
        Grid.SetColumn(close, 2);
        row.Children.Add(close);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Res("BgElevated", Brushes.White),
            BorderBrush = Res("BorderBrush", Brushes.LightGray),
            BorderThickness = new Thickness(1),
            Child = row,
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            Dismiss();
            try { _onClick?.Invoke(); } catch (Exception ex) { Services.DiagnosticLog.LogException("Toast/click", ex); }
        };
        _root.Children.Add(card);
        Content = _root;
        UiZoom.Apply(this);   // follows the UI size, like every popup

        _life = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1500, milliseconds)) };
        _life.Tick += (_, _) => Dismiss();
        // Reading it is not ignoring it: the clock stops while the pointer is on it.
        MouseEnter += (_, _) => _life.Stop();
        MouseLeave += (_, _) => { if (!_closing) _life.Start(); };

        SourceInitialized += (_, _) => MakeNonActivating();
        ContentRendered += (_, _) => Place();
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) => { Place(); Enter(); _life.Start(); };
        Closed += (_, _) => { _life.Stop(); if (ReferenceEquals(_current, this)) _current = null; };
    }

    /// <summary>The corner of the screen the pointer is on, above the taskbar - and
    /// above a connection prompt if one is open there, never over it.</summary>
    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 90;
        double left = area.Right - w + ShadowMargin - EdgeGap;
        double top = area.Bottom - h + ShadowMargin - EdgeGap;
        try
        {
            foreach (Window o in Application.Current.Windows)
                if (o is AlertWindow { IsVisible: true } a && a.Top < top + h && a.Left + a.ActualWidth > left)
                    top = Math.Min(top, a.Top - h + 2 * ShadowMargin);
        }
        catch { }
        Left = Math.Max(area.Left, left);
        Top = Math.Max(area.Top, top);
    }

    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private void Enter()
    {
        if (!Animate) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _root.Opacity = 0;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(Rise, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _life.Stop();
        if (!Animate) { CloseNow(); return; }
        var fade = new DoubleAnimation(_root.Opacity, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => CloseNow();
        _root.BeginAnimation(OpacityProperty, fade);
    }

    private void CloseNow()
    {
        _closing = true;
        try { Close(); } catch { }
    }

    /// <summary>WS_EX_NOACTIVATE: clicking it does not take the keyboard from the app
    /// in use. WS_EX_TOOLWINDOW: it is not an Alt+Tab entry.</summary>
    private void MakeNonActivating()
    {
        try
        {
            IntPtr h = new WindowInteropHelper(this).Handle;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }
        catch { }
    }

    private Brush Res(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? Application.Current?.TryFindResource(key) as Brush ?? fallback;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
