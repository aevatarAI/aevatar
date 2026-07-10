using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public sealed class WorkflowActorCurrentStateListQuery
{
    public int Take { get; init; } = 200;

    public WorkflowSagaStatus? SagaStatus { get; init; }

    public string ScopeId { get; init; } = string.Empty;

    public IReadOnlyList<string> DefinitionActorIds { get; init; } = [];

    // Observatory filter dimensions (06-23-observatory-run-coverage-filter). Empty/null = not filtered.
    public IReadOnlyList<string> RunOrigins { get; init; } = [];

    // Per-schedule filter (06-24-schedules-page-and-schedule-run-filter): runs produced by a specific
    // cron schedule. Matches the document's schedule_id; empty/null = not filtered.
    public IReadOnlyList<string> ScheduleIds { get; init; } = [];

    // Run status string as materialized on the current-state document (running/completed/failed/...).
    public string Status { get; init; } = string.Empty;

    // Inclusive updated-at window, filtered at the source on the document's updated_at field.
    public DateTimeOffset? UpdatedFromUtc { get; init; }

    public DateTimeOffset? UpdatedToUtc { get; init; }
}

public interface IWorkflowExecutionCurrentStateQueryPort
{
    bool WorkflowActorCurrentStateQueryEnabled { get; }

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: query callers requested an actor snapshot by raw actorId through actor-query naming.
    //   New principle: query callers request a workflow actor current-state readmodel by actorId.
    Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default);

    Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default);
}
