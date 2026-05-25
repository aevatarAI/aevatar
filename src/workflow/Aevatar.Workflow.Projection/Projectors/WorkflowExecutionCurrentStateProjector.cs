using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Core;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Projectors;

public sealed class WorkflowExecutionCurrentStateProjector
    : MappedCurrentStateProjectionMaterializer<
        WorkflowExecutionMaterializationContext,
        WorkflowRunState,
        WorkflowExecutionCurrentStateDocument>
{
    public WorkflowExecutionCurrentStateProjector(
        IProjectionWriteDispatcher<WorkflowExecutionCurrentStateDocument> writeDispatcher,
        IProjectionClock clock)
        : base(writeDispatcher, clock)
    {
    }

    protected override WorkflowExecutionCurrentStateDocument Map(
        MappedCurrentStateProjectionInput<WorkflowExecutionMaterializationContext, WorkflowRunState> input)
    {
        var context = input.Context;
        var stateEvent = input.StateEvent;
        var state = input.State;

        // Refactor (iter97/cluster-591): Old/New
        //   Old: every current-state projector hand-rolled committed-state unpack, timestamp resolution, and upsert.
        //   New: core mapped helper owns that projection shell; this projector keeps only WorkflowRunState -> read model mapping.
        return new WorkflowExecutionCurrentStateDocument
        {
            Id = context.RootActorId,
            RootActorId = context.RootActorId,
            CommandId = state.LastCommandId ?? string.Empty,
            DefinitionActorId = state.DefinitionActorId ?? string.Empty,
            RunId = string.IsNullOrWhiteSpace(state.RunId) ? context.RootActorId : state.RunId,
            WorkflowName = state.WorkflowName ?? string.Empty,
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
            UpdatedAt = input.ObservedAt,
        };
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
