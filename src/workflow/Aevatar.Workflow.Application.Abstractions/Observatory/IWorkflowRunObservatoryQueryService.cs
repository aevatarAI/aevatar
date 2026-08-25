using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Queries;
using System.Text.Json.Serialization;

namespace Aevatar.Workflow.Application.Abstractions.Observatory;

// 06-19-workflow-run-observatory (C2): read-only, scope-gated run viewer query port.
//   scopeId is always an INPUT parameter (never read from HttpContext); the implementation depends on
//   query ports only (no IActorDispatchPort) and is the single scope-enforcement seam. A caller can only
//   ever enumerate their own runs; a cross-scope runId returns null so the endpoint maps it to 404 (D8 —
//   no existence disclosure). Live and history share this one read path.
public interface IWorkflowRunObservatoryQueryService
{
    Task<IReadOnlyList<ObservatoryRunSummary>> ListRunsForScopeAsync(
        string scopeId,
        ObservatoryRunListFilter filter,
        CancellationToken ct = default);

    Task<WorkflowActivityRunFeedPage> ListActivityRunsForScopeAsync(
        string scopeId,
        WorkflowActivityRunFeedFilter filter,
        CancellationToken ct = default);

    Task<ObservatoryRunDetail?> GetRunForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default);

    Task<ObservatoryRunGraph?> GetRunGraphForScopeAsync(
        string scopeId,
        string runId,
        CancellationToken ct = default);
}

public sealed class ObservatoryRunListFilter
{
    public string? Status { get; init; }

    // 06-23-observatory-run-coverage-filter: additional filter dimensions. Null/empty = not filtered.
    public IReadOnlyList<string> Origins { get; init; } = [];

    public IReadOnlyList<string> DefinitionActorIds { get; init; } = [];

    // 06-24-schedules-page-and-schedule-run-filter: runs produced by a specific cron schedule.
    public IReadOnlyList<string> ScheduleIds { get; init; } = [];

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public int Take { get; init; } = 100;
}

public sealed class WorkflowActivityRunFeedFilter
{
    public string? Status { get; init; }

    public IReadOnlyList<string> Origins { get; init; } = [];

    public IReadOnlyList<string> DefinitionActorIds { get; init; } = [];

    public IReadOnlyList<string> ScheduleIds { get; init; } = [];

    public string? WorkflowId { get; init; }

    public string? SearchText { get; init; }

    public DateTimeOffset? FromUtc { get; init; }

    public DateTimeOffset? ToUtc { get; init; }

    public int Take { get; init; } = 100;

    public string? Cursor { get; init; }

    public bool IncludeTotalCount { get; init; }
}

public sealed class WorkflowActivityRunFeedPage
{
    public IReadOnlyList<WorkflowActivityRunFeedRow> Items { get; init; } = [];

    public string? NextCursor { get; init; }

    public bool HasMore { get; init; }

    public long? TotalCount { get; init; }
}

public sealed class WorkflowActivityRunFeedRow
{
    public string RunId { get; init; } = string.Empty;

    public string ActorId { get; init; } = string.Empty;

    public string WorkflowId { get; init; } = string.Empty;

    public string WorkflowName { get; init; } = string.Empty;

    public string ScopeId { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string RunOrigin { get; init; } = string.Empty;

    public bool? Success { get; init; }

    public WorkflowActivityRunInitiatorSummary Initiator { get; init; } = new();

    public string InputSummary { get; init; } = string.Empty;

    public WorkflowActivityRunStepSummary CurrentStep { get; init; } = new();

    public WorkflowActivityRunFailureSummary FirstFailure { get; init; } = new();

    public WorkflowActivityRunWaitingSummary Waiting { get; init; } = new();

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public double? DurationMs { get; init; }

    public long StateVersion { get; init; }

    public WorkflowRunRecoveryCapability RecoveryCapability { get; init; } = new();

    public WorkflowRunLineage Lineage { get; init; } = new();
}

public sealed class WorkflowActivityRunInitiatorSummary
{
    public string Platform { get; init; } = string.Empty;

    public string Tenant { get; init; } = string.Empty;

    public string ExternalUserId { get; init; } = string.Empty;

    public string Scope { get; init; } = string.Empty;

    public string BindingId { get; init; } = string.Empty;

    public string DisplayValue { get; init; } = "Unknown";

    public string Availability { get; init; } = "unavailable";
}

public sealed class WorkflowActivityRunStepSummary
{
    public string StepId { get; init; } = string.Empty;

    public string InputSummary { get; init; } = string.Empty;

    public string Availability { get; init; } = "unavailable";
}

public sealed class WorkflowActivityRunFailureSummary
{
    public string StepId { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Availability { get; init; } = "unavailable";
}

public sealed class WorkflowActivityRunWaitingSummary
{
    public string StepId { get; init; } = string.Empty;

    public string WaitingKind { get; init; } = string.Empty;

    public string Prompt { get; init; } = string.Empty;

    public string Availability { get; init; } = "unavailable";
}

// Read-only view DTOs (Host -> browser JSON, sanctioned by observability.md §9). All carry the
// authoritative state version / refresh stamp so the page can be honest that the readmodel is
// eventually consistent.
public sealed class ObservatoryRunSummary
{
    public string RunId { get; init; } = string.Empty;

    public string WorkflowId { get; init; } = string.Empty;

    public string WorkflowName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public bool? Success { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public double? DurationMs { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public long StateVersion { get; init; }

    // 06-20-observatory-admin-cross-scope: the run's owning scope. Populated for every read; the page only
    // surfaces it in admin cross-scope mode (own-scope callers already know their scope).
    public string ScopeId { get; init; } = string.Empty;

    // 06-23-observatory-run-coverage-filter: canonical run origin/type (draft | member-invoke | ...),
    // empty for legacy/unstamped runs. Drives the run-type filter + badge.
    public string RunOrigin { get; init; } = string.Empty;

    public WorkflowRunLineage Lineage { get; init; } = new();
}

public sealed class ObservatoryRunDetail
{
    public ObservatoryRunSummary Summary { get; init; } = new();

    public WorkflowActivityRunInitiatorSummary Initiator { get; init; } = new();

    public string InputSummary { get; init; } = string.Empty;

    public WorkflowActivityRunFailureSummary FirstFailure { get; init; } = new();

    public ObservatoryRunDetailSectionVersions Sections { get; init; } = new();

    // Schema version of the materialized run-report artifact. Empty means no report is currently available.
    public string ReportVersion { get; init; } = string.Empty;

    // 06-26 detail enrichment: the run's authoritative input + final result, surfaced from the committed
    // run-report artifact. These are NOT truncated by materialization (unlike per-step OutputPreview), so the
    // viewer can show the real final output/error honestly. FinalOutput is empty while a run is still running.
    public string Input { get; init; } = string.Empty;

    public string FinalOutput { get; init; } = string.Empty;

    public string FinalError { get; init; } = string.Empty;

    public string CompilationError { get; init; } = string.Empty;

    // Diagnostics derived from committed current-state/readmodel facts and the run-report artifact. They are
    // query-time explanations, not durable log entries or deletion tombstones.
    public IReadOnlyList<ObservatoryRunDiagnostic> Diagnostics { get; init; } = [];

    // Per-step structured trace, including bounded failed-step evidence and its explicit truncation status.
    public IReadOnlyList<ObservatoryStepDetail> Steps { get; init; } = [];

    public IReadOnlyList<ObservatoryViewEvent> Timeline { get; init; } = [];

    public IReadOnlyList<ObservatoryOperationDetail> Operations { get; init; } = [];

    public ObservatoryRunGraph ExecutionPath { get; init; } = new();

    public ObservatoryRunStatistics Statistics { get; init; } = new();

    public ObservatoryUsageTotals UsageTotals { get; init; } = new();

    public WorkflowRunRecoveryCapability RecoveryCapability { get; init; } = new();

    public WorkflowRunLineage Lineage { get; init; } = new();
}

public sealed class ObservatoryOperationDetail
{
    public string SessionId { get; init; } = string.Empty;
    public string OperationId { get; init; } = string.Empty;
    public long ProgressSequence { get; init; }
    public int Round { get; init; }
    public string Kind { get; init; } = string.Empty;
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string RoleActorId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string InputSummary { get; init; } = string.Empty;
    public IReadOnlyList<string> AvailableToolNames { get; init; } = [];
    public string Output { get; init; } = string.Empty;
    public string ReasoningContent { get; init; } = string.Empty;
    public string FinishReason { get; init; } = string.Empty;
    public ObservatoryUsageTotals Usage { get; init; } = new();
    public bool? Success { get; init; }
    public string Error { get; init; } = string.Empty;
    public string ToolCallId { get; init; } = string.Empty;
    public string ToolName { get; init; } = string.Empty;
    public string ArgumentsJson { get; init; } = string.Empty;
    public string ResultJson { get; init; } = string.Empty;
    public double? DurationMs { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservatoryRunDetailSectionVersionStatus>))]
public enum ObservatoryRunDetailSectionVersionStatus
{
    [JsonStringEnumMemberName("unknown")]
    Unknown = 0,

    [JsonStringEnumMemberName("aligned")]
    Aligned = 1,

    [JsonStringEnumMemberName("unavailable")]
    Unavailable = 2,

    [JsonStringEnumMemberName("version_mismatch")]
    VersionMismatch = 3,

    [JsonStringEnumMemberName("disabled")]
    Disabled = 4,
}

public sealed class ObservatoryRunDetailSectionVersions
{
    public ObservatoryRunDetailSectionVersion Overview { get; init; } = new();

    public ObservatoryRunDetailSectionVersion Steps { get; init; } = new();

    public ObservatoryRunDetailSectionVersion Timeline { get; init; } = new();

    public ObservatoryRunDetailSectionVersion ExecutionPath { get; init; } = new();
}

public sealed class ObservatoryRunDetailSectionVersion
{
    public long DetailStateVersion { get; init; }

    public long SourceStateVersion { get; init; }

    public ObservatoryRunDetailSectionVersionStatus VersionStatus { get; init; } =
        ObservatoryRunDetailSectionVersionStatus.Unknown;

    public string Reason { get; init; } = string.Empty;
}

public sealed class ObservatoryRunDiagnostic
{
    public DateTimeOffset? TimestampUtc { get; init; }

    public string Severity { get; init; } = string.Empty;

    public string Code { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Hint { get; init; } = string.Empty;

    public string StepId { get; init; } = string.Empty;

    public string StepType { get; init; } = string.Empty;

    public string TargetRole { get; init; } = string.Empty;

    public string OperationId { get; init; } = string.Empty;

    public string OperationKind { get; init; } = string.Empty;
}

// 06-26 detail enrichment: read-only per-step view DTO mirroring the committed run-report step trace.
public sealed class ObservatoryStepDetail
{
    public string StepId { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string StepType { get; init; } = string.Empty;

    public string TargetRole { get; init; } = string.Empty;

    public DateTimeOffset? RequestedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public bool? Success { get; init; }

    [JsonConverter(typeof(WorkflowRunStepOutcomeJsonConverter))]
    public WorkflowRunStepOutcome Outcome { get; init; } = WorkflowRunStepOutcome.Unspecified;

    public double? DurationMs { get; init; }

    public string WorkerId { get; init; } = string.Empty;

    // A short display preview retained for successful and failed steps.
    public string OutputPreview { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    // Failed-step evidence is stored separately from the short preview. The truncation bit is authoritative:
    // false means the materialized failure output is complete, true means the bounded projection kept its head
    // and tail and omitted the middle.
    public string FailureOutput { get; init; } = string.Empty;

    public bool FailureOutputTruncated { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowStepFailureOutcome>))]
    public WorkflowStepFailureOutcome FailureOutcome { get; init; } = WorkflowStepFailureOutcome.Unspecified;

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowRecoveryFailureKind>))]
    public WorkflowRecoveryFailureKind RecoveryFailureKind { get; init; } = WorkflowRecoveryFailureKind.Unspecified;

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowStepRetryDisposition>))]
    public WorkflowStepRetryDisposition RetryDisposition { get; init; } = WorkflowStepRetryDisposition.Unspecified;

    public ObservatoryFileItemResultSetDetail? FileItemResults { get; init; }

    public ObservatoryVoteAgreementDecisionDetail? VoteAgreementDecision { get; init; }

    // Present only when a failed attempt was followed by a new request that is still in progress.
    // Its request identity and timestamps belong to the failed attempt, never to the current retry.
    public ObservatoryFailedStepAttemptDetail? LatestFailedAttempt { get; init; }

    public IReadOnlyDictionary<string, string> RequestParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> CompletionAnnotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string NextStepId { get; init; } = string.Empty;

    public string BranchKey { get; init; } = string.Empty;

    public string AssignedVariable { get; init; } = string.Empty;

    public string AssignedValue { get; init; } = string.Empty;

    public string SuspensionType { get; init; } = string.Empty;

    public string SuspensionPrompt { get; init; } = string.Empty;

    public string SuspensionContent { get; init; } = string.Empty;

    public int? SuspensionTimeoutSeconds { get; init; }

    public string RequestedVariableName { get; init; } = string.Empty;

    public ObservatoryToolApprovalDetail? ToolApproval { get; init; }

    public ObservatoryUsageTotals Usage { get; init; } = new();
}

public sealed class ObservatoryFailedStepAttemptDetail
{
    public string DisplayName { get; init; } = string.Empty;

    public string StepType { get; init; } = string.Empty;

    public string TargetRole { get; init; } = string.Empty;

    public DateTimeOffset? RequestedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public bool? Success { get; init; }

    public double? DurationMs { get; init; }

    public string WorkerId { get; init; } = string.Empty;

    public string OutputPreview { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string FailureOutput { get; init; } = string.Empty;

    public bool FailureOutputTruncated { get; init; }

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowStepFailureOutcome>))]
    public WorkflowStepFailureOutcome FailureOutcome { get; init; } = WorkflowStepFailureOutcome.Unspecified;

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowRecoveryFailureKind>))]
    public WorkflowRecoveryFailureKind RecoveryFailureKind { get; init; } = WorkflowRecoveryFailureKind.Unspecified;

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowStepRetryDisposition>))]
    public WorkflowStepRetryDisposition RetryDisposition { get; init; } = WorkflowStepRetryDisposition.Unspecified;

    public ObservatoryFileItemResultSetDetail? FileItemResults { get; init; }

    public ObservatoryVoteAgreementDecisionDetail? VoteAgreementDecision { get; init; }

    public IReadOnlyDictionary<string, string> RequestParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> CompletionAnnotations { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string NextStepId { get; init; } = string.Empty;

    public string BranchKey { get; init; } = string.Empty;

    public string AssignedVariable { get; init; } = string.Empty;

    public string AssignedValue { get; init; } = string.Empty;

    public string SuspensionType { get; init; } = string.Empty;

    public string SuspensionPrompt { get; init; } = string.Empty;

    public string SuspensionContent { get; init; } = string.Empty;

    public int? SuspensionTimeoutSeconds { get; init; }

    public string RequestedVariableName { get; init; } = string.Empty;

    public ObservatoryToolApprovalDetail? ToolApproval { get; init; }

    public ObservatoryUsageTotals Usage { get; init; } = new();
}

public sealed class ObservatoryFileItemResultSetDetail
{
    public IReadOnlyList<ObservatoryFileItemResultDetail> Results { get; init; } = [];

    // Zero means unknown only when ResultsTruncated is true; otherwise it is the exact source count.
    public int SourceResultCount { get; init; }

    public bool ResultsTruncated { get; init; }
}

public sealed class ObservatoryFileItemResultDetail
{
    public int Index { get; init; }

    public ObservatoryWorkflowFileRefDetail? FileRef { get; init; }

    public bool Success { get; init; }

    public string Output { get; init; } = string.Empty;

    public bool OutputTruncated { get; init; }

    public string Error { get; init; } = string.Empty;

    public bool ErrorTruncated { get; init; }
}

public sealed class ObservatoryWorkflowFileRefDetail
{
    public string FileId { get; init; } = string.Empty;

    public string ArtifactId { get; init; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter<WorkflowFileSourceKind>))]
    public WorkflowFileSourceKind SourceKind { get; init; } = WorkflowFileSourceKind.Unspecified;

    public string SourceMessageId { get; init; } = string.Empty;

    public string SourceResourceKey { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    public string MediaType { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = string.Empty;

    public long CreatedAtUnixMs { get; init; }

    public long ExpiresAtUnixMs { get; init; }

    public string OwnerRunId { get; init; } = string.Empty;

    public string OwnerScopeId { get; init; } = string.Empty;
}

public sealed class ObservatoryVoteAgreementDecisionDetail
{
    [JsonConverter(typeof(JsonStringEnumConverter<AgreementDecisionKind>))]
    public AgreementDecisionKind Kind { get; init; } = AgreementDecisionKind.Unspecified;

    public string BranchKey { get; init; } = string.Empty;

    public string WinnerCandidateId { get; init; } = string.Empty;

    public string Output { get; init; } = string.Empty;

    public bool OutputTruncated { get; init; }

    public string Reason { get; init; } = string.Empty;

    public bool ReasonTruncated { get; init; }

    public IReadOnlyDictionary<string, int> LabelCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class ObservatoryToolApprovalDetail
{
    public string ExecutionId { get; init; } = string.Empty;

    public string ToolName { get; init; } = string.Empty;

    public string ToolCallId { get; init; } = string.Empty;

    public string ApprovalRequestId { get; init; } = string.Empty;
}

// 06-26 detail enrichment: run-level rollup statistics from the committed run-report artifact.
public sealed class ObservatoryRunStatistics
{
    public int TotalSteps { get; init; }

    public int RequestedSteps { get; init; }

    public int CompletedSteps { get; init; }

    public int RoleReplyCount { get; init; }

    public IReadOnlyDictionary<string, int> StepTypeCounts { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

public sealed class ObservatoryUsageTotals
{
    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int TotalTokens { get; init; }

    public double Cost { get; init; }
}

// AGUI-shaped view event reconstructed from a committed timeline-export stage (spec §7).
public sealed class ObservatoryViewEvent
{
    public string Kind { get; init; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; init; }

    public string Stage { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string AgentId { get; init; } = string.Empty;

    public string StepId { get; init; } = string.Empty;

    public string StepType { get; init; } = string.Empty;

    public ObservatoryToolCallDetail? ToolCall { get; init; }

    // 06-23 detail enrichment: the actual LLM/role reply text (for role.reply Message events), merged from
    // the committed role-reply artifact so the timeline shows real responses, not just the role id.
    public string Content { get; init; } = string.Empty;

    // AGUI event detail bag (the committed timeline event's data map) — model/tokens/usage and other
    // event-type-specific fields the viewer can surface beautified.
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed class ObservatoryToolCallDetail
{
    public string ToolName { get; init; } = string.Empty;

    public string CallId { get; init; } = string.Empty;

    public string ArgumentsJson { get; init; } = string.Empty;

    public string ResultJson { get; init; } = string.Empty;

    public bool Success { get; init; }

    public string Error { get; init; } = string.Empty;
}

public sealed class ObservatoryRunGraph
{
    public string RootNodeId { get; init; } = string.Empty;

    public long DetailStateVersion { get; init; }

    public long SourceStateVersion { get; init; }

    public ObservatoryRunDetailSectionVersionStatus VersionStatus { get; init; } =
        ObservatoryRunDetailSectionVersionStatus.Unknown;

    public string VersionReason { get; init; } = string.Empty;

    public IReadOnlyList<ObservatoryGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<ObservatoryGraphEdge> Edges { get; init; } = [];
}

public sealed class ObservatoryGraphNode
{
    public string NodeId { get; init; } = string.Empty;

    public string NodeType { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    // Bare workflow step id for WorkflowStep nodes (empty for run / actor topology nodes). The graph node
    // id is a composite key (step:{actor}:{cmd}:{stepId}); this surfaces the plain stepId so the viewer can
    // join a node to its committed timeline step events (status + detail) without parsing the composite id.
    public string StepId { get; init; } = string.Empty;
}

public sealed class ObservatoryGraphEdge
{
    public string EdgeId { get; init; } = string.Empty;

    public string FromNodeId { get; init; } = string.Empty;

    public string ToNodeId { get; init; } = string.Empty;

    public string EdgeType { get; init; } = string.Empty;

    // For NEXT (step-flow) edges, the branch taken (e.g. success / error); empty for unconditional flow
    // and for non-flow edges. Lets the viewer label conditional branches.
    public string BranchKey { get; init; } = string.Empty;
}
