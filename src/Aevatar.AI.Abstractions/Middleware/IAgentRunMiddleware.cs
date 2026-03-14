namespace Aevatar.AI.Abstractions.Middleware;

/// <summary>
/// Middleware that wraps the entire AI agent run (ChatAsync / ChatStreamAsync).
/// Can inspect/modify input, short-circuit execution, or transform output.
/// </summary>
public interface IAgentRunMiddleware
{
    Task InvokeAsync(AgentRunContext context, Func<Task> next);
}

/// <summary>Context for agent run middleware.</summary>
public sealed class AgentRunContext
{
    /// <summary>User message that triggered this run.</summary>
    public required string UserMessage { get; set; }

    /// <summary>Agent identifier.</summary>
    public string? AgentId { get; init; }

    /// <summary>Agent name.</summary>
    public string? AgentName { get; init; }

    /// <summary>Cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Set to short-circuit the run with a custom result.</summary>
    public string? Result { get; set; }

    /// <summary>When true, execution is terminated and Result is returned as-is.</summary>
    public bool Terminate { get; set; }

    /// <summary>Arbitrary items shared across the middleware chain.</summary>
    public IDictionary<string, object> Items { get; } = new Dictionary<string, object>();
}
