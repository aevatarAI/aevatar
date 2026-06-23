using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;

namespace Aevatar.Workflow.Application.Observatory;

// 06-19-workflow-run-observatory (C2): THE scope-enforcement seam for the read-only run viewer.
//   - scopeId is an INPUT parameter (never read from HttpContext).
//   - depends ONLY on query ports (current-state + artifact); no IActorDispatchPort, no actor runtime.
//   - List is filtered by scope at the source, so a caller can only ever enumerate their own runs.
//   - Per-run ownership is authoritative via the scope-stamped current-state snapshot: assert
//     snapshot.ScopeId == scopeId, else return null so the endpoint maps it to 404 (D8 — no existence
//     disclosure). Only then serve the timeline / graph (reused, runId-only, pure-read ports).
public sealed class WorkflowRunObservatoryQueryService
    : IWorkflowRunObservatoryQueryService, IWorkflowRunAdminOverviewQueryService
{
    private const int DefaultRunListTake = 100;
    private const int MaxRunListTake = 500;

    private readonly IWorkflowExecutionCurrentStateQueryPort _currentStateQueryPort;
    private readonly IWorkflowExecutionArtifactQueryPort _artifactQueryPort;

    public WorkflowRunObservatoryQueryService(
        IWorkflowExecutionCurrentStateQueryPort currentStateQueryPort,
        IWorkflowExecutionArtifactQueryPort artifactQueryPort)
    {
        _currentStateQueryPort = currentStateQueryPort ?? throw new ArgumentNullException(nameof(currentStateQueryPort));
        _artifactQueryPort = artifactQueryPort ?? throw new ArgumentNullException(nameof(artifactQueryPort));
    }

    public async Task<IReadOnlyList<ObservatoryRunSummary>> ListRunsForScopeAsync(
        string scopeId,
        ObservatoryRunListFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        if (normalizedScopeId.Length == 0)
            return [];

        var take = Math.Clamp(filter.Take <= 0 ? DefaultRunListTake : filter.Take, 1, MaxRunListTake);
        var snapshots = await _currentStateQueryPort.ListWorkflowActorCurrentStatesAsync(
            BuildListQuery(filter, take, normalizedScopeId),
            ct);

        // Status is pushed to the source query (so it narrows BEFORE the bounded Take); this in-memory pass
        // is a case-insensitive safety net over the same predicate.
        var statusFilter = filter.Status?.Trim();
        var summaries = snapshots
            .Where(snapshot => string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            .Select(ToRunSummary)
            .Where(summary => string.IsNullOrEmpty(statusFilter) ||
                              string.Equals(summary.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(summary => summary.StartedAtUtc ?? summary.UpdatedAtUtc)
            .ToList();

        return summaries;
    }

    // 06-20-observatory-admin-cross-scope (G3/G4): cross-scope overview. No ScopeId in the query => the projection
    //   returns runs across ALL scopes (recent-N, bounded by Take). No ownership gate. Authorization is enforced
    //   upstream at the endpoint (admin/operator only) BEFORE this is called. Status filter is applied within the
    //   recent-N window, mirroring the scope-bound list.
    public async Task<IReadOnlyList<ObservatoryRunSummary>> ListAllRunsAsync(
        ObservatoryRunListFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var take = Math.Clamp(filter.Take <= 0 ? DefaultRunListTake : filter.Take, 1, MaxRunListTake);
        var snapshots = await _currentStateQueryPort.ListWorkflowActorCurrentStatesAsync(
            BuildListQuery(filter, take, scopeId: null),
            ct);

        // Status pushed to the source query; in-memory pass is a case-insensitive safety net (see above).
        var statusFilter = filter.Status?.Trim();
        return snapshots
            .Select(ToRunSummary)
            .Where(summary => string.IsNullOrEmpty(statusFilter) ||
                              string.Equals(summary.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(summary => summary.StartedAtUtc ?? summary.UpdatedAtUtc)
            .ToList();
    }

    // Translates the observatory filter into the source query so status / origin / definition / time-range
    // are filtered (and the list recency-sorted) at the projection store, not after a bounded Take.
    private static WorkflowActorCurrentStateListQuery BuildListQuery(
        ObservatoryRunListFilter filter,
        int take,
        string? scopeId) =>
        new()
        {
            Take = take,
            ScopeId = scopeId ?? string.Empty,
            Status = filter.Status?.Trim() ?? string.Empty,
            RunOrigins = filter.Origins,
            DefinitionActorIds = filter.DefinitionActorIds,
            UpdatedFromUtc = filter.FromUtc,
            UpdatedToUtc = filter.ToUtc,
        };

    public async Task<ObservatoryRunDetail?> GetRunForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveOwnedRunAsync(scopeId, runId, ct);
        if (snapshot == null)
            return null;

        // One read gives the committed timeline + authoritative usage aggregate (no usage in the timeline
        // data map). Falls back to the snapshot summary if the run-isolated report has not materialized yet.
        var report = await _artifactQueryPort.GetWorkflowRunReportArtifactAsync(snapshot.ActorId, ct);
        if (report == null)
        {
            return new ObservatoryRunDetail
            {
                Summary = ToRunSummary(snapshot),
                Timeline = [],
                UsageTotals = new ObservatoryUsageTotals(),
            };
        }

        var viewEvents = report.Timeline
            .OrderBy(item => item.Timestamp)
            .Select(WorkflowRunObservatoryTimelineMapper.ToViewEvent)
            .ToList();

        return new ObservatoryRunDetail
        {
            Summary = ToRunSummary(snapshot),
            Timeline = viewEvents,
            UsageTotals = WorkflowRunObservatoryTimelineMapper.ToUsageTotals(report.Usage),
        };
    }

    public async Task<ObservatoryRunGraph?> GetRunGraphForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveOwnedRunAsync(scopeId, runId, ct);
        if (snapshot == null)
            return null;

        var subgraph = await _artifactQueryPort.GetWorkflowRunGraphExportSubgraphAsync(
            snapshot.ActorId,
            ct: ct);

        return new ObservatoryRunGraph
        {
            RootNodeId = subgraph.RootNodeId,
            Nodes = subgraph.Nodes
                .Select(node => new ObservatoryGraphNode
                {
                    NodeId = node.NodeId,
                    NodeType = node.NodeType,
                    // WorkflowStep nodes carry the bare stepId in their properties; surface it so the
                    // viewer can join the node to its committed timeline steps (run / actor nodes have none).
                    StepId = node.Properties != null && node.Properties.TryGetValue("stepId", out var stepId)
                        ? stepId
                        : string.Empty,
                })
                .ToList(),
            Edges = subgraph.Edges
                .Select(edge => new ObservatoryGraphEdge
                {
                    EdgeId = edge.EdgeId,
                    FromNodeId = edge.FromNodeId,
                    ToNodeId = edge.ToNodeId,
                    EdgeType = edge.EdgeType,
                    BranchKey = edge.Properties != null && edge.Properties.TryGetValue("branchKey", out var branchKey)
                        ? branchKey
                        : string.Empty,
                })
                .ToList(),
        };
    }

    // Ownership gate: the run is visible only when its scope-stamped current-state snapshot matches the
    // caller scope. Returning null on mismatch (or missing run) lets the endpoint answer 404 uniformly.
    private async Task<WorkflowActorSnapshot?> ResolveOwnedRunAsync(
        string scopeId,
        string runId,
        CancellationToken ct)
    {
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedScopeId.Length == 0 || normalizedRunId.Length == 0)
            return null;

        var snapshot = await _currentStateQueryPort.GetWorkflowActorCurrentStateAsync(normalizedRunId, ct);
        if (snapshot == null)
            return null;

        return string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal)
            ? snapshot
            : null;
    }

    private static ObservatoryRunSummary ToRunSummary(WorkflowActorSnapshot snapshot)
    {
        return new ObservatoryRunSummary
        {
            RunId = snapshot.ActorId,
            WorkflowName = snapshot.WorkflowName,
            Status = MapStatus(snapshot.CompletionStatus),
            Success = snapshot.LastSuccess,
            StartedAtUtc = snapshot.StartedAtUtc?.ToDateTimeOffset(),
            UpdatedAtUtc = snapshot.LastUpdatedAt,
            StateVersion = snapshot.StateVersion,
            ScopeId = snapshot.ScopeId,
            RunOrigin = snapshot.RunOrigin,
        };
    }

    private static string MapStatus(WorkflowRunCompletionStatus status) =>
        status switch
        {
            WorkflowRunCompletionStatus.Running => "running",
            WorkflowRunCompletionStatus.Completed => "completed",
            WorkflowRunCompletionStatus.TimedOut => "timed_out",
            WorkflowRunCompletionStatus.Failed => "failed",
            WorkflowRunCompletionStatus.Stopped => "stopped",
            WorkflowRunCompletionStatus.NotFound => "not_found",
            WorkflowRunCompletionStatus.Disabled => "disabled",
            _ => "unknown",
        };
}
