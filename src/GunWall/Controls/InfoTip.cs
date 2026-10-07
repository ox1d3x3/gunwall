using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GunWall.Controls;

/// <summary>
/// A small information icon that shows an explanation when hovered.
///
/// Section and page descriptions used to sit under every heading as a muted
/// paragraph. Read once, they are useful; on every later visit they are a wall
/// of grey text between the heading and the controls it introduces. The icon
/// keeps the explanation one hover away without taking a line of the layout.
///
/// What stays visible instead of moving here: state (anything code updates),
/// and labels a control cannot be understood without. This is for the
/// explanation, not the instruction.
///
/// The tooltip is built from <see cref="Text"/> as a wrapped TextBlock with a
/// width limit. A plain string tooltip does not wrap, so a paragraph would show
/// as one line running off the screen. The text is also exposed to screen
/// readers as the element's help text, since a tooltip alone is not announced.
///
/// Appearance (size, glyph, colours, show delay and duration) is in the
/// implicit style in Themes/Controls.xaml.
/// </summary>
public sealed class InfoTip : Control
{
    /// <summary>Width the tooltip text wraps at, in device-independent pixels.</summary>
    public const double TipWidth = 340;

    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(InfoTip),
            new PropertyMetadata("", OnTextChanged));

    /// <summary>The explanation shown on hover.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var tip = (InfoTip)d;
        string text = e.NewValue as string ?? "";
        // A new TextBlock per instance: one element cannot be the tooltip of two
        // controls, which is why this is not a setter in the style.
        tip.ToolTip = text.Length == 0
            ? null
            : new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = TipWidth };
        AutomationProperties.SetHelpText(tip, text);
    }
}
