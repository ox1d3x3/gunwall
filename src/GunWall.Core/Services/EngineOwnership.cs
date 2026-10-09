using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GunWall.Services;

/// <summary>
/// Exactly one process runs the engine at a time: the GunWall window or the
/// background service (0.99.199, service split stage 2, "hand-over" shape).
///
/// Ownership is an exclusive lock on <c>engine.lock</c> in the data folder. Windows
/// releases it when the owner exits for ANY reason - a crash, a kill, a sign-out -
/// so a dead owner can never keep the other side out. Filters live in the kernel
/// and keep enforcing across the hand-over; only detection pauses for the moment
/// it takes.
///
/// The window asks the service to let go over a named pipe, waits for the lock,
/// and as a last resort stops the service.
/// </summary>
public static class EngineOwnership
{
    public const string LockFileName = "engine.lock";
    public const string PipeName = "GunWall.Handover.v1";

    /// <summary>The lock, or null if another process holds it.</summary>
    public static FileStream? TryAcquire(string lockPath)
    {
        try
        {
            return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                  FileShare.None, 1, FileOptions.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the lock.</summary>
    public static FileStream? WaitAcquire(string lockPath, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var s = TryAcquire(lockPath);
            if (s != null) return s;
            if (DateTime.UtcNow >= until || ct.IsCancellationRequested) return null;
            try { Task.Delay(250, ct).Wait(ct); } catch (OperationCanceledException) { return null; }
        }
    }

    /// <summary>Sends one line to the service and returns its one-line answer, or
    /// null if no service is listening.</summary>
    public static string? Ask(string pipeName, string request, TimeSpan timeout)
    {
        try
        {
            // Identification only: whatever answers on this name may learn who is
            // asking, never act as them.
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                                                       System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect((int)Math.Min(int.MaxValue, Math.Max(100, timeout.TotalMilliseconds / 4)));
            var bytes = Encoding.UTF8.GetBytes(request + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            return ReadLine(pipe, timeout);
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>One line, or null on timeout. Pipes have no read timeout of their
    /// own, so each byte is awaited with one.</summary>
    public static string? ReadLine(Stream s, TimeSpan timeout)
    {
        var buf = new List<byte>(64);
        var until = DateTime.UtcNow + timeout;
        var one = new byte[1];
        while (buf.Count < 256)
        {
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var read = s.ReadAsync(one, 0, 1);
            if (!read.Wait(left)) return null;
            int n = read.Result;
            if (n == 0) break;
            if (one[0] == (byte)'\n') break;
            buf.Add(one[0]);
        }
        return Encoding.UTF8.GetString(buf.ToArray()).Trim();
    }

    /// <summary>
    /// The window's claim at startup. Returns the held lock, or null if it could not
    /// be had (logged; the window then runs as it always did, which is no worse
    /// than before this existed).
    /// </summary>
    public static FileStream? ClaimForWindow(string lockPath, string pipeName, TimeSpan timeout,
                                             Action<string> log, Action? stopService)
    {
        var started = DateTime.UtcNow;
        var held = TryAcquire(lockPath);
        if (held != null) return held;

        // The service may be mid-way through starting its engine (at sign-in, the
        // cold-boot restore) and cannot let go until that finishes: the answer is
        // waited for with most of the budget.
        log("Engine hand-over: waiting for the background service to hand over the engine.");
        string? answer = Ask(pipeName, "release", timeout);
        log($"Engine hand-over: the background service holds the engine; asked it to release - answer: {answer ?? "no answer"}.");
        held = WaitAcquire(lockPath, TimeSpan.FromSeconds(answer == "released" ? 5 : 1));
        if (held == null && stopService != null)
        {
            // Last resort, and it WAITS for the service process to be gone, so two
            // engines never run at once.
            log("Engine hand-over: no release in time - stopping the background service.");
            try { stopService(); } catch (Exception ex) { log($"Engine hand-over: stopping the service failed: {ex.Message}"); }
            held = WaitAcquire(lockPath, TimeSpan.FromSeconds(5));
        }
        log(held != null
            ? $"Engine hand-over: the window owns the engine ({(DateTime.UtcNow - started).TotalMilliseconds:0} ms)."
            : "Engine hand-over: FAILED - the window could not take the engine; continuing as before.");
        return held;
    }
}
