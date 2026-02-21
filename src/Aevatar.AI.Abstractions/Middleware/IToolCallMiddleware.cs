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

    /// <summary>Tool execution result. Set after execution, or by middleware to override.</summary>
    public string? Result { get; set; }

    /// <summary>When true, tool execution is skipped and Result is returned as-is.</summary>
    public bool Terminate { get; set; }

    /// <summary>Arbitrary metadata shared across the middleware chain.</summary>
    public IDictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
}
