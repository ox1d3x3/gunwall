using System.IO;

namespace GunWall.Services;

/// <summary>
/// "The window uses the background service" (service split stage 4b). With it on,
/// the service keeps the engine while the window is open and answers it over the
/// engine pipe; the window no longer takes the engine over.
///
/// On by default since 0.99.224 (experimental and opt-in in 0.99.208-223). It
/// applies only where it can work: an installed copy whose background service is
/// installed and set to start with Windows. A portable copy, or one whose service
/// was turned off, never asks - asking a service that is not there costs the
/// window its start-up wait. Turning it off in Settings writes an opt-out file in
/// the data folder, which the service, the window and the installer all see.
/// </summary>
public static class ServiceMode
{
    /// <summary>Written when the user turns the option off (0.99.224).</summary>
    public const string OptOutMarkerName = "window-own-engine.marker";

    /// <summary>0.99.208-223 opt-in file: meaningless now that on is the default;
    /// removed when seen.</summary>
    public const string LegacyOptInMarkerName = "window-uses-service.marker";

    public static string OptOutPath => ProfilePaths.FileIn(OptOutMarkerName);

    /// <summary>The user has not turned it off (what the Settings box shows).</summary>
    public static bool Preferred
    {
        get { try { return !File.Exists(OptOutPath); } catch { return true; } }
    }

    /// <summary>Whether this copy should use the service now.</summary>
    public static bool Requested => Decide(ProfilePaths.IsPortable, !Preferred, ServiceStartsWithWindows());

    /// <summary>The rule, without the machine (bench-tested).</summary>
    public static bool Decide(bool portable, bool optedOut, bool serviceAutoStart) =>
        !portable && !optedOut && serviceAutoStart;

    /// <summary>A window that wants the service got no answer (0.99.225): is the
    /// service just not up yet? Seen on a domain laptop: the window started at
    /// sign-in 20 s after boot, the service 60 s after boot, so the window took the
    /// engine itself and kept it. Starting (2) or running (4): wait for it. Stopped
    /// (1) in the first five minutes after Windows started: start it and wait.
    /// Anything else: do not wait - the window runs the engine.</summary>
    public static (bool Wait, bool StartIt) ShouldWaitForService(int scState, long uptimeMs) => scState switch
    {
        2 or 4 => (true, false),
        1 when uptimeMs < BootWindowMs => (true, true),
        _ => (false, false),
    };

    public const long BootWindowMs = 5 * 60 * 1000;
    public static readonly TimeSpan StartWait = TimeSpan.FromSeconds(90);

    /// <summary>The service is registered and set to start with Windows - read from
    /// the registry: instant, unlike asking sc.exe, and it cannot hang.</summary>
    public static bool ServiceStartsWithWindows()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{BackgroundServiceControl.ServiceName}");
            return key?.GetValue("Start") is int start && start == 2;   // 2 = automatic
        }
        catch { return false; }
    }

    /// <summary>Settings: on (the default) removes the opt-out file, off writes it.
    /// Also clears the old opt-in file.</summary>
    public static void Set(bool on)
    {
        try
        {
            string legacy = ProfilePaths.FileIn(LegacyOptInMarkerName);
            if (File.Exists(legacy)) File.Delete(legacy);
            if (on) { if (File.Exists(OptOutPath)) File.Delete(OptOutPath); }
            else File.WriteAllText(OptOutPath, "Written by GunWall's Settings: this window runs the firewall itself instead "
                                             + "of using the background service. Takes effect when GunWall next starts. "
                                             + "Delete to go back to the default.");
        }
        catch (Exception ex) { DiagnosticLog.LogException("ServiceMode.Set", ex); }
    }

    /// <summary>The old opt-in file, from a build that had the option off by
    /// default: remove it (once, at start-up).</summary>
    public static void RemoveLegacyMarker()
    {
        try
        {
            string legacy = ProfilePaths.FileIn(LegacyOptInMarkerName);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch { /* harmless if it stays */ }
    }
}
