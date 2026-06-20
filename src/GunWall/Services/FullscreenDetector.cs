using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Detects whether a fullscreen app, game, or presentation is currently running,
/// so notification popups can be held back. Uses the same OS signal Windows itself
/// uses to decide whether to show toasts (SHQueryUserNotificationState). Best-effort:
/// if the query fails, we report "not fullscreen" so behaviour is unchanged.
/// </summary>
public static class FullscreenDetector
{
    // QUERY_USER_NOTIFICATION_STATE values.
    private const int QUNS_BUSY = 2;                  // a fullscreen app is running
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3; // a fullscreen (exclusive) game
    private const int QUNS_PRESENTATION_MODE = 4;      // presentation mode

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>True if a fullscreen app/game/presentation is foreground.</summary>
    public static bool IsFullscreenAppActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0) // S_OK
                return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
        }
        catch { /* unavailable -> treat as not fullscreen */ }
        return false;
    }
}
