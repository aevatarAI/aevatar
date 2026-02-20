using Aevatar.AI.Projection.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection;

/// <summary>
/// Actor-scoped projection context for CQRS read model updates.
/// </summary>
public sealed class WorkflowExecutionProjectionContext
    : IProjectionContext, IAIProjectionContext
{
    public required string ProjectionId { get; init; }
    public required string CommandId { get; set; }
    public required string RootActorId { get; init; }
    public required string WorkflowName { get; set; }
    public required DateTimeOffset StartedAt { get; set; }
    public required string Input { get; set; }

    string IProjectionContext.ProjectionId => ProjectionId;

    private readonly object _liveSinkGate = new();
    private readonly List<LiveSinkBinding> _liveSinkBindings = [];

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
        ArgumentNullException.ThrowIfNull(sink);
        lock (_liveSinkGate)
        {
            var index = _liveSinkBindings.FindIndex(x => ReferenceEquals(x.Sink, sink));
            if (index >= 0)
            {
                _liveSinkBindings[index] = new LiveSinkBinding(commandId, sink);
                return;
            }

            _liveSinkBindings.Add(new LiveSinkBinding(commandId, sink));
        }
    }

    public void DetachLiveSink(IWorkflowRunEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_liveSinkGate)
            _liveSinkBindings.RemoveAll(x => ReferenceEquals(x.Sink, sink));
    }

    public IReadOnlyList<IWorkflowRunEventSink> GetLiveSinksSnapshot(string? commandId = null)
    {
        lock (_liveSinkGate)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                return _liveSinkBindings.Select(x => x.Sink).ToList();

            return _liveSinkBindings
                .Where(x => string.Equals(x.CommandId, commandId, StringComparison.Ordinal))
                .Select(x => x.Sink)
                .ToList();
        }
    }

    public IReadOnlyList<IWorkflowRunEventSink> GetLiveSinksForCommand(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
            return [];

        return GetLiveSinksSnapshot(commandId);
    }

    private sealed record LiveSinkBinding(string CommandId, IWorkflowRunEventSink Sink);
}
