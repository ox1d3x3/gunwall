using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using GunWall.Services;

namespace GunWall.Controls;

/// <summary>
/// Rolling numbers in table cells (0.99.227, Traffic page). Mahabub asked for the
/// Traffic page's figures to roll like the Overview's.
///
///     &lt;TextBlock gw:RollText.Scope="country" gw:RollText.KeyPath="Code"
///                gw:RollText.ValuePath="Count" gw:RollText.Item="{Binding}" /&gt;
///
/// The row arrives as ONE binding (Item) and key and value are read from it, so a
/// recycled cell never sees the new row's value with the old row's key. Format:
/// "N0" (a count, the default) or "bytes" (1.5 MB). See RollingNumber.SetKeyed.
/// Recomputed whenever ANY of these changes - their order in a template is not fixed.
/// </summary>
public static class RollText
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(object), typeof(RollText), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(string), typeof(RollText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty KeyPathProperty = DependencyProperty.RegisterAttached(
        "KeyPath", typeof(string), typeof(RollText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty ValuePathProperty = DependencyProperty.RegisterAttached(
        "ValuePath", typeof(string), typeof(RollText), new PropertyMetadata("", Changed));
    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format", typeof(string), typeof(RollText), new PropertyMetadata("N0", Changed));

    public static object? GetItem(DependencyObject d) => d.GetValue(ItemProperty);
    public static void SetItem(DependencyObject d, object? v) => d.SetValue(ItemProperty, v);
    public static string GetScope(DependencyObject d) => (string)d.GetValue(ScopeProperty);
    public static void SetScope(DependencyObject d, string v) => d.SetValue(ScopeProperty, v);
    public static string GetKeyPath(DependencyObject d) => (string)d.GetValue(KeyPathProperty);
    public static void SetKeyPath(DependencyObject d, string v) => d.SetValue(KeyPathProperty, v);
    public static string GetValuePath(DependencyObject d) => (string)d.GetValue(ValuePathProperty);
    public static void SetValuePath(DependencyObject d, string v) => d.SetValue(ValuePathProperty, v);
    public static string GetFormat(DependencyObject d) => (string)d.GetValue(FormatProperty);
    public static void SetFormat(DependencyObject d, string v) => d.SetValue(FormatProperty, v);

    // Every property, not only Item (0.99.228): in a cell template WPF can apply the
    // Item binding BEFORE ValuePath / KeyPath are set. Updating on Item alone found no
    // path, showed nothing, and never tried again - every Traffic number was blank.
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => Update(d as TextBlock);

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> Props = new();

    private static object? Read(object item, string name) =>
        name.Length == 0 ? null
        : Props.GetOrAdd((item.GetType(), name), k => k.Item1.GetProperty(k.Item2))?.GetValue(item);

    private static readonly Func<double, string> Count = v => Math.Round(v).ToString("N0");
    private static readonly Func<double, string> Bytes = v => AppUsageService.FormatBytes((long)Math.Round(v));

    private static void Update(TextBlock? tb)
    {
        if (tb == null) return;
        var item = GetItem(tb);
        if (item == null) { tb.Text = ""; return; }
        if (GetValuePath(tb).Length == 0 || GetKeyPath(tb).Length == 0) return;   // not all set yet
        try
        {
            object? raw = Read(item, GetValuePath(tb));
            if (raw == null) return;
            double value = Convert.ToDouble(raw);
            string key = GetScope(tb) + "|" + GetValuePath(tb) + "|" + (Read(item, GetKeyPath(tb))?.ToString() ?? "");
            RollingNumber.SetKeyed(tb, key, value, GetFormat(tb) == "bytes" ? Bytes : Count);
        }
        catch (Exception ex) { DiagnosticLog.LogException("RollText", ex); }
    }
}
