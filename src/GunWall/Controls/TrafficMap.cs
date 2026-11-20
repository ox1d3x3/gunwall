using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GunWall.Services;

namespace GunWall.Controls;

/// <summary>The country under the pointer (null: none) and where the pointer is,
/// in the map's own coordinates - for the hover card.</summary>
public sealed class TrafficMapHoverEventArgs : EventArgs
{
    public TrafficMapHoverEventArgs(string? code, Point position) { Code = code; Position = position; }
    public string? Code { get; }
    public Point Position { get; }
}

/// <summary>
/// The Traffic page's live world map (0.99.243, Mahabub 2026-11-19: dynamic, like
/// his UniSense theme's Traffic Map). Countries shaded by how much traffic went to
/// them this session, an arc from this device to each country it is talking to now,
/// streaks of light along each arc - downloads travelling home, uploads away - at a
/// speed and in a number that follow the rate, and a pulse where each arc lands.
/// Hovering a country outlines it, dims the other arcs and raises HoverMoved for
/// the card the window shows.
///
/// Drawn in layers, so a frame repaints only what moves:
///   land     every country, once per size or theme      (cached as a bitmap)
///   heat     the shaded countries, when levels change    (cached; new levels fade in)
///   arcs     the faint arcs and home, when flows change   (each arc's shape kept)
///   hover    the outline of the country under the pointer
///   anim     streaks, pulses and home's rings - every frame, nothing else, and
///            with nothing created per frame: lines and circles in cached pens
/// The frame hook runs only while the map is on screen (Active), Windows' animation
/// effects are on, and something is moving. It draws on every Nth refresh of the
/// display - about 70 frames a second, each held for the same number of refreshes -
/// and at half that while recording a frame costs more than 4 ms (a slow or busy PC),
/// so the map never takes a large share of the window's thread. Frames slower than
/// 8 ms, and frames that came late, are recorded for the diagnostics bundle. The logic is TrafficMapModel
/// (GunWall.Core), stepped here and benched there. No Effects: a blur or shadow on
/// a layer would make every frame re-render it in software.
/// </summary>
public sealed class TrafficMap : FrameworkElement
{
    private sealed record ShapeGeo(CountryShape Shape, Geometry Geometry);

    // Parsed once per run, off the UI thread, and frozen. Started when the first map
    // loads (the Traffic page is built with the window), so it is ready on first show.
    private static Task<ShapeGeo[]>? _shapesTask;

    private static Task<ShapeGeo[]> LoadShapes() => _shapesTask ??= Task.Run(() =>
    {
        using var _perf = PerfMonitor.Measure("LoadShapes");
        var list = new List<ShapeGeo>(WorldMapShapes.Countries.Length);
        foreach (var c in WorldMapShapes.Countries)
        {
            try
            {
                var g = Geometry.Parse(c.PathData);
                g.Freeze();
                list.Add(new ShapeGeo(c, g));
            }
            catch (Exception ex) { DiagnosticLog.LogException($"TrafficMap/shape {c.Code}", ex); }
        }
        return list.ToArray();
    });

    /// <summary>The most frames a second the map draws (0.99.244). WPF raises a
    /// frame per refresh of the display - 240 a second on a 240 Hz monitor - and the
    /// map draws on every Nth one, N chosen from the measured refresh interval, so
    /// every drawn frame is held for the same number of refreshes. 0.99.243 skipped
    /// by elapsed time against 12.5 ms, which is exactly three refreshes at 240 Hz:
    /// it drew after three or four by rounding, and the motion juddered.</summary>
    private const double TargetFps = 70;
    /// <summary>Without graphics acceleration (render tier 0: a remote session, a VM)
    /// WPF rasterises on the CPU, where the frame's real cost is on the render thread
    /// and is not seen by the timing below - so 30 at most there.</summary>
    private const double SoftwareFps = 30;
    /// <summary>The longest step one drawn frame advances: after a hitch, streaks
    /// catch up over a few frames instead of jumping.</summary>
    private const double MaxStep = 0.05;
    /// <summary>An arrival blink's strength, and how far one must fade before the
    /// next can start (at 4/s decay, ~0.35 s apart at most).</summary>
    private const double FlashPeak = 0.6, FlashRearm = 0.15;
    private const double HeatFadeSeconds = 0.2;      // SPEC section 7: nothing longer than 200 ms
    /// <summary>Above this average cost per drawn frame, every other frame is drawn.</summary>
    private const double HalfRateCost = 0.004;
    private const double FullRateCost = 0.0025;
    private const double SlowFrame = 0.008;

    private readonly TrafficMapModel _model = new();
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _land = new(), _heatBase = new(), _heatTop = new(), _arcs = new(), _hoverLayer = new(), _anim = new();

    private ShapeGeo[]? _geo;
    private Dictionary<string, ShapeGeo> _geoByCode = new(StringComparer.OrdinalIgnoreCase);
    private double _dpi = 1;
    private bool _active, _hooked, _broken, _colorsRead;
    private TimeSpan _lastTick = TimeSpan.MinValue, _lastDrawn = TimeSpan.MinValue;
    private double _tickInterval = 1.0 / 60, _cost, _halfUntil, _divisorAt, _targetFps = TargetFps;
    private int _divisor = 1, _ticks;
    private bool _half;
    // Arrival flashes (0.99.244): home glows as downloads land, a destination
    // blinks as uploads reach it.
    private double _homeFlash;
    private readonly Dictionary<string, double> _flash = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What the map did since its last frame, for a late frame's record:
    /// new figures, a shading change, a hover. "" when nothing of its own.</summary>
    private string _lastWork = "";
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Heat: what the base layer shows, and a fade of the new levels over it.
    private Dictionary<string, int> _heatShown = new(StringComparer.OrdinalIgnoreCase);
    private string _heatShownHome = "";
    private string _heatSig = "";
    private bool _fading;
    private double _fade;

    private Point _pointer;
    private bool _hoverQueued;

    // Colours, re-read on a theme change (code-drawn visuals do not follow DynamicResource).
    private Color _cLand, _cEdge, _cDown, _cUp, _cInk;
    private readonly Dictionary<long, Pen> _pens = new();
    private readonly Dictionary<long, SolidColorBrush> _brushes = new();
    private RectangleGeometry? _clip;
    private MatrixTransform? _world;

    /// <summary>The pointer moved over the map, onto another country or off all of them.</summary>
    public event EventHandler<TrafficMapHoverEventArgs>? HoverMoved;

    public TrafficMap()
    {
        _visuals = new VisualCollection(this) { _land, _heatBase, _heatTop, _arcs, _hoverLayer, _anim };
        Focusable = false;
        Loaded += OnLoaded;
        Unloaded += (_, _) => UpdateHook();
        IsVisibleChanged += (_, _) => UpdateHook();
    }

    /// <summary>The map is on screen: the Traffic page is showing and the window is
    /// neither minimised nor in the tray. Set by the window on every such change.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!value) SetHover(null, default);
            UpdateHook();
        }
    }

    public string? HoverCode => _model.Hover;

    private static bool Motion => SystemParameters.ClientAreaAnimation;

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <summary>The whole rectangle answers the pointer, oceans included, so moving
    /// between countries never flickers the hover off and on.</summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null!;

    /// <summary>As wide as offered, as tall as the map's proportions (MaxHeight caps it).</summary>
    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? WorldMapShapes.Width : availableSize.Width;
        double h = w * WorldMapShapes.Height / WorldMapShapes.Width;
        if (!double.IsInfinity(availableSize.Height)) h = Math.Min(h, availableSize.Height);
        return new System.Windows.Size(w, h);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        bool changed = _model.SetView(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
        if (!_model.HasView) ClearAll();
        else if (changed) ViewChanged();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpi = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1;
        ApplyCaches();
        RenderAll();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try { _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { _dpi = 1; }
        if (_dpi <= 0) _dpi = 1;
        ApplyCaches();
        if (_geo == null)
        {
            LoadShapes().ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (_geo != null) return;
                    _geo = t.Result;
                    _geoByCode = _geo.ToDictionary(g => g.Shape.Code, StringComparer.OrdinalIgnoreCase);
                    _heatSig = "";
                    RenderAll();
                    UpdateHook();
                }));
            }, TaskScheduler.Default);
        }
        UpdateHook();
    }

    /// <summary>The static layers as bitmaps at the screen's resolution: composited
    /// each frame, re-rasterised only when they are redrawn.</summary>
    private void ApplyCaches()
    {
        // Not the arcs (0.99.244): a few dozen curves cost the GPU nothing to draw,
        // while a cached layer is re-rasterised every time it changes - every frame
        // of a fade, a grow-in or a hover.
        foreach (var v in new[] { _land, _heatBase, _heatTop })
            v.CacheMode = new BitmapCache(_dpi) { SnapsToDevicePixels = false, EnableClearType = false };
    }

    /// <summary>The window's theme changed: colours are re-read and every layer redrawn.</summary>
    public void RefreshTheme()
    {
        _colorsRead = false;
        _heatSig = "";
        RenderAll();
    }

    /// <summary>The fill of heat level 1..5 (0: plain land), for the legend under the map.</summary>
    public Brush HeatSwatch(int level)
    {
        ReadColors();
        return BrushOf(level <= 0 ? _cLand : Mix(_cLand, _cDown, TrafficMapModel.HeatMix[Math.Clamp(level, 1, 5)]), 1);
    }

    /// <summary>New figures, about once a second while the page is showing.</summary>
    public void SetData(IReadOnlyList<CountryLive> live, IReadOnlyDictionary<string, int>? destinations, string? home)
    {
        if (_broken) return;
        _lastWork = "new figures";
        try
        {
            _model.SetData(live, destinations, home);
            if (!Motion) _model.Step(0, false);
            if (_geo != null && _model.HasView)
            {
                RenderHeat(force: false);
                RenderArcs();
                if (!_hooked) RenderAnim(0);
            }
            UpdateHook();
        }
        catch (Exception ex) { Break("data", ex); }
    }

    // ------------------------------------------------------------------ frames
    /// <summary>On screen, drawn, and allowed to move.</summary>
    private bool CanAnimate =>
        _active && IsLoaded && IsVisible && !_broken && _model.HasView && _geo != null && Motion;

    private bool WantFrames => CanAnimate && (_model.HasFlows || _fading);

    private void UpdateHook()
    {
        bool want = WantFrames;
        if (want == _hooked) return;
        if (want)
        {
            _lastTick = _lastDrawn = TimeSpan.MinValue;
            _ticks = 0;
            _divisorAt = 0;
            _targetFps = (RenderCapability.Tier >> 16) == 0 ? SoftwareFps : TargetFps;
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnFrame;
            _hooked = false;
            // Left as a still picture: arcs settled, nothing in flight.
            if (!_broken)
                try
                {
                    if (_fading) FinishHeatFade();
                    if (_geo != null && _model.HasView)
                    {
                        _model.Step(0, false);
                        RenderArcs();
                        RenderAnim(0);
                    }
                }
                catch (Exception ex) { Break("still", ex); }
            return;
        }
        _hooked = true;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        try
        {
            // Rendering can be raised more than once for one frame: the same
            // RenderingTime means the same frame.
            TimeSpan t = e is RenderingEventArgs r ? r.RenderingTime : _clock.Elapsed;
            if (t == _lastTick) return;
            double raw = _lastTick == TimeSpan.MinValue ? 0 : (t - _lastTick).TotalSeconds;
            _lastTick = t;
            double now = t.TotalSeconds;

            // The display's refresh interval, learnt from the frames themselves; a
            // frame that came well after it is a hitch (the window's thread was busy).
            // Recorded before the interval's own limit: a hitch of 100 ms or more is
            // the one most worth knowing about.
            if (raw > _tickInterval * 2.5) PerfMonitor.RecordMapGap(raw * 1000, _lastWork);
            if (raw > 0 && raw < 0.1) _tickInterval += (raw - _tickInterval) * 0.05;
            if (now >= _divisorAt)
            {
                _divisorAt = now + 1;
                _divisor = TrafficMapModel.FrameDivisor(_tickInterval, _targetFps) * (_half ? 2 : 1);
            }
            if (_lastDrawn != TimeSpan.MinValue && ++_ticks < _divisor) return;
            _ticks = 0;
            double since = _lastDrawn == TimeSpan.MinValue ? 0 : (t - _lastDrawn).TotalSeconds;
            double dt = Math.Min(MaxStep, since);
            _lastDrawn = t;

            long started = Stopwatch.GetTimestamp();
            Advance(dt);
            PerfMonitor.RecordFrame();     // counted; timed below only when slow
            PerfMonitor.RecordMapFrame(since, _tickInterval > 0 ? 1 / _tickInterval : 0, _divisor);
            _lastWork = "";

            // When drawing a frame costs too much (a slow PC, or one busy with
            // something else), half the rate for a while: a steady half rate looks
            // smoother than an uneven full one, and halves the map's share.
            double cost = Stopwatch.GetElapsedTime(started).TotalSeconds;
            _cost += (cost - _cost) * 0.1;
            bool half = _half;
            if (!_half && _cost > HalfRateCost) { _half = true; _halfUntil = now + 8; }
            else if (_half && now > _halfUntil && _cost < FullRateCost) _half = false;
            if (half != _half) _divisorAt = 0;
            if (cost > SlowFrame) PerfMonitor.Record("TrafficMap frame", started);

            if (!WantFrames) UpdateHook();
        }
        catch (Exception ex) { Break("frame", ex); }
    }

    /// <summary>
    /// A failure anywhere in the map: it stops where it is, once, and the page goes
    /// on. Nothing here draws - the failure may be in the drawing - and every way in
    /// (frames, new figures, layout, hover) returns early from now on.
    /// </summary>
    private void Break(string where, Exception ex)
    {
        _broken = true;
        CompositionTarget.Rendering -= OnFrame;
        _hooked = false;
        try { DiagnosticLog.LogException("TrafficMap/" + where, ex); } catch { }
    }

    private void Advance(double dt)
    {
        if (_model.Step(dt, Motion)) RenderArcs();
        if (_fading)
        {
            _fade = Math.Min(1, _fade + dt / HeatFadeSeconds);
            _heatTop.Opacity = EaseOut(_fade);
            if (_fade >= 1) FinishHeatFade();
        }
        RenderAnim(dt);
    }

    private static double EaseOut(double t) => 1 - (1 - t) * (1 - t) * (1 - t);

    // ----------------------------------------------------------------- layers
    private void ViewChanged()
    {
        _clip = null;
        _world = null;
        RenderAll();
        if (_model.Hover != null) RaiseHover(_model.Hover, _pointer);
        UpdateHook();
    }

    private void ClearAll()
    {
        foreach (var v in new[] { _land, _heatBase, _heatTop, _arcs, _hoverLayer, _anim })
            using (v.RenderOpen()) { }
        UpdateHook();
    }

    private void RenderAll()
    {
        if (_broken || _geo == null || !_model.HasView) return;
        try
        {
            ReadColors();
            RenderLand();
            _heatSig = "";
            RenderHeat(force: true);
            RenderArcs();
            RenderHover();
            RenderAnim(0);
        }
        catch (Exception ex) { Break("draw", ex); }
    }

    private void ReadColors()
    {
        if (_colorsRead) return;
        // The palette's own keys, each with the key it was made from as a fallback
        // (both themes define all of them; the parity check holds that).
        Color Res(params string[] keys)
        {
            foreach (var key in keys)
                if (TryFindResource(key) is SolidColorBrush b) return b.Color;
            return Colors.Gray;
        }
        _cLand = Res("MapLand");
        _cEdge = Res("MapEdge", "BgCard");
        _cDown = Res("MapDownload", "InboundBrush");
        _cUp = Res("MapUpload", "AllowText");
        _cInk = Res("TextPrimary");
        _pens.Clear();
        _brushes.Clear();
        _colorsRead = true;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A frozen brush per colour and opacity step (1/64), made once.</summary>
    private SolidColorBrush BrushOf(Color c, double opacity)
    {
        int q = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 64);
        long key = ((long)ColorKey(c) << 8) | (uint)q;
        if (_brushes.TryGetValue(key, out var b)) return b;
        if (_brushes.Count > 4096) _brushes.Clear();   // bounded by the steps; a guard all the same
        b = new SolidColorBrush(c) { Opacity = q / 64.0 };
        b.Freeze();
        _brushes[key] = b;
        return b;
    }

    /// <summary>A frozen pen per colour, opacity step and width step (1/20), made once.</summary>
    private Pen PenOf(Color c, double opacity, double width)
    {
        int q = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 64);
        int w = (int)Math.Round(Math.Clamp(width, 0.01, 50) * 20);
        long key = ((long)ColorKey(c) << 24) | ((long)q << 16) | (uint)w;
        if (_pens.TryGetValue(key, out var p)) return p;
        if (_pens.Count > 4096) _pens.Clear();
        p = new Pen(BrushOf(c, q / 64.0), w / 20.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        p.Freeze();
        _pens[key] = p;
        return p;
    }

    private static int ColorKey(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    private RectangleGeometry Clip()
    {
        if (_clip != null) return _clip;
        _clip = new RectangleGeometry(new Rect(_model.OffsetX, _model.OffsetY,
            WorldMapShapes.Width * _model.Scale, WorldMapShapes.Height * _model.Scale));
        _clip.Freeze();
        return _clip;
    }

    private MatrixTransform World()
    {
        if (_world != null) return _world;
        _world = new MatrixTransform(_model.Scale, 0, 0, _model.Scale, _model.OffsetX, _model.OffsetY);
        _world.Freeze();
        return _world;
    }

    /// <summary>Every country in land colour with hairline edges in the card's colour.</summary>
    private void RenderLand()
    {
        using var _perf = PerfMonitor.Measure("RenderLand");
        using var dc = _land.RenderOpen();
        if (_geo == null || !_model.HasView) return;
        var fill = BrushOf(_cLand, 1);
        var edge = PenOf(_cEdge, 1, 0.7 / _model.Scale);
        dc.PushTransform(World());
        foreach (var g in _geo) dc.DrawGeometry(fill, edge, g.Geometry);
        dc.Pop();
    }

    private Color HeatColor(string code, int level) =>
        string.Equals(code, _model.Home, StringComparison.OrdinalIgnoreCase) ? Mix(_cLand, _cUp, 0.35)
        : level > 0 ? Mix(_cLand, _cDown, TrafficMapModel.HeatMix[Math.Clamp(level, 0, 5)])
        : _cLand;

    /// <summary>
    /// Shades the countries by level, home in green. A change is drawn on the top
    /// heat layer and faded in over the old picture (including countries that went
    /// back to plain land), then folded into the base layer - so levels move softly
    /// rather than flicking, and the base is re-rasterised once per change.
    /// </summary>
    private void RenderHeat(bool force)
    {
        if (_geo == null || !_model.HasView) return;
        var levels = _model.Levels;
        string sig = _model.Home + "|" + string.Join(",", levels.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + kv.Value));
        if (!force && sig == _heatSig) return;
        using var _perf = PerfMonitor.Measure("RenderHeat");
        _heatSig = sig;
        _lastWork = "shading";   // two cached layers re-rasterised on the render thread
        if (_fading) FinishHeatFade();

        var codes = new HashSet<string>(levels.Keys, StringComparer.OrdinalIgnoreCase);
        codes.UnionWith(_heatShown.Keys);
        if (_model.Home.Length > 0) codes.Add(_model.Home);
        if (_heatShownHome.Length > 0) codes.Add(_heatShownHome);

        // Faded in only while frames can run it; otherwise drawn straight away.
        bool fade = CanAnimate && !force && _heatShown.Count > 0;
        var target = fade ? _heatTop : _heatBase;
        DrawHeat(target, codes, levels);
        if (fade)
        {
            _heatTop.Opacity = 0;
            _fade = 0;
            _fading = true;
        }
        else
        {
            using (_heatTop.RenderOpen()) { }
            _heatTop.Opacity = 1;
        }
        _heatShown = new Dictionary<string, int>(levels, StringComparer.OrdinalIgnoreCase);
        _heatShownHome = _model.Home;
        UpdateHook();
    }

    private void DrawHeat(DrawingVisual layer, IEnumerable<string> codes, IReadOnlyDictionary<string, int> levels)
    {
        using var dc = layer.RenderOpen();
        var edge = PenOf(_cEdge, 1, 0.7 / _model.Scale);
        dc.PushTransform(World());
        foreach (var cc in codes)
        {
            if (!_geoByCode.TryGetValue(cc, out var g)) continue;
            levels.TryGetValue(cc, out int level);
            var color = HeatColor(cc, level);
            dc.DrawGeometry(BrushOf(color, 1), edge, g.Geometry);
            // Singapore, Hong Kong...: too small to see as a shape, so a dot.
            if (g.Shape.Tiny && (level > 0 || string.Equals(cc, _model.Home, StringComparison.OrdinalIgnoreCase)))
                dc.DrawEllipse(BrushOf(string.Equals(cc, _model.Home, StringComparison.OrdinalIgnoreCase) ? _cUp : _cDown, 1),
                               PenOf(_cEdge, 1, 1 / _model.Scale), new Point(g.Shape.X, g.Shape.Y), 2.6, 2.6);
        }
        // Places with no shape (Gibraltar, Reunion...): a dot when visited.
        foreach (var (code, x, y) in WorldMapShapes.ExtraAnchors)
            if (levels.TryGetValue(code, out int l) && l > 0)
                dc.DrawEllipse(BrushOf(_cDown, 1), PenOf(_cEdge, 1, 1 / _model.Scale), new Point(x, y), 2.6, 2.6);
        dc.Pop();
    }

    /// <summary>The fade is over: the new picture becomes the base, the top is cleared.</summary>
    private void FinishHeatFade()
    {
        _fading = false;
        _lastWork = "shading";
        var drawing = _heatTop.Drawing;
        if (drawing is { CanFreeze: true }) drawing.Freeze();
        using (var dc = _heatBase.RenderOpen())
            if (drawing != null) dc.DrawDrawing(drawing);
        using (_heatTop.RenderOpen()) { }
        _heatTop.Opacity = 1;
    }

    /// <summary>The faint arcs (bolder for busier countries, the hovered one bolder
    /// still, the rest dimmed), home's marker, and - with animation off - a dot where
    /// each arc lands instead of a pulse. A new arc draws itself out from home
    /// (0.99.244). Each arc's geometry is made once and kept with it; only an arc still
    /// growing is rebuilt, for the few frames it grows.</summary>
    private void RenderArcs()
    {
        using var dc = _arcs.RenderOpen();
        if (_geo == null || !_model.HasView) return;
        ReadColors();
        dc.PushClip(Clip());
        string? hover = _model.Hover;
        bool still = !Motion;
        foreach (var k in _model.Copies)
        {
            var f = k.Flow;
            if (f.Vis <= 0) continue;
            double grow = still ? 1 : EaseOut(f.Grow);
            if (grow <= 0.001) continue;
            bool mine = hover != null && string.Equals(hover, f.Code, StringComparison.OrdinalIgnoreCase);
            double dim = hover != null && !mine ? 0.3 : 1;
            double width = mine ? f.LineWidth + 0.8 : f.LineWidth;
            var g = grow >= 0.999 ? ArcGeometry(k) : PartialArc(k, grow);
            dc.DrawGeometry(null, PenOf(_cDown, 0.32 * f.Vis * dim, width), g);
            if (still && k.End)
                dc.DrawEllipse(BrushOf(_cDown, 0.95 * f.Vis * dim), PenOf(_cEdge, 0.7 * f.Vis * dim, 1.2), new Point(k.X2, k.Y2), 2.4, 2.4);
        }
        if (WorldMapShapes.TryAnchor(_model.Home, out var h))
        {
            var (hx, hy) = _model.ToView(h.X, h.Y);
            dc.DrawEllipse(BrushOf(_cUp, 0.22), null, new Point(hx, hy), 8, 8);
            dc.DrawEllipse(BrushOf(_cUp, 1), PenOf(_cEdge, 0.8, 1.2), new Point(hx, hy), 3.6, 3.6);
        }
        dc.Pop();
    }

    /// <summary>The whole arc, made once and kept on the copy while it is unchanged.</summary>
    private static Geometry ArcGeometry(TrafficMapModel.ArcCopy k)
    {
        if (k.Tag is Geometry kept) return kept;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(k.X0, k.Y0), false, false);
            ctx.QuadraticBezierTo(new Point(k.Cx, k.Cy), new Point(k.X2, k.Y2), true, true);
        }
        g.Freeze();
        k.Tag = g;
        return g;
    }

    /// <summary>The arc from its start to <paramref name="t"/> (0..1): the same curve,
    /// cut with de Casteljau - start, start moved t towards the control, point at t.</summary>
    private static Geometry PartialArc(TrafficMapModel.ArcCopy k, double t)
    {
        double q1x = k.X0 + (k.Cx - k.X0) * t, q1y = k.Y0 + (k.Cy - k.Y0) * t;
        double a = (1 - t) * (1 - t), b = 2 * (1 - t) * t, c = t * t;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(k.X0, k.Y0), false, false);
            ctx.QuadraticBezierTo(new Point(q1x, q1y),
                new Point(a * k.X0 + b * k.Cx + c * k.X2, a * k.Y0 + b * k.Cy + c * k.Y2), true, true);
        }
        g.Freeze();
        return g;
    }

    // Visible area for culling, in view units: a piece of a streak wholly outside
    // it (the far copy of an arc that crosses the Pacific) is not drawn at all.
    private double _cullL, _cullT, _cullR, _cullB;

    private bool Outside(double ax, double ay, double bx, double by) =>
        (ax < _cullL && bx < _cullL) || (ax > _cullR && bx > _cullR) || (ay < _cullT && by < _cullT) || (ay > _cullB && by > _cullB);

    /// <summary>One frame of motion: the streaks - a four-piece tapering tail and a
    /// head with a soft glow, each on a casing in the card's colour - the tip of an arc
    /// still drawing itself out, a ring growing out of each destination, a blink where
    /// an upload lands, home glowing as downloads land, and two slow rings around home.
    /// Drawn as plain lines and circles with cached pens and brushes: nothing is
    /// created per frame for the composition engine to upload and later free (0.99.243
    /// made a geometry per batch per frame).</summary>
    private void RenderAnim(double dt)
    {
        using var dc = _anim.RenderOpen();
        if (_geo == null || !_model.HasView || !Motion || !_model.HasFlows) { _homeFlash = 0; _flash.Clear(); return; }
        ReadColors();
        _cullL = _model.OffsetX - 8; _cullT = _model.OffsetY - 8;
        _cullR = _model.OffsetX + WorldMapShapes.Width * _model.Scale + 8;
        _cullB = _model.OffsetY + WorldMapShapes.Height * _model.Scale + 8;
        dc.PushClip(Clip());

        // Arrivals become blinks. A blink starts only once the last has nearly gone,
        // so busy traffic gives a steady beat of separate blinks (about three a
        // second at most) rather than a glow that never goes out.
        double decay = dt > 0 ? Math.Exp(-dt * 4) : 1;
        _homeFlash *= decay;
        foreach (var f in _model.Flows.Values)
        {
            double em = _model.Emphasis(f);
            if (f.ArrivedDown > 0 && _homeFlash < FlashRearm) _homeFlash = Math.Max(_homeFlash, FlashPeak * em);
            f.ArrivedDown = 0;
            _flash.TryGetValue(f.Code, out double fl);
            fl *= decay;
            if (f.ArrivedUp > 0 && fl < FlashRearm) fl = FlashPeak;
            f.ArrivedUp = 0;
            if (fl > 0.01) _flash[f.Code] = fl; else _flash.Remove(f.Code);
        }

        foreach (var k in _model.Copies)
        {
            var f = k.Flow;
            double mul = _model.Emphasis(f);
            if (mul <= 0.004) continue;
            double grow = EaseOut(f.Grow);
            double tail = Math.Min(0.4, 64 / Math.Max(64, f.Length));
            DrawLane(dc, k.Lut, f.Down, tail, towardsHome: true, _cDown, mul, grow);
            DrawLane(dc, k.Lut, f.Up, tail, towardsHome: false, _cUp, mul, grow);
            if (grow < 0.999)
            {
                // The pen's tip while the arc draws itself out.
                TrafficMapModel.PointAt(k.Lut, grow, out double tx, out double ty);
                if (!Outside(tx, ty, tx, ty))
                    dc.DrawEllipse(BrushOf(_cDown, 0.85 * mul), null, new Point(tx, ty), 1.8, 1.8);
            }
        }

        // Destinations: a ring growing and fading every ~1.7 s, out of step with
        // each other, a steady dot - once the arc has reached them - and a blink in
        // the upload colour as an upload lands.
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var k in _model.Copies)
        {
            if (!k.End || k.Flow.Vis <= 0 || k.Flow.Grow < 0.999) continue;
            double dim = _model.Emphasis(k.Flow);
            var at = new Point(k.X2, k.Y2);
            double ph = (now * 0.6 + k.Flow.Phase) % 1;
            double ringAlpha = (1 - ph) * 0.6 * dim;
            if (ringAlpha > 0.02)
            {
                double rr = 2 + ph * 9;
                dc.DrawEllipse(null, PenOf(_cDown, ringAlpha, 1.2), at, rr, rr);
            }
            if (_flash.TryGetValue(k.Flow.Code, out double fl) && fl > 0.02)
            {
                double fr = 3 + (1 - fl) * 6;
                dc.DrawEllipse(null, PenOf(_cUp, fl * 0.85 * dim, 1.6), at, fr, fr);
            }
            dc.DrawEllipse(BrushOf(_cDown, 0.95 * dim), PenOf(_cEdge, 0.7 * dim, 1.2), at, 2.4, 2.4);
        }

        // Home: a soft glow as downloads arrive, and two slow rings, half a beat apart.
        if (WorldMapShapes.TryAnchor(_model.Home, out var h))
        {
            var (hx, hy) = _model.ToView(h.X, h.Y);
            var home = new Point(hx, hy);
            if (_homeFlash > 0.02)
                dc.DrawEllipse(BrushOf(_cDown, 0.4 * _homeFlash), null, home, 6 + 5 * (FlashPeak - _homeFlash), 6 + 5 * (FlashPeak - _homeFlash));
            for (int ring = 0; ring < 2; ring++)
            {
                double hp = (now * 0.45 + ring * 0.5) % 1;
                double alpha = (1 - hp) * (ring == 0 ? 0.55 : 0.35);
                if (alpha > 0.02) dc.DrawEllipse(null, PenOf(_cUp, alpha, 1.2), home, 4 + hp * 16, 4 + hp * 16);
            }
        }
        dc.Pop();
    }

    /// <summary>A lane's streaks. Downloads run from the destination towards home
    /// (u = 1 - p), uploads from home outwards; while the arc is still drawing itself
    /// out, only what is behind its tip is shown.</summary>
    private void DrawLane(DrawingContext dc, float[] lut, List<double> lane, double tail, bool towardsHome,
                          Color color, double mul, double grow)
    {
        var pieces = TrafficMapModel.Tail;
        var widths = TrafficMapModel.TailWidth;
        int nt = pieces.Length;
        double step = tail / nt;
        foreach (double p in lane)
        {
            double hu = towardsHome ? 1 - p : p;
            if (hu > grow) continue;
            for (int s = 0; s < nt; s++)
            {
                double p1 = p - step * (nt - s - 1);
                if (p1 <= 0) continue;
                double p0 = Math.Max(0, p1 - step);
                TrafficMapModel.PointAt(lut, towardsHome ? 1 - p0 : p0, out double ax, out double ay);
                TrafficMapModel.PointAt(lut, towardsHome ? 1 - p1 : p1, out double bx, out double by);
                if (Outside(ax, ay, bx, by)) continue;
                var a = new Point(ax, ay);
                var b = new Point(bx, by);
                if (s >= nt - 2) dc.DrawLine(PenOf(_cEdge, pieces[s] * mul * 0.5, widths[s] + 1.6), a, b);
                dc.DrawLine(PenOf(color, pieces[s] * mul, widths[s]), a, b);
            }
            TrafficMapModel.PointAt(lut, hu, out double hx, out double hy);
            if (Outside(hx, hy, hx, hy)) continue;
            var head = new Point(hx, hy);
            dc.DrawEllipse(BrushOf(color, 0.14 * mul), null, head, 5, 5);                     // soft glow
            dc.DrawEllipse(BrushOf(color, mul), PenOf(_cEdge, 0.6 * mul, 1.2), head, 2.3, 2.3); // the head, cased
        }
    }

    // ------------------------------------------------------------------ hover
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _pointer = e.GetPosition(this);
        if (_hoverQueued) return;
        // Once per frame however fast the pointer moves.
        _hoverQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(ResolveHover));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(null, default);
    }

    private void ResolveHover()
    {
        _hoverQueued = false;
        if (_broken) return;
        try
        {
            if (!IsMouseOver || _geo == null || !_model.HasView) { SetHover(null, default); return; }
            var (wx, wy) = _model.ToWorld(_pointer.X, _pointer.Y);
            string? code = null;
            if (wx >= 0 && wy >= 0 && wx <= WorldMapShapes.Width && wy <= WorldMapShapes.Height)
            {
                // Bounds first, smallest first, then the shape itself: a handful of
                // FillContains calls, not two hundred.
                double tiny = 2.6 + 4 / _model.Scale;
                var p = new Point(wx, wy);
                foreach (var c in TrafficMapModel.HitCandidates(wx, wy, tiny))
                {
                    if (c.Tiny) { code = c.Code; break; }
                    if (_geoByCode.TryGetValue(c.Code, out var g) && g.Geometry.FillContains(p)) { code = c.Code; break; }
                }
                // Places with no shape answer only where their dot is drawn.
                if (code == null)
                    foreach (var (cc, x, y) in WorldMapShapes.ExtraAnchors)
                        if (_model.Levels.TryGetValue(cc, out int l) && l > 0 && (wx - x) * (wx - x) + (wy - y) * (wy - y) <= tiny * tiny)
                        { code = cc; break; }
            }
            SetHover(code, _pointer);
        }
        catch (Exception ex) { Break("hover", ex); }
    }

    private void SetHover(string? code, Point at)
    {
        if (!string.Equals(code, _model.Hover, StringComparison.OrdinalIgnoreCase))
        {
            _model.Hover = code;
            _lastWork = "hover";
            if (_geo != null && _model.HasView && !_broken)
                try
                {
                    RenderHover();
                    RenderArcs();
                    if (!_hooked) RenderAnim(0);
                }
                catch (Exception ex) { Break("hover", ex); }
        }
        RaiseHover(code, at);
    }

    private void RaiseHover(string? code, Point at)
    {
        try { HoverMoved?.Invoke(this, new TrafficMapHoverEventArgs(code, at)); }
        catch (Exception ex) { DiagnosticLog.LogException("TrafficMap/hover", ex); }
    }

    /// <summary>The hovered country's outline in the text colour.</summary>
    private void RenderHover()
    {
        using var dc = _hoverLayer.RenderOpen();
        string? code = _model.Hover;
        if (code == null || _geo == null || !_model.HasView || !_geoByCode.TryGetValue(code, out var g))
        {
            if (code != null && _model.HasView && WorldMapShapes.TryAnchor(code, out var a))
            {
                var (ax, ay) = _model.ToView(a.X, a.Y);
                dc.DrawEllipse(null, PenOf(_cInk, 1, 1.2), new Point(ax, ay), 5, 5);
            }
            return;
        }
        dc.PushTransform(World());
        dc.DrawGeometry(null, PenOf(_cInk, 1, 1.1 / _model.Scale), g.Geometry);
        if (g.Shape.Tiny) dc.DrawEllipse(null, PenOf(_cInk, 1, 1.2 / _model.Scale), new Point(g.Shape.X, g.Shape.Y), 4.2, 4.2);
        dc.Pop();
    }
}
