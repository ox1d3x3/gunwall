using System.Runtime.InteropServices;
using System.Text;
using static GunWall.Services.Wfp.WfpEvents;

namespace GunWall.Services.Wfp;

/// <summary>
/// Subscribes to kernel network events so GunWall is told about every filtered
/// connection the instant it happens — the event-driven detection that replaces
/// table polling. Raises <see cref="ConnectionEvent"/> for each event on a
/// background (kernel-supplied) thread; the UI marshals to its own thread.
///
/// Safety model:
///  - The delegate is pinned via a field reference so the GC can't collect it
///    while the kernel holds a pointer to it.
///  - The callback is wrapped in try/catch and does the minimum work, because
///    throwing from a kernel callback would crash the process.
///  - If subscription fails for any reason, <see cref="Start"/> returns false
///    and the caller keeps using poll-based detection — no crash, no regression.
/// </summary>
public sealed class NetEventMonitor : IDisposable
{
    public sealed record Event(
        string AppPath, string Protocol, string RemoteAddress, int RemotePort,
        string LocalAddress, int LocalPort, bool Dropped, uint Direction);

    /// <summary>Raised for each kernel network event (background thread).</summary>
    public event Action<Event>? ConnectionEvent;

    private readonly IntPtr _engine;
    private IntPtr _subHandle = IntPtr.Zero;
    private FwpmNetEventCallback1? _callback; // kept alive deliberately
    private GCHandle _callbackHandle;
    private bool _running;

    public bool IsRunning => _running;

    public NetEventMonitor(IntPtr engineHandle) => _engine = engineHandle;

    /// <summary>
    /// Enables collection and subscribes. Returns true on success; false means
    /// the caller should keep polling. Never throws.
    /// </summary>
    public bool Start()
    {
        if (_running) return true;
        try
        {
            // Turn on net-event collection (FWP_UINT32 = 1).
            var val = new FWP_VALUE0 { type = 3 /*FWP_UINT32*/, value = 1 };
            FwpmEngineSetOption0(_engine, FWPM_ENGINE_COLLECT_NET_EVENTS, ref val);

            _callback = OnKernelEvent;                  // keep a strong ref
            _callbackHandle = GCHandle.Alloc(_callback); // pin against GC

            var sub = new FWPM_NET_EVENT_SUBSCRIPTION0
            {
                enumTemplate = IntPtr.Zero, // null = all events
                flags = 0,
                sessionKey = Guid.Empty
            };

            uint r = FwpmNetEventSubscribe0(_engine, ref sub, _callback, IntPtr.Zero, out _subHandle);
            if (r != 0)
            {
                Cleanup();
                System.Diagnostics.Debug.WriteLine($"NetEventSubscribe failed 0x{r:X8}");
                return false;
            }

            _running = true;
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"NetEventMonitor.Start error: {ex.Message}");
            Cleanup();
            return false;
        }
    }

    // Invoked by the kernel for each event. Must be fast and must never throw.
    private void OnKernelEvent(IntPtr context, IntPtr eventPtr)
    {
        try
        {
            if (eventPtr == IntPtr.Zero) return;
            var evt = Marshal.PtrToStructure<FWPM_NET_EVENT1>(eventPtr);
            var h = evt.header;

            string appPath = ReadAppId(h.appId);
            if (string.IsNullOrEmpty(appPath)) return;

            string proto = h.ipProtocol switch { 6 => "TCP", 17 => "UDP", 1 => "ICMP", _ => "IP" };
            string remote = FormatV4(h.remoteAddr);
            string local = FormatV4(h.localAddr);
            bool dropped = evt.type == FWPM_NET_EVENT_TYPE_CLASSIFY_DROP;
            uint dir = 0;
            if (dropped && evt.value != IntPtr.Zero)
            {
                try
                {
                    var drop = Marshal.PtrToStructure<FWPM_NET_EVENT_CLASSIFY_DROP1>(evt.value);
                    dir = drop.msFwpDirection;
                }
                catch { /* ignore payload read issues */ }
            }

            ConnectionEvent?.Invoke(new Event(
                NtToDosPath(appPath), proto, remote, h.remotePort, local, h.localPort, dropped, dir));
        }
        catch
        {
            // Swallow everything — a throw here would tear down the process.
        }
    }

    private static string ReadAppId(FWP_BYTE_BLOB blob)
    {
        if (blob.data == IntPtr.Zero || blob.size == 0) return "";
        try
        {
            // App ID is a wide-char NT path; size is in bytes including the null.
            int chars = (int)(blob.size / 2);
            string s = Marshal.PtrToStringUni(blob.data, chars) ?? "";
            return s.TrimEnd('\0');
        }
        catch { return ""; }
    }

    private static string FormatV4(byte[]? addr)
    {
        if (addr == null || addr.Length < 4) return "";
        // IPv4 occupies the first 4 bytes in network byte order. The WFP header
        // stores the V4 address as a UINT32 in host order inside the union, but
        // when read as bytes the first 4 are the address octets. All-zero = none.
        if (addr[0] == 0 && addr[1] == 0 && addr[2] == 0 && addr[3] == 0) return "";
        return $"{addr[3]}.{addr[2]}.{addr[1]}.{addr[0]}";
    }

    // Best-effort NT (\device\harddiskvolumeN\...) -> DOS (C:\...) path mapping
    // so detected apps match the paths used elsewhere in the UI.
    private static readonly Dictionary<string, string> NtMap = BuildNtMap();

    private static string NtToDosPath(string ntPath)
    {
        if (string.IsNullOrEmpty(ntPath)) return ntPath;
        foreach (var (device, drive) in NtMap)
        {
            if (ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return drive + ntPath[device.Length..];
        }
        return ntPath;
    }

    private static Dictionary<string, string> BuildNtMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string drive = c + ":";
                var sb = new StringBuilder(1024);
                if (WfpNative.QueryDosDeviceW(drive, sb, (uint)sb.Capacity) != 0)
                    map[sb.ToString()] = drive;
            }
        }
        catch { /* mapping best-effort */ }
        return map;
    }

    private void Cleanup()
    {
        if (_subHandle != IntPtr.Zero)
        {
            try { FwpmNetEventUnsubscribe0(_engine, _subHandle); } catch { }
            _subHandle = IntPtr.Zero;
        }
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
        _callback = null;
        _running = false;
    }

    public void Dispose() => Cleanup();
}
