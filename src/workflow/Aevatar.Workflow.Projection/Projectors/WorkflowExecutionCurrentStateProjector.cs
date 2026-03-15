using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionCurrentStateProjector
    : IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>
{
    private readonly IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ValueTask InitializeAsync(
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        _ = context;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask ProjectAsync(
        WorkflowExecutionProjectionContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        if (!CommittedStateEventEnvelope.TryUnpackState<WorkflowRunState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        var document = new WorkflowExecutionCurrentStateDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = context.CommandId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId,
            WorkflowName = string.IsNullOrWhiteSpace(state.WorkflowName) ? context.WorkflowName ?? string.Empty : state.WorkflowName,
            Status = state.Status ?? string.Empty,
            Compiled = state.Compiled,
            CompilationError = state.CompilationError ?? string.Empty,
            Input = state.Input ?? string.Empty,
            FinalOutput = state.FinalOutput ?? string.Empty,
            FinalError = state.FinalError ?? string.Empty,
            ExecutionStateCount = state.ExecutionStates.Count,
            Success = ResolveSuccess(state.Status),
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow),
        };

        await _writeDispatcher.UpsertAsync(document, ct);
    }

    public ValueTask CompleteAsync(
        WorkflowExecutionProjectionContext context,
        IReadOnlyList<WorkflowExecutionTopologyEdge> topology,
        CancellationToken ct = default)
    {
        _ = context;
        _ = topology;
        _ = ct;
        return ValueTask.CompletedTask;
    }

    private static bool? ResolveSuccess(string? status)
    {
        return (status ?? string.Empty).Trim() switch
        {
            "completed" => true,
            "failed" => false,
            _ => null,
        };
    }
}
