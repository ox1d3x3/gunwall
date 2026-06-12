using System.Diagnostics;
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Resolves PIDs to process names and executable paths.
///
/// Performance design: querying MainModule opens a process handle and is the
/// single most expensive call in the app, so results are CACHED per PID and
/// only resolved once. Dead PIDs are evicted each snapshot. With the cache,
/// a steady-state snapshot costs almost nothing.
/// </summary>
public sealed class ProcessService
{
    private readonly Dictionary<int, (string Name, string Path)> _cache = new();

    /// <summary>
    /// Returns a PID -> (name, path) map for all running processes, resolving
    /// only PIDs not seen before. Safe to call from a background thread.
    /// </summary>
    public Dictionary<int, (string Name, string Path)> SnapshotProcesses()
    {
        var live = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            live.Add(p.Id);
            if (!_cache.ContainsKey(p.Id))
            {
                _cache[p.Id] = (SafeProcessName(p), SafeMainModulePath(p));
            }
            p.Dispose();
        }

        // Evict dead PIDs so recycled IDs don't show stale names.
        var dead = _cache.Keys.Where(pid => !live.Contains(pid)).ToList();
        foreach (var pid in dead) _cache.Remove(pid);

        return new Dictionary<int, (string, string)>(_cache);
    }

    /// <summary>
    /// Distinct applications that currently own at least one connection.
    /// </summary>
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
                app = new AppInfo
                {
                    Name = proc.Name,
                    ExecutablePath = proc.Path,
                    Status = AppStatus.Allowed
                };
                byPath[proc.Path] = app;
            }
            app.ActiveConnections++;
        }

        return byPath.Values
                     .OrderByDescending(a => a.ActiveConnections)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    /// <summary>
    /// Every running app with a resolvable path (for "show all apps" mode,
    /// like simplewall's full app list), with connection counts merged in.
    /// </summary>
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

        return byPath.Values
                     .OrderByDescending(a => a.ActiveConnections)
                     .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                     .ToList();
    }

    private static string SafeProcessName(Process p)
    {
        try { return p.ProcessName; }
        catch { return $"PID {p.Id}"; }
    }

    private static string SafeMainModulePath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; }
        catch { return ""; } // Access denied for protected processes — expected.
    }
}
