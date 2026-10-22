namespace SteamSwitcher.Services;

/// <summary>Serializes activation against Stop/Dispose and invalidates late async initializers.</summary>
internal sealed class AsyncRunLifetime
{
    internal sealed record Lease(long Generation, CancellationToken Token);
    private readonly object _sync = new();
    private long _generation;
    private int _running;
    private int _disposed;
    private CancellationTokenSource? _runCancellation;

    internal Lease? Begin()
    {
        CancellationTokenSource? previous;
        long generation;
        lock (_sync)
        {
            if (_disposed != 0) return null;
            previous = _runCancellation;
            _runCancellation = new CancellationTokenSource();
            _running = 1;
            generation = ++_generation;
        }
        // Cancel outside the lock: a cancellation callback that re-enters these
        // methods would otherwise deadlock on _sync.
        CancelAndDispose(previous);
        return new Lease(generation, _runCancellation.Token);
    }

    internal bool IsCurrent(Lease lease) => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _running) != 0 &&
        Volatile.Read(ref _generation) == lease.Generation && !lease.Token.IsCancellationRequested;

    internal bool TryActivate(Lease lease, Action activate)
    {
        lock (_sync)
        {
            if (!IsCurrent(lease)) return false;
            activate();
            return true;
        }
    }

    internal void Stop(Action stop, bool dispose = false)
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _running = 0;
            _generation++;
            if (dispose) _disposed = 1;
            cancellation = _runCancellation;
            _runCancellation = null;
        }
        // Cancel and run the stop callback outside the lock to avoid re-entrancy deadlocks.
        try { CancelAndDispose(cancellation); }
        finally { stop(); }
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (AggregateException ex) { AppLogger.Warn($"Media lifecycle cancellation callback failed: {ex.GetBaseException().Message}"); }
        finally { cancellation?.Dispose(); }
    }
}
