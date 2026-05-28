using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Tests;

internal sealed class NoopCurrentStateQueryPort : IWorkflowExecutionCurrentStateQueryPort
{
    public bool WorkflowRunCurrentStateQueryEnabled => true;

    public Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        _ = actorId;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<WorkflowActorSnapshot?>(null);
    }

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowRunCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default)
    {
        _ = take;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkflowActorSnapshot>>([]);
    }

    public Task<WorkflowActorProjectionState?> GetWorkflowRunProjectionStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        _ = actorId;
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<WorkflowActorProjectionState?>(null);
    }
}
