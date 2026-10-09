using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Shell helpers that behave correctly when GunWall is running elevated.
/// Spawning "explorer.exe /select,..." from a high-integrity process is
/// unreliable (Explorer runs at medium integrity and the request is often
/// silently dropped), so we use the shell COM entry point and fall back to
/// opening the containing folder.
/// </summary>
public static class ShellHelper
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    /// <summary>Opens Explorer with <paramref name="filePath"/> selected.
    /// Returns true if something was shown.</summary>
    public static bool RevealInExplorer(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            DiagnosticLog.Log("RevealInExplorer: empty path.");
            return false;
        }
        if (!File.Exists(filePath))
        {
            DiagnosticLog.Log($"RevealInExplorer: file not found: {filePath}");
            return false;
        }

        // Preferred: shell COM "open folder and select item" (works elevated).
        try
        {
            IntPtr pidl = ILCreateFromPathW(filePath);
            if (pidl != IntPtr.Zero)
            {
                try
                {
                    int hr = SHOpenFolderAndSelectItems(pidl, 0, null, 0);
                    DiagnosticLog.Log($"RevealInExplorer: SHOpenFolderAndSelectItems hr=0x{hr:X8} for {filePath}");
                    if (hr == 0) return true;
                }
                finally { ILFree(pidl); }
            }
            else
            {
                DiagnosticLog.Log("RevealInExplorer: ILCreateFromPathW returned null.");
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("RevealInExplorer/SHOpen", ex); }

        // Fallback: open the containing folder (no selection).
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                DiagnosticLog.Log($"RevealInExplorer: opened folder {dir}");
                return true;
            }
        }
        catch (Exception ex) { DiagnosticLog.LogException("RevealInExplorer/folder", ex); }

        return false;
    }
}
