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

    public IReadOnlyList<ObservatoryViewEvent> Timeline { get; init; } = [];

    public ObservatoryUsageTotals UsageTotals { get; init; } = new();
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
