// ─────────────────────────────────────────────────────────────
// AevatarActivitySource - distributed tracing ActivitySource.
// Runtime-level spans for actor event handling.
// ─────────────────────────────────────────────────────────────

using System.Diagnostics;

namespace Aevatar.Foundation.Runtime.Observability;

/// <summary>ActivitySource for Aevatar runtime tracing.</summary>
public static class AevatarActivitySource
{
    /// <summary>ActivitySource instance.</summary>
    public static readonly ActivitySource Source = new("Aevatar.Agents", "1.0.0");

    /// <summary>Starts a HandleEvent activity used by LocalActor runtime dispatch.</summary>
    public static Activity? StartHandleEvent(string agentId, string eventId) =>
        Source.StartActivity($"HandleEvent {agentId}")
            ?.SetTag("aevatar.agent.id", agentId)
            ?.SetTag("aevatar.event.id", eventId);
}
