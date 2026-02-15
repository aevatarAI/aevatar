using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projection.WorkflowExecution;

/// <summary>
/// Per-run projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
    : IProjectionRunContext
{
    public required string RunId { get; init; }
    public required string RootActorId { get; init; }
    public required string WorkflowName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required string Input { get; init; }

    private readonly ConcurrentDictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true when the event is seen for the first time in this run context.
    /// </summary>
    public bool TryMarkProcessed(string eventId) => _processedEventIds.TryAdd(eventId, 0);

    /// <summary>
    /// Sets an extension property for this run context.
    /// </summary>
    public void SetProperty(string key, object? value) => _properties[key] = value;

    /// <summary>
    /// Tries to read an extension property from this run context.
    /// </summary>
    public bool TryGetProperty<T>(string key, out T? value)
    {
        if (_properties.TryGetValue(key, out var raw) && raw is T casted)
        {
            value = casted;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Removes an extension property.
    /// </summary>
    public bool RemoveProperty(string key) => _properties.TryRemove(key, out _);
}
