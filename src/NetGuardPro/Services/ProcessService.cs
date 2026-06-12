using System.Diagnostics;
using NetGuardPro.Models;

namespace NetGuardPro.Services;

/// <summary>
/// Enumerates running processes and resolves PIDs to executable paths.
/// Used to build the application list and to attribute live connections.
/// </summary>
public sealed class ProcessService
{
    /// <summary>
    /// Builds a PID -> (name, path) map for all currently running processes.
    /// Paths for some protected/system processes may be unavailable; those are
    /// returned with an empty path.
    /// </summary>
    public Dictionary<int, (string Name, string Path)> SnapshotProcesses()
    {
        var map = new Dictionary<int, (string, string)>();
        foreach (var p in Process.GetProcesses())
        {
            string name = SafeProcessName(p);
            string path = SafeMainModulePath(p);
            map[p.Id] = (name, path);
            p.Dispose();
        }
        return map;
    }

    /// <summary>
    /// Produces the distinct list of applications that currently own at least
    /// one network connection, merged with their executable paths.
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
