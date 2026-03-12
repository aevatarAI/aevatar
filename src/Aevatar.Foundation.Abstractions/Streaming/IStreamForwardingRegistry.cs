namespace Aevatar.Foundation.Abstractions.Streaming;

/// <summary>
/// Forwarding mode for stream-to-stream bindings.
/// </summary>
public enum StreamForwardingMode
{
    TransitOnly = 0,
    HandleThenForward = 1,
}

/// <summary>
/// Binding model used by stream-layer forwarding registry.
/// </summary>
public sealed class StreamForwardingBinding
{
    public string SourceStreamId { get; set; } = string.Empty;

    public string TargetStreamId { get; set; } = string.Empty;

    public StreamForwardingMode ForwardingMode { get; set; } = StreamForwardingMode.HandleThenForward;

    public HashSet<BroadcastDirection> DirectionFilter { get; set; } =
    [
        BroadcastDirection.Down,
        BroadcastDirection.Both,
    ];

    public HashSet<string> EventTypeFilter { get; set; } = new(StringComparer.Ordinal);

    public long Version { get; set; }

    public string? LeaseId { get; set; }
}

/// <summary>
/// Registry for stream forwarding bindings.
/// </summary>
public interface IStreamForwardingRegistry
{
    Task UpsertAsync(StreamForwardingBinding binding, CancellationToken ct = default);

    Task RemoveAsync(string sourceStreamId, string targetStreamId, CancellationToken ct = default);

    Task<IReadOnlyList<StreamForwardingBinding>> ListBySourceAsync(string sourceStreamId, CancellationToken ct = default);
}
