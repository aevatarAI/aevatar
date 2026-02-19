using Aevatar.AI.Abstractions.LLMProviders;

namespace Aevatar.AI.Abstractions.Middleware;

/// <summary>
/// Middleware that wraps LLM provider calls.
/// Can modify messages, inject system prompts, transform responses, or short-circuit.
/// </summary>
public interface ILLMCallMiddleware
{
    Task InvokeAsync(LLMCallContext context, Func<Task> next);
}

/// <summary>Context for LLM call middleware.</summary>
public sealed class LLMCallContext
{
    /// <summary>LLM request. Can be modified by middleware before the call.</summary>
    public required LLMRequest Request { get; set; }

    /// <summary>LLM provider being used.</summary>
    public required ILLMProvider Provider { get; init; }

    /// <summary>Cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>LLM response. Set after the call, or by middleware to override.</summary>
    public LLMResponse? Response { get; set; }

    /// <summary>When true, the LLM call is skipped and Response is returned as-is.</summary>
    public bool Terminate { get; set; }

    /// <summary>Whether this is a streaming call.</summary>
    public bool IsStreaming { get; init; }

    /// <summary>Arbitrary metadata shared across the middleware chain.</summary>
    public IDictionary<string, object> Metadata { get; } = new Dictionary<string, object>();
}
