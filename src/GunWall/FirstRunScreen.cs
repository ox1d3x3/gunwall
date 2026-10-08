using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GunWall;

/// <summary>
/// The first-run screen (0.99.184): an animated loading screen, the offer of
/// the two optional databases, their download, and a welcome. Since 0.99.186 it
/// also has an upgrade mode (<see cref="ForUpgrade"/>): the first launch after
/// updating to a newer version shows "Update complete", the versions, and a
/// "See what's new" button that opens the changelog on GitHub.
///
/// Asked for from use: on a fresh install the app opened, a popup offered the
/// downloads, and the window sat there half-ready while they ran. Now one screen
/// covers the app below the title bar and walks through it:
///
///   1. Getting ready - the brand mark animates while startup settles.
///   2. The offer - only if a database is missing. "Not now" goes straight to 4.
///   3. Downloading - one row per database: waiting, downloading, then ready with
///      what was loaded, or the reason it failed. The screen moves on only when
///      every download has FINISHED and the data is loaded and in use; a failure
///      is shown plainly with a Continue button, because the app works without
///      either database and a first run must never be trapped on a screen.
///   4. Welcome - the mark settles, the welcome line, and Get started fades the
///      screen away.
///
/// Shown only on a genuine first run (see MainWindow.OfferFirstRunDownloadsAsync,
/// which owns the first-run and upgrade-marker rules). The title bar stays
/// usable throughout, so the window can always be moved or closed.
///
/// The mark is the sidebar logo - a 4x4 grid with one brand-red cell and one
/// hollow cell - drawn larger, with its cells lighting in a diagonal wave. With
/// Windows' "reduce animations" setting on, the mark is drawn still.
/// </summary>
public sealed class FirstRunScreen : UserControl
{
    /// <summary>One database the screen can fetch.</summary>
    public sealed record Item(string Title, string Size, Func<Task<(bool Ok, string Detail)>> Fetch);

    private readonly IReadOnlyList<Item> _offer;
    private readonly Action _beforeDownload;
    private readonly Action _done;

    // Upgrade mode: both set, or both empty.
    private string _upgradeFrom = "", _upgradeTo = "";
    private Action? _openChangelog;

    /// <summary>The screen for the first launch after an upgrade: no offer, no
    /// downloads - the mark, then "Update complete" with the versions, "See what's
    /// new" (opens the changelog; the screen stays) and "Continue".</summary>
    public static FirstRunScreen ForUpgrade(string from, string to, Action openChangelog, Action done) =>
        new(Array.Empty<Item>(), () => { }, done)
        {
            _upgradeFrom = from,
            _upgradeTo = to,
            _openChangelog = openChangelog,
        };

    private readonly Border _stage = new();
    private readonly List<Rectangle> _cells = new();
    private readonly List<Storyboard> _wave = new();
    private static readonly bool Animate = SystemParameters.ClientAreaAnimation;
    private bool _started;

    /// <param name="offer">Databases still missing; empty skips the offer.</param>
    /// <param name="beforeDownload">Run once, after "Download now" and before the
    /// first request (GunWall's own network permit).</param>
    /// <param name="done">Called after the screen has faded out and been removed.</param>
    public FirstRunScreen(IReadOnlyList<Item> offer, Action beforeDownload, Action done)
    {
        _offer = offer;
        _beforeDownload = beforeDownload;
        _done = done;

        SetResourceReference(BackgroundProperty, "BgPrimary");
        Panel.SetZIndex(this, 1000);
        Focusable = true;

        var column = new StackPanel
        {
            MaxWidth = 560,
            Margin = new Thickness(32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        column.Children.Add(BuildMark());
        _stage.Margin = new Thickness(0, 28, 0, 0);
        column.Children.Add(_stage);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = column,
        };

        Loaded += async (_, _) =>
        {
            if (_started) return;        // Loaded can fire again if the tree is rebuilt
            _started = true;
            await RunAsync();
        };
    }

    // ------------------------------------------------------------------ flow

    private async Task RunAsync()
    {
        try
        {
            StartWave();
            if (_upgradeTo.Length > 0)
            {
                Show(Centered(Text("Finishing the update…", 15, "TextSecondary")));
                Services.DiagnosticLog.Log($"Upgrade screen shown ({_upgradeFrom} -> {_upgradeTo}).");
                await Task.Delay(1100);
                ShowUpgraded();
                return;
            }
            Show(Centered(Text("Setting up GunWall…", 15, "TextSecondary")));
            Services.DiagnosticLog.Log("First run: first-run screen shown.");
            // Long enough to read as a deliberate screen rather than a flicker; the
            // window has already loaded by now, so this is not hiding real work.
            await Task.Delay(1300);

            if (_offer.Count > 0) ShowOffer();
            else ShowWelcome();
        }
        catch (Exception ex)
        {
            // A first run must never be stuck behind its own welcome.
            Services.DiagnosticLog.LogException("FirstRunScreen", ex);
            Finish();
        }
    }

    private void ShowOffer()
    {
        var panel = new StackPanel();
        panel.Children.Add(Centered(Text("Two optional databases", 22, "TextPrimary", bold: true)));
        panel.Children.Add(Centered(Text(
            "They add detail GunWall cannot work out on its own. Both are stored on this PC "
            + "and used offline - nothing about you is sent.", 13, "TextSecondary"), top: 8));

        var list = new StackPanel();
        foreach (var item in _offer) list.Children.Add(OfferRow(item));
        panel.Children.Add(Card(list));

        panel.Children.Add(Centered(Text(
            "To fetch them, GunWall lets its own program reach the internet. Nothing else is "
            + "allowed, and every other application still needs your approval.", 12, "TextTertiary"), top: 14));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
        };
        var download = MakeButton("Download now", primary: true);
        var later = MakeButton("Not now", primary: false);
        later.Margin = new Thickness(10, 0, 0, 0);
        download.Click += async (_, _) =>
        {
            // One press only: a double-click would start every download twice.
            download.IsEnabled = later.IsEnabled = false;
            try { await DownloadAsync(); }
            catch (Exception ex)
            {
                // Never stranded: whatever went wrong, the app is usable.
                Services.DiagnosticLog.LogException("FirstRun/download", ex);
                ShowWelcome(note: "Missing data can be downloaded from Settings \u2192 Additional data.");
            }
        };
        later.Click += (_, _) =>
        {
            download.IsEnabled = later.IsEnabled = false;
            Services.DiagnosticLog.Log("First run: optional databases declined (Not now).");
            ShowWelcome(note: "You can download them any time from Settings → Additional data.");
        };
        buttons.Children.Add(download);
        buttons.Children.Add(later);
        panel.Children.Add(buttons);

        Show(panel);
        download.Focus();
    }

    private async Task DownloadAsync()
    {
        Services.DiagnosticLog.Log("First run: optional databases accepted (Download now).");
        try { _beforeDownload(); }
        catch (Exception ex) { Services.DiagnosticLog.LogException("FirstRun/beforeDownload", ex); }

        var rows = _offer.Select(i => new ProgressRow(this, i)).ToList();
        var panel = new StackPanel();
        panel.Children.Add(Centered(Text("Getting GunWall ready", 22, "TextPrimary", bold: true)));
        panel.Children.Add(Centered(Text("This takes a moment on most connections.", 13, "TextSecondary"), top: 8));
        var list = new StackPanel();
        foreach (var r in rows) list.Children.Add(r.Element);
        panel.Children.Add(Card(list));
        Show(panel);

        // One after the other, as before: the vendor file is small, and two
        // downloads competing make both look slower.
        foreach (var r in rows) await r.FetchAsync();

        int failed = rows.Count(r => !r.Ok);
        Services.DiagnosticLog.Log($"First run: downloads finished, {rows.Count - failed} ready, {failed} failed.");
        if (failed == 0)
        {
            await Task.Delay(500);            // let the last "Ready" be seen
            ShowWelcome();
            return;
        }

        // Shown, not skipped past: the user chose to download, so they are told
        // what did not arrive and where to get it.
        var cont = MakeButton("Continue", primary: true);
        cont.Click += (_, _) => ShowWelcome(note: "Missing data can be downloaded from Settings → Additional data.");
        panel.Children.Add(Centered(Text(failed == rows.Count
            ? "The downloads didn't finish. GunWall works fully without them."
            : "One download didn't finish. GunWall works fully without it.", 13, "TextSecondary"), top: 16));
        var holder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
        holder.Children.Add(cont);
        panel.Children.Add(holder);
        cont.Focus();
    }

    private void ShowWelcome(string note = "")
    {
        SettleMark();
        var panel = new StackPanel();
        panel.Children.Add(Centered(Text("Welcome to GunWall", 34, "TextPrimary", bold: true)));
        var tag = Text("Take back control of your internet.", 16, "BlockText");
        panel.Children.Add(Centered(tag, top: 8));
        panel.Children.Add(Centered(Text(
            // A fresh install starts in Monitoring only, so this says what turning
            // protection on does rather than claiming it is already happening.
            "Turn protection on when you're ready - then every application asks before "
            + "it connects, and you decide who gets through.",
            13, "TextSecondary"), top: 18));
        if (note.Length > 0) panel.Children.Add(Centered(Text(note, 12, "TextTertiary"), top: 10));

        var start = MakeButton("Get started", primary: true);
        start.Click += (_, _) => Finish();
        var holder = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 0) };
        holder.Children.Add(start);
        panel.Children.Add(holder);
        Show(panel);
        start.Focus();
        Services.DiagnosticLog.Log("First run: welcome shown.");
    }

    /// <summary>Upgrade mode's only stage.</summary>
    private void ShowUpgraded()
    {
        SettleMark();
        var panel = new StackPanel();
        panel.Children.Add(Centered(Text("Update complete", 34, "TextPrimary", bold: true)));
        panel.Children.Add(Centered(Text($"GunWall {_upgradeTo} is ready.", 16, "BlockText"), top: 8));
        panel.Children.Add(Centered(Text(
            $"Updated from {_upgradeFrom}. Your rules and settings were kept, and a copy of "
            + "the previous profile was saved alongside them.", 13, "TextSecondary"), top: 18));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 26, 0, 0),
        };
        var news = MakeButton("See what's new", primary: false);
        var go = MakeButton("Continue", primary: true);
        go.Margin = new Thickness(10, 0, 0, 0);
        news.Click += (_, _) =>
        {
            // The screen stays: reading the changelog in a browser is a detour,
            // not the end of the welcome.
            Services.DiagnosticLog.Log("Upgrade screen: See what's new opened.");
            try { _openChangelog?.Invoke(); }
            catch (Exception ex) { Services.DiagnosticLog.LogException("Upgrade/changelog", ex); }
        };
        go.Click += (_, _) => Finish();
        buttons.Children.Add(news);
        buttons.Children.Add(go);
        panel.Children.Add(buttons);
        Show(panel);
        go.Focus();
    }

    private void Finish()
    {
        IsHitTestVisible = false;
        StopWave();
        void Remove()
        {
            if (Parent is Panel p) p.Children.Remove(this);
            Services.DiagnosticLog.Log("First run: screen closed.");
            try { _done(); } catch (Exception ex) { Services.DiagnosticLog.LogException("FirstRun/done", ex); }
        }
        if (!Animate) { Remove(); return; }
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) { DecelerationRatio = 0.6 };
        fade.Completed += (_, _) => Remove();
        BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ stage

    /// <summary>Swaps the stage content with a short fade and rise, like the
    /// app's own page transitions.</summary>
    private void Show(UIElement content)
    {
        _stage.Child = content;
        if (!Animate) return;
        var shift = new TranslateTransform(0, 14);
        _stage.RenderTransform = shift;
        _stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { DecelerationRatio = 0.7 });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { DecelerationRatio = 0.7 });
    }

    // ------------------------------------------------------------------ mark

    /// <summary>The sidebar logo at 72 px: a 4x4 grid, cell (2,0) in the brand
    /// red, cell (1,2) hollow.</summary>
    private FrameworkElement BuildMark()
    {
        var grid = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < 4; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition());
        }
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                var cell = new Rectangle { Margin = new Thickness(0, 0, 4, 4), RadiusX = 1.5, RadiusY = 1.5, Tag = r + c };
                if (r == 0 && c == 2) cell.SetResourceReference(Shape.FillProperty, "BlockText");
                else if (r == 2 && c == 1)
                {
                    cell.SetResourceReference(Shape.StrokeProperty, "TextPrimary");
                    cell.StrokeThickness = 3;
                }
                else cell.SetResourceReference(Shape.FillProperty, "TextPrimary");
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
                _cells.Add(cell);
            }
        return grid;
    }

    /// <summary>Cells dim and light in a diagonal wave, top-left to bottom-right.</summary>
    private void StartWave()
    {
        if (!Animate) return;
        foreach (var cell in _cells)
        {
            int diag = (int)cell.Tag;
            var anim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(1400),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromMilliseconds(diag * 110),
            };
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(0)));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.18, KeyTime.FromPercent(0.45)) { EasingFunction = new SineEase() });
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0)) { EasingFunction = new SineEase() });
            var sb = new Storyboard();
            Storyboard.SetTarget(anim, cell);
            Storyboard.SetTargetProperty(anim, new PropertyPath(OpacityProperty));
            sb.Children.Add(anim);
            sb.Begin(cell, true);
            _wave.Add(sb);
        }
    }

    private void StopWave()
    {
        for (int i = 0; i < _wave.Count; i++)
            try { _wave[i].Stop(_cells[i]); } catch { }
        _wave.Clear();
    }

    /// <summary>The wave stops and every cell comes up to full, a beat apart -
    /// the mark "assembling" as the app becomes ready.</summary>
    private void SettleMark()
    {
        StopWave();
        foreach (var cell in _cells)
        {
            if (!Animate) { cell.Opacity = 1; continue; }
            int diag = (int)cell.Tag;
            cell.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(380))
            {
                BeginTime = TimeSpan.FromMilliseconds(diag * 60),
                DecelerationRatio = 0.7,
            });
        }
    }

    // ------------------------------------------------------------------ rows

    private UIElement OfferRow(Item item)
    {
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Text(item.Title, 13, "TextPrimary"));
        var size = Text(item.Size, 12, "TextTertiary");
        size.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(size, 1);
        g.Children.Add(size);
        return g;
    }

    /// <summary>One database's line on the download stage: a status mark, the
    /// title, what is happening, and a thin bar while it runs.</summary>
    private sealed class ProgressRow
    {
        private readonly Item _item;
        private readonly TextBlock _state;
        private readonly TextBlock _icon;
        private readonly ProgressBar _bar;
        public UIElement Element { get; }
        public bool Ok { get; private set; }

        public ProgressRow(FirstRunScreen owner, Item item)
        {
            _item = item;
            var g = new Grid { Margin = new Thickness(0, 8, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());
            g.RowDefinitions.Add(new RowDefinition());

            _icon = new TextBlock
            {
                Text = "•",
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI"),
            };
            _icon.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            g.Children.Add(_icon);

            var title = owner.Text(item.Title, 13, "TextPrimary");
            Grid.SetColumn(title, 1);
            g.Children.Add(title);

            _state = owner.Text("Waiting", 12, "TextTertiary");
            _state.Margin = new Thickness(0, 3, 0, 0);
            Grid.SetColumn(_state, 1);
            Grid.SetRow(_state, 1);
            g.Children.Add(_state);

            _bar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                Margin = new Thickness(0, 7, 0, 0),
                Visibility = Visibility.Collapsed,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
            };
            Grid.SetColumn(_bar, 1);
            Grid.SetRow(_bar, 2);
            g.Children.Add(_bar);
            Element = g;
        }

        public async Task FetchAsync()
        {
            _state.Text = "Downloading…  " + _item.Size;
            _state.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            _icon.SetResourceReference(TextBlock.ForegroundProperty, "WarnText");
            _bar.Visibility = Visibility.Visible;

            (bool ok, string detail) result;
            try { result = await _item.Fetch(); }
            catch (Exception ex) { result = (false, ex.Message); }

            _bar.Visibility = Visibility.Collapsed;
            Ok = result.ok;
            if (result.ok)
            {
                _icon.Text = "";                                   // CheckMark
                _icon.SetResourceReference(TextBlock.ForegroundProperty, "AllowText");
                _state.Text = "Ready - " + result.detail;
            }
            else
            {
                _icon.Text = "";                                   // Error
                _icon.SetResourceReference(TextBlock.ForegroundProperty, "BlockText");
                _state.Text = "Couldn't download: " + result.detail;
                _state.SetResourceReference(TextBlock.ForegroundProperty, "BlockText");
            }
            _state.TextWrapping = TextWrapping.Wrap;
            Services.DiagnosticLog.Log($"First run: {_item.Title} - {(result.ok ? "ready" : "failed")}: {result.detail}");
        }
    }

    // ------------------------------------------------------------------ helpers

    private TextBlock Text(string text, double size, string brushKey, bool bold = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    private static FrameworkElement Centered(TextBlock t, double top = 0)
    {
        t.TextAlignment = TextAlignment.Center;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, top, 0, 0);
        return t;
    }

    private Border Card(UIElement child)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 10, 20, 10),
            Margin = new Thickness(0, 22, 0, 0),
            Child = child,
        };
        b.SetResourceReference(Border.BackgroundProperty, "BgCard");
        b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return b;
    }

    private Button MakeButton(string text, bool primary)
    {
        var b = new Button { Content = text, MinWidth = 150 };
        if (TryFindResource(primary ? "PrimaryButton" : "ActionButton") is Style st) b.Style = st;
        return b;
    }
}
