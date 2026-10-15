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

    // ---- the Run key (stage 4c, 0.99.229) ----
    // The installed window opens without administrator rights, so this user's Run
    // key starts it at sign-in - no task, no UAC. The task (highest privileges) is
    // kept for a window that runs the firewall itself (portable, service off).
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "GunWall";

    /// <summary>A Run key starts GunWall at sign-in: this user's, or the machine's
    /// (the installer's "Start with Windows" task writes that one).</summary>
    public static bool IsRunKeySet() => HasValue(false) || HasValue(true);

    private static bool HasValue(bool machine)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = (machine ? Microsoft.Win32.Registry.LocalMachine : Microsoft.Win32.Registry.CurrentUser).OpenSubKey(RunKeyPath);
            return k?.GetValue(RunValue) is string v && v.Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Adds or removes GunWall in this user's Run key. True on success. The
    /// machine's entry (the installer's) is left alone: removing it needs an
    /// administrator, and the installer owns it.</summary>
    public static bool SetRunKey(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (k == null) return false;
            if (enabled)
            {
                if (HasValue(machine: true)) return true;   // the installer's entry starts it already: not twice
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return false;
                k.SetValue(RunValue, "\"" + exe + "\"");
            }
            else if (k.GetValue(RunValue) != null) k.DeleteValue(RunValue, throwOnMissingValue: false);
            return true;
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

    /// <summary>The elevated sign-in task, removed (needs an administrator: the
    /// service does it for the window, 0.99.229).</summary>
    public static bool RemoveTask() { try { return Remove(); } catch { return false; } }

    /// <summary>The scheduled task is there (the service can always see it).</summary>
    public static bool TaskExists() => IsEnabled();

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
