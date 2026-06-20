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
}

public sealed class ObservatoryGraphEdge
{
    public string EdgeId { get; init; } = string.Empty;

    public string FromNodeId { get; init; } = string.Empty;

    public string ToNodeId { get; init; } = string.Empty;

    public string EdgeType { get; init; } = string.Empty;
}
