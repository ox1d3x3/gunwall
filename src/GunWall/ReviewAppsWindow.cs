using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GunWall;

/// <summary>
/// Several applications waiting at once, decided in one window (0.99.195).
///
/// After a reset or a fresh install, every running application asks at the same
/// moment: 14 popups in 3 seconds was reported. With "Group popups" on (the
/// default) the window shows this list instead once three or more are waiting.
/// Each row gets its own Allow / Block; anything left undecided stays as it is -
/// blocked in Zero Trust - exactly as if a single popup had been closed.
///
/// Built in code like NoteWindow: no layout worth a XAML file, and nothing pins a
/// Width or Height on an input (trap 2.22).
/// </summary>
public sealed class ReviewAppsWindow : Window
{
    private readonly Action<AlertWindow.AlertInfo> _onAllow;
    private readonly Action<AlertWindow.AlertInfo> _onBlock;
    private readonly List<(AlertWindow.AlertInfo Info, StackPanel Buttons, TextBlock Verdict)> _rows = new();
    private readonly TextBlock _remaining;

    public ReviewAppsWindow(IReadOnlyList<AlertWindow.AlertInfo> apps, bool strictMode,
                            Action<AlertWindow.AlertInfo> onAllow, Action<AlertWindow.AlertInfo> onBlock)
    {
        _onAllow = onAllow;
        _onBlock = onBlock;

        Title = "New applications - GunWall";
        WindowTheme.Attach(this);
        Width = 680;
        SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(360, SystemParameters.WorkArea.Height * 0.8);
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        ShowInTaskbar = true;   // a list this long must be findable if it goes behind
        Background = Res("BgCard", Brushes.White);

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18), LastChildFill = true };

        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(new TextBlock
        {
            Text = $"{apps.Count} applications want to connect",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimary", Brushes.Black),
        });
        head.Children.Add(new TextBlock
        {
            Text = strictMode
                ? "They are blocked until you allow them. Anything you leave undecided stays blocked."
                : "Decide each one, or all at once.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextSecondary", Brushes.Gray),
            Margin = new Thickness(0, 4, 0, 0),
        });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        // Footer before the list, so DockPanel gives the list what is left.
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        _remaining = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Res("TextSecondary", Brushes.Gray),
        };
        DockPanel.SetDock(_remaining, Dock.Left);
        footer.Children.Add(_remaining);

        var close = MakeButton("Close", "PromptSecondary");
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var allowAll = MakeButton("Allow all remaining", "PromptPrimary");
        allowAll.Click += (_, _) => DecideAll(allow: true);
        var blockAll = MakeButton("Block all remaining", "PromptSecondary");
        blockAll.Click += (_, _) => DecideAll(allow: false);
        foreach (var b in new[] { close, allowAll, blockAll })
        {
            DockPanel.SetDock(b, Dock.Right);
            footer.Children.Add(b);
        }
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var list = new StackPanel();
        foreach (var info in apps) list.Children.Add(MakeRow(info));
        root.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        Content = root;
        UpdateRemaining();
    }

    private UIElement MakeRow(AlertWindow.AlertInfo info)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image { Width = 24, Height = 24, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        try { icon.Source = Services.IconService.GetIcon(info.ExePath); }
        catch { /* no icon is cosmetic */ }
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        string where = string.IsNullOrEmpty(info.RemoteAddress)
            ? info.Protocol
            : $"{info.Protocol} {info.RemoteAddress}" + (info.RemotePort > 0 ? $":{info.RemotePort}" : "");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = info.ProcessName,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("TextPrimary", Brushes.Black),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = $"{info.ExePath}  ·  {where}",
            FontSize = 11,
            Foreground = Res("TextSecondary", Brushes.Gray),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = info.ExePath,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var verdict = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, FontWeight = FontWeights.SemiBold };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var block = MakeButton("Block", "PromptSecondary");
        var allow = MakeButton("Allow", "PromptPrimary");
        block.Margin = new Thickness(0, 0, 8, 0);
        allow.Margin = new Thickness(0);
        buttons.Children.Add(block);
        buttons.Children.Add(allow);
        var cell = new Grid();
        cell.Children.Add(buttons);
        cell.Children.Add(verdict);
        Grid.SetColumn(cell, 2);
        grid.Children.Add(cell);

        var entry = (info, buttons, verdict);
        _rows.Add(entry);
        block.Click += (_, _) => Decide(entry, allow: false);
        allow.Click += (_, _) => Decide(entry, allow: true);

        return new Border
        {
            Child = grid,
            Padding = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Res("BorderBrush", Brushes.LightGray),
        };
    }

    private void Decide((AlertWindow.AlertInfo Info, StackPanel Buttons, TextBlock Verdict) row, bool allow)
    {
        if (row.Buttons.Visibility != Visibility.Visible) return;   // already decided
        try
        {
            if (allow) _onAllow(row.Info); else _onBlock(row.Info);
        }
        catch (Exception ex)
        {
            Services.DiagnosticLog.Log($"Review window: {(allow ? "Allow" : "Block")} {row.Info.ProcessName} failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }
        row.Buttons.Visibility = Visibility.Collapsed;
        row.Verdict.Text = allow ? "Allowed" : "Blocked";
        row.Verdict.Foreground = Res(allow ? "AllowText" : "BlockText", allow ? Brushes.Green : Brushes.Red);
        row.Verdict.Visibility = Visibility.Visible;
        UpdateRemaining();
    }

    private void DecideAll(bool allow)
    {
        foreach (var row in _rows.ToList()) Decide(row, allow);
    }

    private void UpdateRemaining()
    {
        int left = _rows.Count(r => r.Buttons.Visibility == Visibility.Visible);
        _remaining.Text = left == 0 ? "All decided." : $"{left} undecided";
    }

    private Button MakeButton(string text, string style)
    {
        var b = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
        if (TryFindResource(style) is Style s) b.Style = s;
        return b;
    }

    /// <summary>A theme brush, or a plain fallback (see NoteWindow.Res).</summary>
    private Brush Res(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;
}
