using System.Runtime.InteropServices;
using System.Threading;

namespace GunWall.Services;

// =============================================================================
//  EtwByteMeterService.cs
//  Precise per-process network byte metering via a raw ETW real-time session
//  against the Microsoft-Windows-Kernel-Network provider — no helper libraries,
//  in keeping with GunWall's zero-dependency rule.
//
//  Like WfpNative.cs, this is delicate interop: the structs below match the
//  x64 ABI (GunWall builds x64-only) using explicit layouts with documented
//  offsets. If byte counts ever look like garbage, this file's offsets are the
//  first place to inspect.
//
//  Safety model (mirrors NetEventMonitor):
//   - The event callback delegate is held in a field and pinned with a
//     GCHandle so the OS can never call into collected memory.
//   - The callback is wrapped in try/catch and does minimal work; throwing
//     from an ETW dispatch thread would crash the process.
//   - Start() returns false on any failure and never throws; the caller keeps
//     using the approximation engine — a meter failure can never blank usage.
//   - A stale session left by a crashed run (ETW sessions outlive processes!)
//     is stopped by name before starting, so restarts always succeed.
//
//  Event IDs (from the provider manifest; PID and size are the first two
//  UINT32s of the payload for every one of them):
//    TCP  v4: 10 = send, 11 = recv        TCP  v6: 26 = send, 27 = recv
//    UDP  v4: 42 = send, 43 = recv        UDP  v6: 58 = send, 59 = recv
//
//  References:
//    https://learn.microsoft.com/windows/win32/api/evntrace/
//    https://learn.microsoft.com/windows/win32/etw/event-trace-properties
// =============================================================================

public sealed class EtwByteMeterService : IDisposable
{
    private const string SessionName = "GunWallByteMeter";

    // Microsoft-Windows-Kernel-Network
    private static readonly Guid ProviderId = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Provider keywords: 0x10 = IPv4, 0x20 = IPv6.
    private const ulong KeywordIPv4AndIPv6 = 0x30;
    private const byte TRACE_LEVEL_INFORMATION = 4;

    // ------------------------------------------------------------ public state
    /// <summary>True while the session and processing thread are up.</summary>
    public bool SessionActive { get; private set; }

    /// <summary>Total kernel-network events accepted since Start().</summary>
    public long EventsTotal => Interlocked.Read(ref _eventsTotal);

    /// <summary>Payloads too short to carry PID+size (should stay at 0).</summary>
    public long ParseFailures => Interlocked.Read(ref _parseFailures);

    /// <summary>Human-readable reason if Start() failed or the session died.</summary>
    public string LastError { get; private set; } = "";

    // ------------------------------------------------------------ accumulation
    private static readonly Dictionary<uint, (long Down, long Up)> s_empty = new();
    private readonly object _gate = new();
    private Dictionary<uint, (long Down, long Up)> _byPid = new();
    private long _eventsTotal;
    private long _parseFailures;

    // ------------------------------------------------------------ session state
    private ulong _sessionHandle;   // from StartTraceW (controller side)
    private ulong _traceHandle;     // from OpenTraceW  (consumer side)
    private IntPtr _propsBuffer = IntPtr.Zero;
    private Thread? _thread;
    private EventRecordCallback? _callback;  // strong ref, deliberately kept
    private GCHandle _callbackHandle;        // pinned against GC

    /// <summary>
    /// Starts the ETW session, enables the kernel-network provider, and begins
    /// consuming on a background thread. Returns true on success. Never throws;
    /// on failure everything is torn back down and LastError explains why.
    /// </summary>
    public bool Start()
    {
        if (SessionActive) return true;
        try
        {
            // A previous GunWall instance that crashed leaves the named session
            // running at OS level — stop it first so StartTrace can't 183 us.
            StopSessionByName();

            _propsBuffer = AllocProperties();
            uint r = StartTraceW(out _sessionHandle, SessionName, _propsBuffer);
            if (r != 0)
            {
                LastError = $"StartTrace failed 0x{r:X8}";
                DiagnosticLog.Log($"ETW meter: {LastError}");
                Cleanup();
                return false;
            }

            r = EnableTraceEx2(_sessionHandle, ref Unsafe_ProviderId, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                               TRACE_LEVEL_INFORMATION, KeywordIPv4AndIPv6, 0, 0, IntPtr.Zero);
            if (r != 0)
            {
                LastError = $"EnableTraceEx2 failed 0x{r:X8}";
                DiagnosticLog.Log($"ETW meter: {LastError}");
                Cleanup();
                return false;
            }

            _callback = OnEventRecord;                    // keep a strong ref
            _callbackHandle = GCHandle.Alloc(_callback);  // pin against GC

            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = Marshal.StringToHGlobalUni(SessionName),
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            try
            {
                _traceHandle = OpenTraceW(ref logfile);
            }
            finally
            {
                Marshal.FreeHGlobal(logfile.LoggerName); // OpenTrace copies the name
            }
            if (_traceHandle == INVALID_TRACE_HANDLE)
            {
                LastError = $"OpenTrace failed (err {Marshal.GetLastWin32Error()})";
                DiagnosticLog.Log($"ETW meter: {LastError}");
                Cleanup();
                return false;
            }

            // ProcessTrace blocks until the session stops; give it its own thread.
            _thread = new Thread(ProcessLoop) { IsBackground = true, Name = "GunWall-ETW" };
            _thread.Start();

            SessionActive = true;
            LastError = "";
            DiagnosticLog.Log("ETW meter: session started, provider enabled (kernel-network, IPv4+IPv6)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagnosticLog.LogException("EtwByteMeter.Start", ex);
            Cleanup();
            return false;
        }
    }

    /// <summary>Stops the session and joins the processing thread. Never throws.</summary>
    public void Stop()
    {
        if (!SessionActive && _propsBuffer == IntPtr.Zero) return;
        try
        {
            SessionActive = false;

            // Stopping the controller session makes ProcessTrace drain and return.
            StopSessionByName();

            if (_traceHandle != 0 && _traceHandle != INVALID_TRACE_HANDLE)
            {
                CloseTrace(_traceHandle);
                _traceHandle = 0;
            }

            if (_thread != null && !_thread.Join(3000))
                DiagnosticLog.Log("ETW meter: processing thread did not exit in 3s (backgrounded)");
            _thread = null;

            DiagnosticLog.Log($"ETW meter: stopped ({EventsTotal:N0} events total, {ParseFailures} parse failures)");
        }
        catch (Exception ex) { DiagnosticLog.LogException("EtwByteMeter.Stop", ex); }
        finally { Cleanup(); }
    }

    /// <summary>
    /// Swap-and-return the bytes accumulated since the last drain, keyed by PID.
    /// Called once per sampling tick from the UI loop.
    /// </summary>
    public Dictionary<uint, (long Down, long Up)> Drain()
    {
        lock (_gate)
        {
            if (_byPid.Count == 0) return s_empty; // never hand out the live dictionary
            var full = _byPid;
            _byPid = new Dictionary<uint, (long, long)>();
            return full;
        }
    }

    public void Dispose() => Stop();

    // ======================================================================
    //  Internals
    // ======================================================================

    private void ProcessLoop()
    {
        try
        {
            ulong h = _traceHandle;
            uint r = ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero);
            DiagnosticLog.Log($"ETW meter: ProcessTrace returned 0x{r:X8}");
            // If the session died underneath us (another tool stopped it, or
            // resource pressure), surface that so the UI can fall back.
            if (SessionActive)
            {
                SessionActive = false;
                LastError = $"session ended unexpectedly (0x{r:X8})";
            }
        }
        catch (Exception ex)
        {
            SessionActive = false;
            LastError = ex.Message;
            DiagnosticLog.LogException("EtwByteMeter.ProcessLoop", ex);
        }
    }

    private bool _firstEventLogged;

    /// <summary>ETW dispatch callback — kernel-adjacent thread; never throw.</summary>
    private void OnEventRecord(ref EVENT_RECORD rec)
    {
        try
        {
            if (rec.ProviderId != ProviderId) return;

            ushort id = rec.EventId;
            bool isSend = id is 10 or 26 or 42 or 58;
            bool isRecv = id is 11 or 27 or 43 or 59;
            if (!isSend && !isRecv) return;

            // Payload begins: UINT32 PID, UINT32 size — identical for all eight IDs.
            if (rec.UserDataLength < 8 || rec.UserData == IntPtr.Zero)
            {
                Interlocked.Increment(ref _parseFailures);
                return;
            }
            uint pid = (uint)Marshal.ReadInt32(rec.UserData, 0);
            uint size = (uint)Marshal.ReadInt32(rec.UserData, 4);
            if (pid == 0 || size == 0) return;

            Interlocked.Increment(ref _eventsTotal);
            if (!_firstEventLogged)
            {
                _firstEventLogged = true;
                DiagnosticLog.Log($"ETW meter: first event received (id {id}, pid {pid}, {size} bytes)");
            }

            lock (_gate)
            {
                _byPid.TryGetValue(pid, out var cur);
                _byPid[pid] = isRecv ? (cur.Down + size, cur.Up) : (cur.Down, cur.Up + size);
            }
        }
        catch
        {
            // Swallow everything: an exception escaping an ETW callback is fatal
            // to the process. The counter is the diagnostic.
            Interlocked.Increment(ref _parseFailures);
        }
    }

    /// <summary>Best-effort stop of the named session (ours or a stale one).</summary>
    private void StopSessionByName()
    {
        IntPtr buf = AllocProperties();
        try
        {
            uint r = ControlTraceW(0, SessionName, buf, EVENT_TRACE_CONTROL_STOP);
            if (r == 0)
                DiagnosticLog.Log("ETW meter: stopped an existing session with our name");
            // ERROR_WMI_INSTANCE_NOT_FOUND (4201) = nothing to stop: the normal case.
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private void Cleanup()
    {
        if (_propsBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_propsBuffer); _propsBuffer = IntPtr.Zero; }
        if (_callbackHandle.IsAllocated) _callbackHandle.Free();
        _callback = null;
        _sessionHandle = 0;
    }

    /// <summary>
    /// Allocates a zeroed EVENT_TRACE_PROPERTIES buffer with room for the
    /// session-name string that the API appends after the fixed struct.
    /// x64 fixed-part size = 120 bytes (48-byte WNODE_HEADER + 72 bytes of
    /// fields); we add 2 KB of name space, mirroring the documented pattern.
    /// </summary>
    private static IntPtr AllocProperties()
    {
        const int fixedSize = 120;
        const int nameSpace = 2048;
        int total = fixedSize + nameSpace;
        IntPtr p = Marshal.AllocHGlobal(total);
        for (int i = 0; i < total; i += 8) Marshal.WriteInt64(p, i, 0);

        Marshal.WriteInt32(p, 0, total);                       // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                          // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, WNODE_FLAG_TRACED_GUID);     // Wnode.Flags
        Marshal.WriteInt32(p, 64, EVENT_TRACE_REAL_TIME_MODE); // LogFileMode
        Marshal.WriteInt32(p, 112, 0);                         // LogFileNameOffset (no file)
        Marshal.WriteInt32(p, 116, fixedSize);                 // LoggerNameOffset
        return p;
    }

    // Boxed copy so EnableTraceEx2 can take it by ref without touching the readonly.
    private static Guid Unsafe_ProviderId = ProviderId;

    // ======================================================================
    //  Native surface (advapi32.dll)
    // ======================================================================

    private const int WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const int EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_TRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

    private delegate void EventRecordCallback(ref EVENT_RECORD rec);

    /// <summary>
    /// EVENT_RECORD, x64 layout, only the fields we read (explicit offsets):
    ///   0..79  EVENT_HEADER   (ProviderId at 24, EventDescriptor.Id at 40)
    ///  84..85  ExtendedDataCount
    ///  86..87  UserDataLength
    ///  96      UserData pointer
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    private struct EVENT_RECORD
    {
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;      // EVENT_DESCRIPTOR.Id
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public IntPtr UserData;
    }

    /// <summary>
    /// EVENT_TRACE_LOGFILEW, x64 layout. The two big embedded structs
    /// (EVENT_TRACE at 32, TRACE_LOGFILE_HEADER at 120) are opaque to us, so
    /// they're represented by the explicit offsets of the fields we DO set:
    ///   0   LogFileName (unused, null)      8   LoggerName
    ///  28   ProcessTraceMode (union w/ LogFileMode)
    /// 400   BufferCallback (unused, null)  424   EventRecordCallback (union)
    /// Total size 448.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILEW
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(400)] public IntPtr BufferCallback;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong traceHandle);
}
