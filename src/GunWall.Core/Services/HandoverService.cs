using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GunWall.Services;

/// <summary>A running engine the service can stop: filters stay in the kernel,
/// detection and the profile are let go.</summary>
public interface IEngineSession : IDisposable { }

/// <summary>
/// The background service's whole job (0.99.199): run the engine whenever the
/// window does not.
///
/// A worker waits for <c>engine.lock</c>. Free means no window is running, so it
/// takes it and starts an engine session - protection, detection, prompts queued
/// for later. A window that opens asks over the pipe ("release"); the worker
/// stops the session, lets the lock go, answers "released", and waits a few
/// seconds so the window gets the lock first. When the window closes, the lock is
/// free again and the worker takes it back.
///
/// The engine itself is behind <see cref="IEngineSession"/>, so the hand-over is
/// tested without Windows (internel/bench/detector).
/// </summary>
public sealed class HandoverService : IDisposable
{
    private readonly string _lockPath;
    private readonly string _pipeName;
    private readonly Func<IEngineSession> _startSession;
    private readonly Action<string> _log;
    private readonly Func<string, NamedPipeServerStream> _createPipe;
    private readonly TimeSpan _backoffAfterRelease;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _releaseRequested = new(false);
    private readonly ManualResetEventSlim _released = new(true);
    private Thread? _worker, _listener;
    private volatile bool _owns;
    private int _pipeErrors;
    private long _lastRequestTicks;
    // Long enough to cover the instant between taking the lock and announcing it,
    // short enough that a window opened and closed quickly never delays the
    // service taking over afterwards.
    private static readonly TimeSpan RecentRequest = TimeSpan.FromSeconds(3);

    public bool OwnsEngine => _owns;
    public int Sessions { get; private set; }

    public HandoverService(string lockPath, string pipeName, Func<IEngineSession> startSession,
                           Action<string> log, Func<string, NamedPipeServerStream>? createPipe = null,
                           TimeSpan? backoffAfterRelease = null)
    {
        _lockPath = lockPath;
        _pipeName = pipeName;
        _startSession = startSession;
        _log = log;
        _createPipe = createPipe ?? DefaultPipe;
        _backoffAfterRelease = backoffAfterRelease ?? TimeSpan.FromSeconds(5);
    }

    public void Start()
    {
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "GunWall hand-over worker" };
        _listener = new Thread(ListenLoop) { IsBackground = true, Name = "GunWall hand-over pipe" };
        _worker.Start();
        _listener.Start();
    }

    private void WorkerLoop()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            var held = EngineOwnership.WaitAcquire(_lockPath, TimeSpan.FromSeconds(1), ct);
            if (held == null) continue;

            // Owning is announced BEFORE looking for a request, and the event is
            // reset before that: a request either lands before (seen by the time
            // check below) or after (seen by the event) - never in between.
            _releaseRequested.Reset();
            _released.Reset();
            _owns = true;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastRequestTicks) < RecentRequest.Ticks)
            {
                // A window asked a moment ago: give it straight back.
                _owns = false;
                held.Dispose();
                _released.Set();
                ct.WaitHandle.WaitOne(_backoffAfterRelease);
                continue;
            }

            IEngineSession? session = null;
            bool failed = false;
            try
            {
                _log("Service: no window is running - the service now runs the engine.");
                try
                {
                    session = _startSession();
                    Sessions++;
                }
                catch (Exception ex)
                {
                    // The lock is let go BEFORE waiting to retry, so a window can
                    // open meanwhile and run the engine itself.
                    _log($"Service: starting the engine failed: {ex.GetType().Name}: {ex.Message}. Retrying in 30 s.");
                    failed = true;
                }

                if (!failed) WaitHandle.WaitAny(new[] { ct.WaitHandle, _releaseRequested.WaitHandle });
                if (!failed)
                    _log(ct.IsCancellationRequested
                        ? "Service: stopping - engine released."
                        : "Service: the window asked for the engine - handing over.");
            }
            finally
            {
                try { session?.Dispose(); }
                catch (Exception ex) { _log($"Service: stopping the engine session failed: {ex.Message}"); }
                held.Dispose();
                _owns = false;
                _released.Set();
            }

            if (failed) { ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)); continue; }

            // Let the window take the lock before trying again. The request is
            // answered, so it must not also count as "recent" next time.
            if (_releaseRequested.IsSet && !ct.IsCancellationRequested)
            {
                ct.WaitHandle.WaitOne(_backoffAfterRelease);
                Interlocked.Exchange(ref _lastRequestTicks, 0);
            }
        }
    }

    private void ListenLoop()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = _createPipe(_pipeName);
                pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult();
                string? req = EngineOwnership.ReadLine(pipe, TimeSpan.FromSeconds(5));
                string reply = Handle(req);
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                pipe.Write(bytes, 0, bytes.Length);
                pipe.Flush();
                try { pipe.WaitForPipeDrain(); } catch { }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Backs off to a minute and logs sparingly: a name squatted by
                // something else must not fill service.log.
                _pipeErrors++;
                if (_pipeErrors == 1 || _pipeErrors % 100 == 0)
                    _log($"Service: hand-over pipe error #{_pipeErrors}: {ex.GetType().Name}: {ex.Message}");
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Min(60, _pipeErrors)));
            }
            finally { try { pipe?.Dispose(); } catch { } }
        }
    }

    private string Handle(string? request)
    {
        switch (request)
        {
            case "release":
                // Time-stamped even when not owning: the worker may be taking the
                // lock right now, and checks for a recent request before starting
                // anything. Only recent ones count, so a stale request never costs
                // a later hand-over a delay.
                Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
                if (!_owns) return "released";
                _releaseRequested.Set();
                return _released.Wait(TimeSpan.FromSeconds(60)) ? "released" : "timeout";
            case "status":
                return _owns ? "owner=service" : "owner=none";
            default:
                return "unknown";
        }
    }

    /// <summary>SYSTEM and Administrators only: the window runs elevated, and
    /// nothing else has any business asking the firewall to stand down.</summary>
    private static NamedPipeServerStream DefaultPipe(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            sec.AddAccessRule(new PipeAccessRule(
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                // CreateNewInstance as well: a client opening InOut asks for
                // GENERIC_WRITE, which on a pipe includes FILE_CREATE_PIPE_INSTANCE -
                // without it the elevated window is refused (reviewed 0.99.199).
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                                                   PipeOptions.Asynchronous, 0, 0, sec);
        }
        return new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(30));
        _listener?.Join(TimeSpan.FromSeconds(5));
    }
}
