using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace GunWall;

/// <summary>
/// The domains in one blocklist category, each with a tick: ticked is blocked
/// while the category is on, unticked is kept working. Security - a category -
/// Show domains.
///
/// A category used to be all or nothing, so one entry that broke a site meant
/// turning off the other few hundred. The choice is stored by name and survives
/// "Update lists from online": a name unticked today stays unticked when it comes
/// back in a newer list.
///
/// Built in code, like NoteWindow, because it is one list and two buttons. The
/// list is virtualised, so a category of thousands of names opens at once.
/// </summary>
public sealed class BlocklistDomainsWindow : Window
{
    /// <summary>One row: a name and whether it is blocked. Bound two-way to the
    /// row's check box.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private bool _blocked;
        public string Name { get; init; } = "";
        public bool Blocked
        {
            get => _blocked;
            set { if (_blocked == value) return; _blocked = value; PropertyChanged?.Invoke(this, new(nameof(Blocked))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<Row> _rows;
    private readonly ICollectionView _view;
    private readonly TextBlock _summary;
    private readonly TextBox _search;

    /// <summary>The names left unticked when Save was pressed.</summary>
    public HashSet<string> Excluded =>
        new(_rows.Where(r => !r.Blocked).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

    public BlocklistDomainsWindow(string categoryName, IReadOnlyList<string> domains, ISet<string> excluded)
    {
        Title = "Domains - " + categoryName;
        Width = 520;
        Height = 600;
        MinWidth = 420;
        MinHeight = 360;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Res("BgCard", Brushes.White);

        _rows = new ObservableCollection<Row>(
            domains.Select(d => new Row { Name = d, Blocked = !excluded.Contains(d) }));
        foreach (var r in _rows) r.PropertyChanged += (_, _) => { if (!_bulk) UpdateSummary(); };
        _view = CollectionViewSource.GetDefaultView(_rows);

        var root = new DockPanel { Margin = new Thickness(20), LastChildFill = true };

        var intro = new TextBlock
        {
            Text = "Ticked domains are blocked while this category is on. Untick a domain to keep it "
                 + "working - for example, one a program you use needs. Your choices are kept when the "
                 + "lists are updated from online.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("TextSecondary", Brushes.Gray),
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(intro, Dock.Top);
        root.Children.Add(intro);

        _search = new TextBox
        {
            Padding = new Thickness(10, 6, 10, 6),
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 6),
            ToolTip = "Type part of a domain to filter the list",
        };
        _search.TextChanged += (_, _) =>
        {
            string q = _search.Text.Trim();
            Predicate<object>? filter = null;
            if (q.Length > 0) filter = o => o is Row r && r.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            _view.Filter = filter;
            UpdateSummary();
        };
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);

        _summary = new TextBlock
        {
            FontSize = 11,
            Foreground = Res("TextSecondary", Brushes.Gray),
            Margin = new Thickness(2, 0, 0, 8),
        };
        DockPanel.SetDock(_summary, Dock.Top);
        root.Children.Add(_summary);

        // Buttons before the list, so the list takes what is left (LastChildFill).
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        var tickShown = new Button { Content = "Tick all shown", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        var untickShown = new Button { Content = "Untick all shown", Padding = new Thickness(12, 6, 12, 6) };
        tickShown.Click += (_, _) => SetShown(true);
        untickShown.Click += (_, _) => SetShown(false);
        DockPanel.SetDock(tickShown, Dock.Left);
        DockPanel.SetDock(untickShown, Dock.Left);
        var save = new Button { Content = "Save", Padding = new Thickness(16, 6, 16, 6), IsDefault = true };
        if (TryFindResource("PrimaryButton") is Style ps) save.Style = ps;
        save.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        DockPanel.SetDock(save, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        buttons.Children.Add(tickShown);
        buttons.Children.Add(untickShown);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(ToggleButton_IsChecked, new Binding(nameof(Row.Blocked)) { Mode = BindingMode.TwoWay });
        check.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Row.Name)));
        check.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        var list = new ListBox
        {
            ItemsSource = _view,
            ItemTemplate = new DataTemplate { VisualTree = check },
            BorderThickness = new Thickness(1),
            BorderBrush = Res("BorderBrush", Brushes.LightGray),
            Background = Res("BgElevated", Brushes.White),
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        root.Children.Add(list);

        Content = root;
        UpdateSummary();
        Loaded += (_, _) => _search.Focus();
    }

    private static readonly DependencyProperty ToggleButton_IsChecked =
        System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private bool _bulk;   // one summary update for a whole tick/untick, not one per row

    private void SetShown(bool blocked)
    {
        _bulk = true;
        try { foreach (var o in _view.Cast<object>().ToList()) if (o is Row r) r.Blocked = blocked; }
        finally { _bulk = false; }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        int shown = _view.Cast<object>().Count();
        int unticked = _rows.Count(r => !r.Blocked);
        _summary.Text = (shown == _rows.Count ? $"{_rows.Count:N0} domains" : $"Showing {shown:N0} of {_rows.Count:N0}")
                      + (unticked > 0 ? $" · {unticked:N0} unticked (kept working)" : " · all blocked");
    }

    /// <summary>A theme brush, or a plain fallback - see NoteWindow.</summary>
    private Brush Res(string key, Brush fallback) =>
        TryFindResource(key) as Brush
        ?? Application.Current?.TryFindResource(key) as Brush
        ?? fallback;
}
