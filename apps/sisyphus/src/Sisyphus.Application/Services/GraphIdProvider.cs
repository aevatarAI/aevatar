namespace Sisyphus.Application.Services;

/// <summary>
/// Holds the resolved chrono-graph UUID for the "sisyphus" graph.
/// Populated by <see cref="GraphBootstrapService"/> on startup.
/// </summary>
public sealed class GraphIdProvider
{
    public const string GraphName = "sisyphus";

    private readonly TaskCompletionSource<string> _ready = new();

    /// <summary>The resolved UUID. Only available after bootstrap completes.</summary>
    public string? GraphId { get; private set; }

    /// <summary>Sets the resolved graph UUID. Called once by the bootstrap service.</summary>
    public void Set(string graphId)
    {
        GraphId = graphId;
        _ready.TrySetResult(graphId);
    }

    /// <summary>Waits until the graph UUID is resolved. Throws if bootstrap failed.</summary>
    public Task<string> WaitAsync(CancellationToken ct = default)
    {
        ct.Register(() => _ready.TrySetCanceled());
        return _ready.Task;
    }
}
