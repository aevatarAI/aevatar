using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Studio.Tests;

internal sealed class WorkflowCurrentStateQueryPortStub : IWorkflowExecutionCurrentStateQueryPort
{
    public bool WorkflowActorCurrentStateQueryEnabled { get; set; } = true;

    public Dictionary<string, WorkflowActorSnapshot?> Snapshots { get; } =
        new(StringComparer.Ordinal);

    public Func<string, WorkflowActorSnapshot?>? Fallback { get; set; }

    public List<string> Queries { get; } = [];

    public Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default)
    {
        Queries.Add(actorId);
        return Task.FromResult(
            Snapshots.TryGetValue(actorId, out var snapshot)
                ? snapshot
                : Fallback?.Invoke(actorId));
    }

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default) => throw new NotSupportedException();

    public static WorkflowActorSnapshot FromServiceRun(
        ServiceRunSnapshot run,
        WorkflowRunCompletionStatus? completionStatus = null,
        bool? lastSuccess = null,
        string? lastOutput = null,
        string? lastError = null,
        long? stateVersion = null)
    {
        var resolvedStatus = completionStatus ?? run.Status switch
        {
            ServiceRunStatus.Completed => WorkflowRunCompletionStatus.Completed,
            ServiceRunStatus.Failed => WorkflowRunCompletionStatus.Failed,
            ServiceRunStatus.Stopped => WorkflowRunCompletionStatus.Stopped,
            ServiceRunStatus.OutcomeUncertain => WorkflowRunCompletionStatus.Unknown,
            _ => WorkflowRunCompletionStatus.Running,
        };
        var resolvedSuccess = lastSuccess ?? run.Status switch
        {
            ServiceRunStatus.Completed => true,
            ServiceRunStatus.Failed or ServiceRunStatus.Stopped => false,
            _ => null,
        };
        return new WorkflowActorSnapshot
        {
            ActorId = run.TargetActorId,
            RunId = run.RunId,
            ScopeId = run.ScopeId,
            CompletionStatus = resolvedStatus,
            StateVersion = stateVersion ?? run.StateVersion,
            LastSuccess = resolvedSuccess,
            LastOutput = lastOutput ?? run.LastOutput,
            LastError = lastError ?? run.LastError,
            LastCommandId = run.CommandId,
        };
    }
}
