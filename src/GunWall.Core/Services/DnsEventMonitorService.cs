using System.Runtime.InteropServices;

namespace GunWall.Services;

// =============================================================================
//  DnsEventMonitorService.cs
//
//  Watches the Windows DNS client and records what resolved to what.
//
//  This is the passive replacement for redirecting the machine's DNS. It claims
//  nothing, rewrites nothing, and answers no queries: it subscribes to the
//  events the DNS client already emits and reads the results. Consequently it
//  cannot conflict with security software or a VPN over ownership of port 53 -
//  the failure mode that made redirection untenable - and it sees every lookup
//  the machine makes, including ones that never go near GunWall's resolver.
//
//  Provider: Microsoft-Windows-DNS-Client {1C95126E-7EEA-49A9-A3FE-A378B03DDB4D}
//  Event 3008 ("query completed") carries, in manifest order:
//      QueryName     UnicodeString (null-terminated UTF-16)
//      QueryType     UInt32
//      QueryOptions  UInt64
//      QueryStatus   UInt32
//      QueryResults  UnicodeString (null-terminated UTF-16)
//
//  Manifest payloads are packed with no alignment padding, so the fields are
//  read sequentially. Every read is bounds-checked and any shortfall counts as
//  a parse failure rather than throwing - an exception escaping an ETW callback
//  terminates the process.
//
//  Session plumbing mirrors EtwByteMeterService, which is hardware-proven at
//  110,000+ events with zero parse failures.
// =============================================================================

public sealed class DnsEventMonitorService : IDisposable
{
    private static readonly Guid ProviderId = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    private const string SessionName = "GunWallDnsObserver";
    private const ushort EventQueryCompleted = 3008;

    public bool SessionActive { get; private set; }
    public string LastError { get; private set; } = "";

    private long _eventsTotal, _parseFailures, _answersRecorded;
    public long EventsSeen => Interlocked.Read(ref _eventsTotal);
    public long ParseFailures => Interlocked.Read(ref _parseFailures);
    public long AnswersRecorded => Interlocked.Read(ref _answersRecorded);

    private ulong _sessionHandle, _traceHandle;
    private IntPtr _propsBuffer = IntPtr.Zero;
    private EventRecordCallback? _callback;      // strong ref, deliberately kept
    private GCHandle _callbackHandle;            // pinned against GC
    private Thread? _pump;
    private bool _firstEventLogged;

    // ---------------------------------------------------------------- parsing

    /// <summary>One address or alias taken from a DNS answer.</summary>
    public readonly record struct ResultEntry(string Text, bool IsAlias);

    /// <summary>
    /// Splits the QueryResults field of event 3008.
    ///
    /// The field is a semicolon-separated list mixing aliases and addresses,
    /// e.g. "type: 5 star.c10r.example.net;93.184.216.34;". Aliases carry a
    /// "type: N " prefix (5 = CNAME); everything else is an address literal,
    /// which may arrive in IPv4-mapped IPv6 form (::ffff:1.2.3.4).
    ///
    /// Pure and side-effect free so it can be tested against real payload
    /// shapes without Windows.
    /// </summary>
    public static List<ResultEntry> ParseQueryResults(string? results)
    {
        var list = new List<ResultEntry>();
        if (string.IsNullOrWhiteSpace(results)) return list;

        foreach (string raw in results.Split(';'))
        {
            string t = raw.Trim();
            if (t.Length == 0) continue;

            if (t.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                // "type: 5 name" - take everything after the numeric type.
                int sp = t.IndexOf(' ');
                if (sp < 0) continue;
                string rest = t[(sp + 1)..].TrimStart();
                int sp2 = rest.IndexOf(' ');
                if (sp2 < 0) continue;                 // a type with no value
                string value = rest[(sp2 + 1)..].Trim();
                if (value.Length > 0) list.Add(new ResultEntry(value, true));
                continue;
            }
            list.Add(new ResultEntry(t, false));
        }
        return list;
    }

    /// <summary>
    /// Reduces parsed entries to the IPv4 and IPv6 addresses they contain.
    /// Aliases are deliberately dropped: an alias is another name, not a
    /// destination, and recording it as one would attribute a connection to a
    /// host that was never contacted.
    /// </summary>
    public static (List<uint> V4, List<string> V6) AddressesFrom(IEnumerable<ResultEntry> entries)
    {
        var v4 = new List<uint>();
        var v6 = new List<string>();
        foreach (var e in entries)
        {
            if (e.IsAlias) continue;
            if (!System.Net.IPAddress.TryParse(e.Text, out var addr)) continue;

            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                v4.Add(Pack(addr));
            }
            else if (addr.IsIPv4MappedToIPv6)
            {
                v4.Add(Pack(addr.MapToIPv4()));       // same host, v4 form
            }
            else
            {
                v6.Add(addr.ToString());
            }
        }
        return (v4, v6);
    }

    private static uint Pack(System.Net.IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    /// <summary>
    /// Reads event 3008's payload. Returns false rather than throwing on any
    /// shortfall, so a malformed or unexpected payload is counted, not fatal.
    /// </summary>
    public static bool TryReadQueryEvent(byte[] payload, out string name, out string results)
    {
        name = ""; results = "";
        if (payload == null || payload.Length < 4) return false;

        int pos = 0;
        if (!TryReadUtf16(payload, ref pos, out name)) return false;
        pos += 4;   // QueryType
        pos += 8;   // QueryOptions
        pos += 4;   // QueryStatus
        if (pos > payload.Length) return false;
        // A query that returned nothing has an empty results string; that is a
        // valid event, just not a useful one.
        if (pos == payload.Length) return true;
        return TryReadUtf16(payload, ref pos, out results);
    }

    private static bool TryReadUtf16(byte[] buf, ref int pos, out string value)
    {
        value = "";
        if (pos < 0 || pos >= buf.Length) return false;
        int start = pos;
        while (pos + 1 < buf.Length)
        {
            if (buf[pos] == 0 && buf[pos + 1] == 0)
            {
                value = System.Text.Encoding.Unicode.GetString(buf, start, pos - start);
                pos += 2;
                return true;
            }
            pos += 2;
        }
        return false;   // unterminated
    }

    // ---------------------------------------------------------------- session

    // ---- crash-loop guard -------------------------------------------------
    // A fault inside an ETW callback terminates the process outright, with no
    // managed exception and nothing useful in the log. If that ever happens
    // again the app must not simply die on every launch: a marker is written
    // before the session is touched and cleared once it has run without
    // incident, so a launch that finds the marker still present skips the
    // observer instead of repeating the crash.
    // Follows the data folder too. Left in the application folder this marker
    // could survive an upgrade that changed the very code it guards, and disable
    // the observer on a build where the crash it recorded no longer exists.
    private static string MarkerPath => ProfilePaths.FileIn("dns-observer.starting");

    private static bool MarkerPresent()
    { try { return System.IO.File.Exists(MarkerPath); } catch { return false; } }

    private static void SetMarker()
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(MarkerPath)!;
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o"));
        }
        catch { }
    }

    private static void ClearMarker()
    { try { if (System.IO.File.Exists(MarkerPath)) System.IO.File.Delete(MarkerPath); } catch { } }

    /// <summary>True when the previous attempt to start never completed.</summary>
    public static bool PreviousAttemptFailed => MarkerPresent();

    /// <summary>True while this process is the headless `--unblock` recovery run.
    ///
    /// The recovery path removes filters and exits in about a tenth of a second.
    /// Anything that starts asynchronously behind it is still starting when the
    /// process dies - which is indistinguishable, from the outside, from a crash.
    ///
    /// The DNS observer sets a marker before it touches anything native and clears
    /// it once the session is up, precisely so a crash mid-start is not repeated on
    /// the next launch. `--unblock` tripped that guard every time: the observer
    /// started at 18:12:15.999, the process exited, and the next real launch at
    /// 18:12:38 refused to start DNS watching and told the user to toggle a setting.
    ///
    /// A recovery tool must not leave the thing it recovered in a worse state than
    /// it found it.</summary>
    public static bool HeadlessRecovery { get; set; }

    public bool Start()
    {
        if (SessionActive) return true;

        if (HeadlessRecovery)
        {
            // No log line: this is not a fault, and a recovery run's output is read
            // by someone whose machine is offline. Nothing is started, so nothing
            // needs cleaning up afterwards.
            return false;
        }

        if (MarkerPresent())
        {
            // Clear it so turning the setting off and on again is a genuine retry.
            ClearMarker();
            LastError = "skipped after a previous start did not complete";
            DiagnosticLog.Log(
                "DNS observer NOT started: the previous attempt did not complete, which usually " +
                "means the process ended during it. Skipping this launch to avoid repeating it. " +
                "Toggle 'Watch system DNS lookups' off and on to try again.");
            return false;
        }

        try
        {
            SetMarker();                  // before anything native is touched
            StopSessionByName();          // clear a stale session of ours

            _propsBuffer = AllocProperties();
            uint r = StartTraceW(out _sessionHandle, SessionName, _propsBuffer);
            if (r != 0)
            {
                LastError = $"StartTrace failed 0x{r:X8}";
                DiagnosticLog.Log($"DNS observer: {LastError}");
                Cleanup();
                ClearMarker();
                return false;
            }

            r = EnableTraceEx2(_sessionHandle, ref Unsafe_ProviderId, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                               TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);
            if (r != 0)
            {
                LastError = $"EnableTraceEx2 failed 0x{r:X8}";
                DiagnosticLog.Log($"DNS observer: {LastError}");
                StopSessionByName();
                Cleanup();
                ClearMarker();
                return false;
            }

            _callback = OnEventRecord;
            _callbackHandle = GCHandle.Alloc(_callback);

            var logfile = new EVENT_TRACE_LOGFILE
            {
                LoggerName = Marshal.StringToHGlobalUni(SessionName),
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_callback)
            };
            try { _traceHandle = OpenTraceW(ref logfile); }
            finally { Marshal.FreeHGlobal(logfile.LoggerName); }

            if (_traceHandle == INVALID_PROCESSTRACE_HANDLE)
            {
                LastError = $"OpenTrace failed (err {Marshal.GetLastWin32Error()})";
                DiagnosticLog.Log($"DNS observer: {LastError}");
                StopSessionByName();
                Cleanup();
                ClearMarker();
                return false;
            }

            _pump = new Thread(Pump) { IsBackground = true, Name = "GunWall DNS observer" };
            _pump.Start();

            // Survive a spell of real event traffic before declaring the attempt
            // good; the dangerous moment is the first dispatch into our callback.
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(20000); if (SessionActive) ClearMarker(); }
                catch { }
            });

            SessionActive = true;
            LastError = "";
            DiagnosticLog.Log("DNS observer: watching Microsoft-Windows-DNS-Client (passive; nothing is intercepted).");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DiagnosticLog.LogException("DnsEventMonitor.Start", ex);
            Cleanup();
            ClearMarker();
            return false;
        }
    }

    public void Stop()
    {
        if (!SessionActive) return;
        SessionActive = false;
        ClearMarker();
        try { StopSessionByName(); } catch { }      // makes ProcessTrace drain and return
        try { if (_traceHandle != 0 && _traceHandle != INVALID_PROCESSTRACE_HANDLE) CloseTrace(_traceHandle); }
        catch { }
        _traceHandle = 0;
        try { _pump?.Join(3000); } catch { }
        _pump = null;
        Cleanup();
        DiagnosticLog.Log($"DNS observer stopped: {EventsSeen} events, {AnswersRecorded} answers recorded, {ParseFailures} parse failures.");
    }

    private void Pump()
    {
        try
        {
            ulong h = _traceHandle;
            uint r = ProcessTrace(new[] { h }, 1, IntPtr.Zero, IntPtr.Zero);
            if (r != 0) DiagnosticLog.Log($"DNS observer: ProcessTrace returned 0x{r:X8}");
        }
        catch (Exception ex) { DiagnosticLog.LogException("DnsEventMonitor.Pump", ex); }
    }

    private void OnEventRecord(ref EVENT_RECORD rec)
    {
        try
        {
            if (rec.ProviderId != ProviderId) return;
            if (rec.EventId != EventQueryCompleted) return;
            if (rec.UserData == IntPtr.Zero || rec.UserDataLength < 4) return;

            Interlocked.Increment(ref _eventsTotal);

            var payload = new byte[rec.UserDataLength];
            Marshal.Copy(rec.UserData, payload, 0, rec.UserDataLength);

            if (!TryReadQueryEvent(payload, out string name, out string results))
            {
                Interlocked.Increment(ref _parseFailures);
                return;
            }

            if (!_firstEventLogged)
            {
                _firstEventLogged = true;
                DiagnosticLog.Log($"DNS observer: first event parsed (name '{name}').");
            }

            if (results.Length == 0) return;          // a lookup that found nothing
            var (v4, v6) = AddressesFrom(ParseQueryResults(results));
            if (v4.Count == 0 && v6.Count == 0) return;

            DnsObservations.Record(name, v4, v6, DnsObservations.Source.SystemObserver);
            Interlocked.Increment(ref _answersRecorded);
        }
        catch
        {
            // An exception escaping an ETW callback kills the process; the
            // counter is the diagnostic.
            Interlocked.Increment(ref _parseFailures);
        }
    }

    private void StopSessionByName()
    {
        IntPtr buf = AllocProperties();
        try { ControlTraceW(0, SessionName, buf, EVENT_TRACE_CONTROL_STOP); }
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

    public void Dispose() => Stop();

    /// <summary>
    /// Zeroed EVENT_TRACE_PROPERTIES with room for the session name the API
    /// appends after the fixed struct. x64 fixed part = 120 bytes.
    /// </summary>
    private static IntPtr AllocProperties()
    {
        // Offsets are the x64 EVENT_TRACE_PROPERTIES layout, identical to the
        // hardware-proven byte meter: WNODE_HEADER is 48 bytes, then the ULONG
        // fields, with LogFileMode at 64 and LoggerNameOffset at 116.
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

    // ----------------------------------------------------------------- native
    private static Guid Unsafe_ProviderId = ProviderId;

    private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    private const byte TRACE_LEVEL_INFORMATION = 4;
    private const int EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const int WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

    private delegate void EventRecordCallback(ref EVENT_RECORD rec);

    /// EVENT_RECORD, x64. EVENT_HEADER is 80 bytes: ProviderId sits at 24 and
    /// EVENT_DESCRIPTOR begins at 40, so its leading Id field is at 40 too.
    /// These offsets are shared with the byte meter, which is hardware-proven.
    [StructLayout(LayoutKind.Explicit)]
    private struct EVENT_RECORD
    {
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;      // EVENT_DESCRIPTOR.Id
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public IntPtr UserData;
    }

    /// <summary>
    /// EVENT_TRACE_LOGFILEW, x64, by explicit offset. The two embedded structs
    /// (EVENT_TRACE at 32, TRACE_LOGFILE_HEADER at 120) are large - 88 and 280
    /// bytes - which puts BufferCallback at 400 and EventRecordCallback at 424.
    /// Describing this layout by hand is how the callback pointer ended up in
    /// the wrong place, leaving OpenTrace to read garbage and ProcessTrace to
    /// call into it, which terminates the process outright. Explicit offsets,
    /// shared with the byte meter, remove the guesswork.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    private struct EVENT_TRACE_LOGFILE
    {
        [FieldOffset(0)] public IntPtr LogFileName;
        [FieldOffset(8)] public IntPtr LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(400)] public IntPtr BufferCallback;
        [FieldOffset(424)] public IntPtr EventRecordCallback;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode,
        byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILE logfile);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint CloseTrace(ulong handle);
}
