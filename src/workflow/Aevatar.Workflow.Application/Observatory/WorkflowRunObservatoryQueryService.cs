using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Abstractions.Security;

namespace Aevatar.Workflow.Application.Observatory;

// 06-19-workflow-run-observatory (C2): THE scope-enforcement seam for the read-only run viewer.
//   - scopeId is an INPUT parameter (never read from HttpContext).
//   - depends ONLY on query ports (current-state + artifact); no IActorDispatchPort, no actor runtime.
//   - List is filtered by scope at the source, so a caller can only ever enumerate their own runs.
//   - Per-run ownership is authoritative via the scope-stamped current-state snapshot: assert
//     snapshot.ScopeId == scopeId, else return null so the endpoint maps it to 404 (D8 — no existence
//     disclosure). Only then serve the timeline / graph (reused, runId-only, pure-read ports).
public sealed class WorkflowRunObservatoryQueryService
    : IWorkflowRunObservatoryQueryService, IWorkflowRunAdminQueryService
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

    public async Task<WorkflowActivityRunFeedPage> ListActivityRunsForScopeAsync(
        string scopeId,
        WorkflowActivityRunFeedFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        if (normalizedScopeId.Length == 0)
            return new WorkflowActivityRunFeedPage();

        var take = ResolveActivityTake(filter);
        var page = await PageActivityRunsAsync(
            filter,
            normalizedScopeId,
            take,
            filter.Cursor,
            filter.IncludeTotalCount,
            ct);

        // Implement (issue #3250):
        //   Behavior: Activity rows expose backend-owned facts from the materialized current-state document.
        //   Why this shape: Scope and status are still checked after the store query as a defense-in-depth gate.
        var statusFilter = filter.Status?.Trim();
        var rows = page.Items
            .Where(snapshot => string.Equals(snapshot.ScopeId, normalizedScopeId, StringComparison.Ordinal))
            .Select(ToActivityRunFeedRow)
            .Where(row => string.IsNullOrEmpty(statusFilter) ||
                          string.Equals(row.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasMore = await HasNextActivityPageAsync(filter, normalizedScopeId, page.NextCursor, statusFilter, ct);
        return ToActivityRunFeedPage(page, rows, hasMore);
    }

    // 06-20-observatory-admin-cross-scope (G3/G4): cross-scope overview. No ScopeId in the query => the projection
    //   returns runs across ALL scopes (recent-N, bounded by Take). No ownership gate. Authorization is enforced
    //   upstream at the endpoint (admin/operator only) BEFORE this is called. Filters are applied by the
    //   projection store before the recent-N result is bounded by Take.
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

    public async Task<WorkflowActivityRunFeedPage> ListAllActivityRunsAsync(
        WorkflowActivityRunFeedFilter filter,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var take = ResolveActivityTake(filter);
        var page = await PageActivityRunsAsync(
            filter,
            scopeId: null,
            take,
            filter.Cursor,
            filter.IncludeTotalCount,
            ct);

        var statusFilter = filter.Status?.Trim();
        var rows = page.Items
            .Select(ToActivityRunFeedRow)
            .Where(row => string.IsNullOrEmpty(statusFilter) ||
                          string.Equals(row.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var hasMore = await HasNextActivityPageAsync(filter, scopeId: null, page.NextCursor, statusFilter, ct);
        return ToActivityRunFeedPage(page, rows, hasMore);
    }

    public async Task<ObservatoryRunDetail?> GetRunAsync(
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveRunAsync(runId, ct);
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ScopeId))
            return null;

        return await BuildRunDetailAsync(snapshot, ct);
    }

    public async Task<ObservatoryRunGraph?> GetRunGraphAsync(
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveRunAsync(runId, ct);
        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.ScopeId))
            return null;

        return await BuildRunGraphAsync(snapshot, ct);
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
            ScheduleIds = filter.ScheduleIds,
            UpdatedFromUtc = filter.FromUtc,
            UpdatedToUtc = filter.ToUtc,
        };

    private async Task<WorkflowActorCurrentStatePage> PageActivityRunsAsync(
        WorkflowActivityRunFeedFilter filter,
        string? scopeId,
        int take,
        string? cursor,
        bool includeTotalCount,
        CancellationToken ct) =>
        await _currentStateQueryPort.PageWorkflowActorCurrentStatesAsync(
            new WorkflowActorCurrentStateListQuery
            {
                Take = take,
                ScopeId = scopeId ?? string.Empty,
                Status = filter.Status?.Trim() ?? string.Empty,
                RunOrigins = filter.Origins,
                DefinitionActorIds = filter.DefinitionActorIds,
                ScheduleIds = filter.ScheduleIds,
                WorkflowId = filter.WorkflowId?.Trim() ?? string.Empty,
                SearchText = filter.SearchText?.Trim() ?? string.Empty,
                UpdatedFromUtc = filter.FromUtc,
                UpdatedToUtc = filter.ToUtc,
                Cursor = cursor,
                IncludeTotalCount = includeTotalCount,
            },
            ct);

    private async Task<bool> HasNextActivityPageAsync(
        WorkflowActivityRunFeedFilter filter,
        string? scopeId,
        string? cursor,
        string? statusFilter,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return false;

        var page = await PageActivityRunsAsync(
            filter,
            scopeId,
            take: 1,
            cursor,
            includeTotalCount: false,
            ct);

        return page.Items
            .Where(snapshot => scopeId == null || string.Equals(snapshot.ScopeId, scopeId, StringComparison.Ordinal))
            .Select(ToActivityRunFeedRow)
            .Any(row => string.IsNullOrEmpty(statusFilter) ||
                        string.Equals(row.Status, statusFilter, StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveActivityTake(WorkflowActivityRunFeedFilter filter) =>
        Math.Clamp(filter.Take <= 0 ? DefaultRunListTake : filter.Take, 1, MaxRunListTake);

    private static WorkflowActivityRunFeedPage ToActivityRunFeedPage(
        WorkflowActorCurrentStatePage source,
        IReadOnlyList<WorkflowActivityRunFeedRow> rows,
        bool hasMore) =>
        new()
        {
            Items = rows,
            NextCursor = hasMore && !string.IsNullOrWhiteSpace(source.NextCursor) ? source.NextCursor : null,
            HasMore = hasMore,
            TotalCount = source.TotalCount,
        };

    public async Task<ObservatoryRunDetail?> GetRunForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveOwnedRunAsync(scopeId, runId, ct);
        if (snapshot == null)
            return null;

        return await BuildRunDetailAsync(snapshot, ct);
    }

    private async Task<ObservatoryRunDetail> BuildRunDetailAsync(
        WorkflowActorSnapshot snapshot,
        CancellationToken ct)
    {
        var summary = ToRunSummary(snapshot);
        var graph = await BuildRunGraphAsync(snapshot, ct);
        // One read gives the committed timeline + authoritative usage aggregate (no usage in the timeline
        // data map). Falls back to the snapshot summary if the run-isolated report has not materialized yet.
        var report = await _artifactQueryPort.GetWorkflowRunReportArtifactAsync(snapshot.ActorId, ct);
        if (report == null || report.StateVersion != snapshot.StateVersion)
        {
            var reportSectionStatus = report == null
                ? UnavailableSection(snapshot.StateVersion, "Run report artifact has not materialized.")
                : VersionMismatchSection(
                    snapshot.StateVersion,
                    report.StateVersion,
                    "Run report artifact source version does not match the current-state detail version.");
            return new ObservatoryRunDetail
            {
                Summary = summary,
                Initiator = ToActivityInitiatorSummary(snapshot.ActivityInitiator),
                InputSummary = snapshot.InputSummary,
                Sections = new ObservatoryRunDetailSectionVersions
                {
                    Overview = AlignedSection(snapshot.StateVersion),
                    Steps = reportSectionStatus,
                    Timeline = reportSectionStatus,
                    ExecutionPath = ToSectionVersion(graph),
                },
                Timeline = [],
                Operations = [],
                Diagnostics = BuildDiagnostics(snapshot, report: null, steps: [], viewEvents: []),
                UsageTotals = new ObservatoryUsageTotals(),
                ExecutionPath = graph,
                RecoveryCapability = CloneRecoveryCapability(snapshot),
                Lineage = CloneLineage(snapshot),
            };
        }

        // Merge the committed role-reply content (the actual LLM/agent responses) into the role.reply
        // timeline events, matched per role id in commit order, so the detail shows the real response text
        // (the timeline event itself only carries the role id). One queue per role, drained in time order.
        var roleReplyByRole = report.RoleReplies
            .GroupBy(reply => reply.RoleId ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => new Queue<string>(
                    group.OrderBy(reply => reply.Timestamp).Select(reply => reply.Content ?? string.Empty)),
                StringComparer.Ordinal);

        var viewEvents = report.Timeline
            .OrderBy(item => item.Timestamp)
            .Select(item => WorkflowRunObservatoryTimelineMapper.ToViewEvent(
                item,
                ResolveRoleReplyContent(item, roleReplyByRole)))
            .ToList();
        var steps = report.Steps.Select(ToStepDetail).ToList();

        return new ObservatoryRunDetail
        {
            Summary = summary,
            Initiator = ToActivityInitiatorSummary(snapshot.ActivityInitiator),
            InputSummary = snapshot.InputSummary,
            Sections = new ObservatoryRunDetailSectionVersions
            {
                Overview = AlignedSection(snapshot.StateVersion),
                Steps = AlignedSection(snapshot.StateVersion),
                Timeline = AlignedSection(snapshot.StateVersion),
                ExecutionPath = ToSectionVersion(graph),
            },
            // Final result is fully materialized (not truncated), so the viewer can show it honestly.
            Input = report.Input,
            FinalOutput = report.FinalOutput,
            FinalError = report.FinalError,
            Diagnostics = BuildDiagnostics(snapshot, report, steps, viewEvents),
            Steps = steps,
            Timeline = viewEvents,
            Operations = report.Operations
                .OrderBy(operation => operation.ProgressSequence > 0 ? 0 : 1)
                .ThenBy(operation => operation.ProgressSequence > 0 ? operation.ProgressSequence : 0)
                .ThenBy(operation => operation.StartedAt ?? operation.CompletedAt ?? DateTimeOffset.MaxValue)
                .ThenBy(operation => operation.SessionId, StringComparer.Ordinal)
                .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
                .Select(ToOperationDetail)
                .ToList(),
            ExecutionPath = graph,
            Statistics = ToStatistics(report.Summary),
            UsageTotals = WorkflowRunObservatoryTimelineMapper.ToUsageTotals(report.Usage),
            RecoveryCapability = CloneRecoveryCapability(snapshot),
            Lineage = CloneLineage(snapshot),
        };
    }

    private static ObservatoryOperationDetail ToOperationDetail(WorkflowRunOperation operation) =>
        new()
        {
            SessionId = operation.SessionId,
            OperationId = operation.OperationId,
            ProgressSequence = operation.ProgressSequence,
            Round = operation.Round,
            Kind = operation.Kind switch
            {
                WorkflowRuntimeOperationKind.Model => "model",
                WorkflowRuntimeOperationKind.Tool => "tool",
                _ => "unknown",
            },
            StartedAtUtc = operation.StartedAt,
            CompletedAtUtc = operation.CompletedAt,
            RoleActorId = operation.RoleActorId,
            Model = operation.Model,
            Provider = operation.Provider,
            InputSummary = operation.InputSummary,
            AvailableToolNames = operation.AvailableToolNames,
            Output = operation.Output,
            ReasoningContent = operation.ReasoningContent,
            FinishReason = operation.FinishReason,
            Usage = WorkflowRunObservatoryTimelineMapper.ToUsageTotals(operation.Usage),
            Success = operation.Success,
            Error = operation.Error,
            ToolCallId = operation.ToolCallId,
            ToolName = operation.ToolName,
            ArgumentsJson = operation.ArgumentsJson,
            ResultJson = operation.ResultJson,
            DurationMs = operation.DurationMs,
        };

    // role.reply timeline events carry the role id (e.g. "writer") in Message; the response text lives in the
    // committed role-reply artifact. Dequeue the next reply for that role in commit order.
    private static string ResolveRoleReplyContent(
        WorkflowRunTimelineEvent item,
        IReadOnlyDictionary<string, Queue<string>> roleReplyByRole)
    {
        if (!string.Equals(item.Stage, "role.reply", StringComparison.Ordinal))
            return string.Empty;
        var roleId = item.Message ?? string.Empty;
        return roleReplyByRole.TryGetValue(roleId, out var queue) && queue.Count > 0
            ? queue.Dequeue()
            : string.Empty;
    }

    public async Task<ObservatoryRunGraph?> GetRunGraphForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default)
    {
        var snapshot = await ResolveOwnedRunAsync(scopeId, runId, ct);
        if (snapshot == null)
            return null;

        return await BuildRunGraphAsync(snapshot, ct);
    }

    private async Task<ObservatoryRunGraph> BuildRunGraphAsync(
        WorkflowActorSnapshot snapshot,
        CancellationToken ct)
    {
        var subgraph = await _artifactQueryPort.GetWorkflowRunGraphExportSubgraphAsync(
            snapshot.ActorId,
            ct: ct);

        var status = ResolveGraphVersionStatus(snapshot.StateVersion, subgraph.SourceStateVersion);
        if (status.VersionStatus != ObservatoryRunDetailSectionVersionStatus.Aligned)
        {
            return new ObservatoryRunGraph
            {
                RootNodeId = subgraph.RootNodeId,
                DetailStateVersion = status.DetailStateVersion,
                SourceStateVersion = status.SourceStateVersion,
                VersionStatus = status.VersionStatus,
                VersionReason = status.Reason,
            };
        }

        return new ObservatoryRunGraph
        {
            RootNodeId = subgraph.RootNodeId,
            DetailStateVersion = status.DetailStateVersion,
            SourceStateVersion = status.SourceStateVersion,
            VersionStatus = status.VersionStatus,
            VersionReason = status.Reason,
            Nodes = subgraph.Nodes
                .Select(node => new ObservatoryGraphNode
                {
                    NodeId = node.NodeId,
                    NodeType = node.NodeType,
                    DisplayName = node.Properties != null && node.Properties.TryGetValue("displayName", out var displayName)
                        ? displayName
                        : string.Empty,
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

    private static ObservatoryRunDetailSectionVersion ResolveGraphVersionStatus(
        long detailStateVersion,
        long sourceStateVersion)
    {
        if (sourceStateVersion <= 0)
        {
            return UnavailableSection(
                detailStateVersion,
                "Execution path graph source version is unavailable.");
        }

        return sourceStateVersion == detailStateVersion
            ? AlignedSection(detailStateVersion)
            : VersionMismatchSection(
                detailStateVersion,
                sourceStateVersion,
                "Execution path graph source version does not match the current-state detail version.");
    }

    private static ObservatoryRunDetailSectionVersion ToSectionVersion(ObservatoryRunGraph graph) =>
        new()
        {
            DetailStateVersion = graph.DetailStateVersion,
            SourceStateVersion = graph.SourceStateVersion,
            VersionStatus = graph.VersionStatus,
            Reason = graph.VersionReason,
        };

    private static ObservatoryRunDetailSectionVersion AlignedSection(long stateVersion) =>
        new()
        {
            DetailStateVersion = stateVersion,
            SourceStateVersion = stateVersion,
            VersionStatus = ObservatoryRunDetailSectionVersionStatus.Aligned,
        };

    private static ObservatoryRunDetailSectionVersion UnavailableSection(long detailStateVersion, string reason) =>
        new()
        {
            DetailStateVersion = detailStateVersion,
            VersionStatus = ObservatoryRunDetailSectionVersionStatus.Unavailable,
            Reason = reason,
        };

    private static ObservatoryRunDetailSectionVersion VersionMismatchSection(
        long detailStateVersion,
        long sourceStateVersion,
        string reason) =>
        new()
        {
            DetailStateVersion = detailStateVersion,
            SourceStateVersion = sourceStateVersion,
            VersionStatus = ObservatoryRunDetailSectionVersionStatus.VersionMismatch,
            Reason = reason,
        };

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

    private async Task<WorkflowActorSnapshot?> ResolveRunAsync(
        string runId,
        CancellationToken ct)
    {
        var normalizedRunId = runId?.Trim() ?? string.Empty;
        if (normalizedRunId.Length == 0)
            return null;

        return await _currentStateQueryPort.GetWorkflowActorCurrentStateAsync(normalizedRunId, ct);
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
            Lineage = CloneLineage(snapshot),
        };
    }

    private static WorkflowActivityRunFeedRow ToActivityRunFeedRow(WorkflowActorSnapshot snapshot)
    {
        var completedAtUtc = snapshot.CompletedAtUtc?.ToDateTimeOffset();
        return new WorkflowActivityRunFeedRow
        {
            RunId = snapshot.RunId,
            ActorId = snapshot.ActorId,
            WorkflowId = snapshot.WorkflowId,
            WorkflowName = snapshot.WorkflowName,
            ScopeId = snapshot.ScopeId,
            Status = MapStatus(snapshot.CompletionStatus),
            RunOrigin = snapshot.RunOrigin,
            Success = snapshot.LastSuccess,
            Initiator = ToActivityInitiatorSummary(snapshot.ActivityInitiator),
            InputSummary = snapshot.InputSummary,
            CurrentStep = ToActivityStepSummary(snapshot.ActivityCurrentStep),
            FirstFailure = ToActivityFailureSummary(snapshot.ActivityFirstFailure),
            Waiting = ToActivityWaitingSummary(snapshot.ActivityWaiting),
            StartedAtUtc = snapshot.StartedAtUtc?.ToDateTimeOffset(),
            CompletedAtUtc = completedAtUtc,
            UpdatedAtUtc = snapshot.LastUpdatedAt,
            // Fix (review round 1, F1):
            //   Activity rows exposed duration whenever completedAt existed, collapsing missing duration to 0.
            //   Read the optional snapshot field so completed-without-start stays unavailable.
            DurationMs = completedAtUtc == null || !snapshot.HasDurationMs ? null : snapshot.DurationMs,
            StateVersion = snapshot.StateVersion,
            RecoveryCapability = CloneRecoveryCapability(snapshot),
            Lineage = CloneLineage(snapshot),
        };
    }

    private static WorkflowRunRecoveryCapability CloneRecoveryCapability(WorkflowActorSnapshot snapshot)
    {
        var source = snapshot.RecoveryCapability;
        return new WorkflowRunRecoveryCapability
        {
            WorkflowDefinitionRevisionId = source?.WorkflowDefinitionRevisionId ?? string.Empty,
            WorkflowDefinitionVersion = source?.WorkflowDefinitionVersion ?? 0,
            RetryFailedStep = CloneRecoveryActionCapability(source?.RetryFailedStep),
            RunAgain = CloneRecoveryActionCapability(source?.RunAgain),
        };
    }

    private static WorkflowRecoveryActionCapability CloneRecoveryActionCapability(
        WorkflowRecoveryActionCapability? source) =>
        source?.Clone() ?? new WorkflowRecoveryActionCapability
        {
            Eligibility = WorkflowRecoveryEligibility.Unavailable,
            UnavailableReasonCode = WorkflowRecoveryUnavailableReasonCode.LegacyUnavailable,
            UnavailableReason = "Recovery capability is unavailable for this legacy run.",
        };

    private static Aevatar.Workflow.Abstractions.WorkflowRunLineage CloneLineage(WorkflowActorSnapshot snapshot) =>
        snapshot.Lineage?.Clone() ?? new Aevatar.Workflow.Abstractions.WorkflowRunLineage
        {
            Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            UnavailableReason = "Run lineage is unavailable for this legacy run.",
            RetryFork = new Aevatar.Workflow.Abstractions.WorkflowRunRetryForkLineage
            {
                Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            },
            SubWorkflow = new Aevatar.Workflow.Abstractions.WorkflowRunSubWorkflowLineage
            {
                Availability = Aevatar.Workflow.Abstractions.WorkflowRunLineageAvailability.LegacyUnavailable,
            },
        };

    private static WorkflowActivityRunInitiatorSummary ToActivityInitiatorSummary(
        WorkflowRunActivityInitiatorSnapshot? source) =>
        source == null
            ? new WorkflowActivityRunInitiatorSummary()
            : new WorkflowActivityRunInitiatorSummary
            {
                Platform = source.Platform,
                Tenant = source.Tenant,
                ExternalUserId = source.ExternalUserId,
                Scope = source.Scope,
                BindingId = string.Empty,
                DisplayValue = string.IsNullOrWhiteSpace(source.DisplayValue) ? "Unknown" : source.DisplayValue,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowActivityRunStepSummary ToActivityStepSummary(
        WorkflowRunActivityStepSnapshot? source) =>
        source == null
            ? new WorkflowActivityRunStepSummary()
            : new WorkflowActivityRunStepSummary
            {
                StepId = source.StepId,
                InputSummary = source.InputSummary,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowActivityRunFailureSummary ToActivityFailureSummary(
        WorkflowRunActivityFailureSnapshot? source) =>
        source == null
            ? new WorkflowActivityRunFailureSummary()
            : new WorkflowActivityRunFailureSummary
            {
                StepId = source.StepId,
                Message = source.Message,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    private static WorkflowActivityRunWaitingSummary ToActivityWaitingSummary(
        WorkflowRunActivityWaitingSnapshot? source) =>
        source == null
            ? new WorkflowActivityRunWaitingSummary()
            : new WorkflowActivityRunWaitingSummary
            {
                StepId = source.StepId,
                WaitingKind = source.WaitingKind,
                Prompt = source.Prompt,
                Availability = string.IsNullOrWhiteSpace(source.Availability) ? "unavailable" : source.Availability,
            };

    // 06-26 detail enrichment: per-step structured trace. OutputPreview is the materialized 240-char preview;
    // the full per-step output is surfaced through the run timeline (role.reply / tool-call), not here.
    private static ObservatoryStepDetail ToStepDetail(WorkflowRunStepTrace step) =>
        new()
        {
            StepId = step.StepId,
            DisplayName = string.IsNullOrWhiteSpace(step.DisplayName) ? step.StepId : step.DisplayName,
            StepType = step.StepType,
            TargetRole = step.TargetRole,
            RequestedAtUtc = step.RequestedAt,
            CompletedAtUtc = step.CompletedAt,
            Success = step.Success,
            Outcome = ResolveStepOutcome(step),
            DurationMs = step.DurationMs,
            OutputPreview = step.OutputPreview,
            Error = step.Error,
            RequestParameters = step.RequestParameters,
            NextStepId = step.NextStepId,
            BranchKey = step.BranchKey,
            SuspensionType = step.SuspensionType,
            SuspensionPrompt = step.SuspensionPrompt,
            SuspensionContent = step.SuspensionContent,
            SuspensionTimeoutSeconds = step.SuspensionTimeoutSeconds,
            ToolApproval = step.ToolApproval == null
                ? null
                : new ObservatoryToolApprovalDetail
                {
                    ExecutionId = step.ToolApproval.ExecutionId,
                    ToolName = step.ToolApproval.ToolName,
                    ToolCallId = step.ToolApproval.ToolCallId,
                    ApprovalRequestId = step.ToolApproval.ApprovalRequestId,
                },
            Usage = new ObservatoryUsageTotals
            {
                PromptTokens = step.Usage.PromptTokens,
                CompletionTokens = step.Usage.CompletionTokens,
                TotalTokens = step.Usage.TotalTokens,
                Cost = step.Usage.Cost,
            },
        };

    private static WorkflowRunStepOutcome ResolveStepOutcome(WorkflowRunStepTrace step)
    {
        if (step.Outcome != WorkflowRunStepOutcome.Unspecified)
            return step.Outcome;
        if (HasSkippedAnnotation(step.CompletionAnnotations))
            return WorkflowRunStepOutcome.Skipped;
        if (step.Success == true)
            return WorkflowRunStepOutcome.Succeeded;
        if (step.Success == false || !string.IsNullOrWhiteSpace(step.Error))
            return WorkflowRunStepOutcome.Failed;
        if (!string.IsNullOrWhiteSpace(step.SuspensionType) ||
            (step.RequestedAt.HasValue && !step.CompletedAt.HasValue))
        {
            return WorkflowRunStepOutcome.Waiting;
        }

        return WorkflowRunStepOutcome.Unspecified;
    }

    private static bool HasSkippedAnnotation(IReadOnlyDictionary<string, string> annotations) =>
        annotations.Any(static item =>
            item.Key.EndsWith(".skipped", StringComparison.Ordinal) &&
            string.Equals(item.Value, "true", StringComparison.OrdinalIgnoreCase));

    private static ObservatoryRunStatistics ToStatistics(WorkflowRunStatistics summary) =>
        new()
        {
            TotalSteps = summary.TotalSteps,
            RequestedSteps = summary.RequestedSteps,
            CompletedSteps = summary.CompletedSteps,
            RoleReplyCount = summary.RoleReplyCount,
            StepTypeCounts = summary.StepTypeCounts,
        };

    private static IReadOnlyList<ObservatoryRunDiagnostic> BuildDiagnostics(
        WorkflowActorSnapshot snapshot,
        WorkflowRunReport? report,
        IReadOnlyList<ObservatoryStepDetail> steps,
        IReadOnlyList<ObservatoryViewEvent> viewEvents)
    {
        var diagnostics = new List<ObservatoryRunDiagnostic>();

        AppendCurrentStateDiagnostics(diagnostics, snapshot);
        AppendReportDiagnostics(diagnostics, snapshot, report, steps, viewEvents);
        AppendToolApprovalResumeRejectionDiagnostics(diagnostics, viewEvents);
        AppendAwaitingToolApprovalDiagnostic(diagnostics, snapshot, steps);
        AppendActiveStepDiagnostic(diagnostics, steps);

        if (IsProblemTerminal(snapshot.CompletionStatus))
            AppendTerminalDiagnostics(diagnostics, snapshot, steps);

        return diagnostics
            .OrderBy(diagnostic => diagnostic.TimestampUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static void AppendCurrentStateDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot)
    {
        if (snapshot.SagaStatus == WorkflowSagaStatus.CompensationDeadLetter ||
            !string.IsNullOrWhiteSpace(snapshot.DeadLetterError) ||
            !string.IsNullOrWhiteSpace(snapshot.DeadLetterFailedCompensationStepId))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
                Severity = "error",
                Code = "compensation_dead_letter",
                Source = "current-state",
                StepId = snapshot.DeadLetterFailedCompensationStepId,
                Message = string.IsNullOrWhiteSpace(snapshot.DeadLetterError)
                    ? "Workflow compensation entered dead-letter state."
                    : WorkflowAuditTextSanitizer.Sanitize(snapshot.DeadLetterError),
                Hint = snapshot.DeadLetterRemainingUncompensated > 0
                    ? $"Compensation stopped with {snapshot.DeadLetterRemainingUncompensated} uncompensated step(s)."
                    : "Compensation stopped in a terminal dead-letter state.",
            });
        }

        if (!string.IsNullOrWhiteSpace(snapshot.LastError))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
                Severity = "error",
                Code = "current_state_last_error",
                Source = "current-state",
                Message = WorkflowAuditTextSanitizer.Sanitize(snapshot.LastError),
                Hint = "This is the latest committed error on the workflow current-state read model.",
            });
        }
    }

    private static void AppendReportDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot,
        WorkflowRunReport? report,
        IEnumerable<ObservatoryStepDetail> steps,
        IEnumerable<ObservatoryViewEvent> viewEvents)
    {
        if (!string.IsNullOrWhiteSpace(report?.FinalError))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = NonDefault(report.EndedAt) ?? NonDefault(snapshot.LastUpdatedAt),
                Severity = "error",
                Code = "final_error",
                Source = "run-report",
                Message = report.FinalError,
                Hint = "This is the final error captured in the committed run report.",
            });
        }

        foreach (var step in steps.Where(step => step.Success == false || !string.IsNullOrWhiteSpace(step.Error)))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = step.CompletedAtUtc ?? step.RequestedAtUtc,
                Severity = "error",
                Code = "step_failed",
                Source = "run-report.step",
                StepId = step.StepId,
                StepType = step.StepType,
                TargetRole = step.TargetRole,
                Message = string.IsNullOrWhiteSpace(step.Error)
                    ? "Step was marked unsuccessful without a materialized error message."
                    : step.Error,
                Hint = BuildStepHint(step),
            });
        }

        foreach (var item in viewEvents.Where(IsFailureViewEvent))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = item.TimestampUtc,
                Severity = item.Kind == "RunStopped" ? "warning" : "error",
                Code = item.ToolCall is { Success: false } ? "tool_call_failed" : "timeline_failure_event",
                Source = "run-report.timeline",
                StepId = item.StepId,
                StepType = item.StepType,
                Message = FailureEventMessage(item),
                Hint = "This event came from the committed workflow timeline.",
            });
        }
    }

    private static void AppendActiveStepDiagnostic(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        IEnumerable<ObservatoryStepDetail> steps)
    {
        var activeStep = steps
            .Where(step => step.RequestedAtUtc.HasValue && !step.CompletedAtUtc.HasValue)
            .OrderBy(step => step.RequestedAtUtc)
            .LastOrDefault();
        if (activeStep == null)
            return;

        AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
        {
            TimestampUtc = activeStep.RequestedAtUtc,
            Severity = "info",
            Code = "active_step",
            Source = "run-report.step",
            StepId = activeStep.StepId,
            StepType = activeStep.StepType,
            TargetRole = activeStep.TargetRole,
            Message = "Last materialized active step has not completed yet.",
            Hint = BuildStepHint(activeStep),
        });
    }

    private static void AppendToolApprovalResumeRejectionDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        IEnumerable<ObservatoryViewEvent> viewEvents)
    {
        foreach (var item in viewEvents.Where(static item =>
                     string.Equals(item.Stage, "tool_approval.resume_rejected", StringComparison.Ordinal)))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = item.TimestampUtc,
                Severity = "warning",
                Code = "tool_approval_resume_rejected",
                Source = "run-report.timeline",
                StepId = item.StepId,
                StepType = item.StepType,
                Message = item.Message,
                Hint = "Refresh the pending approval identity and submit all three IDs under the nested toolApproval object.",
            });
        }
    }

    private static void AppendAwaitingToolApprovalDiagnostic(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot,
        IEnumerable<ObservatoryStepDetail> steps)
    {
        if (snapshot.CompletionStatus != WorkflowRunCompletionStatus.AwaitingToolApproval)
            return;

        var approvalStep = steps
            .Where(static step => step.CompletedAtUtc == null && step.ToolApproval != null)
            .OrderBy(static step => step.RequestedAtUtc)
            .LastOrDefault();
        AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
        {
            TimestampUtc = approvalStep?.RequestedAtUtc ?? NonDefault(snapshot.LastUpdatedAt),
            Severity = "info",
            Code = "awaiting_tool_approval",
            Source = approvalStep == null ? "current-state" : "run-report.step",
            StepId = approvalStep?.StepId ?? string.Empty,
            StepType = approvalStep?.StepType ?? string.Empty,
            TargetRole = approvalStep?.TargetRole ?? string.Empty,
            Message = "Run is suspended pending per-run approval for an admitted tool call.",
            Hint = "Submit a typed resume command with the execution id, tool call id, and approval request id.",
        });
    }

    private static void AppendTerminalDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot,
        IEnumerable<ObservatoryStepDetail> steps)
    {
        var lastStep = steps
            .OrderBy(step => step.CompletedAtUtc ?? step.RequestedAtUtc ?? DateTimeOffset.MinValue)
            .LastOrDefault(step =>
                !string.IsNullOrWhiteSpace(step.StepId) ||
                !string.IsNullOrWhiteSpace(step.StepType) ||
                !string.IsNullOrWhiteSpace(step.TargetRole));
        if (lastStep != null)
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = lastStep.CompletedAtUtc ?? lastStep.RequestedAtUtc,
                Severity = "info",
                Code = "last_known_step",
                Source = "run-report.step",
                StepId = lastStep.StepId,
                StepType = lastStep.StepType,
                TargetRole = lastStep.TargetRole,
                Message = $"Last materialized step before terminal status: {StepDisplayName(lastStep)}.",
                Hint = "Inspect this step first when reporting where the workflow stopped.",
            });
        }

        if (diagnostics.Any(IsProblemDiagnostic))
            return;

        AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
        {
            TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
            Severity = "warning",
            Code = "terminal_without_failure_detail",
            Source = "current-state",
            Message = $"Run ended with status '{MapStatus(snapshot.CompletionStatus)}', but no final error or failed step has materialized.",
            Hint = "Include the run id and state version when filing an issue; the run-report artifact may be incomplete.",
        });
    }

    private static bool IsFailureViewEvent(ObservatoryViewEvent item) =>
        item.Kind is "RunError" or "RunStopped" || item.ToolCall is { Success: false };

    private static string FailureEventMessage(ObservatoryViewEvent item)
    {
        if (item.ToolCall is { Success: false } toolCall)
            return string.IsNullOrWhiteSpace(toolCall.Error)
                ? $"Tool call '{toolCall.ToolName}' failed."
                : toolCall.Error;

        return string.IsNullOrWhiteSpace(item.Message)
            ? item.Stage
            : item.Message;
    }

    private static string BuildStepHint(ObservatoryStepDetail step)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(step.StepType) ? string.Empty : $"type={step.StepType}",
            string.IsNullOrWhiteSpace(step.TargetRole) ? string.Empty : $"target={step.TargetRole}",
            string.IsNullOrWhiteSpace(step.NextStepId) ? string.Empty : $"next={step.NextStepId}",
            string.IsNullOrWhiteSpace(step.BranchKey) ? string.Empty : $"branch={step.BranchKey}",
        }.Where(part => part.Length > 0);

        return string.Join(", ", parts);
    }

    private static string StepDisplayName(ObservatoryStepDetail step)
    {
        if (!string.IsNullOrWhiteSpace(step.DisplayName))
            return step.DisplayName;
        if (!string.IsNullOrWhiteSpace(step.StepId))
            return step.StepId;
        if (!string.IsNullOrWhiteSpace(step.StepType))
            return step.StepType;
        return string.IsNullOrWhiteSpace(step.TargetRole) ? "unknown step" : step.TargetRole;
    }

    private static bool IsProblemTerminal(WorkflowRunCompletionStatus status) =>
        status is WorkflowRunCompletionStatus.Failed or
            WorkflowRunCompletionStatus.TimedOut or
            WorkflowRunCompletionStatus.Stopped;

    private static bool IsProblemDiagnostic(ObservatoryRunDiagnostic diagnostic) =>
        diagnostic.Severity is "error" or "warning";

    private static DateTimeOffset? NonDefault(DateTimeOffset value) =>
        value == default ? null : value;

    private static void AppendDistinct(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        ObservatoryRunDiagnostic entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Message) &&
            string.IsNullOrWhiteSpace(entry.StepId) &&
            string.IsNullOrWhiteSpace(entry.Code))
            return;

        var duplicate = diagnostics.Any(existing =>
            string.Equals(existing.Code, entry.Code, StringComparison.Ordinal) &&
            string.Equals(existing.StepId, entry.StepId, StringComparison.Ordinal) &&
            string.Equals(existing.Message, entry.Message, StringComparison.Ordinal));
        if (!duplicate)
            diagnostics.Add(entry);
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
            WorkflowRunCompletionStatus.AwaitingToolApproval => "awaiting_tool_approval",
            WorkflowRunCompletionStatus.WaitingForSignal => "waiting_for_signal",
            _ => "unknown",
        };
}
