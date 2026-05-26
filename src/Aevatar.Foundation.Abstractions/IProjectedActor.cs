// ─────────────────────────────────────────────────────────────
// IProjectedActor - optional marker for actors whose committed events
// are materialized by a projection scope. The static ProjectionKind
// identifies which committed-state activation plan should start the
// materialization scope for that actor family.
// ─────────────────────────────────────────────────────────────

namespace Aevatar.Foundation.Abstractions;

/// <summary>
/// An agent whose committed events are materialized into a read-model
/// document by a projection scope. The scope is identified by
/// <see cref="ProjectionKind"/>; committed-state projection activation
/// providers use it to bind actor type to readmodel materialization kind.
/// </summary>
public interface IProjectedActor
{
    /// <summary>
    /// Stable projection-scope identifier for this agent type. Must be
    /// unique per actor family and should not vary by instance.
    /// </summary>
    static abstract string ProjectionKind { get; }
}
