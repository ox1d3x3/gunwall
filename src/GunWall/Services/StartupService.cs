using System.Diagnostics;

namespace GunWall.Services;

/// <summary>
/// Manages "run when Windows starts". Because GunWall runs elevated, a plain
/// Run-key entry would pop a UAC prompt at every boot. Instead we register a
/// Scheduled Task set to run with highest privileges at logon, which starts the
/// app silently and elevated. All operations shell out to schtasks.exe and are
/// best-effort — failures are reported, never thrown into the UI as crashes.
/// </summary>
public static class StartupService
{
    private const string TaskName = "GunWallAutoStart";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(4000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Creates or removes the logon task. Returns true on success.</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            return enabled ? Create() : Remove();
        }
        catch { return false; }
    }

    private static bool Create()
    {
        string exe = Environment.ProcessPath ?? "";
        if (string.IsNullOrEmpty(exe)) return false;

        // /rl HIGHEST = run with highest privileges (no UAC at boot)
        // /sc ONLOGON  = trigger at user logon
        // /f           = overwrite if it exists
        string args = $"/create /tn \"{TaskName}\" /tr \"\\\"{exe}\\\"\" " +
                      "/sc ONLOGON /rl HIGHEST /f";
        return RunSchtasks(args);
    }

    private static bool Remove()
        => RunSchtasks($"/delete /tn \"{TaskName}\" /f");

    private static bool RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.WaitForExit(6000);
        return p.ExitCode == 0;
    }
}
