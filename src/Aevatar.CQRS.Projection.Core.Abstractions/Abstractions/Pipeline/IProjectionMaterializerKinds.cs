namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Authoritative actor-scoped current-state replica materializer.
/// Implementations must materialize from committed state roots or equivalent full durable facts,
/// and must not depend on reading the previous document as an input to recompute current state.
/// </summary>
public interface ICurrentStateProjectionMaterializer<in TContext> : IProjectionMaterializer<TContext>
    where TContext : IProjectionMaterializationContext
{
}

/// <summary>
/// Derived durable artifact materializer.
/// Implementations may accumulate event-shaped artifacts, logs, reports, or other non-authoritative
/// outputs that are not the canonical current-state replica for the owning actor.
/// </summary>
public interface IProjectionArtifactMaterializer<in TContext> : IProjectionMaterializer<TContext>
    where TContext : IProjectionMaterializationContext
{
}
