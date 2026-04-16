// ─────────────────────────────────────────────────────────────
// IProjectedActor - optional marker for actors whose committed events
// are materialized by a projection scope. The static ProjectionKind
// identifies which scope should be activated alongside the actor's
// lifetime. Consumers (e.g. Studio / Scripting / Governance bootstraps)
// use this as a compile-time binding between agent type and scope so
// callers cannot pass a mismatched kind at a write path.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// An agent whose committed events are materialized into a read-model
/// document by a projection scope. The scope is identified by
/// <see cref="ProjectionKind"/> and must be activated before the agent
/// publishes any committed event, otherwise the projector never
/// subscribes and materialization silently drops the event.
/// </summary>
public interface IProjectedActor
{
    /// <summary>
    /// Stable projection-scope identifier for this agent type. Must be
    /// unique per actor family and should not vary by instance.
    /// </summary>
    static abstract string ProjectionKind { get; }
}
