// ─────────────────────────────────────────────────────────────
// GAgentHookContext - foundation-level hook context.
//
// Contains core event handler execution information: agent ID, event ID,
// handler name, duration, and exception details.
// AI-level hook contexts can inherit this type and add LLM/tool fields.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions.Hooks;

/// <summary>
/// Foundation-level hook context passed to IGAgentHook methods.
/// </summary>
public class GAgentHookContext
{
    /// <summary>Current agent ID.</summary>
    public string? AgentId { get; set; }

    /// <summary>Current agent type name.</summary>
    public string? AgentType { get; set; }

    /// <summary>Current event ID.</summary>
    public string? EventId { get; set; }

    /// <summary>Current event type (payload TypeUrl).</summary>
    public string? EventType { get; set; }

    /// <summary>Event handler name.</summary>
    public string? HandlerName { get; set; }

    /// <summary>Event handler execution duration.</summary>
    public TimeSpan? Duration { get; set; }

    /// <summary>Exception thrown by the event handler, if any.</summary>
    public Exception? Exception { get; set; }

    /// <summary>Extensible metadata.</summary>
    public Dictionary<string, object?> Metadata { get; } = [];
}
