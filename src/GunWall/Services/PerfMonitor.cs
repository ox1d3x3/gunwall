using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GunWall.Services;

/// <summary>
/// Performance evidence for the diagnostics bundle.
///
/// Until 0.99.152 the log recorded lifecycle events and errors and nothing about
/// cost: no memory, no CPU, no garbage collection, no responsiveness, and no
/// timing for any operation. Every optimisation had to start from reading code.
/// This records enough to start from numbers instead:
///
///   - a summary line every five minutes: memory four ways, GC pause share, CPU,
///     threads, handles, UI responsiveness, and the operations that cost most
///   - per-operation timing for the work that runs repeatedly or matters at
///     startup, through <see cref="Measure"/>
///   - UI freezes of 250 ms or more, logged as they happen with the operation
///     that most likely caused them - or flagged as unattributed, which says
///     where the next measurement point belongs
///   - startup phase timings, and a whole-session summary in every export
///
/// Built to cost nothing that shows: two timestamp reads and one short lock per
/// measured operation, no allocation on that path, one process sample every five
/// minutes. Nothing recorded identifies the user - operation names are code
/// names, never paths, addresses or applications.
/// </summary>
public static class PerfMonitor
{
    // ---- configuration ----------------------------------------------------
    private static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(5);
    private const double StallMs = 250;          // a freeze a person notices
    private const double SlowOpMs = 200;         // an operation worth naming on its own
    private const int TopOps = 6;

    // ---- operation timing -------------------------------------------------
    private sealed class OpStats
    {
        public long Count, Total, Max;           // this sampling window
        public long SCount, STotal, SMax;        // whole session
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, OpStats> Ops = new(StringComparer.Ordinal);
    private static string _lastLongName = "";
    private static long _lastLongTicks, _lastLongEnd;
    private static long _lastSlowLog;

    /// <summary>Times the enclosing block: <c>using var _ = PerfMonitor.Measure("X");</c>.
    /// A struct, so the using statement allocates nothing.</summary>
    public static Scope Measure(string name) => new(name);

    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;
        private readonly int _thread;
        internal Scope(string name)
        {
            _name = name; _start = Stopwatch.GetTimestamp();
            _thread = Environment.CurrentManagedThreadId;
        }
        public void Dispose() { if (_name != null) End(_name, _start, _thread); }
    }

    /// <summary>The UI thread, recorded by Start(). Only work done on it can
    /// explain a UI freeze.</summary>
    private static int _uiThread = -1;

    private static void End(string name, long start, int thread)
    {
        long now = Stopwatch.GetTimestamp();
        long d = now - start;
        bool logSlow = false;
        bool onUi = thread == _uiThread;
        lock (Gate)
        {
            if (!Ops.TryGetValue(name, out var o)) Ops[name] = o = new OpStats();
            o.Count++; o.Total += d; if (d > o.Max) o.Max = d;
            o.SCount++; o.STotal += d; if (d > o.SMax) o.SMax = d;

            // Only UI-thread work can be a freeze's cause. Background work - the
            // startup reconcile, the purge, warming caches - runs in parallel with
            // the UI thread, and crediting a freeze to it would send an
            // optimisation after code that never blocked anything.
            if (onUi && Ms(d) >= 50) { _lastLongName = name; _lastLongTicks = d; _lastLongEnd = now; }
            if (Ms(d) >= SlowOpMs && Ms(now - _lastSlowLog) >= 5000) { _lastSlowLog = now; logSlow = true; }
        }
        if (logSlow)
            DiagnosticLog.Log($"Perf: slow operation {name} took {Ms(d):F0} ms"
                            + (onUi ? " on the UI thread." : " in the background."));
    }

    // ---- UI responsiveness ------------------------------------------------
    // Buckets in ms: <16, <33, <50, <100, <250, <500, <1000, >=1000
    private static readonly double[] LagEdges = { 16, 33, 50, 100, 250, 500, 1000 };
    private static readonly long[] LagHist = new long[8], SLagHist = new long[8];
    private static double _lagMax, _sLagMax;
    private static long _stalls, _sStalls, _lastStallLog, _stallsUnlogged;

    // Hidden time, kept apart. The first 24-hour bundle mixed the two: overnight,
    // with the window hidden and every measured operation under a millisecond,
    // waits sat at 16-50 ms - multiples of the 15.6 ms timer tick, the signature
    // of Windows throttling a background process - and 132 unattributed
    // "freezes" were logged. Mixed in, they read as a responsiveness problem the
    // user never experiences.
    private static double _hidMax, _sHidMax;
    private static long _hidProbes, _sHidProbes, _hidStalls, _sHidStalls;

    /// <summary>
    /// Records how long the UI thread took to run a probe posted to it, and
    /// whether the window was on screen. Responsiveness figures are for visible
    /// time only - that is what a person feels. Hidden waits are counted
    /// separately, and a hidden freeze is logged only when a measured operation
    /// caused it, since that is real work rather than the system idling a
    /// background process.
    /// </summary>
    public static void RecordUiLag(double ms, bool onScreen)
    {
        string? line = null;
        lock (Gate)
        {
            long now = Stopwatch.GetTimestamp();
            bool attributed = ms >= StallMs && _lastLongTicks > 0
                && Ms(now - _lastLongEnd) <= ms + 100
                && Ms(_lastLongTicks) >= ms * 0.5;

            if (!onScreen)
            {
                _hidProbes++; _sHidProbes++;
                if (ms > _hidMax) _hidMax = ms;
                if (ms > _sHidMax) _sHidMax = ms;
                if (ms < StallMs) return;
                _hidStalls++; _sHidStalls++;
                if (!attributed || Ms(now - _lastStallLog) < 5000) return;
                _lastStallLog = now;
                line = $"Perf: UI froze for {ms:F0} ms while hidden - {_lastLongName} took "
                     + $"{Ms(_lastLongTicks):F0} ms.";
            }
            else
            {
                int b = 0;
                while (b < LagEdges.Length && ms >= LagEdges[b]) b++;
                LagHist[b]++; SLagHist[b]++;
                if (ms > _lagMax) _lagMax = ms;
                if (ms > _sLagMax) _sLagMax = ms;
                if (ms < StallMs) return;

                _stalls++; _sStalls++;
                if (Ms(now - _lastStallLog) < 5000) { _stallsUnlogged++; return; }
                _lastStallLog = now;
                string extra = _stallsUnlogged > 0 ? $" ({_stallsUnlogged} more since the last report)" : "";
                _stallsUnlogged = 0;
                line = attributed
                    ? $"Perf: UI froze for {ms:F0} ms - {_lastLongName} took {Ms(_lastLongTicks):F0} ms.{extra}"
                    : $"Perf: UI froze for {ms:F0} ms - unattributed: no measured operation explains it, "
                      + $"so the cause is in code not yet measured.{extra}";
            }
        }
        if (line != null) DiagnosticLog.Log(line);
    }

    // ---- frames -------------------------------------------------------------
    private static long _frames, _sFrames;

    /// <summary>One per rendered frame of an animation GunWall drives itself.</summary>
    public static void RecordFrame() { Interlocked.Increment(ref _frames); Interlocked.Increment(ref _sFrames); }

    // ---- startup --------------------------------------------------------------
    private static readonly List<(string Phase, double Ms)> Startup = new();
    private static bool _startupLogged;

    private static DateTime? _processStart;
    private const int MaxStartupMarks = 16;

    /// <summary>
    /// Marks a startup phase, in milliseconds since the process started.
    ///
    /// Bounded, and allocation-free after the first call. A mark placed where it
    /// ran once a second instead of once - which happened - must not become a
    /// list that grows for the life of the process, or an undisposed Process
    /// object per call.
    /// </summary>
    public static void MarkStartup(string phase)
    {
        if (_processStart is null)
        {
            try { using var p = Process.GetCurrentProcess(); _processStart = p.StartTime; }
            catch { return; }
        }
        double ms = (DateTime.Now - _processStart.Value).TotalMilliseconds;
        lock (Gate)
        {
            if (_startupLogged || Startup.Count >= MaxStartupMarks) return;
            Startup.Add((phase, ms));
        }
    }

    /// <summary>Writes the startup phases as one line, once.</summary>
    public static void LogStartup()
    {
        string line;
        lock (Gate)
        {
            if (_startupLogged || Startup.Count == 0) return;
            _startupLogged = true;
            line = "Perf: startup " + string.Join(", ", Startup.Select(p => $"{p.Phase} {p.Ms:F0} ms"))
                 + " (from process start).";
        }
        DiagnosticLog.Log(line);
    }

    // ---- periodic sampling ----------------------------------------------------
    private static System.Threading.Timer? _timer;   // qualified, as elsewhere in the tree
    private static TimeSpan _lastCpu, _lastPause;
    private static DateTime _lastSampleAt;
    private static readonly int[] LastGc = new int[3];
    private static readonly DateTime StartedAt = DateTime.UtcNow;
    private static TimeSpan _cpuAtStart;

    /// <summary>Starts the five-minute summary. Safe to call more than once.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_timer != null) return;
            _uiThread = Environment.CurrentManagedThreadId;   // Start() is called on the UI thread
            using (var p = Process.GetCurrentProcess()) { _lastCpu = _cpuAtStart = p.TotalProcessorTime; }
            _lastPause = GC.GetTotalPauseDuration();
            _lastSampleAt = DateTime.UtcNow;
            for (int g = 0; g < 3; g++) LastGc[g] = GC.CollectionCount(g);
            _timer = new System.Threading.Timer(_ => { try { DiagnosticLog.Log(SampleLine()); } catch { } },
                               null, SampleEvery, SampleEvery);
        }
    }

    /// <summary>Builds one summary line and resets the window it describes.</summary>
    public static string SampleLine()
    {
        using var p = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        double wall = Math.Max(1, (now - _lastSampleAt).TotalMilliseconds);
        var cpu = p.TotalProcessorTime;
        double cpuPct = (cpu - _lastCpu).TotalMilliseconds / (wall * Environment.ProcessorCount) * 100;
        var pause = GC.GetTotalPauseDuration();
        double pausePct = (pause - _lastPause).TotalMilliseconds / wall * 100;
        var gcInfo = GC.GetGCMemoryInfo();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long frames = Interlocked.Exchange(ref _frames, 0);

        string ops, lag;
        lock (Gate)
        {
            ops = FormatOps(window: true);
            lag = FormatLag(LagHist, _lagMax);
            long stalls = _stalls;
            lag += stalls > 0 ? $", freezes {stalls}" : "";
            lag += $"] hidden[{_hidProbes} probes, max {_hidMax:F0} ms, freezes {_hidStalls}";
            Array.Clear(LagHist); _lagMax = 0; _stalls = 0;
            _hidProbes = 0; _hidMax = 0; _hidStalls = 0;
            foreach (var o in Ops.Values) { o.Count = 0; o.Total = 0; o.Max = 0; }
        }

        string line =
            $"Perf: taskmgr={PrivateWs()} ws={Mb(p.WorkingSet64)} private={Mb(p.PrivateMemorySize64)} " +
            $"heap={Mb(GC.GetTotalMemory(false))} committed={Mb(gcInfo.TotalCommittedBytes)} " +
            $"gc0/1/2=+{g0 - LastGc[0]}/+{g1 - LastGc[1]}/+{g2 - LastGc[2]} gcPause={pausePct:F2}% " +
            // A percentage over a near-empty interval is noise: two samples taken
            // milliseconds apart once reported 117.9%.
            $"cpu={(wall >= 1000 ? $"{cpuPct:F1}%" : "n/a")} threads={p.Threads.Count} handles={p.HandleCount} " +
            $"ui[{lag}] frames={frames / (wall / 1000):F0}/s | {ops}";

        _lastCpu = cpu; _lastPause = pause; _lastSampleAt = now;
        LastGc[0] = g0; LastGc[1] = g1; LastGc[2] = g2;
        return line;
    }

    /// <summary>The whole session, for the closing block of a diagnostics export.</summary>
    public static IEnumerable<string> SessionSummaryLines()
    {
        using var p = Process.GetCurrentProcess();
        var up = DateTime.UtcNow - StartedAt;
        double cpuPct = (p.TotalProcessorTime - _cpuAtStart).TotalMilliseconds
                      / Math.Max(1, up.TotalMilliseconds * Environment.ProcessorCount) * 100;
        var pause = GC.GetTotalPauseDuration();
        var gcInfo = GC.GetGCMemoryInfo();

        yield return $"Performance: uptime {up.TotalHours:F1} h, cpu avg {cpuPct:F2}%, " +
                     $"Task Manager {PrivateWs()}, ws {Mb(p.WorkingSet64)} (peak {Mb(p.PeakWorkingSet64)}), private {Mb(p.PrivateMemorySize64)}, " +
                     $"heap {Mb(GC.GetTotalMemory(false))}, committed {Mb(gcInfo.TotalCommittedBytes)}, " +
                     $"threads {p.Threads.Count}, handles {p.HandleCount}";
        yield return $"Performance: gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} " +
                     $"(gen0/1/2), paused {pause.TotalMilliseconds:F0} ms = " +
                     $"{pause.TotalMilliseconds / Math.Max(1, up.TotalMilliseconds) * 100:F3}% of uptime, " +
                     $"graph frames {Interlocked.Read(ref _sFrames):N0}";
        string ops, lag;
        List<(string, double)> startup;
        lock (Gate)
        {
            lag = FormatLag(SLagHist, _sLagMax) + $", freezes {_sStalls}"
                + $"] hidden[{_sHidProbes} probes, max {_sHidMax:F0} ms, freezes {_sHidStalls}";
            ops = FormatOps(window: false);
            startup = Startup.ToList();
        }
        yield return $"Performance: ui responsiveness while visible [{lag}]";
        yield return $"Performance: costliest operations | {ops}";
        if (startup.Count > 0)
            yield return "Performance: startup " + string.Join(", ", startup.Select(s => $"{s.Item1} {s.Item2:F0} ms"));
    }

    // ---- formatting -------------------------------------------------------------
    private static string FormatOps(bool window)
    {
        var rows = Ops.Select(kv => (Name: kv.Key,
                                     Count: window ? kv.Value.Count : kv.Value.SCount,
                                     Total: window ? kv.Value.Total : kv.Value.STotal,
                                     Max: window ? kv.Value.Max : kv.Value.SMax))
                      .Where(r => r.Count > 0)
                      .OrderByDescending(r => r.Total)
                      .Take(TopOps)
                      .Select(r => $"{r.Name} {r.Count}x avg {Ms(r.Total) / r.Count:F1} max {Ms(r.Max):F0} ms");
        string s = string.Join("; ", rows);
        return s.Length > 0 ? s : "no measured work";
    }

    private static string FormatLag(long[] hist, double max)
    {
        long n = hist.Sum();
        if (n == 0) return "no samples";
        static string Edge(int i) => i < LagEdges.Length ? $"<{LagEdges[i]:F0}" : ">=1000";
        long seen = 0; int p50 = -1, p95 = -1;
        for (int i = 0; i < hist.Length; i++)
        {
            seen += hist[i];
            if (p50 < 0 && seen >= n * 0.50) p50 = i;
            if (p95 < 0 && seen >= n * 0.95) { p95 = i; break; }
        }
        return $"p50 {Edge(p50)} ms, p95 {Edge(p95)} ms, max {max:F0} ms, {n} probes";
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    // ---- private working set ------------------------------------------------
    // Task Manager's Memory column is the private working set. The log showed only
    // the full working set, which counts shared Windows, .NET and graphics-driver
    // pages: ~480 MB beside Task Manager's 141 MB in the first 0.99.153 bundle, and
    // two numbers that could not be compared. EX2 adds PrivateWorkingSetSize.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemCountersEx2
    {
        public uint cb, PageFaultCount;
        public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                       QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
                       PrivateUsage, PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemCountersEx2 counters, uint cb);

    /// <summary>Task Manager's Memory figure, or "n/a" where Windows cannot give it.</summary>
    private static string PrivateWs()
    {
        if (!OperatingSystem.IsWindows()) return "n/a";
        try
        {
            var c = new MemCountersEx2 { cb = (uint)Marshal.SizeOf<MemCountersEx2>() };
            if (!GetProcessMemoryInfo((IntPtr)(-1), ref c, c.cb)) return "n/a";   // -1: this process
            ulong v = (ulong)c.PrivateWorkingSetSize;
            return v == 0 ? "n/a" : Mb((long)v);
        }
        catch { return "n/a"; }
    }
    private static string Mb(long bytes) => $"{bytes / 1048576.0:F0}MB";
}
