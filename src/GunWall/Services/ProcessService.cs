using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Resolves PIDs to process names and executable paths.
///
/// Path resolution uses QueryFullProcessImageName with PROCESS_QUERY_LIMITED_
/// INFORMATION rather than Process.MainModule. MainModule throws Access Denied
/// for any process at higher integrity or owned by another user (services
/// running as SYSTEM, security software, VPN helpers, etc.), which would cause
/// those apps to silently vanish from detection. The limited-information query
/// succeeds for the large majority of processes, so far more apps are seen.
///
/// Results are cached per PID (path resolution is the most expensive call), and
/// dead PIDs are evicted each snapshot so recycled IDs never show stale names.
/// </summary>
public sealed class ProcessService
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        IntPtr hProcess, uint flags, StringBuilder buffer, ref uint size);

    private readonly Dictionary<int, (string Name, string Path)> _cache = new();
    private readonly object _gate = new();

    /// <summary>Terminates a process by PID. Returns false if it can't be ended.</summary>
    public static bool KillProcess(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.Kill();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public Dictionary<int, (string Name, string Path)> SnapshotProcesses()
    {
        // Called concurrently by the sampling loop and the (faster) detection loop.
        // Dictionary is not thread-safe: unsynchronized concurrent writes corrupt its
        // internal buckets and throw IndexOutOfRangeException from deep inside - the
        // exact failure the diagnostics captured. One lock serializes the two loops.
        lock (_gate)
        {
            var live = new HashSet<int>();
            foreach (var p in Process.GetProcesses())
            {
                live.Add(p.Id);
                if (!_cache.ContainsKey(p.Id))
                    _cache[p.Id] = (SafeProcessName(p), ResolvePath(p.Id));
                p.Dispose();
            }

            var dead = _cache.Keys.Where(pid => !live.Contains(pid)).ToList();
            foreach (var pid in dead) _cache.Remove(pid);

            return new Dictionary<int, (string, string)>(_cache);
        }
    }

    /// <summary>
    /// Robust path resolution. Tries the limited-information query first (works
    /// for most processes), then falls back to MainModule for the rest.
    /// </summary>
    private static string ResolvePath(int pid)
    {
        if (pid <= 0) return "";
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (QueryFullProcessImageNameW(h, 0, sb, ref size))
                    return sb.ToString(0, (int)size);
            }
            finally { CloseHandle(h); }
        }

        // Fallback for the few cases the query can't open.
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "";
        }
        catch { return ""; }
    }

    public List<AppInfo> GetNetworkedApps(IEnumerable<ConnectionInfo> connections,
                                          Dictionary<int, (string Name, string Path)> processes)
    {
        var byPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            if (!processes.TryGetValue(c.ProcessId, out var proc)) continue;
            if (string.IsNullOrEmpty(proc.Path)) continue;
            if (!byPath.TryGetValue(proc.Path, out var app))
            {
                app = new AppInfo { Name = proc.Name, ExecutablePath = proc.Path };
                byPath[proc.Path] = app;
            }
            app.ActiveConnections++;
        }
        return Sort(byPath.Values);
    }

    public List<AppInfo> GetAllApps(IEnumerable<ConnectionInfo> connections,
                                    Dictionary<int, (string Name, string Path)> processes)
    {
        var counts = new Dictionary<int, int>();
        foreach (var c in connections)
            counts[c.ProcessId] = counts.TryGetValue(c.ProcessId, out var n) ? n + 1 : 1;

        var byPath = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (pid, proc) in processes)
        {
            if (string.IsNullOrEmpty(proc.Path)) continue;
            if (!byPath.TryGetValue(proc.Path, out var app))
            {
                app = new AppInfo { Name = proc.Name, ExecutablePath = proc.Path };
                byPath[proc.Path] = app;
            }
            if (counts.TryGetValue(pid, out var n)) app.ActiveConnections += n;
        }
        return Sort(byPath.Values);
    }

    private static List<AppInfo> Sort(IEnumerable<AppInfo> apps) =>
        apps.OrderByDescending(a => a.ActiveConnections)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SafeProcessName(Process p)
    {
        try { return p.ProcessName; }
        catch { return $"PID {p.Id}"; }
    }
}
