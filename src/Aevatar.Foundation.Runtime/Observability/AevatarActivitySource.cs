// ─────────────────────────────────────────────────────────────
// AevatarActivitySource - distributed tracing ActivitySource.
// Creates HandleEvent activities with agentId/eventId tags.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>ActivitySource for Aevatar agent distributed tracing.</summary>
public static class AevatarActivitySource
{
    /// <summary>ActivitySource instance.</summary>
    public static readonly ActivitySource Source = new("Aevatar.Agents", "1.0.0");

    /// <summary>Starts a HandleEvent activity and attaches agentId/eventId tags.</summary>
    /// <param name="agentId">Agent ID.</param>
    /// <param name="eventId">Event ID.</param>
    /// <returns>Started activity, or null when sampling drops it.</returns>
    public static Activity? StartHandleEvent(string agentId, string eventId) =>
        Source.StartActivity($"HandleEvent {agentId}")?.SetTag("aevatar.agent.id", agentId)?.SetTag("aevatar.event.id", eventId);
}
