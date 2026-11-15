using System.Runtime.InteropServices;

namespace GunWall.Services;

/// <summary>
/// Hands memory GunWall is not using back to Windows (0.99.233).
///
/// Task Manager's Memory column is the private working set: pages in RAM right
/// now. A window sitting in the tray still holds every page it touched while it was
/// shown - the WPF render data of every page visited, caches, garbage not yet
/// collected - though nothing reads them until it is opened again. Trimming lets
/// Windows take those pages back; whatever is needed again is read back in on use
/// (a few milliseconds when the window reappears). Not a leak fix and not a trick
/// that hides one: the diagnostics keep reporting private bytes, which this does not
/// change.
/// </summary>
public static class MemoryTrim
{
    /// <summary>One collection of what is already garbage, then the working set
    /// emptied. Returns the working set before and after, in MB.</summary>
    public static (double BeforeMb, double AfterMb) Trim()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            double before = p.WorkingSet64 / 1048576.0;
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            if (OperatingSystem.IsWindows())
                SetProcessWorkingSetSize(p.Handle, (IntPtr)(-1), (IntPtr)(-1));
            p.Refresh();
            return (before, p.WorkingSet64 / 1048576.0);
        }
        catch { return (0, 0); }   // housekeeping never fails anything
    }

    /// <summary>Whether a repeat trim is worth it (0.99.235). Emptying the working set
    /// every 10 minutes took back pages the next snapshot needed straight away; on a PC
    /// short of memory reading them in again stalled the hidden window for up to 3.5 s
    /// (2026-11-14). After the first trim, only when memory has grown past
    /// <paramref name="thresholdMb"/> again.</summary>
    public static bool WorthTrimming(double workingSetMb, double thresholdMb) => workingSetMb > thresholdMb;

    /// <summary>The working set now, in MB (0 when it cannot be read).</summary>
    public static double WorkingSetMb()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.WorkingSet64 / 1048576.0; }
        catch { return 0; }
    }

    /// <summary>Repeat trims: the window above this, the service above the next.</summary>
    public const double WindowRepeatAboveMb = 120;
    public const double ServiceRepeatAboveMb = 220;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimumWorkingSetSize, IntPtr maximumWorkingSetSize);
}
