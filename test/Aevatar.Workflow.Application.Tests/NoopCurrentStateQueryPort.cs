using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Tests;

internal sealed class NoopCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
{
    public bool WorkflowActorCurrentStateQueryEnabled => true;

    public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        _ = actorId;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<WorkflowActorSnapshot?>(null);
    }

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        _ = take;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
    }

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default)
    {
        _ = query;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
    }

    public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        _ = actorId;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<WorkflowActorProjectionState?>(null);
    }
}
