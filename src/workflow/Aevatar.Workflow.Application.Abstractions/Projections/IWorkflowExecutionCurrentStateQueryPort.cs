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

    public string RunId { get; init; } = string.Empty;

    public string WorkflowId { get; init; } = string.Empty;

    public string SearchText { get; init; } = string.Empty;

    // Inclusive updated-at window, filtered at the source on the document's updated_at field.
    public DateTimeOffset? UpdatedFromUtc { get; init; }

    public DateTimeOffset? UpdatedToUtc { get; init; }

    public string? Cursor { get; init; }

    public bool IncludeTotalCount { get; init; }
}

public sealed record WorkflowActorCurrentStatePage(
    IReadOnlyList<WorkflowActorSnapshot> Items,
    string? NextCursor,
    long? TotalCount);

public interface IWorkflowExecutionCurrentStateQueryPort
{
    bool WorkflowActorCurrentStateQueryEnabled { get; }

    // Refactor (iter165/cluster-003-workflow-actor-shaped-query-surface):
    //   Old pattern: query callers requested an actor snapshot by raw actorId through actor-query naming.
    //   New principle: query callers request a workflow actor current-state readmodel by actorId.
    Task<WorkflowActorSnapshot?> GetWorkflowActorCurrentStateAsync(
        string actorId,
        CancellationToken ct = default);

    async Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateAsync(
        string runId,
        CancellationToken ct = default)
    {
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0)
            return null;

        var page = await PageWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 2,
                RunId = normalizedRunId,
            },
            ct);
        var exactMatches = page.Items
            .Where(snapshot => string.Equals(snapshot.RunId?.Trim(), normalizedRunId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        return exactMatches.Length == 1 ? exactMatches[0] : null;
    }

    async Task<WorkflowActorSnapshot?> GetWorkflowRunCurrentStateForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedScopeId.Length == 0 || normalizedRunId.Length == 0)
            return null;

        var page = await PageWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = 2,
                ScopeId = normalizedScopeId,
                RunId = normalizedRunId,
            },
            ct);
        var exactMatches = page.Items
            .Where(snapshot =>
                string.Equals(snapshot.ScopeId?.Trim(), normalizedScopeId, StringComparison.Ordinal) &&
                string.Equals(snapshot.RunId?.Trim(), normalizedRunId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();

        return exactMatches.Length == 1 ? exactMatches[0] : null;
    }

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        int take = 200,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowActorSnapshot>> ListWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default);

    async Task<WorkflowActorCurrentStatePage> PageWorkflowActorCurrentStatesAsync(
        WorkflowActorCurrentStateListQuery query,
        CancellationToken ct = default)
    {
        var items = await ListWorkflowActorCurrentStatesAsync(query, ct);
        return new WorkflowActorCurrentStatePage(items, null, null);
    }

    Task<WorkflowActorProjectionState?> GetWorkflowActorProjectionStateAsync(
        string actorId,
        CancellationToken ct = default);
}
