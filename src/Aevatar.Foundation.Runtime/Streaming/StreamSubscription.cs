// ─────────────────────────────────────────────────────────────
// StreamSubscription - stream subscription handle with async disposal.
// Wraps unsubscribe callback and guarantees dispose runs once.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Runtime.Streaming;

/// <summary>Async-disposable subscription handle used to unsubscribe from stream.</summary>
internal sealed class StreamSubscription : IAsyncDisposable
{
    private readonly Action _unsubscribe;
    private int _disposed;

    /// <summary>Creates subscription handle.</summary>
    /// <param name="unsubscribe">Callback invoked during unsubscribe.</param>
    public StreamSubscription(Action unsubscribe) => _unsubscribe = unsubscribe;

    /// <summary>Disposes subscription and invokes unsubscribe callback once.</summary>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0) _unsubscribe();
        return ValueTask.CompletedTask;
    }
}
