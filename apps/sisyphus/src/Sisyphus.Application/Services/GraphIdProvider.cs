namespace Sisyphus.Application.Services;

/// <summary>
/// Holds the resolved chrono-graph UUIDs for read and write operations.
/// Populated by <see cref="GraphBootstrapService"/> on startup.
/// </summary>
public sealed class GraphIdProvider
{
    private readonly TaskCompletionSource<string> _readReady = new();
    private readonly TaskCompletionSource<string> _writeReady = new();

    /// <summary>Resolved UUID for read operations (snapshot, traverse).</summary>
    public string? ReadGraphId { get; private set; }

    /// <summary>Resolved UUID for write operations (create nodes/edges).</summary>
    public string? WriteGraphId { get; private set; }

    /// <summary>Sets the resolved read graph UUID.</summary>
    public void SetRead(string graphId)
    {
        ReadGraphId = graphId;
        _readReady.TrySetResult(graphId);
    }

    /// <summary>Sets the resolved write graph UUID.</summary>
    public void SetWrite(string graphId)
    {
        WriteGraphId = graphId;
        _writeReady.TrySetResult(graphId);
    }

    /// <summary>Waits until the read graph UUID is resolved.</summary>
    public Task<string> WaitReadAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>();
        ct.Register(() => tcs.TrySetCanceled());
        _readReady.Task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) tcs.TrySetResult(t.Result);
            else if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
            else tcs.TrySetCanceled();
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    /// <summary>Waits until the write graph UUID is resolved.</summary>
    public Task<string> WaitWriteAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<string>();
        ct.Register(() => tcs.TrySetCanceled());
        _writeReady.Task.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) tcs.TrySetResult(t.Result);
            else if (t.IsFaulted) tcs.TrySetException(t.Exception!.InnerExceptions);
            else tcs.TrySetCanceled();
        }, TaskScheduler.Default);
        return tcs.Task;
    }
}
