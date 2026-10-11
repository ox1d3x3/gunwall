using System.IO;

namespace GunWall.Services;

/// <summary>
/// "The window uses the background service" (service split stage 4b, 0.99.208;
/// experimental, off by default). With it on, the service keeps the engine while
/// the window is open and answers it over the engine pipe; the window no longer
/// takes the engine over. A file in the data folder, so the service, the window
/// and the installer all read the same switch without a profile parser.
/// </summary>
public static class ServiceMode
{
    public const string MarkerName = "window-uses-service.marker";

    public static string MarkerPath => ProfilePaths.FileIn(MarkerName);

    public static bool Requested
    {
        get { try { return File.Exists(MarkerPath); } catch { return false; } }
    }

    public static void Set(bool on)
    {
        try
        {
            if (on) File.WriteAllText(MarkerPath, "Written by GunWall's Settings: the window uses the background service. "
                                                + "Takes effect when GunWall next starts. Delete to turn off.");
            else if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        }
        catch (Exception ex) { DiagnosticLog.LogException("ServiceMode.Set", ex); }
    }
}
