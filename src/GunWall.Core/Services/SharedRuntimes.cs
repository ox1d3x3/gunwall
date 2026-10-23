using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GunWall.Services;

/// <summary>
/// Programs that connect on behalf of other programs (0.99.234.2).
///
/// Microsoft Edge WebView2 (msedgewebview2.exe) is the browser engine inside many
/// Windows apps: WhatsApp (since 2025), new Outlook, Teams, Widgets, Clipchamp and
/// others. Each app starts its own copies of msedgewebview2.exe, and those - not the
/// app - make the connections. Windows identifies a connection only by the program
/// file that made it, so one rule covers every app built on WebView2: blocking it
/// cuts all of them off, however each app itself is set.
///
/// Reported 2026-10-23: WhatsApp allowed, but "reconnecting" whenever GunWall was on.
/// WebView2 had been blocked in the review window a week earlier, and nothing
/// connected the two.
/// </summary>
public static class SharedRuntimes
{
    public const string WebView2File = "msedgewebview2.exe";

    /// <summary>True for Microsoft Edge WebView2's process, in any version folder.</summary>
    public static bool IsWebView2(string? exePath) =>
        !string.IsNullOrWhiteSpace(exePath)
        && string.Equals(Path.GetFileName(exePath.Trim()), WebView2File, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a prompt or a Block confirmation says about WebView2.</summary>
    public const string WebView2Note =
        "Microsoft Edge WebView2 connects for many apps - WhatsApp, Outlook, Teams, Widgets and others. " +
        "Blocking it blocks all of them, even the ones you allowed.";

    private static readonly Regex HostArg = new(
        "--webview-exe-name=(?:\"(?<n>[^\"]+)\"|(?<n>[^\\s\"]+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The program a WebView2 process works for, from its command line
    /// ("--webview-exe-name=WhatsApp.Root.exe"), or null.</summary>
    public static string? HostFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine)) return null;
        var m = HostArg.Match(commandLine);
        if (!m.Success) return null;
        string n = Path.GetFileName(m.Groups["n"].Value.Trim());
        return n.Length == 0 ? null : n;
    }

    /// <summary>The programs (file names) running WebView2 right now, each once.
    /// Reads each WebView2 process's command line; a process that cannot be read
    /// is skipped. Empty off Windows.</summary>
    public static IReadOnlyList<string> WebView2Hosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows()) return hosts.ToList();
        Process[] procs;
        try { procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WebView2File)); }
        catch { return hosts.ToList(); }
        foreach (var p in procs)
        {
            try
            {
                string? h = HostFromCommandLine(CommandLineOf(p.Id));
                if (h != null) hosts.Add(h);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return hosts.ToList();
    }

    /// <summary>A process's command line (Windows 8.1 and later), or null.</summary>
    private static string? CommandLineOf(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        IntPtr buf = IntPtr.Zero;
        try
        {
            int size = 4096;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                buf = Marshal.AllocHGlobal(size);
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out int needed);
                if (status == 0)
                {
                    // UNICODE_STRING { ushort Length; ushort MaximumLength; [pad]; PWSTR Buffer; }
                    ushort bytes = (ushort)Marshal.ReadInt16(buf);
                    IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size);
                    return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
                }
                Marshal.FreeHGlobal(buf); buf = IntPtr.Zero;
                if (needed <= size || needed > 1 << 20) return null;
                size = needed;
            }
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
}
