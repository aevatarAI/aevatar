using System.Collections.Concurrent;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection;

/// <summary>
/// Actor-scoped projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
    : IProjectionContext
{
    public required string ProjectionId { get; init; }
    public required string CommandId { get; set; }
    public required string RootActorId { get; init; }
    public required string WorkflowName { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public required string Input { get; set; }

    string IProjectionContext.ProjectionId => ProjectionId;

    private readonly ConcurrentDictionary<string, byte> _processedEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object?> _properties = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IWorkflowRunEventSink, string> _liveSinks = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Returns true when the event is seen for the first time in this projection context.
    /// </summary>
    public bool TryMarkProcessed(string eventId) => _processedEventIds.TryAdd(eventId, 0);

    /// <summary>
    /// Sets an extension property for this projection context.
    /// </summary>
    public void SetProperty(string key, object? value) => _properties[key] = value;

    /// <summary>
    /// Tries to read an extension property from this projection context.
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

    public void UpdateRunMetadata(
        string commandId,
        string workflowName,
        string input,
        DateTimeOffset startedAt)
    {
        if (!string.IsNullOrWhiteSpace(commandId))
            CommandId = commandId;
        if (!string.IsNullOrWhiteSpace(workflowName))
            WorkflowName = workflowName;

        Input = input;
        StartedAt = startedAt;
    }

    public void AttachLiveSink(string commandId, IWorkflowRunEventSink sink)
    {
        _liveSinks[sink] = commandId;
    }

    public void DetachLiveSink(IWorkflowRunEventSink sink) => _liveSinks.TryRemove(sink, out _);

    public IReadOnlyList<IWorkflowRunEventSink> GetLiveSinksSnapshot(string? commandId = null)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return _liveSinks.Keys.ToList();

        return _liveSinks
            .Where(x => string.Equals(x.Value, commandId, StringComparison.Ordinal))
            .Select(x => x.Key)
            .ToList();
    }
}
