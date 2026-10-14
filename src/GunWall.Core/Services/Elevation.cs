using System.ComponentModel;
using System.Diagnostics;

namespace GunWall.Services;

/// <summary>
/// Administrator rights, asked for only when needed (stage 4c, 0.99.229).
///
/// Until 0.99.228 GunWall.exe asked for them every time it opened (manifest
/// requireAdministrator), because the window could run the firewall itself. Now the
/// installed window uses the background service and opens as the signed-in user;
/// what still needs an administrator asks Windows for it at that moment, through
/// GunWall.exe started again with a task to do:
///   - running the firewall in the window (portable copy, or the service off),
///   - the recovery commands (--unblock, --purge-sublayer),
///   - starting the service, turning it or "window uses the service" on or off,
///   - unlocking Protect settings (--authorize).
/// </summary>
public static class Elevation
{
    /// <summary>Win32 ERROR_CANCELLED: the person said No to UAC.</summary>
    public const int Cancelled = 1223;

    /// <summary>This process runs with an administrator token (elevated). False for
    /// a standard user and for an administrator's normal, filtered token.</summary>
    public static bool IsElevated
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    /// <summary>Runs this program again, elevated (UAC), with <paramref name="args"/>.
    /// Waits up to <paramref name="wait"/> for it to finish when given. Returns its
    /// exit code, <see cref="Cancelled"/> when UAC was declined, or -1 when it could not
    /// be started or did not finish in time.</summary>
    public static int RunSelfElevated(string args, TimeSpan? wait)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas" });
            if (p == null) return -1;
            if (wait == null) return 0;
            if (!p.WaitForExit((int)wait.Value.TotalMilliseconds)) return -1;
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled) { return Cancelled; }
        catch (Exception ex)
        {
            DiagnosticLog.Log($"Elevation: could not start GunWall as administrator ({ex.GetType().Name}: {ex.Message}).");
            return -1;
        }
    }

    /// <summary>Quotes one argument for a command line.</summary>
    public static string Quote(string arg) =>
        arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? arg : "\"" + arg.Replace("\"", "\\\"") + "\"";

    /// <summary>The tasks GunWall.exe runs when started with --elevated (always as
    /// administrator, always headless). A fixed list: the argument picks one, it never
    /// names a command or a path.</summary>
    public static readonly string[] Tasks = { "start-service", "service-on", "service-off", "uses-service-on", "uses-service-off" };
}
