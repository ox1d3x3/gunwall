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
    // Capped so a failure loop can't grow it without bound.
    private static readonly List<string> _recentErrors = new();
    private const int MaxRecentErrors = 300;

    /// <summary>Snapshot of this session's captured errors, newest first.</summary>
    public static string[] RecentErrors() { lock (_lock) return _recentErrors.ToArray(); }

    public static void ClearRecentErrors() { lock (_lock) _recentErrors.Clear(); }

    public static void LogException(string context, Exception ex)
    {
        lock (_lock)
        {
            _recentErrors.Insert(0,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {context}: {ex.GetType().Name}: {ex.Message}" +
                (ex.InnerException != null ? $" (inner: {ex.InnerException.Message})" : ""));
            if (_recentErrors.Count > MaxRecentErrors)
                _recentErrors.RemoveAt(_recentErrors.Count - 1);
        }
        Log($"[EXCEPTION] {context}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        if (ex.InnerException != null)
            Log($"  inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
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
