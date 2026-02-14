using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projections.Abstractions;

/// <summary>
/// Per-run projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
{
    public required string RunId { get; init; }
    public required string RootActorId { get; init; }
    public required string WorkflowName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Input { get; init; }

    private readonly ConcurrentDictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the event is seen for the first time in this run context.
    /// </summary>
    public bool TryMarkProcessed(string eventId) => _processedEventIds.TryAdd(eventId, 0);
}
