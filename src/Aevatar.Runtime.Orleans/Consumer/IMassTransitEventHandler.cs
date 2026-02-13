// ─────────────────────────────────────────────────────────────
// IMassTransitEventHandler - bridge interface from MassTransit
// Consumer to Orleans Grain.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Orleans.Consumer;

/// <summary>
/// Bridges MassTransit consumer messages to the correct Orleans Grain.
/// </summary>
public interface IMassTransitEventHandler
{
    /// <summary>Routes an event envelope to the target Grain.</summary>
    /// <param name="agentId">Target agent/Grain ID.</param>
    /// <param name="envelope">Deserialized event envelope.</param>
    /// <returns>True if handled successfully.</returns>
    Task<bool> HandleEventAsync(string agentId, EventEnvelope envelope);
}
