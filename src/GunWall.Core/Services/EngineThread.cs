using System.Collections.Concurrent;

namespace GunWall.Services;

/// <summary>
/// One dedicated thread that runs everything posted to it, in order (0.99.199).
///
/// FirewallManager is not thread-safe. In the window, its UI thread serialises
/// every call; the background service has no UI thread, so it runs the engine
/// here instead - the same one-at-a-time guarantee, given to DetectionHost as its
/// engine context.
/// </summary>
public sealed class EngineThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();
    private readonly Thread _thread;

    public EngineThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (work, state) in _queue.GetConsumingEnumerable())
        {
            try { work(state); }
            catch (Exception ex) { DiagnosticLog.LogException("EngineThread", ex); }
        }
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _queue.Add((d, state)); }
        catch (InvalidOperationException) { /* completed: shutting down, drop */ }
    }

    public bool IsDisposed => _queue.IsAddingCompleted;

    /// <summary>Runs <paramref name="d"/> on the engine thread and waits for it.</summary>
    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread) { d(state); return; }
        if (_queue.IsAddingCompleted) throw new ObjectDisposedException(nameof(EngineThread));
        Exception? error = null;
        using var done = new ManualResetEventSlim();
        // Added directly, not through Post: Post drops work once the queue is
        // closed, and a Send whose work was dropped would wait here for ever - the
        // queue can close between the check above and this line.
        try { _queue.Add((s => { try { d(s); } catch (Exception ex) { error = ex; } finally { done.Set(); } }, state)); }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(EngineThread)); }
        done.Wait();
        if (error != null) throw new InvalidOperationException("Engine thread work failed: " + error.Message, error);
    }

    public void Invoke(Action work) => Send(_ => work(), null);

    public override SynchronizationContext CreateCopy() => this;

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(10));
    }
}
