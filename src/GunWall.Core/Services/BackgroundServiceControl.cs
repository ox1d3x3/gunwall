using System.Diagnostics;
using System.Text.RegularExpressions;

namespace GunWall.Services;

/// <summary>
/// The GunWall background service as Windows sees it (0.99.199): registered by
/// the installer, switched on and off from Settings. Shells out to sc.exe, as
/// StartupService does to schtasks.exe - no extra package.
///
/// Stopping always WAITS: sc stop returns at STOP_PENDING, and anything that then
/// kills GunWall.exe processes (--unblock, the installer) would kill the stopping
/// service - which Windows' recovery settings restart five seconds later, taking
/// the engine back and re-applying filters that were just removed.
/// </summary>
public static class BackgroundServiceControl
{
    public const string ServiceName = "GunWallService";

    /// <summary>The recovery actions the service normally has (installer and Enable).</summary>
    public const string RecoveryActions = "reset= 86400 actions= restart/5000/restart/30000/restart/60000";

    /// <summary>Exit code and output of one sc.exe call.</summary>
    public static (int Code, string Output) Sc(string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (-1, output); }
            return (p.ExitCode, output);
        }
        catch (Exception ex) { return (-2, ex.Message); }
    }

    /// <summary>STATE number (1 stopped, 3 stop pending, 4 running...) and PID from
    /// sc queryex, or (0, 0) when the service is not installed.</summary>
    public static (int State, int Pid) Query()
    {
        var (code, output) = Sc($"queryex {ServiceName}");
        if (code != 0) return (0, 0);
        return ParseQueryEx(output);
    }

    /// <summary>Parses sc queryex output (public for the bench).</summary>
    public static (int State, int Pid) ParseQueryEx(string output)
    {
        var st = Regex.Match(output, @"STATE\s*:\s*(\d+)");
        var pid = Regex.Match(output, @"PID\s*:\s*(\d+)");
        return (st.Success ? int.Parse(st.Groups[1].Value) : 0, pid.Success ? int.Parse(pid.Groups[1].Value) : 0);
    }

    public static bool IsInstalled() => Sc($"query {ServiceName}").Code == 0;

    public static bool IsRunning() => Query().State == 4;

    /// <summary>Installed and set to start with Windows - what the Settings box shows.
    /// Read from Windows, not stored in the profile, so it cannot disagree with
    /// what Windows will actually do.</summary>
    public static bool IsEnabled()
    {
        var (code, output) = Sc($"qc {ServiceName}");
        return code == 0 && output.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stops the service and waits until its process has exited.</summary>
    public static bool StopAndWait(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        var (state, pid) = Query();
        if (state == 0) return true;            // not installed
        if (state != 1) Sc($"stop {ServiceName}");
        while (DateTime.UtcNow < until)
        {
            var (s, p) = Query();
            if (p != 0) pid = p;
            if (s == 1 || s == 0) break;
            Thread.Sleep(250);
        }
        if (pid > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                var left = until - DateTime.UtcNow;
                proc.WaitForExit((int)Math.Max(0, left.TotalMilliseconds));
            }
            catch (Exception) { /* already gone, or not ours to wait on - the state check below decides */ }
        }
        var final = Query().State;
        return final == 1 || final == 0;
    }

    /// <summary>Starts with Windows and runs now (it waits for the window to close
    /// before it does anything). Restores the recovery actions a recovery may
    /// have cleared.</summary>
    public static string Enable()
    {
        if (!IsInstalled()) return "not installed - reinstall GunWall to add the background service";
        var cfg = Sc($"config {ServiceName} start= auto");
        Sc($"failure {ServiceName} {RecoveryActions}");
        Sc($"failureflag {ServiceName} 1");   // recovery also after a reported failure, not only a crash
        if (!IsRunning()) Sc($"start {ServiceName}");
        return cfg.Code == 0 ? "on" : $"failed (config {cfg.Code})";
    }

    /// <summary>Stopped (and waited for), and does not start with Windows.</summary>
    public static string Disable()
    {
        if (!IsInstalled()) return "not installed";
        bool stopped = StopAndWait(TimeSpan.FromSeconds(30));
        var cfg = Sc($"config {ServiceName} start= demand");
        return cfg.Code == 0 && stopped ? "off" : $"failed (stopped {stopped}, config {cfg.Code})";
    }

    /// <summary>For --unblock and the like: no recovery restart, no start with
    /// Windows, stopped and waited for. Settings turns it back on (Enable restores
    /// the recovery actions).</summary>
    public static string DisableForRecovery()
    {
        if (!IsInstalled()) return "not installed";
        Sc($"failure {ServiceName} reset= 0 actions= \"\"");
        return Disable();
    }

    /// <summary>The hand-over's last resort: stop it now, and wait.</summary>
    public static void Stop() => StopAndWait(TimeSpan.FromSeconds(30));
}
