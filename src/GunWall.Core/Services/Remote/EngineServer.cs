using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GunWall.Services.Remote;

/// <summary>
/// The service end of the engine pipe (stage 4a: built and bench-tested, not yet
/// started by the service - 4b switches it on).
///
/// Each connection has a reader thread and a writer thread. A call is checked
/// against the catalog and the permission policy, then run on the engine's own
/// context (FirewallManager is not thread-safe), and answered. Every answer to
/// something that is not a read carries a fresh state message, and
/// <see cref="PushState"/> sends one to every window when protection changes on
/// its own. Nothing a client does - stop reading, never say hello, send garbage,
/// open many connections - may stall the engine thread or end the service.
/// </summary>
public sealed class EngineServer : IDisposable
{
    /// <summary>Windows that may be connected at once. One per signed-in user is
    /// the real need; the rest is slack.</summary>
    public const int MaxSessions = 8;

    /// <summary>A connection that has not said hello by then is dropped.</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Messages waiting for a window that is not reading. Past this the
    /// window is dropped rather than letting the queue grow.</summary>
    public const int MaxQueued = 64;

    private readonly IEngine _engine;
    private readonly SynchronizationContext _ctx;
    private readonly Func<EngineCaller, Access, CallVerdict> _permitted;
    private readonly Func<int, EngineCaller> _identify;

    /// <summary>Protect settings' unlock (stage 4c). Null: the engine itself answers
    /// the unlock members ("" / -1: nothing is locked here).</summary>
    public SettingsGate? Gate { get; set; }
    private readonly Func<string, NamedPipeServerStream> _createPipe;
    private readonly string _pipeName;
    private readonly string _appVersion;
    private readonly List<Session> _sessions = new();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _stopping;
    private Thread? _accept;
    private NamedPipeServerStream? _listening;
    private long _stateSeq;

    /// <param name="permitted">Whether this caller may make a call of this
    /// (resolved) access now. 4c: Loosen only while unlocked when Protect settings
    /// is on, and changes only from the verified GunWall.exe in the console session.</param>
    public EngineServer(IEngine engine, SynchronizationContext engineContext, Func<EngineCaller, Access, bool> permitted,
                        string appVersion, string pipeName = EngineWire.PipeName,
                        Func<string, NamedPipeServerStream>? createPipe = null)
        : this(engine, engineContext, (c, a) => permitted(c, a) ? CallVerdict.Allow : CallVerdict.Denied,
               appVersion, pipeName, createPipe) { }

    /// <param name="identify">Who a process id is (stage 4c). Default: read from
    /// Windows and checked against this program's own file.</param>
    public EngineServer(IEngine engine, SynchronizationContext engineContext, Func<EngineCaller, Access, CallVerdict> permitted,
                        string appVersion, string pipeName = EngineWire.PipeName,
                        Func<string, NamedPipeServerStream>? createPipe = null,
                        Func<int, EngineCaller>? identify = null)
    {
        _engine = engine;
        _ctx = engineContext;
        _permitted = permitted;
        string? self = Environment.ProcessPath;
        _identify = identify ?? (pid => CallerIdentity.Of(pid, self));
        _appVersion = appVersion;
        _pipeName = pipeName;
        _createPipe = createPipe ?? DefaultPipe;
    }

    /// <summary>Connections open now (the bench).</summary>
    public int SessionCount { get { lock (_gate) return _sessions.Count; } }

    public void Start()
    {
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "GunWall engine pipe" };
        _accept.Start();
    }

    private void AcceptLoop()
    {
        while (!_stopping)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = _createPipe(_pipeName);
                _listening = pipe;
                pipe.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                _listening = null;
                if (_stopping) { pipe.Dispose(); return; }
                lock (_gate)
                {
                    if (_sessions.Count >= MaxSessions)
                    {
                        DiagnosticLog.Log($"Engine pipe: refused a connection - {MaxSessions} already open.");
                        pipe.Dispose();
                        pipe = null;
                        continue;
                    }
                    var s = new Session(this, pipe, _identify(ClientProcessId(pipe)));
                    _sessions.Add(s);
                    s.Begin();
                }
                pipe = null;   // the session owns it now
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                if (_stopping) return;
                DiagnosticLog.Log($"Engine pipe: accept failed ({ex.GetType().Name}: {ex.Message}).");
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Queues the current state for every connected window. Safe to call
    /// from the engine thread: it never waits on a pipe.</summary>
    public void PushState()
    {
        Session[] all;
        lock (_gate) all = _sessions.ToArray();
        if (all.Length == 0) return;
        Dictionary<string, JsonElement> state;
        try { state = Capture(); }
        catch (Exception ex) { DiagnosticLog.LogException("EngineServer.PushState", ex); return; }
        long seq = Interlocked.Increment(ref _stateSeq);
        foreach (var s in all)
            if (s.Open) s.Queue(new EngineMessage { Kind = "state", State = state, StateSeq = seq });
    }

    /// <summary>Sends a message to every connected window without waiting. A
    /// droppable one (packet-log rows) is skipped for a window that has fallen
    /// behind; anything else counts toward dropping that window.</summary>
    public void Push(EngineMessage m, bool droppable)
    {
        Session[] all;
        lock (_gate) all = _sessions.ToArray();
        // Only to windows past the handshake (0.99.226): a push queued before the hello
        // reply reached the window first and it read the handshake as refused - at
        // boot, when the service pushes the most, the window then ran the engine itself.
        foreach (var s in all) if (s.Open) s.Queue(m, droppable);
    }

    /// <summary>On the engine context; directly when already on it (PushState from
    /// a ProtectionChanged handler).</summary>
    private Dictionary<string, JsonElement> Capture() => OnEngine(() => EngineCatalog.Instance.CaptureState(_engine));

    private T OnEngine<T>(Func<T> work)
    {
        if (SynchronizationContext.Current == _ctx) return work();
        T result = default!;
        Exception? error = null;
        // Checked again ON the engine thread: a call queued behind the session's
        // shutdown must not run against an engine that is already disposed.
        _ctx.Send(_ =>
        {
            try { result = _stopping ? throw new ObjectDisposedException("engine", "the service is stopping") : work(); }
            catch (Exception ex) { error = ex; }
        }, null);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    /// <summary>One call, checked and run. Public for the bench.</summary>
    public EngineMessage Handle(EngineMessage call, EngineCaller caller)
    {
        var reply = new EngineMessage { Kind = "reply", Id = call.Id };
        if (TryGateMember(call, caller, reply)) return reply;
        if (!EngineCatalog.Instance.TryGet(call.Member, out var entry) || entry.Access == Access.Local
            || (entry.Method == null && entry.Property == null))
            return Deny(reply, $"'{call.Member}' cannot be called over the engine pipe");

        try
        {
            if (entry.Property != null)
            {
                if (call.Args.Count != 0) return Deny(reply, "a property takes no arguments");
                if (_permitted(caller, Access.Read) != CallVerdict.Allow) return Deny(reply, "not permitted");
                var p = entry.Property;
                object? v = OnEngine(() => p.GetValue(_engine));
                reply.Result = EngineWire.ToElement(v, p.PropertyType);
                reply.Ok = true;
                return reply;
            }

            var m = entry.Method!;
            var ps = m.GetParameters();
            if (call.Args.Count != ps.Length) return Deny(reply, $"{m.Name} takes {ps.Length} argument(s)");
            var args = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].IsOut) continue;
                var t = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType;
                args[i] = EngineWire.FromElement(call.Args[i], t);
            }

            var access = EngineAccessAttribute.Resolve(entry.Access, args);
            if (access == Access.Loosen && SetsCurrentValue(entry, args)) access = Access.Preference;
            switch (_permitted(caller, access))
            {
                case CallVerdict.Locked:
                    reply.Ok = false;
                    reply.ErrorKind = "locked";
                    reply.Error = $"{m.Name} loosens protection - Protect settings is on, so it needs an administrator unlock";
                    return reply;
                case CallVerdict.Denied:
                    DiagnosticLog.Log($"Engine pipe: refused {m.Name} from process {caller.ProcessId} "
                                    + $"({(caller.Verified ? "GunWall.exe" : "not the installed GunWall.exe")}, "
                                    + $"{(caller.InConsoleSession ? "console session" : "not the console session")}).");
                    return Deny(reply, $"{m.Name}: only the installed GunWall window at this PC's screen may change settings");
            }

            object? raw = OnEngine(() => m.Invoke(_engine, args));

            // A task is started on the engine thread and waited for HERE, so any
            // continuation it posts back to the engine thread can run.
            Type resultType = m.ReturnType;
            if (raw is Task task)
            {
                task.GetAwaiter().GetResult();
                if (resultType.IsGenericType)
                {
                    resultType = resultType.GetGenericArguments()[0];
                    raw = task.GetType().GetProperty("Result")!.GetValue(task);
                }
                else { resultType = typeof(void); raw = null; }
            }
            if (resultType != typeof(void)) reply.Result = EngineWire.ToElement(raw, resultType);
            for (int i = 0; i < ps.Length; i++)
                if (ps[i].IsOut)
                    reply.Outs.Add(EngineWire.ToElement(args[i], ps[i].ParameterType.GetElementType()!));
            reply.Ok = true;
            if (entry.Access is not (Access.Read or Access.ReadProfile))
            {
                reply.State = Capture();
                reply.StateSeq = Interlocked.Increment(ref _stateSeq);
            }
            return reply;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            reply.Ok = false;
            reply.Error = $"{inner.GetType().Name}: {inner.Message}";
            return reply;
        }
    }

    /// <summary>
    /// "SetX(value)" where value is what X already is (stage 4c). Settings saves every
    /// switch on the page at once, so changing the notification sound also sent
    /// SetTamperWatch(false) with the tamper watch already off - a "loosening" that
    /// loosens nothing, and with Protect settings on it asked for administrator
    /// approval. Only for a one-flag setter with a bool property of the same name.
    /// </summary>
    private bool SetsCurrentValue(EngineCatalog.Entry entry, object?[] args)
    {
        if (entry.Access is not (Access.TightenIfTrue or Access.TightenIfFalse)) return false;
        if (args.Length != 1 || args[0] is not bool wanted || !entry.Name.StartsWith("Set", StringComparison.Ordinal)) return false;
        string x = entry.Name[3..];
        foreach (var name in new[] { x, x + "Enabled", x + "Engaged" })   // SetTamperWatch -> TamperWatchEnabled, SetLockdown -> LockdownEngaged
        {
            if (!EngineCatalog.Instance.TryGet(name, out var prop) || prop.Property is not { } pi || pi.PropertyType != typeof(bool)
                || prop.Access is not (Access.Read or Access.ReadProfile)) continue;
            try { return OnEngine(() => (bool)pi.GetValue(_engine)!) == wanted; }
            catch { return false; }
        }
        return false;
    }

    /// <summary>The three unlock members (stage 4c) are about THIS caller, which no
    /// IEngine implementation knows - answered here, by the gate.</summary>
    private bool TryGateMember(EngineMessage call, EngineCaller caller, EngineMessage reply)
    {
        if (Gate == null) return false;   // no gate: the engine answers ("" / -1 - nothing locked)
        switch (call.Member)
        {
            case nameof(IEngine.BeginSettingsUnlock):
                if (!caller.Verified || !caller.InConsoleSession) { Deny(reply, "only the GunWall window at this PC's screen may unlock"); return true; }
                reply.Result = EngineWire.ToElement(Gate.Begin(caller.ProcessId), typeof(string));
                reply.Ok = true;
                return true;
            case nameof(IEngine.SettingsUnlockSecondsLeft):
                reply.Result = EngineWire.ToElement(Gate.SecondsLeft(caller.ProcessId), typeof(int));
                reply.Ok = true;
                return true;
            case nameof(IEngine.LockSettingsNow):
                Gate.Lock(caller.ProcessId);
                reply.Ok = true;
                return true;
            default:
                return false;
        }
    }

    private static EngineMessage Deny(EngineMessage reply, string why)
    {
        reply.Ok = false;
        reply.ErrorKind = "denied";
        reply.Error = why;
        return reply;
    }

    private void Remove(Session s) { lock (_gate) _sessions.Remove(s); }

    private sealed class Session
    {
        private readonly EngineServer _server;
        private readonly NamedPipeServerStream _pipe;
        private readonly EngineCaller _caller;
        private readonly BlockingCollection<EngineMessage> _out = new();
        private int _closed;
        private volatile bool _greeted;
        /// <summary>The hello reply is queued: pushes may follow it (0.99.226).</summary>
        public bool Open => _open;
        private volatile bool _open;

        public Session(EngineServer server, NamedPipeServerStream pipe, EngineCaller caller)
        {
            _server = server;
            _pipe = pipe;
            _caller = caller;
        }

        public void Begin()
        {
            new Thread(Run) { IsBackground = true, Name = "GunWall engine session" }.Start();
            new Thread(WriteLoop) { IsBackground = true, Name = "GunWall engine session writer" }.Start();
        }

        /// <summary>Never blocks. A window that has stopped reading is dropped
        /// instead of being waited for.</summary>
        public void Queue(EngineMessage m, bool droppable = false)
        {
            if (Volatile.Read(ref _closed) != 0) return;
            if (droppable && _out.Count >= MaxQueued / 2) return;
            if (_out.Count >= MaxQueued) { Close(); return; }
            try { _out.Add(m); } catch (InvalidOperationException) { /* closed meanwhile */ }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (var m in _out.GetConsumingEnumerable())
                {
                    try { EngineWire.Write(_pipe, m); }
                    catch (InvalidDataException)
                    {
                        // Too big for one message. A reply says so, rather than leave
                        // the window waiting out its timeout; a pushed message is
                        // dropped, never the connection.
                        if (m.Kind == "reply")
                            EngineWire.Write(_pipe, new EngineMessage
                            {
                                Kind = "reply", Id = m.Id, Ok = false,
                                Error = $"the answer is over the {EngineWire.MaxAssembledBytes / (1024 * 1024)} MB message limit",
                            });
                    }
                }
            }
            catch (Exception) { /* broken pipe: the reader ends the session */ }
            finally { Close(); }
        }

        private void Run()
        {
            // No hello in time: the pipe is closed, which ends the read below.
            using var helloTimer = new System.Threading.Timer(_ => { if (!_greeted) Close(); }, null, HelloTimeout, Timeout.InfiniteTimeSpan);
            try
            {
                var hello = EngineWire.Read(_pipe);
                if (hello is not { Kind: "hello" } || hello.Protocol != EngineWire.ProtocolVersion)
                {
                    _greeted = true;   // answered; the writer closes after sending
                    Queue(new EngineMessage { Kind = "hello", Protocol = EngineWire.ProtocolVersion,
                                              AppVersion = _server._appVersion, Ok = false,
                                              Error = "protocol version differs - restart the GunWall window" });
                    try { _out.CompleteAdding(); } catch { }
                    return;
                }
                _greeted = true;
                Queue(new EngineMessage
                {
                    Kind = "hello", Ok = true, Protocol = EngineWire.ProtocolVersion, AppVersion = _server._appVersion,
                    State = _server.Capture(), StateSeq = Interlocked.Increment(ref _server._stateSeq),
                });
                _open = true;   // after the hello is queued: nothing can overtake it
                while (!_server._stopping && Volatile.Read(ref _closed) == 0)
                {
                    var m = EngineWire.Read(_pipe);
                    if (m == null) break;
                    if (m.Kind != "call") break;   // anything else is a confused or hostile client
                    Queue(_server.Handle(m, _caller));
                }
                Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ObjectDisposedException)
            {
                // A window closing mid-message, or garbage: drop the connection.
                Close();
            }
            catch (Exception ex)
            {
                // Anything else ends this connection, never the service: an
                // exception escaping a background thread kills the process.
                DiagnosticLog.LogException("EngineServer.Session", ex);
                Close();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            try { _out.CompleteAdding(); } catch { }
            try { _pipe.Dispose(); } catch { }
            // Q3 = A: an unlock ends with its window.
            try { _server.Gate?.Lock(_caller.ProcessId); } catch { }
            _server.Remove(this);
        }
    }

    /// <summary>The connected process, from Windows. 0 elsewhere (the bench).</summary>
    private static int ClientProcessId(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) ? (int)pid : 0; }
        catch { return 0; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>SYSTEM and Administrators, and since 4c (0.99.229) the signed-in
    /// (interactive) user, whose window no longer runs elevated. Every caller is
    /// identified on connect (CallerIdentity) and only the installed GunWall.exe at
    /// the console may change anything. Never over the network: a deny entry for
    /// network logons, which Windows checks before any allow.</summary>
    private static NamedPipeServerStream DefaultPipe(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.InteractiveSid, null),
                // CreateNewInstance: a client opening InOut asks for GENERIC_WRITE,
                // which includes it (see HandoverService). The window checks that the
                // server it reached is the service's process (RemoteEngine.Connect).
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                                   PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, sec);
        }
        return new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                         PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public void Dispose()
    {
        _stopping = true;
        try { _cts.Cancel(); } catch { }
        try { _listening?.Dispose(); } catch { }
        Session[] all;
        lock (_gate) all = _sessions.ToArray();
        foreach (var s in all) s.Close();
        _accept?.Join(2000);
    }
}
