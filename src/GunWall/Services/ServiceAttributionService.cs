using System.Runtime.InteropServices;

namespace GunWall.Services;

// =============================================================================
//  ServiceAttributionService.cs
//
//  Answers "which Windows services does this process host?".
//
//  Without this, a machine's most talkative network citizens are a row of
//  identical "svchost" entries. That is the single biggest obstacle to
//  reasoning about firewall rules: a rule you cannot explain is not really
//  control. Resolving PID -> service names makes each connection legible
//  (Windows Update, DNS Client, Delivery Optimization, ...).
//
//  Implementation is a direct advapi32 P/Invoke, in keeping with the
//  zero-dependency rule. The alternative already in the codebase - shelling out
//  to sc.exe - cannot report process IDs at all and costs a process launch per
//  query, which is unusable on a per-tick path.
//
//  Struct layout and constants below were verified against the Windows SDK
//  header (winsvc.h) and Microsoft's win32metadata, not from memory:
//     ENUM_SERVICE_STATUS_PROCESSW = { LPWSTR, LPWSTR, SERVICE_STATUS_PROCESS }
//     x64: dwProcessId at offset 44, total struct size 56.
//     SERVICE_WIN32 = 48, SERVICE_STATE_ALL = 3, SC_ENUM_PROCESS_INFO = 0,
//     SC_MANAGER_CONNECT = 1, SC_MANAGER_ENUMERATE_SERVICE = 4.
// =============================================================================

public sealed class ServiceAttributionService
{
    /// <summary>One Windows service hosted by a process.</summary>
    public readonly record struct HostedService(string Name, string Display)
    {
        /// <summary>Display name where available, else the short service name.</summary>
        public string Best => Display.Length > 0 ? Display : Name;
    }

    // Services start and stop rarely compared with the 1-second sampling loop,
    // so the map is cached; the enumeration is a syscall, not free.
    private static readonly object _gate = new();
    private static Dictionary<int, List<HostedService>> _map = new();
    private static DateTime _fetched = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(20);

    /// <summary>Distinct processes currently hosting at least one service.</summary>
    public static int HostingProcessCount { get { lock (_gate) return _map.Count; } }

    /// <summary>Total services mapped to a running process.</summary>
    public static int MappedServiceCount
    {
        get { lock (_gate) { int n = 0; foreach (var v in _map.Values) n += v.Count; return n; } }
    }

    /// <summary>Last enumeration failure, or "" when the last attempt succeeded.</summary>
    public static string LastError { get; private set; } = "";

    /// <summary>
    /// Services hosted by <paramref name="pid"/>, or an empty list. Refreshes
    /// the cached map when it is older than the TTL. Never throws.
    /// </summary>
    public static IReadOnlyList<HostedService> ForPid(int pid)
    {
        EnsureFresh();
        lock (_gate)
            return _map.TryGetValue(pid, out var list) ? list : System.Array.Empty<HostedService>();
    }

    /// <summary>
    /// A short label for a process: "" when it hosts no services, otherwise the
    /// service names, truncated once the list gets long. Intended for a list
    /// column, so it stays on one line.
    /// </summary>
    public static string LabelForPid(int pid, int maxChars = 34)
    {
        var svcs = ForPid(pid);
        if (svcs.Count == 0) return "";
        if (svcs.Count == 1) return svcs[0].Best;

        // Fit as many names as the column can show, then say how many remain.
        // Windows service display names run long ("Function Discovery Resource
        // Publication"), so a fixed name count truncates mid-word; budgeting by
        // width keeps every row readable and the full list is in the tooltip.
        var shown = new List<string>();
        int used = 0;
        foreach (var s in svcs)
        {
            int cost = s.Best.Length + (shown.Count > 0 ? 2 : 0);
            if (shown.Count > 0 && used + cost > maxChars) break;
            shown.Add(s.Best);
            used += cost;
        }
        int rest = svcs.Count - shown.Count;
        return rest == 0
            ? string.Join(", ", shown)
            : string.Join(", ", shown) + $" +{rest}";
    }

    /// <summary>Full multi-line detail for a tooltip or the inspector.</summary>
    public static string DetailForPid(int pid)
    {
        var svcs = ForPid(pid);
        if (svcs.Count == 0) return "";
        var lines = svcs
            .OrderBy(s => s.Best, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Display.Length > 0 && !string.Equals(s.Display, s.Name, StringComparison.OrdinalIgnoreCase)
                ? $"\u2022 {s.Display}  ({s.Name})"
                : $"\u2022 {s.Name}");
        return $"Hosts {svcs.Count} service{(svcs.Count == 1 ? "" : "s")}:" +
               Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>Forces the next lookup to re-enumerate.</summary>
    public static void Invalidate() { lock (_gate) _fetched = DateTime.MinValue; }

    private static void EnsureFresh()
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _fetched < Ttl) return;
            _fetched = DateTime.UtcNow;   // set first: a failure shouldn't cause a retry storm
        }
        var fresh = Enumerate();
        if (fresh == null) return;        // keep the previous map rather than blanking it
        lock (_gate) _map = fresh;
    }

    /// <summary>
    /// Enumerates every Win32 service and groups the running ones by host PID.
    /// Returns null on failure so the caller keeps the last good map.
    /// </summary>
    private static Dictionary<int, List<HostedService>>? Enumerate()
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
            if (scm == IntPtr.Zero)
            {
                LastError = $"OpenSCManager failed ({Marshal.GetLastWin32Error()})";
                return null;
            }

            // First call sizes the buffer; ERROR_MORE_DATA here is expected.
            uint needed = 0, returned = 0, resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                                  IntPtr.Zero, 0, ref needed, ref returned, ref resume, null);
            if (needed == 0)
            {
                LastError = $"EnumServicesStatusEx sizing failed ({Marshal.GetLastWin32Error()})";
                return null;
            }

            buffer = Marshal.AllocHGlobal((int)needed);
            resume = 0;
            if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                                       buffer, needed, ref needed, ref returned, ref resume, null))
            {
                LastError = $"EnumServicesStatusEx failed ({Marshal.GetLastWin32Error()})";
                return null;
            }

            var map = new Dictionary<int, List<HostedService>>();
            int stride = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
            for (int i = 0; i < returned; i++)
            {
                var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * stride);

                // PID 0 means the service isn't running - nothing to attribute.
                int pid = (int)entry.ServiceStatusProcess.dwProcessId;
                if (pid <= 0) continue;

                string name = entry.lpServiceName != IntPtr.Zero
                    ? Marshal.PtrToStringUni(entry.lpServiceName) ?? "" : "";
                string display = entry.lpDisplayName != IntPtr.Zero
                    ? Marshal.PtrToStringUni(entry.lpDisplayName) ?? "" : "";
                if (name.Length == 0 && display.Length == 0) continue;

                if (!map.TryGetValue(pid, out var list))
                    map[pid] = list = new List<HostedService>();
                list.Add(new HostedService(name, display));
            }

            foreach (var list in map.Values)
                list.Sort((a, b) => string.Compare(a.Best, b.Best, StringComparison.OrdinalIgnoreCase));

            LastError = "";
            return map;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagnosticLog.LogException("ServiceAttribution.Enumerate", ex);
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    // ---------------------------------------------------------------- native
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x30;        // OWN_PROCESS | SHARE_PROCESS
    private const uint SERVICE_STATE_ALL = 0x03;    // ACTIVE | INACTIVE

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    /// <summary>x64: pointers at 0 and 8, status block from 16, dwProcessId at
    /// 44, total 56 bytes. Sequential layout reproduces this exactly.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        public IntPtr lpServiceName;
        public IntPtr lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusExW(
        IntPtr hSCManager, uint infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, ref uint bytesNeeded, ref uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
