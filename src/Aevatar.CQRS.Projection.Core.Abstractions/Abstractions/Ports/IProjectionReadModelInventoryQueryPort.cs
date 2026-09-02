using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.CQRS.Projection.Core.Abstractions;

// Read-model INVENTORY query port: lists the materialized read-model types grouped by sink shape, so an
// operations surface (the CQRS observatory read-model panel) can show what is materialized and how fresh.
//
// It enumerates the opt-in IProjectionReadModelDescriptor set registered next to each read-model's store
// and captures each one's freshness metrics. Every capture reads the materialized read-model store ONLY:
// it never replays event streams, rebuilds state, or touches IEventStore (the read-write-separation
// invariant shared with IProjectionScopeStatusListQueryPort).
public interface IProjectionReadModelInventoryQueryPort
{
    Task<ProjectionReadModelInventory> GetInventoryAsync(CancellationToken ct = default);
}

// Read-model inventory grouped by sink shape (doc/graph/mem), shaped for the operations surface.
public sealed record ProjectionReadModelInventory(
    IReadOnlyList<ProjectionReadModelGroup> Groups);

// One sink-shape group (e.g. all document read-models on Elasticsearch).
public sealed record ProjectionReadModelGroup(
    ProjectionReadModelSinkShape Shape,
    string Engine,
    IReadOnlyList<ProjectionReadModelInventoryItem> Items);

// One materialized read-model and its captured freshness. Version/Updated/Count are null when the
// backing store cannot cheaply provide them.
public sealed record ProjectionReadModelInventoryItem(
    string Name,
    string Actor,
    long? Version,
    DateTimeOffset? Updated,
    long? Count);
