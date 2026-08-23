using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GunWall;

/// <summary>A one-line text prompt with an explanation above it.
///
/// Built in code rather than XAML because it is small and has no layout worth
/// designing. Nothing here pins a Width or Height on an input: WPF UI binds those
/// straight to a control's border and lays its content inside Padding, so a fixed
/// dimension clips the text. That cost three releases to find once already - see
/// HANDOVER.md trap 2.22.
/// </summary>
public sealed class NoteWindow : Window
{
    private readonly TextBox _box;

    /// <summary>What the user typed, trimmed. Empty means "remove the note".</summary>
    public string Note => _box.Text.Trim();

    public NoteWindow(string prompt, string existing)
    {
        Title = "Name this device";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Res("BgCard", Brushes.White);

        var panel = new StackPanel { Margin = new Thickness(22), MaxWidth = 460 };

        panel.Children.Add(new TextBlock
        {
            Text = prompt,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextSecondary", Brushes.Gray),
            Margin = new Thickness(0, 0, 0, 14),
        });

        _box = new TextBox
        {
            Text = existing,
            MinWidth = 380,
            Padding = new Thickness(10, 6, 10, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_box);

        panel.Children.Add(new TextBlock
        {
            Text = "For example: My homelab, Door CCTV, Kitchen speaker. "
                 + "Clear the box to remove the name.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = Res("TextSecondary", Brushes.Gray),
            Margin = new Thickness(0, 8, 0, 16),
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button
        {
            Content = "Cancel",
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsCancel = true,
        };
        var save = new Button
        {
            Content = "Save",
            Padding = new Thickness(14, 6, 14, 6),
            IsDefault = true,
        };
        if (TryFindResource("PrimaryButton") is Style ps) save.Style = ps;
        save.Click += (_, _) => DialogResult = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        panel.Children.Add(buttons);

        Content = panel;

        // Focus in Loaded, not the constructor: the control has no visual parent
        // until the window is shown, so focusing here does nothing at all.
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }

    /// <summary>A theme brush, or a plain fallback if the dictionary has not been
    /// merged - which is the case when this window is constructed in a test or
    /// before the application resources exist.</summary>
    private Brush Res(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;
}
