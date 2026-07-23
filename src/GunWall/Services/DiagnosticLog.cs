using System.IO;

namespace GunWall.Services;

/// <summary>
/// Lightweight always-on diagnostic logger. Appends timestamped lines to
/// diagnostics.log in the data folder (with one rotation), and captures
/// exceptions. The diagnostics export bundles this file so an error report
/// carries the runtime history that led up to it. Auto-initialises to a sane
/// default path if Init() hasn't run yet, so very early crashes are still caught.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object _lock = new();
    private static string? _path;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string? LogPath => _path;
    public static string? PreviousLogPath => _path == null ? null : _path + ".1";

    /// <summary>Sets the proper data folder, rotates an oversized log, writes a session header.</summary>
    public static void Init(string dataFolder)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(dataFolder);
                _path = Path.Combine(dataFolder, "diagnostics.log");
                RotateIfLarge();
            }
            catch { }
        }
        Log("================ GunWall session started ================");
    }

    public static void Log(string message)
    {
        lock (_lock)
        {
            if (_path == null) TryInitDefault();
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
                if (_path != null) File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { }
        }
    }

    // This session's captured errors, newest first, for the Settings viewer.
    // Entries are DEDUPLICATED by their signature (context + type + message):
    // a repeating fault - a torn-down socket aborting fifty pooled connections,
    // say - collapses into one entry with a count, so a single noisy event can
    // never crowd genuine errors out of the buffer.
    private sealed class ErrorEntry
    {
        public string Signature = "";
        public string Text = "";
        public DateTime First;
        public DateTime Last;
        public int Count = 1;

        public string Render() => Count == 1
            ? $"{First:yyyy-MM-dd HH:mm:ss}  {Text}"
            : $"{First:yyyy-MM-dd HH:mm:ss}  {Text}   [x{Count}, last {Last:HH:mm:ss}]";
    }

    private static readonly List<ErrorEntry> _recentErrors = new();
    private const int MaxRecentErrors = 300;

    /// <summary>Snapshot of this session's captured errors, newest first,
    /// with repeats collapsed.</summary>
    public static string[] RecentErrors()
    {
        lock (_lock)
        {
            var outp = new string[_recentErrors.Count];
            for (int i = 0; i < _recentErrors.Count; i++) outp[i] = _recentErrors[i].Render();
            return outp;
        }
    }

    /// <summary>Distinct errors captured this session (repeats counted once).</summary>
    public static int DistinctErrorCount { get { lock (_lock) return _recentErrors.Count; } }

    /// <summary>Total error events captured this session, including repeats.</summary>
    public static int TotalErrorCount
    {
        get { lock (_lock) { int n = 0; foreach (var e in _recentErrors) n += e.Count; return n; } }
    }

    public static void ClearRecentErrors() { lock (_lock) _recentErrors.Clear(); }

    /// <summary>Records one error in the deduplicating buffer. Returns true if
    /// this signature is new this session - callers use that to write the full
    /// detail to the log file only once instead of on every repeat.</summary>
    private static bool RecordError(string context, Exception ex)
    {
        string sig = $"{context}|{ex.GetType().Name}|{ex.Message}";
        string text = $"{context}: {ex.GetType().Name}: {ex.Message}" +
                      (ex.InnerException != null ? $" (inner: {ex.InnerException.Message})" : "");
        var now = DateTime.Now;
        lock (_lock)
        {
            for (int i = 0; i < _recentErrors.Count; i++)
            {
                if (_recentErrors[i].Signature != sig) continue;
                var hit = _recentErrors[i];
                hit.Count++;
                hit.Last = now;
                _recentErrors.RemoveAt(i);      // move to the front: newest activity first
                _recentErrors.Insert(0, hit);
                return false;
            }
            _recentErrors.Insert(0, new ErrorEntry
            { Signature = sig, Text = text, First = now, Last = now });
            if (_recentErrors.Count > MaxRecentErrors)
                _recentErrors.RemoveAt(_recentErrors.Count - 1);
            return true;
        }
    }

    // Benign, expected faults (aborted sockets during teardown) are counted
    // rather than logged individually - visible in diagnostics without noise.
    private static readonly Dictionary<string, int> _benignFaults = new();

    /// <summary>Records an expected, non-error fault by category. The first
    /// occurrence is logged; the rest are counted for the diagnostics export.</summary>
    public static void NoteBenignFault(string category)
    {
        bool first;
        lock (_lock)
        {
            _benignFaults.TryGetValue(category, out int n);
            _benignFaults[category] = n + 1;
            first = n == 0;
        }
        if (first) Log($"Benign fault (expected, will be counted not logged): {category}");
    }

    /// <summary>Benign fault tallies for the diagnostics export, e.g. "network teardown (aborted socket) x118".</summary>
    public static string BenignFaultSummary()
    {
        lock (_lock)
        {
            if (_benignFaults.Count == 0) return "none";
            var parts = new List<string>(_benignFaults.Count);
            foreach (var kv in _benignFaults) parts.Add($"{kv.Key} x{kv.Value}");
            return string.Join(", ", parts);
        }
    }

    public static void LogException(string context, Exception ex)
    {
        bool isNew = RecordError(context, ex);

        // Full detail (with stack trace) goes to the log file only the first
        // time a given fault is seen. Repeats get a single terse line, so one
        // recurring error can't bury the rest of the log under stack traces.
        if (isNew)
        {
            Log($"[EXCEPTION] {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
            if (ex.InnerException != null)
                Log($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
        else
        {
            int n;
            lock (_lock)
            {
                n = 0;
                string sig = $"{context}|{ex.GetType().Name}|{ex.Message}";
                foreach (var e in _recentErrors) if (e.Signature == sig) { n = e.Count; break; }
            }
            // Log milestones only (2nd, 10th, 100th, ...) rather than every repeat.
            if (n == 2 || n == 10 || n == 100 || n == 1000)
                Log($"[EXCEPTION x{n}] {context}: {ex.GetType().Name}: {ex.Message}  (repeat; detail logged above)");
        }
    }

    private static void TryInitDefault()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "GunWallData");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, "diagnostics.log");
        }
        catch { }
    }

    private static void RotateIfLarge()
    {
        try
        {
            if (_path != null && File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                string older = _path + ".1";
                if (File.Exists(older)) File.Delete(older);
                File.Move(_path, older);
            }
        }
        catch { }
    }
}
