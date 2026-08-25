using Aevatar.Workflow.Application.Abstractions.Observatory;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Security;
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
        var reportVersion = WorkflowAuditTextSanitizer.SanitizeForStorage(report?.ReportVersion);
        if (report == null || report.StateVersion != snapshot.StateVersion)
        {
            var reportSectionStatus = report == null
                ? UnavailableSection(snapshot.StateVersion, "Run report artifact has not materialized.")
                : VersionMismatchSection(
                    snapshot.StateVersion,
                    report.StateVersion,
                    "Run report artifact source version does not match the current-state detail version.");
            var fallbackSections = new ObservatoryRunDetailSectionVersions
            {
                Overview = AlignedSection(snapshot.StateVersion),
                Steps = reportSectionStatus,
                Timeline = reportSectionStatus,
                ExecutionPath = ToSectionVersion(graph),
            };
            return new ObservatoryRunDetail
            {
                Summary = summary,
                Initiator = ToActivityInitiatorSummary(snapshot.ActivityInitiator),
                InputSummary = snapshot.InputSummary,
                FirstFailure = ToActivityFailureSummary(snapshot.ActivityFirstFailure),
                Sections = fallbackSections,
                ReportVersion = reportVersion,
                CompilationError = WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.CompilationError),
                Timeline = [],
                Operations = [],
                Diagnostics = BuildDiagnostics(
                    snapshot,
                    report: null,
                    steps: [],
                    viewEvents: [],
                    fallbackSections,
                    reportVersion),
                UsageTotals = new ObservatoryUsageTotals(),
                ExecutionPath = graph,
                RecoveryCapability = CloneRecoveryCapability(snapshot),
                Lineage = CloneLineage(snapshot),
            };
        }

        report = WorkflowAuditReportSanitizer.Sanitize(report);

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

        var steps = report.Steps.Select(ToStepDetail).ToList();
        var viewEvents = report.Timeline
            .OrderBy(item => item.Timestamp)
            .Select(item => WorkflowRunObservatoryTimelineMapper.ToViewEvent(
                item,
                ResolveRoleReplyContent(item, roleReplyByRole)))
            .ToList();
        var operations = report.Operations
            .OrderBy(operation => operation.ProgressSequence > 0 ? 0 : 1)
            .ThenBy(operation => operation.ProgressSequence > 0 ? operation.ProgressSequence : 0)
            .ThenBy(operation => operation.StartedAt ?? operation.CompletedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(operation => operation.SessionId, StringComparer.Ordinal)
            .ThenBy(operation => operation.OperationId, StringComparer.Ordinal)
            .Select(ToOperationDetail)
            .ToList();
        var sections = new ObservatoryRunDetailSectionVersions
        {
            Overview = AlignedSection(snapshot.StateVersion),
            Steps = AlignedSection(snapshot.StateVersion),
            Timeline = AlignedSection(snapshot.StateVersion),
            ExecutionPath = ToSectionVersion(graph),
        };

        return new ObservatoryRunDetail
        {
            Summary = summary,
            Initiator = ToActivityInitiatorSummary(snapshot.ActivityInitiator),
            InputSummary = snapshot.InputSummary,
            FirstFailure = ToActivityFailureSummary(snapshot.ActivityFirstFailure),
            Sections = sections,
            ReportVersion = report.ReportVersion,
            // Final result is fully materialized (not truncated), so the viewer can show it honestly.
            Input = report.Input,
            FinalOutput = report.FinalOutput,
            FinalError = report.FinalError,
            CompilationError = WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.CompilationError),
            Diagnostics = BuildDiagnostics(snapshot, report, steps, viewEvents, sections, report.ReportVersion),
            Steps = steps,
            Timeline = viewEvents,
            Operations = operations,
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
            Kind = MapOperationKind(operation.Kind),
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

    private static string MapOperationKind(WorkflowRuntimeOperationKind kind) =>
        kind switch
        {
            WorkflowRuntimeOperationKind.Model => "model",
            WorkflowRuntimeOperationKind.Tool => "tool",
            _ => "unknown",
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

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

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
        if (!_artifactQueryPort.WorkflowGraphExportEnabled)
        {
            return new ObservatoryRunGraph
            {
                RootNodeId = snapshot.ActorId,
                DetailStateVersion = snapshot.StateVersion,
                VersionStatus = ObservatoryRunDetailSectionVersionStatus.Disabled,
            };
        }

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

        var snapshot = await _currentStateQueryPort.GetWorkflowRunCurrentStateForScopeAsync(
            normalizedScopeId,
            normalizedRunId,
            ct);
        if (snapshot == null ||
            !string.Equals(snapshot.RunId?.Trim(), normalizedRunId, StringComparison.Ordinal))
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

        var snapshot = await _currentStateQueryPort.GetWorkflowRunCurrentStateAsync(normalizedRunId, ct);
        return snapshot != null &&
               string.Equals(snapshot.RunId?.Trim(), normalizedRunId, StringComparison.Ordinal)
            ? snapshot
            : null;
    }

    private static ObservatoryRunSummary ToRunSummary(WorkflowActorSnapshot snapshot)
    {
        return new ObservatoryRunSummary
        {
            RunId = snapshot.RunId,
            WorkflowId = snapshot.WorkflowId,
            WorkflowName = snapshot.WorkflowName,
            Status = MapStatus(snapshot.CompletionStatus),
            Success = snapshot.LastSuccess,
            StartedAtUtc = snapshot.StartedAtUtc?.ToDateTimeOffset(),
            CompletedAtUtc = snapshot.CompletedAtUtc?.ToDateTimeOffset(),
            DurationMs = snapshot.HasDurationMs ? snapshot.DurationMs : null,
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
                Message = WorkflowAuditTextSanitizer.SanitizeForStorage(source.Message),
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

    private static ObservatoryStepDetail ToStepDetail(WorkflowRunStepTrace step)
    {
        var waiting = ResolveStepOutcome(step) == WorkflowRunStepOutcome.Waiting;
        return new ObservatoryStepDetail
        {
            StepId = step.StepId,
            DisplayName = string.IsNullOrWhiteSpace(step.DisplayName) ? step.StepId : step.DisplayName,
            StepType = step.StepType,
            TargetRole = step.TargetRole,
            RequestedAtUtc = step.RequestedAt,
            CompletedAtUtc = waiting ? null : step.CompletedAt,
            Success = waiting ? null : step.Success,
            Outcome = ResolveStepOutcome(step),
            DurationMs = waiting ? null : step.DurationMs,
            WorkerId = waiting ? string.Empty : step.WorkerId,
            OutputPreview = waiting ? string.Empty : step.OutputPreview,
            Error = waiting ? string.Empty : step.Error,
            FailureOutput = waiting ? string.Empty : step.FailureOutput,
            FailureOutputTruncated = !waiting && step.FailureOutputTruncated,
            FailureOutcome = waiting ? WorkflowStepFailureOutcome.Unspecified : step.FailureOutcome,
            RecoveryFailureKind = waiting ? WorkflowRecoveryFailureKind.Unspecified : step.RecoveryFailureKind,
            RetryDisposition = waiting ? WorkflowStepRetryDisposition.Unspecified : step.RetryDisposition,
            FileItemResults = waiting ? null : ToFileItemResultSetDetail(step.FileItemResults),
            VoteAgreementDecision = waiting ? null : ToVoteAgreementDecisionDetail(step.VoteAgreementDecision),
            LatestFailedAttempt = ToFailedStepAttemptDetail(step.LatestFailedAttempt),
            RequestParameters = step.RequestParameters,
            CompletionAnnotations = waiting
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : step.CompletionAnnotations,
            NextStepId = waiting ? string.Empty : step.NextStepId,
            BranchKey = waiting ? string.Empty : step.BranchKey,
            AssignedVariable = waiting ? string.Empty : step.AssignedVariable,
            AssignedValue = waiting ? string.Empty : step.AssignedValue,
            SuspensionType = step.SuspensionType,
            SuspensionPrompt = step.SuspensionPrompt,
            SuspensionContent = step.SuspensionContent,
            SuspensionTimeoutSeconds = step.SuspensionTimeoutSeconds,
            RequestedVariableName = step.RequestedVariableName,
            ToolApproval = step.ToolApproval == null
                ? null
                : new ObservatoryToolApprovalDetail
                {
                    ExecutionId = step.ToolApproval.ExecutionId,
                    ToolName = step.ToolApproval.ToolName,
                    ToolCallId = step.ToolApproval.ToolCallId,
                    ApprovalRequestId = step.ToolApproval.ApprovalRequestId,
                },
            Usage = waiting
                ? new ObservatoryUsageTotals()
                : new ObservatoryUsageTotals
            {
                PromptTokens = step.Usage.PromptTokens,
                CompletionTokens = step.Usage.CompletionTokens,
                TotalTokens = step.Usage.TotalTokens,
                Cost = step.Usage.Cost,
            },
        };
    }

    private static ObservatoryFailedStepAttemptDetail? ToFailedStepAttemptDetail(
        WorkflowRunFailedStepAttempt? source) =>
        source == null
            ? null
            : new ObservatoryFailedStepAttemptDetail
            {
                DisplayName = source.DisplayName,
                StepType = source.StepType,
                TargetRole = source.TargetRole,
                RequestedAtUtc = source.RequestedAt,
                CompletedAtUtc = source.CompletedAt,
                Success = source.Success,
                DurationMs = source.DurationMs,
                WorkerId = source.WorkerId,
                OutputPreview = source.OutputPreview,
                Error = source.Error,
                FailureOutput = source.FailureOutput,
                FailureOutputTruncated = source.FailureOutputTruncated,
                FailureOutcome = source.FailureOutcome,
                RecoveryFailureKind = source.RecoveryFailureKind,
                RetryDisposition = source.RetryDisposition,
                FileItemResults = ToFileItemResultSetDetail(source.FileItemResults),
                VoteAgreementDecision = ToVoteAgreementDecisionDetail(source.VoteAgreementDecision),
                RequestParameters = source.RequestParameters,
                CompletionAnnotations = source.CompletionAnnotations,
                NextStepId = source.NextStepId,
                BranchKey = source.BranchKey,
                AssignedVariable = source.AssignedVariable,
                AssignedValue = source.AssignedValue,
                SuspensionType = source.SuspensionType,
                SuspensionPrompt = source.SuspensionPrompt,
                SuspensionContent = source.SuspensionContent,
                SuspensionTimeoutSeconds = source.SuspensionTimeoutSeconds,
                RequestedVariableName = source.RequestedVariableName,
                ToolApproval = source.ToolApproval == null
                    ? null
                    : new ObservatoryToolApprovalDetail
                    {
                        ExecutionId = source.ToolApproval.ExecutionId,
                        ToolName = source.ToolApproval.ToolName,
                        ToolCallId = source.ToolApproval.ToolCallId,
                        ApprovalRequestId = source.ToolApproval.ApprovalRequestId,
                    },
                Usage = new ObservatoryUsageTotals
                {
                    PromptTokens = source.Usage.PromptTokens,
                    CompletionTokens = source.Usage.CompletionTokens,
                    TotalTokens = source.Usage.TotalTokens,
                    Cost = source.Usage.Cost,
                },
            };

    private static ObservatoryFileItemResultSetDetail? ToFileItemResultSetDetail(
        WorkflowFileItemResultSet? source) =>
        source == null
            ? null
            : new ObservatoryFileItemResultSetDetail
            {
                SourceResultCount = source.SourceResultCount,
                ResultsTruncated = source.ResultsTruncated,
                Results = source.Results.Select(item => new ObservatoryFileItemResultDetail
                {
                    Index = item.Index,
                    FileRef = ToWorkflowFileRefDetail(item.FileRef),
                    Success = item.Success,
                    Output = item.Output,
                    OutputTruncated = item.OutputTruncated,
                    Error = item.Error,
                    ErrorTruncated = item.ErrorTruncated,
                }).ToList(),
            };

    private static ObservatoryWorkflowFileRefDetail? ToWorkflowFileRefDetail(WorkflowFileRef? source) =>
        source == null
            ? null
            : new ObservatoryWorkflowFileRefDetail
            {
                FileId = source.FileId,
                ArtifactId = source.ArtifactId,
                SourceKind = source.SourceKind,
                SourceMessageId = source.SourceMessageId,
                SourceResourceKey = source.SourceResourceKey,
                FileName = source.FileName,
                MediaType = source.MediaType,
                SizeBytes = source.SizeBytes,
                Sha256 = source.Sha256,
                CreatedAtUnixMs = source.CreatedAtUnixMs,
                ExpiresAtUnixMs = source.ExpiresAtUnixMs,
                OwnerRunId = source.OwnerRunId,
                OwnerScopeId = source.OwnerScopeId,
            };

    private static ObservatoryVoteAgreementDecisionDetail? ToVoteAgreementDecisionDetail(
        VoteAgreementDecision? source) =>
        source == null
            ? null
            : new ObservatoryVoteAgreementDecisionDetail
            {
                Kind = source.Kind,
                BranchKey = source.BranchKey,
                WinnerCandidateId = source.WinnerCandidateId,
                Output = source.Output,
                OutputTruncated = source.OutputTruncated,
                Reason = source.Reason,
                ReasonTruncated = source.ReasonTruncated,
                LabelCounts = source.LabelCounts.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal),
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
        IReadOnlyList<ObservatoryViewEvent> viewEvents,
        ObservatoryRunDetailSectionVersions sections,
        string reportVersion)
    {
        var diagnostics = new List<ObservatoryRunDiagnostic>();

        AppendCurrentStateDiagnostics(diagnostics, snapshot);
        AppendReportDiagnostics(diagnostics, snapshot, report, steps, viewEvents);
        AppendToolApprovalResumeRejectionDiagnostics(diagnostics, viewEvents);
        AppendAwaitingToolApprovalDiagnostic(diagnostics, snapshot, steps);
        AppendActiveStepDiagnostic(diagnostics, steps);

        if (IsProblemTerminal(snapshot.CompletionStatus))
            AppendTerminalDiagnostics(diagnostics, snapshot, steps);

        AppendFailureEvidenceSchemaDiagnostic(diagnostics, snapshot, reportVersion);
        AppendRecoveryDiagnostics(diagnostics, snapshot);
        AppendSectionDiagnostics(diagnostics, sections);

        return diagnostics
            .OrderBy(diagnostic => diagnostic.TimestampUtc ?? DateTimeOffset.MaxValue)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static void AppendCurrentStateDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.CompilationError))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
                Severity = "error",
                Code = "compilation_error",
                Source = "current-state",
                Message = WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.CompilationError),
                Hint = "The workflow definition did not compile successfully for this committed run state.",
            });
        }

        if (!string.IsNullOrWhiteSpace(snapshot.ActivityFirstFailure?.Message))
        {
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
                Severity = "error",
                Code = "activity_first_failure",
                Source = "current-state.activity",
                StepId = snapshot.ActivityFirstFailure.StepId,
                Message = WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.ActivityFirstFailure.Message),
                Hint = "This is the committed activity failure summary; it is not proof that this was the first failed attempt.",
            });
        }

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
                    : WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.DeadLetterError),
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
                Message = WorkflowAuditTextSanitizer.SanitizeForStorage(snapshot.LastError),
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
                Message = WorkflowAuditTextSanitizer.SanitizeForStorage(report.FinalError),
                Hint = "This is the final error captured in the committed run report.",
            });
        }

        foreach (var step in steps.Where(HasStepFailureEvidence))
        {
            var failedAttempt = step.Outcome == WorkflowRunStepOutcome.Waiting
                ? step.LatestFailedAttempt
                : null;
            var retryWaiting = failedAttempt != null;
            var failedAt = retryWaiting
                ? failedAttempt!.CompletedAtUtc ?? failedAttempt.RequestedAtUtc
                : step.CompletedAtUtc ?? step.RequestedAtUtc;
            var failedStepType = retryWaiting ? failedAttempt!.StepType : step.StepType;
            var failedTargetRole = retryWaiting ? failedAttempt!.TargetRole : step.TargetRole;
            var error = retryWaiting ? failedAttempt!.Error : step.Error;
            var failureOutput = retryWaiting ? failedAttempt!.FailureOutput : step.FailureOutput;
            var failureOutputTruncated = retryWaiting
                ? failedAttempt!.FailureOutputTruncated
                : step.FailureOutputTruncated;
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = failedAt,
                Severity = retryWaiting ? "warning" : "error",
                Code = retryWaiting ? "step_retry_waiting_after_failure" : "step_failed",
                Source = retryWaiting ? "run-report.step.latest-failed-attempt" : "run-report.step",
                StepId = step.StepId,
                StepType = failedStepType,
                TargetRole = failedTargetRole,
                Message = string.IsNullOrWhiteSpace(error)
                    ? retryWaiting
                        ? "The step is waiting for a retry after a failed attempt without a materialized error message."
                        : "Step was marked unsuccessful without a materialized error message."
                    : WorkflowAuditTextSanitizer.SanitizeForStorage(error),
                Hint = retryWaiting
                    ? AppendHint(
                        "This evidence belongs to the latest failed attempt; the current step state is waiting for retry.",
                        BuildStepHint(failedAttempt!))
                    : BuildStepHint(step),
            });

            if (!string.IsNullOrWhiteSpace(failureOutput))
            {
                AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
                {
                    TimestampUtc = failedAt,
                    Severity = retryWaiting ? "warning" : "error",
                    Code = retryWaiting ? "step_retry_waiting_failure_output" : "step_failure_output",
                    Source = retryWaiting ? "run-report.step.latest-failed-attempt" : "run-report.step",
                    StepId = step.StepId,
                    StepType = failedStepType,
                    TargetRole = failedTargetRole,
                    Message = WorkflowAuditTextSanitizer.SanitizeForStorage(failureOutput),
                    Hint = AppendHint(
                        retryWaiting
                            ? "This output belongs to the latest failed attempt; the current step state is waiting for retry."
                            : string.Empty,
                        failureOutputTruncated
                            ? "Failure output exceeded the projection limit; the materialized value preserves its beginning and end but omits the middle."
                            : "This is the complete materialized failure output for the step."),
                });
            }
        }

        foreach (var operation in report?.Operations.Where(operation =>
                     operation.Success == false || !string.IsNullOrWhiteSpace(operation.Error)) ??
                 Enumerable.Empty<WorkflowRunOperation>())
        {
            var operationKind = MapOperationKind(operation.Kind);
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                TimestampUtc = operation.CompletedAt ?? operation.StartedAt,
                Severity = "error",
                Code = "operation_failed",
                Source = "run-report.operation",
                OperationId = operation.OperationId,
                OperationKind = operationKind,
                Message = FirstNonEmpty(
                    WorkflowAuditTextSanitizer.SanitizeForStorage(operation.Error),
                    WorkflowAuditTextSanitizer.SanitizeForStorage(operation.ResultJson),
                    $"{operationKind} operation '{operation.OperationId}' was marked unsuccessful."),
                Hint = string.IsNullOrWhiteSpace(operation.ToolName)
                    ? $"session={operation.SessionId}, round={operation.Round}"
                    : $"tool={operation.ToolName}, session={operation.SessionId}, round={operation.Round}",
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

    private static bool HasStepFailureEvidence(ObservatoryStepDetail step)
    {
        if (step.Outcome == WorkflowRunStepOutcome.Waiting)
            return step.LatestFailedAttempt != null;

        return step.Success == false ||
               step.Outcome == WorkflowRunStepOutcome.Failed ||
               !string.IsNullOrWhiteSpace(step.Error) ||
               !string.IsNullOrWhiteSpace(step.FailureOutput);
    }

    private static string AppendHint(params string[] parts) =>
        string.Join(" ", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static void AppendFailureEvidenceSchemaDiagnostic(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot,
        string reportVersion)
    {
        if (!IsProblemTerminal(snapshot.CompletionStatus) || SupportsDedicatedFailureEvidence(reportVersion))
            return;

        var version = string.IsNullOrWhiteSpace(reportVersion) ? "missing" : $"'{reportVersion}'";
        AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
        {
            TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
            Severity = "warning",
            Code = "failure_evidence_schema_legacy",
            Source = "run-report.schema",
            Message = $"Run report schema version is {version}; dedicated failed-step evidence may be unavailable.",
            Hint = "Background repair or reprojection to report schema 3.1 or newer is required to recover dedicated failure details; the Observatory does not replay events on the query path.",
        });
    }

    private static bool SupportsDedicatedFailureEvidence(string reportVersion) =>
        Version.TryParse(reportVersion, out var parsed) && parsed.CompareTo(new Version(3, 1)) >= 0;

    private static void AppendRecoveryDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot)
    {
        if (!IsProblemTerminal(snapshot.CompletionStatus) || snapshot.RecoveryCapability == null)
            return;

        AppendRecoveryActionDiagnostic(
            diagnostics,
            snapshot,
            snapshot.RecoveryCapability.RetryFailedStep,
            "retry_failed_step");
        AppendRecoveryActionDiagnostic(
            diagnostics,
            snapshot,
            snapshot.RecoveryCapability.RunAgain,
            "run_again");
    }

    private static void AppendRecoveryActionDiagnostic(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        WorkflowActorSnapshot snapshot,
        WorkflowRecoveryActionCapability? capability,
        string action)
    {
        if (capability == null ||
            capability.Eligibility == WorkflowRecoveryEligibility.Eligible ||
            (capability.Eligibility == WorkflowRecoveryEligibility.Unspecified &&
             string.IsNullOrWhiteSpace(capability.UnavailableReason)))
        {
            return;
        }

        var reasonCode = capability.UnavailableReasonCode.ToString();
        var message = string.IsNullOrWhiteSpace(capability.UnavailableReason)
            ? $"Recovery action '{action}' is {capability.Eligibility.ToString().ToLowerInvariant()} ({reasonCode})."
            : WorkflowAuditTextSanitizer.SanitizeForStorage(capability.UnavailableReason);
        var recommendedActions = capability.RecommendedActions
            .Where(static recommended => recommended != WorkflowRecoveryRecommendedAction.Unspecified)
            .Select(static recommended => recommended.ToString())
            .ToList();
        AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
        {
            TimestampUtc = NonDefault(snapshot.LastUpdatedAt),
            Severity = "warning",
            Code = $"recovery_{action}_blocked",
            Source = "current-state.recovery",
            StepId = capability.StartingStepId,
            Message = message,
            Hint = recommendedActions.Count == 0
                ? $"eligibility={capability.Eligibility}, reason={reasonCode}"
                : $"eligibility={capability.Eligibility}, reason={reasonCode}, recommended={string.Join(",", recommendedActions)}",
        });
    }

    private static void AppendSectionDiagnostics(
        ICollection<ObservatoryRunDiagnostic> diagnostics,
        ObservatoryRunDetailSectionVersions sections)
    {
        var entries = new[]
        {
            (Name: "overview", Value: sections.Overview),
            (Name: "steps", Value: sections.Steps),
            (Name: "timeline", Value: sections.Timeline),
            (Name: "execution_path", Value: sections.ExecutionPath),
        };
        foreach (var (name, section) in entries.Where(static entry =>
                     entry.Value.VersionStatus is ObservatoryRunDetailSectionVersionStatus.Unavailable or
                         ObservatoryRunDetailSectionVersionStatus.VersionMismatch))
        {
            var mismatch = section.VersionStatus == ObservatoryRunDetailSectionVersionStatus.VersionMismatch;
            AppendDistinct(diagnostics, new ObservatoryRunDiagnostic
            {
                Severity = "warning",
                Code = mismatch ? "section_version_mismatch" : "section_unavailable",
                Source = $"read-model.{name}",
                Message = string.IsNullOrWhiteSpace(section.Reason)
                    ? $"Observatory section '{name}' is {section.VersionStatus}."
                    : section.Reason,
                Hint = $"detail_state_version={section.DetailStateVersion}, source_state_version={section.SourceStateVersion}",
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
            step.FailureOutcome == WorkflowStepFailureOutcome.Unspecified
                ? string.Empty
                : $"failure_outcome={step.FailureOutcome}",
            step.RecoveryFailureKind == WorkflowRecoveryFailureKind.Unspecified
                ? string.Empty
                : $"recovery_failure_kind={step.RecoveryFailureKind}",
            step.RetryDisposition == WorkflowStepRetryDisposition.Unspecified
                ? string.Empty
                : $"retry_disposition={step.RetryDisposition}",
            step.FailureOutputTruncated ? "failure_output_truncated=true" : string.Empty,
        }.Where(part => part.Length > 0);

        return string.Join(", ", parts);
    }

    private static string BuildStepHint(ObservatoryFailedStepAttemptDetail attempt)
    {
        var parts = new[]
        {
            string.IsNullOrWhiteSpace(attempt.StepType) ? string.Empty : $"type={attempt.StepType}",
            string.IsNullOrWhiteSpace(attempt.TargetRole) ? string.Empty : $"target={attempt.TargetRole}",
            string.IsNullOrWhiteSpace(attempt.NextStepId) ? string.Empty : $"next={attempt.NextStepId}",
            string.IsNullOrWhiteSpace(attempt.BranchKey) ? string.Empty : $"branch={attempt.BranchKey}",
            attempt.FailureOutcome == WorkflowStepFailureOutcome.Unspecified
                ? string.Empty
                : $"failure_outcome={attempt.FailureOutcome}",
            attempt.RecoveryFailureKind == WorkflowRecoveryFailureKind.Unspecified
                ? string.Empty
                : $"recovery_failure_kind={attempt.RecoveryFailureKind}",
            attempt.RetryDisposition == WorkflowStepRetryDisposition.Unspecified
                ? string.Empty
                : $"retry_disposition={attempt.RetryDisposition}",
            attempt.FailureOutputTruncated ? "failure_output_truncated=true" : string.Empty,
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
            string.Equals(existing.Source, entry.Source, StringComparison.Ordinal) &&
            string.Equals(existing.StepId, entry.StepId, StringComparison.Ordinal) &&
            string.Equals(existing.OperationId, entry.OperationId, StringComparison.Ordinal) &&
            string.Equals(existing.OperationKind, entry.OperationKind, StringComparison.Ordinal) &&
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
