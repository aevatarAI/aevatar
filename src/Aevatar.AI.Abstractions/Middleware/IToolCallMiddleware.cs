using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Abstractions.Middleware;

/// <summary>
/// Middleware that wraps individual tool call executions.
/// Can validate arguments, override results, block execution, or add approval gates.
/// </summary>
public interface IToolCallMiddleware
{
    Task InvokeAsync(ToolCallContext context, Func<Task> next);
}

/// <summary>Closed middleware termination status for tool-call policy outcomes.</summary>
public enum ToolCallTerminationKind
{
    None = 0,
    ApprovalDenied = 1,
    ApprovalTimedOut = 2,
    ApprovalPending = 3,
    MiddlewareTerminated = 4,
}

public sealed record ToolApprovalPendingContext(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId,
    string ArgumentsJson,
    ToolApprovalMode ApprovalMode,
    bool IsReadOnly,
    bool IsDestructive);

public sealed record ToolApprovalGrant(
    string ApprovalRequestId,
    string ToolName,
    string ToolCallId);

/// <summary>Context for tool call middleware.</summary>
public sealed class ToolCallContext
{
    /// <summary>Tool being called.</summary>
    public required IAgentTool Tool { get; init; }

    /// <summary>Tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>Tool call ID from LLM.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Arguments JSON. Can be modified by middleware before execution.</summary>
    public required string ArgumentsJson { get; set; }

    /// <summary>Cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Typed execution context snapshot for terminal observers.</summary>
    public AgentToolExecutionContext? ExecutionContext { get; init; }

    /// <summary>Credential source selected for this tool call.</summary>
    public AgentToolCredentialSource CredentialSource { get; set; }

    /// <summary>Tool execution result. Set after execution, or by middleware to override.</summary>
    public string? Result { get; set; }

    /// <summary>Typed receipt for receipt-worthy tool execution or approval yield.</summary>
    public AgentToolReceipt? Receipt { get; set; }

    /// <summary>Typed business keys for a tool approval yield.</summary>
    public ToolApprovalPendingContext? PendingApproval { get; set; }

    /// <summary>Typed approval replay grant supplied by the workflow actor.</summary>
    public ToolApprovalGrant? ApprovalGrant { get; set; }

    /// <summary>When true, tool execution is skipped and Result is returned as-is.</summary>
    public bool Terminate { get; set; }

    /// <summary>Closed reason for middleware termination.</summary>
    public ToolCallTerminationKind TerminationKind { get; set; }

    /// <summary>Human-readable policy or middleware termination reason.</summary>
    public string? TerminationReason { get; set; }

    /// <summary>Arbitrary items shared across the middleware chain.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}
