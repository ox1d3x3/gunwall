namespace GunWall.Services;

/// <summary>Per-application byte counts from the kernel (ETW), wherever they are
/// measured: in this process (<see cref="EtwByteMeterService"/>), or - for a window
/// running as the signed-in user, which may not start a kernel trace - in the
/// background service (<see cref="RemoteByteMeter"/>, 0.99.229).</summary>
public interface IByteMeter
{
    bool SessionActive { get; }
    long EventsTotal { get; }
    long ParseFailures { get; }
    string LastError { get; }
    bool Start();
    void Stop();
    /// <summary>The bytes since the last drain, by process id.</summary>
    Dictionary<uint, (long Down, long Up)> Drain();
}

/// <summary>One process's bytes since the last drain.</summary>
public sealed record PidBytes(uint Pid, long Down, long Up);

/// <summary>What the service's meter says, and the bytes it drained.</summary>
public sealed record ByteMeterReading(bool Active, long EventsTotal, long ParseFailures, string LastError, List<PidBytes> Bytes);

/// <summary>
/// The service's ETW meter, read by the window once a tick (0.99.229). A real-time
/// kernel trace needs administrator rights, which the window no longer has: before
/// this, it fell back to the estimate and said "ETW meter unavailable" every start.
/// One call per tick drains the bytes and carries the meter's state with them; the
/// state properties answer from that. After the service restarts its meter is off:
/// started again on the next drain while this window wants it.
/// </summary>
public sealed class RemoteByteMeter : IByteMeter
{
    private readonly Func<IEngine> _engine;
    private bool _wanted;

    public RemoteByteMeter(Func<IEngine> engine) => _engine = engine;

    public bool SessionActive { get; private set; }
    public long EventsTotal { get; private set; }
    public long ParseFailures { get; private set; }
    public string LastError { get; private set; } = "";

    public bool Start()
    {
        _wanted = true;
        try
        {
            bool ok = _engine().StartByteMeter();
            Apply(_engine().DrainByteMeter());
            SessionActive = ok && SessionActive;
            return SessionActive;
        }
        catch (Exception ex)
        {
            SessionActive = false;
            LastError = "the background service's meter: " + ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        _wanted = false;
        SessionActive = false;
        try { _engine().StopByteMeter(); } catch { /* service gone: nothing runs */ }
    }

    public Dictionary<uint, (long Down, long Up)> Drain()
    {
        var d = new Dictionary<uint, (long Down, long Up)>();
        try
        {
            var r = _engine().DrainByteMeter();
            if (r is not { Active: true } && _wanted)
            {
                // The service restarted (a new session, meter off): start it again.
                if (_engine().StartByteMeter()) r = _engine().DrainByteMeter();
            }
            Apply(r);
            foreach (var b in r?.Bytes ?? new List<PidBytes>()) d[b.Pid] = (b.Down, b.Up);
        }
        catch (Exception ex)
        {
            SessionActive = false;
            LastError = "the background service's meter: " + ex.Message;
        }
        return d;
    }

    private void Apply(ByteMeterReading? r)
    {
        if (r == null) { SessionActive = false; LastError = "no answer from the background service's meter"; return; }
        SessionActive = r.Active;
        EventsTotal = r.EventsTotal;
        ParseFailures = r.ParseFailures;
        LastError = r.LastError ?? "";
    }
}
