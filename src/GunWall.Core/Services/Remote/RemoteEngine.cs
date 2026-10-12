using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;

namespace GunWall.Services.Remote;

/// <summary>The service is not there - stopped, restarting, or gone mid-call.
/// The window waits for it to come back rather than calling it an error.</summary>
public sealed class EngineUnavailableException : IOException
{
    public EngineUnavailableException(string message) : base(message) { }
}

/// <summary>The service refused a call (Protect settings locked, or a member that
/// may not cross the pipe) - as opposed to the call failing.</summary>
public sealed class EngineDeniedException : InvalidOperationException
{
    public EngineDeniedException(string message) : base(message) { }
}

/// <summary>
/// The window's end of the engine pipe (stage 4a: built and bench-tested; 4b
/// switches the installed window to it). An <see cref="IEngine"/> made at run time
/// by <see cref="DispatchProxy"/>: every call is sent to the background service,
/// so the ~220 members need no hand-written forwarding.
///
/// Property reads that the service mirrors (protection, lockdown, pause, counts,
/// settings) are answered from the last state message, without a round trip: the
/// window reads some of them every second. Everything else waits for the reply.
/// </summary>
public class RemoteEngine : DispatchProxy, IDisposable
{
    private Stream? _pipe;
    private Thread? _reader;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<EngineMessage>> _pending = new();
    private readonly object _write = new();
    private long _nextId;
    private volatile Dictionary<string, JsonElement> _state = new();
    private long _stateSeq;
    private readonly object _stateGate = new();
    private readonly object _localGate = new();
    private SynchronizationContext? _eventContext;
    private Action? _protectionChanged;
    private volatile bool _closed;
    private static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    /// <summary>How long a call waits for the service before it fails.</summary>
    public TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The service's version from the handshake.</summary>
    public string ServiceVersion { get; private set; } = "";

    /// <summary>The pipe broke: the service stopped or restarted. Raised once, on
    /// the event context.</summary>
    public event Action? Disconnected;

    /// <summary>What the service's detection decided (packet log, activity,
    /// prompts), in order, on the event context (0.99.208).</summary>
    public event Action<DetectionResult>? Detected;

    /// <summary>Something the service's enforcement wants the user told.</summary>
    public event Action<UpkeepNotice>? Notice;

    /// <summary>Where events are raised: the window's UI thread. Settable because
    /// the connection is made before the window exists.</summary>
    public SynchronizationContext? EventContext { get => _eventContext; set => _eventContext = value; }

    /// <summary>Serves the members tagged Local - what the window does for itself
    /// (GeoIP lookups, exports, the VirusTotal key) - from a read-only copy of the
    /// profile in the window's own process. Null: Local members throw.</summary>
    public IEngine? LocalFallback { get; set; }

    /// <summary>Re-reads that copy. Called before a Local member runs if anything
    /// was changed through the service since the last read.</summary>
    public Action? ReloadLocal { get; set; }
    private int _localStale;

    /// <summary>The pipe is gone (or never fully opened).</summary>
    public bool IsClosed => _closed;

    /// <summary>Connects and completes the handshake, or throws. Events
    /// (ProtectionChanged, Disconnected) are raised on <paramref name="eventContext"/>
    /// - the window's UI thread - or on the reader thread when it is null.</summary>
    public static IEngine Connect(string appVersion, SynchronizationContext? eventContext,
                                  string pipeName = EngineWire.PipeName, int timeoutMs = 3000)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                                               System.Security.Principal.TokenImpersonationLevel.Identification);
        try
        {
            client.Connect(timeoutMs);
            return Attach(client, appVersion, eventContext);
        }
        catch { client.Dispose(); throw; }
    }

    /// <summary>Over an already-open stream (the bench).</summary>
    public static IEngine Attach(Stream stream, string appVersion, SynchronizationContext? eventContext)
    {
        var proxy = DispatchProxy.Create<IEngine, RemoteEngine>();
        var self = (RemoteEngine)(object)proxy;
        self._pipe = stream;
        self._eventContext = eventContext;
        EngineWire.Write(stream, new EngineMessage { Kind = "hello", Protocol = EngineWire.ProtocolVersion, AppVersion = appVersion });
        // The first "hello" is the answer; anything before it is skipped (0.99.226) -
        // a service that pushed a state or detection batch ahead of the reply made this
        // read the handshake as refused. Bounded, so a confused peer cannot hold us.
        EngineMessage? hello = null;
        for (int skipped = 0; skipped < 64; skipped++)
        {
            hello = EngineWire.Read(stream) ?? throw new IOException("the service closed the pipe during the handshake");
            if (hello.Kind == "hello") break;
        }
        if (hello == null || hello.Kind != "hello" || !hello.Ok)
            throw new InvalidOperationException(hello?.Error is { Length: > 0 } why ? why : "the service refused the handshake");
        self.ServiceVersion = hello.AppVersion;
        if (hello.State != null) { self._state = hello.State; self._stateSeq = hello.StateSeq; }
        self._reader = new Thread(self.ReadLoop) { IsBackground = true, Name = "GunWall engine client" };
        self._reader.Start();
        return proxy;
    }

    /// <summary>The RemoteEngine behind an IEngine from <see cref="Connect"/>, or null.</summary>
    public static RemoteEngine? Of(IEngine engine) => engine as RemoteEngine;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var m = targetMethod ?? throw new ArgumentNullException(nameof(targetMethod));
        args ??= Array.Empty<object?>();

        // Events: kept here, raised from state messages.
        if (m.Name == "add_ProtectionChanged") { _protectionChanged += (Action?)args[0]; return null; }
        if (m.Name == "remove_ProtectionChanged") { _protectionChanged -= (Action?)args[0]; return null; }

        string member = m.IsSpecialName && m.Name.StartsWith("get_", StringComparison.Ordinal) ? m.Name[4..] : m.Name;
        if (!EngineCatalog.Instance.TryGet(member, out var entry))
            throw new NotSupportedException($"IEngine.{member} is not in the engine catalog");
        if (entry.Access == Access.Local || (entry.Access == Access.ReadProfile && LocalFallback != null))
        {
            if (LocalFallback == null)
                throw new NotSupportedException($"IEngine.{member} is handled by the window itself, not the service");
            if (ReloadLocal != null && Interlocked.Exchange(ref _localStale, 0) == 1)
                lock (_localGate) { try { ReloadLocal.Invoke(); } catch { } }
            try { return m.Invoke(LocalFallback, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }

        // Mirrored property: no round trip.
        if (entry.Property != null && _state.TryGetValue(member, out var mirrored))
            return EngineWire.FromElement(mirrored, entry.Property.PropertyType);

        var ret = m.ReturnType;
        if (typeof(Task).IsAssignableFrom(ret))
        {
            var copy = (object?[])args.Clone();
            if (ret == typeof(Task)) return Task.Run(() => { Call(member, m, copy); });
            var t = ret.GetGenericArguments()[0];
            return typeof(RemoteEngine).GetMethod(nameof(RunTyped), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(t).Invoke(this, new object?[] { member, m, copy });
        }
        return Call(member, m, args);
    }

    /// <summary>A Task&lt;T&gt; for an async member, returned boxed (Invoke returns object).</summary>
    private object RunTyped<T>(string member, MethodInfo m, object?[] args) =>
        Task.Run(() => (T)Call(member, m, args)!);

    private object? Call(string member, MethodInfo m, object?[] args)
    {
        if (_closed) throw new EngineUnavailableException("the GunWall service is not connected");
        var ps = m.IsSpecialName ? Array.Empty<ParameterInfo>() : m.GetParameters();
        var msg = new EngineMessage { Kind = "call", Id = Interlocked.Increment(ref _nextId), Member = member };
        for (int i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType.IsByRef ? ps[i].ParameterType.GetElementType()! : ps[i].ParameterType;
            // An out parameter has nothing to send; a typed null would not even
            // serialize for a value type (out bool).
            msg.Args.Add(ps[i].IsOut ? NullElement : EngineWire.ToElement(args[i], t));
        }

        var tcs = new TaskCompletionSource<EngineMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[msg.Id] = tcs;
        try
        {
            lock (_write) EngineWire.Write(_pipe!, msg);
            bool done;
            try { done = tcs.Task.Wait(CallTimeout); }
            catch (AggregateException ae) when (ae.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ae.InnerException).Throw(); throw; }
            if (!done)
                throw new TimeoutException($"the GunWall service did not answer {member} in {CallTimeout.TotalSeconds:0} s");
        }
        finally { _pending.TryRemove(msg.Id, out _); }

        var reply = tcs.Task.Result;
        if (reply.State != null) { ApplyState(reply.State, reply.StateSeq); Volatile.Write(ref _localStale, 1); }
        if (!reply.Ok)
        {
            if (reply.ErrorKind == "denied") throw new EngineDeniedException(reply.Error);
            throw new InvalidOperationException(reply.Error);
        }
        int o = 0;
        for (int i = 0; i < ps.Length; i++)
            if (ps[i].IsOut && o < reply.Outs.Count)
                args[i] = EngineWire.FromElement(reply.Outs[o++], ps[i].ParameterType.GetElementType()!);

        Type rt = m.ReturnType;
        if (typeof(Task).IsAssignableFrom(rt)) rt = rt.IsGenericType ? rt.GetGenericArguments()[0] : typeof(void);
        return rt == typeof(void) ? null : EngineWire.FromElement(reply.Result, rt);
    }

    private void ReadLoop()
    {
        try
        {
            while (!_closed)
            {
                var m = EngineWire.Read(_pipe!);
                if (m == null) break;
                if (m.Kind == "reply" && _pending.TryGetValue(m.Id, out var tcs)) tcs.TrySetResult(m);
                else if (m.Kind == "state" && m.State != null)
                {
                    ApplyState(m.State, m.StateSeq);
                    // The service may have saved (a timer, a prompt it decided): the
                    // window's profile copy is checked again on its next read. The
                    // reload itself skips an unchanged file (0.99.221).
                    Volatile.Write(ref _localStale, 1);
                }
                else if (m.Kind == "detected" && m.Detected != null)
                {
                    var batch = m.Detected;
                    var h = Detected;
                    if (h != null) Raise(() => { foreach (var r in batch) h(r); });
                }
                else if (m.Kind == "notice" && m.Notices != null)
                {
                    var list = m.Notices;
                    var h = Notice;
                    if (h != null) Raise(() => { foreach (var n in list) h(n); });
                }
            }
        }
        catch (Exception) { /* broken pipe or garbage: treated as disconnected below */ }
        Close(raise: true);
    }

    /// <summary>The keys that mean "the posture changed" - the window repaints
    /// the switch, the pause line, lockdown, sidebar and tray on any of them.</summary>
    private static readonly string[] PostureKeys = { "StrictMode", "LockdownEngaged", "IsSnoozed", "SnoozeUntil" };

    private void ApplyState(Dictionary<string, JsonElement> next, long seq)
    {
        Dictionary<string, JsonElement> prev;
        lock (_stateGate)
        {
            if (seq <= _stateSeq) return;   // an older capture arriving late
            _stateSeq = seq;
            prev = _state;
            _state = next;
        }
        bool changed = false;
        foreach (var k in PostureKeys)
        {
            bool a = prev.TryGetValue(k, out var x), b = next.TryGetValue(k, out var y);
            if (a != b || (a && x.GetRawText() != y.GetRawText())) { changed = true; break; }
        }
        if (changed) Raise(_protectionChanged);
    }

    private void Raise(Action? handler)
    {
        if (handler == null) return;
        if (_eventContext != null) _eventContext.Post(_ => handler(), null);
        else handler();
    }

    private void Close(bool raise)
    {
        if (_closed) return;
        _closed = true;
        foreach (var p in _pending.Values) p.TrySetException(new EngineUnavailableException("the GunWall service disconnected"));
        try { _pipe?.Dispose(); } catch { }
        if (raise) Raise(Disconnected);
    }

    public void Dispose() => Close(raise: false);
}
