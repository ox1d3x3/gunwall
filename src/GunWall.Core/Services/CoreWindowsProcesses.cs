using System.IO;

namespace GunWall.Services;

/// <summary>
/// Windows' own core processes, which a zero-trust user is asked about after every
/// reset or fresh install but has no real choice over: blocking them breaks the
/// machine (0.99.195; reported - 14 popups in 3 seconds after Remove all,
/// including wininit, spoolsv and System).
///
/// Deliberately narrow. Only these exact files in System32, which only
/// administrators and Windows can write, so a program cannot qualify by copying
/// itself under one of these names elsewhere. svchost.exe is NOT here: it hosts
/// most Windows services, telemetry included, and allowing it is a decision the
/// user should make (per-service blocks still apply inside it).
/// </summary>
public static class CoreWindowsProcesses
{
    /// <summary>The kernel's own traffic is reported with this path, not a file.</summary>
    public const string SystemProcess = "System";

    public static readonly IReadOnlyList<string> Names = new[]
    {
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "spoolsv.exe",
    };

    public static bool IsCore(string? exePath, string? system32Dir)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        if (string.Equals(exePath, SystemProcess, StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(system32Dir)) return false;

        // Exact parent folder only: no subfolders, no relative tricks.
        string? dir;
        string name;
        try
        {
            if (exePath.Contains("..", StringComparison.Ordinal)) return false;
            int cut = exePath.LastIndexOfAny(new[] { '\\', '/' });
            if (cut <= 0) return false;
            dir = exePath[..cut];
            name = exePath[(cut + 1)..];
        }
        catch { return false; }

        string sys = system32Dir.TrimEnd('\\', '/');
        if (!string.Equals(dir.TrimEnd('\\', '/'), sys, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var n in Names)
            if (string.Equals(name, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
