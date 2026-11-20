namespace GunWall.Services;

/// <summary>One country on the Traffic map (0.99.243): its shape as SVG path data
/// and, in map units, the anchor arcs end on (centroid of its largest landmass),
/// that landmass's area, and the shape's bounds (for hover).</summary>
public sealed record CountryShape(string Code, string PathData, double X, double Y, double Area,
                                  double MinX, double MinY, double MaxX, double MaxY)
{
    /// <summary>Too small to point at as a shape (Singapore, Hong Kong, Malta...):
    /// drawn as a dot when it has traffic.</summary>
    public bool Tiny => Area < WorldMapShapes.TinyArea;
}

public static partial class WorldMapShapes
{
    public const double TinyArea = 12;

    private static Dictionary<string, (double X, double Y)>? _anchors;
    private static Dictionary<string, CountryShape>? _byCode;

    /// <summary>Where arcs to <paramref name="code"/> end: its shape's anchor, or the
    /// point of a place with no shape. False for an unknown code.</summary>
    public static bool TryAnchor(string? code, out (double X, double Y) point)
    {
        point = default;
        if (string.IsNullOrEmpty(code)) return false;
        var map = _anchors ??= BuildAnchors();
        return map.TryGetValue(code, out point);
    }

    public static CountryShape? Shape(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var map = _byCode ??= Countries.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        return map.TryGetValue(code, out var s) ? s : null;
    }

    private static Dictionary<string, (double X, double Y)> BuildAnchors()
    {
        var map = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Countries) map[c.Code] = (c.X, c.Y);
        foreach (var (code, x, y) in ExtraAnchors) map.TryAdd(code, (x, y));
        return map;
    }
}

/// <summary>What the map knows about one country right now (0.99.243). Rates are
/// bytes per second, attributed: a program's measured bytes split evenly across its
/// connections (or, without the kernel meter, the adapter's total split across all
/// of them) - good for showing where traffic flows, not a per-country meter.</summary>
public sealed record CountryLive(string Code, double DownBps, double UpBps, int Connections, long SessionBytes,
                                 IReadOnlyList<string> Networks)
{
    public double TotalBps => DownBps + UpBps;
}

/// <summary>
/// Per-country traffic for the Traffic map (0.99.243): bytes attributed to each
/// country during a snapshot tick become smoothed download and upload rates, plus
/// how many connections are open to it now and which networks (ASN owners) they go
/// to. Fed by the window's snapshot loop; no WPF type, so the bench runs it.
/// </summary>
public sealed class CountryTrafficTracker
{
    /// <summary>How much of each new tick's rate is taken: the rest is the previous
    /// value, so one quiet second does not make a country's flow vanish.</summary>
    public const double Smoothing = 0.6;
    /// <summary>Below this (bytes/s) a rate counts as zero and is forgotten.</summary>
    public const double Floor = 1;

    private readonly object _lock = new();
    private readonly Dictionary<string, (long Down, long Up)> _tick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double Down, double Up)> _rate = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _session = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (int Conns, List<string> Nets)> _live = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bytes this tick for one connection's country. An empty country (not
    /// placed yet) or nothing moved: ignored.</summary>
    public void AddBytes(string? country, long down, long up)
    {
        if (string.IsNullOrEmpty(country) || country.Length != 2) return;
        down = Math.Max(0, down); up = Math.Max(0, up);
        if (down + up == 0) return;
        lock (_lock)
        {
            _tick.TryGetValue(country, out var t);
            _tick[country] = (t.Down + down, t.Up + up);
        }
    }

    /// <summary>Closes a tick of <paramref name="seconds"/>: rates from the bytes
    /// added since the last call, and the connections open now.</summary>
    public void EndTick(double seconds, IEnumerable<(string Country, string Network)> openConnections)
    {
        double dt = Math.Clamp(double.IsFinite(seconds) ? seconds : 1, 0.2, 10);
        var live = new Dictionary<string, (int Conns, Dictionary<string, int> Nets)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cc, net) in openConnections)
        {
            if (string.IsNullOrEmpty(cc) || cc.Length != 2) continue;
            if (!live.TryGetValue(cc, out var e)) live[cc] = e = (0, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
            var nets = e.Nets;
            if (!string.IsNullOrWhiteSpace(net)) nets[net] = nets.TryGetValue(net, out int n) ? n + 1 : 1;
            live[cc] = (e.Conns + 1, nets);
        }
        lock (_lock)
        {
            foreach (var cc in _rate.Keys.Union(_tick.Keys, StringComparer.OrdinalIgnoreCase).ToList())
            {
                _tick.TryGetValue(cc, out var t);
                _rate.TryGetValue(cc, out var r);
                double down = r.Down + (t.Down / dt - r.Down) * Smoothing;
                double up = r.Up + (t.Up / dt - r.Up) * Smoothing;
                if (down < Floor) down = 0;
                if (up < Floor) up = 0;
                if (down == 0 && up == 0) _rate.Remove(cc); else _rate[cc] = (down, up);
                if (t.Down + t.Up > 0) _session[cc] = (_session.TryGetValue(cc, out long s) ? s : 0) + t.Down + t.Up;
            }
            _tick.Clear();
            _live = live.ToDictionary(kv => kv.Key,
                kv => (kv.Value.Conns, kv.Value.Nets.OrderByDescending(n => n.Value).ThenBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                                                   .Take(3).Select(n => n.Key).ToList()),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Every country with a rate, an open connection or bytes this session,
    /// busiest first.</summary>
    public List<CountryLive> Snapshot()
    {
        lock (_lock)
        {
            var codes = new HashSet<string>(_rate.Keys, StringComparer.OrdinalIgnoreCase);
            codes.UnionWith(_live.Keys);
            codes.UnionWith(_session.Keys);
            return codes.Select(cc =>
                {
                    _rate.TryGetValue(cc, out var r);
                    _live.TryGetValue(cc, out var l);
                    _session.TryGetValue(cc, out long s);
                    return new CountryLive(cc.ToUpperInvariant(), r.Down, r.Up, l.Conns, s, l.Nets ?? (IReadOnlyList<string>)Array.Empty<string>());
                })
                .OrderByDescending(c => c.TotalBps).ThenByDescending(c => c.Connections).ThenBy(c => c.Code, StringComparer.Ordinal)
                .ToList();
        }
    }
}

/// <summary>
/// The Traffic map's moving parts (0.99.243, Mahabub 2026-11-19: "make it dynamic,
/// like UniSense"), a port of UniSense's Traffic Map logic: an arc from this device
/// to each busy country, "streaks" of light travelling along it - downloads towards
/// home, uploads away - at a speed and in a number that follow the rate, a pulse at
/// each destination, and a heat level for every country visited.
///
/// Everything eases: speeds glide to a new rate, arcs fade in and out, and streaks
/// are added at the source and retired only on arrival, so a refresh never makes
/// anything jump. Arcs more than half the map wide take the short way round the
/// back (Perth to the Americas over the Pacific): two copies, one leaving each edge.
///
/// No WPF here: the control (GunWall.Controls.TrafficMap) draws this state, and the
/// bench steps it. Screen units throughout, except where "world" is said: the map's
/// own 1000 x 449 units.
/// </summary>
public sealed class TrafficMapModel
{
    public const int MaxFlows = 20;
    /// <summary>Samples per arc: a frame interpolates, never evaluates the curve.</summary>
    public const int Seg = 96;
    /// <summary>A flow starts at 2 kbit/s and stops below 1 kbit/s (bytes/s here):
    /// the gap keeps a country hovering at the line from blinking on and off.</summary>
    public const double StartBps = 250, StopBps = 125;
    /// <summary>Opacity of each piece of a streak's tail, tail to head (0.99.244:
    /// four pieces, tapering, so it reads as a comet rather than three dashes).</summary>
    public static readonly double[] Tail = { 0.08, 0.22, 0.48, 0.9 };
    /// <summary>Width of each piece, tail to head, in pixels.</summary>
    public static readonly double[] TailWidth = { 1.1, 1.5, 1.9, 2.2 };
    /// <summary>How long a new arc takes to draw itself out from home (0.99.244).</summary>
    public const double GrowSeconds = 0.7;
    /// <summary>How far each heat level is mixed from land towards the download colour.
    /// UniSense goes to 85%; here the top stops at 74% so the deepest countries stay
    /// a background the streaks read against (they also carry a casing).</summary>
    public static readonly double[] HeatMix = { 0, 0.16, 0.28, 0.42, 0.58, 0.74 };

    public sealed class Flow
    {
        public string Code = "";
        public bool Want, Fresh = true;
        public double Vis, Phase, Rate;
        public double TargetDown, TargetUp, SpeedDown, SpeedUp;
        public int CountDown, CountUp;
        public readonly List<double> Down = new(), Up = new();
        public double LineWidth = 1;
        public (double X, double Y) From, To;
        public double Wrap, Length;
        /// <summary>How far the arc has drawn itself out from home, 0..1 (0.99.244).
        /// Streaks and the landing pulse appear only behind it.</summary>
        public double Grow;
        /// <summary>Streaks that reached their end since the drawing last took them:
        /// downloads arriving home, uploads arriving at the destination.</summary>
        public int ArrivedDown, ArrivedUp;
        // The copies for the view and endpoints they were built for: reused while
        // those stay the same, so a second's new figures do not rebuild every arc.
        internal (double, double, double, double, double, double, double, double) CopyKey;
        internal readonly List<ArcCopy> MyCopies = new();
    }

    public sealed class ArcCopy
    {
        public required Flow Flow { get; init; }
        public double X0, Y0, Cx, Cy, X2, Y2, Length;
        /// <summary>This copy ends at the destination (the pulse is drawn here).</summary>
        public bool End;
        public required float[] Lut { get; init; }
        /// <summary>The drawing's own cache for this arc (its geometry); the model
        /// keeps a copy, and so its tag, while the arc is unchanged.</summary>
        public object? Tag;
    }

    private readonly Dictionary<string, Flow> _flows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ArcCopy> _copies = new();
    private Dictionary<string, int> _levels = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ArcCopy> Copies => _copies;
    public IReadOnlyDictionary<string, Flow> Flows => _flows;
    public IReadOnlyDictionary<string, int> Levels => _levels;
    public string Home { get; private set; } = "";
    /// <summary>The country under the pointer: its arc bolder, the others dimmed.</summary>
    public string? Hover { get; set; }

    // The view: world units -> screen, uniform fit, centred.
    public double Scale { get; private set; }
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public double ViewWidth { get; private set; }
    public double ViewHeight { get; private set; }
    public bool HasView => Scale > 0;

    /// <summary>Something to animate: a flow, or one fading out.</summary>
    public bool HasFlows => _flows.Count > 0;

    /// <summary>Fits the map into <paramref name="width"/> x <paramref name="height"/>.
    /// True when the view changed (the arcs were rebuilt for it).</summary>
    public bool SetView(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1)
        {
            bool had = HasView;
            Scale = 0; ViewWidth = ViewHeight = 0; _copies.Clear();
            return had;
        }
        double s = Math.Min(width / WorldMapShapes.Width, height / WorldMapShapes.Height);
        double ox = (width - WorldMapShapes.Width * s) / 2, oy = (height - WorldMapShapes.Height * s) / 2;
        if (Math.Abs(s - Scale) < 1e-9 && Math.Abs(ox - OffsetX) < 1e-6 && Math.Abs(oy - OffsetY) < 1e-6) return false;
        Scale = s; OffsetX = ox; OffsetY = oy; ViewWidth = width; ViewHeight = height;
        BuildCopies();
        return true;
    }

    public (double X, double Y) ToWorld(double x, double y) =>
        Scale > 0 ? ((x - OffsetX) / Scale, (y - OffsetY) / Scale) : (double.NaN, double.NaN);

    public (double X, double Y) ToView(double wx, double wy) => (OffsetX + wx * Scale, OffsetY + wy * Scale);

    /// <summary>Travel speed in px/s from bytes/s (UniSense's scale in bits:
    /// 10 kb/s ~75, 1 Mb/s ~165, 100 Mb/s ~255, at most 300).</summary>
    public static double PxSpeed(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits > 2000 ? Math.Min(300, 30 + Math.Log10(bits / 1000) * 45) : 0;
    }

    /// <summary>On every how-many-th refresh of the display to draw, so the map runs
    /// at about <paramref name="targetFps"/> or a little more, and every drawn frame
    /// is held for the same number of refreshes (0.99.244). 60 Hz and 75 Hz: every
    /// one; 120 and 144: every second; 240: every third.</summary>
    public static int FrameDivisor(double refreshSeconds, double targetFps)
    {
        if (!double.IsFinite(refreshSeconds) || refreshSeconds <= 0 || !double.IsFinite(targetFps) || targetFps <= 0) return 1;
        return Math.Clamp((int)Math.Round(1 / (refreshSeconds * targetFps)), 1, 8);
    }

    /// <summary>Streaks on a lane: one per decade of rate, 1 to 6.</summary>
    public static int StreakCount(double bytesPerSec)
    {
        double bits = bytesPerSec * 8;
        return bits > 2000 ? Math.Clamp((int)Math.Round(Math.Log10(bits / 1000) * 1.2), 1, 6) : 0;
    }

    /// <summary>Heat level 0..5 for each country: by bytes this session when any
    /// were measured, else by destinations; anything contacted at all is at least 1.</summary>
    public static Dictionary<string, int> HeatLevels(IReadOnlyList<CountryLive> live, IReadOnlyDictionary<string, int>? destinations)
    {
        var weight = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool bytes = live.Any(c => c.SessionBytes > 0);
        foreach (var c in live) weight[c.Code] = bytes ? c.SessionBytes : 0;
        if (destinations != null)
            foreach (var (cc, n) in destinations)
                if (!bytes) weight[cc] = Math.Max(weight.TryGetValue(cc, out var w) ? w : 0, n);
                else weight.TryAdd(cc, 0);
        foreach (var c in live) if (c.Connections > 0) weight.TryAdd(c.Code, 0);
        double max = weight.Count > 0 ? weight.Values.Max() : 0;
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cc, v) in weight)
            levels[cc] = max > 0 && v > 0 ? Math.Clamp((int)Math.Ceiling(5 * Math.Log(1 + v) / Math.Log(1 + max)), 1, 5) : 1;
        return levels;
    }

    /// <summary>New figures (about once a second). Flows for the busiest countries
    /// other than home, each existing flow keeping its streaks.</summary>
    public void SetData(IReadOnlyList<CountryLive> live, IReadOnlyDictionary<string, int>? destinations, string? home)
    {
        Home = (home ?? "").ToUpperInvariant();
        _levels = HeatLevels(live, destinations);
        foreach (var f in _flows.Values) f.Want = false;
        bool hasHome = WorldMapShapes.TryAnchor(Home, out var from);
        if (hasHome)
        {
            var top = live.Where(c => !string.Equals(c.Code, Home, StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(c => c.TotalBps).Take(MaxFlows).ToList();
            double max = top.Count > 0 ? top.Max(c => c.TotalBps) : 0;
            int i = 0;
            foreach (var c in top)
            {
                i++;
                _flows.TryGetValue(c.Code, out var f);
                double need = f != null ? StopBps : StartBps;   // showing (or fading): the lower line
                if (c.TotalBps < need || !WorldMapShapes.TryAnchor(c.Code, out var to)) continue;
                if (f == null) _flows[c.Code] = f = new Flow { Code = c.Code.ToUpperInvariant(), Phase = (i * 0.37) % 1 };
                f.Want = true;
                f.Rate = c.TotalBps;
                f.From = from;
                f.To = to;
                double dx = to.X - from.X;
                f.Wrap = Math.Abs(dx) > WorldMapShapes.Width / 2 ? (dx < 0 ? WorldMapShapes.Width : -WorldMapShapes.Width) : 0;
                f.TargetDown = PxSpeed(c.DownBps);
                f.TargetUp = PxSpeed(c.UpBps);
                f.CountDown = StreakCount(c.DownBps);
                f.CountUp = StreakCount(c.UpBps);
                f.LineWidth = 0.8 + 1.4 * (max > 0 ? Math.Log(1 + c.TotalBps) / Math.Log(1 + max) : 0);
            }
        }
        BuildCopies();
        foreach (var f in _flows.Values) Prime(f);
    }

    /// <summary>A new flow arrives already populated, evenly spaced, at full speed -
    /// once it has an arc (a flow added before the map had a size waits for one).</summary>
    private static void Prime(Flow f)
    {
        if (!f.Fresh || f.Length <= 0) return;
        f.Fresh = false;
        f.SpeedDown = f.TargetDown;
        f.SpeedUp = f.TargetUp;
        Fill(f.Down, f.CountDown, f.Phase);
        Fill(f.Up, f.CountUp, (f.Phase + 0.5) % 1);
    }

    private static void Fill(List<double> lane, int n, double start)
    {
        lane.Clear();
        for (int i = 0; i < n; i++) lane.Add((start + (double)i / n) % 1);
    }

    /// <summary>Advances <paramref name="dt"/> seconds. <paramref name="motion"/>
    /// false (Windows' animation effects off): fades complete at once and nothing
    /// travels. Returns true when the arcs' look changed (a fade, a flow gone).</summary>
    public bool Step(double dt, bool motion)
    {
        if (!double.IsFinite(dt) || dt < 0) dt = 0;
        dt = Math.Min(dt, 0.1);
        double ease = 1 - Math.Exp(-dt * 2.5);
        bool changed = false;
        _gone.Clear();
        // Every frame: no list or closure is allocated here (0.99.244 - the 0.99.243
        // bundle showed 86 mid-life collections in five minutes with the map open).
        foreach (var f in _flows.Values)
        {
            double target = f.Want ? 1 : 0, v0 = f.Vis, g0 = f.Grow;
            if (!motion || dt == 0) { if (!motion) { f.Vis = target; f.Grow = 1; } }
            else
            {
                f.Vis += (target - f.Vis) * Math.Min(1, dt * 3.2);
                if (Math.Abs(f.Vis - target) < 0.01) f.Vis = target;
                if (f.Length > 0) f.Grow = Math.Min(1, f.Grow + dt / GrowSeconds);
            }
            if (f.Vis != v0 || f.Grow != g0) changed = true;
            if (!f.Want && f.Vis == 0) { _gone.Add(f.Code); continue; }
            Prime(f);
            if (motion && dt > 0 && f.Length > 0)
            {
                f.SpeedDown += ((f.Want ? f.TargetDown : 0) - f.SpeedDown) * ease;
                f.SpeedUp += ((f.Want ? f.TargetUp : 0) - f.SpeedUp) * ease;
                f.ArrivedDown += StepLane(f.Down, f.Want ? f.CountDown : 0, f.SpeedDown * dt / f.Length);
                f.ArrivedUp += StepLane(f.Up, f.Want ? f.CountUp : 0, f.SpeedUp * dt / f.Length);
            }
        }
        foreach (var code in _gone) _flows.Remove(code);
        if (_gone.Count > 0) BuildCopies();
        return changed || _gone.Count > 0;
    }

    private readonly List<string> _gone = new();

    /// <summary>Advances one lane by <paramref name="du"/> of the arc: streaks are
    /// added at the source, evenly spaced, and removed only when they arrive, so a
    /// change in their number never pops. Returns how many reached the end.</summary>
    public static int StepLane(List<double> lane, int n, double du)
    {
        if (!double.IsFinite(du) || du < 0) du = 0;
        double minP = 1;
        int arrived = 0;
        for (int i = lane.Count - 1; i >= 0; i--)
        {
            double p = lane[i] + du;
            if (p >= 1)
            {
                arrived++;
                if (lane.Count > n) { lane.RemoveAt(i); continue; }
                p -= Math.Floor(p);
            }
            lane[i] = p;
            minP = Math.Min(minP, p);
        }
        if (lane.Count < n && (lane.Count == 0 || minP >= 1.0 / n)) lane.Add(0);
        return arrived;
    }

    private void BuildCopies()
    {
        _copies.Clear();
        if (!HasView) return;
        foreach (var f in _flows.Values)
        {
            var key = (f.From.X, f.From.Y, f.To.X, f.To.Y, f.Wrap, Scale, OffsetX, OffsetY);
            if (f.MyCopies.Count == 0 || f.CopyKey != key)
            {
                f.MyCopies.Clear();
                if (f.Wrap == 0)
                {
                    var a = MakeCopy(f, f.From, f.To, true);
                    f.MyCopies.Add(a);
                    f.Length = a.Length;
                }
                else
                {
                    var a = MakeCopy(f, f.From, (f.To.X + f.Wrap, f.To.Y), false);
                    var b = MakeCopy(f, (f.From.X - f.Wrap, f.From.Y), f.To, true);
                    f.MyCopies.Add(a);
                    f.MyCopies.Add(b);
                    f.Length = Math.Max(a.Length, b.Length);
                }
                f.CopyKey = key;
            }
            _copies.AddRange(f.MyCopies);
        }
    }

    /// <summary>A quadratic arc in screen units, bent upwards - more for longer hops -
    /// and sampled once into a lookup table.</summary>
    private ArcCopy MakeCopy(Flow f, (double X, double Y) from, (double X, double Y) to, bool end)
    {
        double s = Scale;
        double x0 = OffsetX + from.X * s, y0 = OffsetY + from.Y * s;
        double x2 = OffsetX + to.X * s, y2 = OffsetY + to.Y * s;
        double dx = x2 - x0, dy = y2 - y0;
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-6) d = 1e-6;
        double nx = -dy / d, ny = dx / d;
        if (ny > 0) { nx = -nx; ny = -ny; }
        double k = Math.Min(0.32, 40 * s / d + 0.12) * d;
        double cx = (x0 + x2) / 2 + nx * k, cy = (y0 + y2) / 2 + ny * k;
        double poly = Math.Sqrt((cx - x0) * (cx - x0) + (cy - y0) * (cy - y0)) + Math.Sqrt((x2 - cx) * (x2 - cx) + (y2 - cy) * (y2 - cy));
        var lut = new float[(Seg + 1) * 2];
        for (int i = 0; i <= Seg; i++)
        {
            double u = (double)i / Seg, a = (1 - u) * (1 - u), b = 2 * (1 - u) * u, e = u * u;
            lut[i * 2] = (float)(a * x0 + b * cx + e * x2);
            lut[i * 2 + 1] = (float)(a * y0 + b * cy + e * y2);
        }
        return new ArcCopy { Flow = f, X0 = x0, Y0 = y0, Cx = cx, Cy = cy, X2 = x2, Y2 = y2, End = end, Length = (d + poly) / 2, Lut = lut };
    }

    /// <summary>The point at <paramref name="u"/> (0 = source, 1 = end) of a sampled arc.</summary>
    public static void PointAt(float[] lut, double u, out double x, out double y)
    {
        if (!double.IsFinite(u)) u = 0;
        double t = Math.Clamp(u, 0, 1) * Seg;
        int i = (int)t;
        if (i >= Seg) i = Seg - 1;
        double r = t - i;
        x = lut[i * 2] + (lut[i * 2 + 2] - lut[i * 2]) * r;
        y = lut[i * 2 + 1] + (lut[i * 2 + 3] - lut[i * 2 + 1]) * r;
    }

    /// <summary>How strongly a flow is drawn: its fade, dimmed when another country
    /// is hovered.</summary>
    public double Emphasis(Flow f) => f.Vis * (Hover != null && !string.Equals(Hover, f.Code, StringComparison.OrdinalIgnoreCase) ? 0.25 : 1);

    /// <summary>Countries whose bounds hold the world point, smallest first - so an
    /// enclave or an island wins over the country around it. Tiny ones match within
    /// <paramref name="tinyRadius"/> world units of their anchor.</summary>
    public static List<CountryShape> HitCandidates(double wx, double wy, double tinyRadius)
    {
        var hits = new List<CountryShape>();
        if (!double.IsFinite(wx) || !double.IsFinite(wy)) return hits;
        foreach (var c in WorldMapShapes.Countries)
        {
            if (c.Tiny)
            {
                double dx = wx - c.X, dy = wy - c.Y;
                if (dx * dx + dy * dy <= tinyRadius * tinyRadius) hits.Add(c);
            }
            else if (wx >= c.MinX && wx <= c.MaxX && wy >= c.MinY && wy <= c.MaxY) hits.Add(c);
        }
        hits.Sort((a, b) => a.Area.CompareTo(b.Area));
        return hits;
    }
}
