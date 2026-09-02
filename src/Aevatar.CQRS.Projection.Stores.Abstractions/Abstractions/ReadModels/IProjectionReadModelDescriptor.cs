namespace Aevatar.CQRS.Projection.Stores.Abstractions;

// Opt-in inventory descriptor for a materialized read-model, declared AT the read-model's store
// registration site. There is no central type->store->engine registry: the store/engine choice is a
// per-type branch inside each host's projection-store registration. So each read-model that wants to
// appear in the read-model inventory registers ONE descriptor next to its store, and DI enumerates
// IEnumerable<IProjectionReadModelDescriptor> to assemble the inventory.
//
// CaptureAsync reads the materialized read-model store ONLY (via that read-model's document reader);
// it never replays events, rebuilds state, or touches IEventStore. A read-model whose store cannot
// cheaply expose a metric returns null for it (honest, not fabricated).
public interface IProjectionReadModelDescriptor
{
    // Human-friendly read-model / index / type name (e.g. "workflow-execution").
    string Name { get; }

    // Sink shape this read-model materializes into. Drives the inventory grouping.
    ProjectionReadModelSinkShape Shape { get; }

    // Human label for the backing store engine (e.g. "Elasticsearch", "Neo4j", "dev/InMemory").
    string Engine { get; }

    // Best-available GAgent/actor kind this read-model replicates the current state of.
    string ActorKind { get; }

    // Captures the live freshness metrics by reading the materialized read-model store only.
    Task<ProjectionReadModelInventorySnapshot> CaptureAsync(CancellationToken ct = default);
}

// Sink shape of a materialized read-model. Mirrors the inventory contract shape vocabulary
// (doc/graph/mem) the console surface groups by.
public enum ProjectionReadModelSinkShape
{
    // Document store (e.g. Elasticsearch) — wire value "doc".
    Document = 0,

    // Graph store (e.g. Neo4j) — wire value "graph".
    Graph = 1,

    // In-memory / development store — wire value "mem".
    Memory = 2,
}

// Live freshness metrics captured from a read-model's materialized store. Any field is null when the
// store cannot cheaply provide it (e.g. a graph store that cannot count a single read-model type).
public sealed record ProjectionReadModelInventorySnapshot(
    long? Count,
    long? MaxStateVersion,
    DateTimeOffset? LatestUpdatedAt);
