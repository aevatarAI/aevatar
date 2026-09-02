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

// Read-only view DTOs (Host -> browser JSON, sanctioned by observability.md §9). All carry the
// authoritative state version / refresh stamp so the page can be honest that the readmodel is
// eventually consistent.
public sealed class ObservatoryRunSummary
{
    public string RunId { get; init; } = string.Empty;

    public string WorkflowName { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public bool? Success { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public long StateVersion { get; init; }

    // 06-20-observatory-admin-cross-scope: the run's owning scope. Populated for every read; the page only
    // surfaces it in admin cross-scope mode (own-scope callers already know their scope).
    public string ScopeId { get; init; } = string.Empty;

    // 06-23-observatory-run-coverage-filter: canonical run origin/type (draft | member-invoke | ...),
    // empty for legacy/unstamped runs. Drives the run-type filter + badge.
    public string RunOrigin { get; init; } = string.Empty;
}

public sealed class ObservatoryRunDetail
{
    public ObservatoryRunSummary Summary { get; init; } = new();

    // 06-26 detail enrichment: the run's authoritative input + final result, surfaced from the committed
    // run-report artifact. These are NOT truncated by materialization (unlike per-step OutputPreview), so the
    // viewer can show the real final output/error honestly. FinalOutput is empty while a run is still running.
    public string Input { get; init; } = string.Empty;

    public string FinalOutput { get; init; } = string.Empty;

    public string FinalError { get; init; } = string.Empty;

    // Diagnostics derived from committed current-state/readmodel facts and the run-report artifact. They are
    // query-time explanations, not durable log entries or deletion tombstones.
    public IReadOnlyList<ObservatoryRunDiagnostic> Diagnostics { get; init; } = [];

    // 06-26 detail enrichment: per-step structured trace (status / timing / params / per-step usage). The step
    // OUTPUT is a 240-char preview by current materialization; the FULL per-step output lives in Timeline
    // (role.reply Content / tool-call ResultJson), which this detail already carries.
    public IReadOnlyList<ObservatoryStepDetail> Steps { get; init; } = [];

    public IReadOnlyList<ObservatoryViewEvent> Timeline { get; init; } = [];

    public ObservatoryRunStatistics Statistics { get; init; } = new();

    public ObservatoryUsageTotals UsageTotals { get; init; } = new();
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
}

// 06-26 detail enrichment: read-only per-step view DTO mirroring the committed run-report step trace.
public sealed class ObservatoryStepDetail
{
    public string StepId { get; init; } = string.Empty;

    public string StepType { get; init; } = string.Empty;

    public string TargetRole { get; init; } = string.Empty;

    public DateTimeOffset? RequestedAtUtc { get; init; }

    public DateTimeOffset? CompletedAtUtc { get; init; }

    public bool? Success { get; init; }

    public double? DurationMs { get; init; }

    // A 240-char preview of the step output (current materialization truncates here). The full output is
    // available via the run Timeline (role.reply Content / tool-call ResultJson); this field is a preview.
    public string OutputPreview { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> RequestParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public string NextStepId { get; init; } = string.Empty;

    public string BranchKey { get; init; } = string.Empty;

    public string SuspensionType { get; init; } = string.Empty;

    public string SuspensionPrompt { get; init; } = string.Empty;

    public string SuspensionContent { get; init; } = string.Empty;

    public int? SuspensionTimeoutSeconds { get; init; }

    public ObservatoryUsageTotals Usage { get; init; } = new();
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

    public IReadOnlyList<ObservatoryGraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<ObservatoryGraphEdge> Edges { get; init; } = [];
}

public sealed class ObservatoryGraphNode
{
    public string NodeId { get; init; } = string.Empty;

    public string NodeType { get; init; } = string.Empty;

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
