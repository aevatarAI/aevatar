---
title: "Projection Graph Retention and Capacity"
status: active
owner: platform
---

# Projection Graph Retention and Capacity

## Product contract

Milestone 44 selects **indefinite retention** for projection graphs. Workflow
run graphs and script-native graphs remain queryable for as long as their
authoritative committed facts are retained. Archiving a workflow definition or
finishing a run does not expire its graph, report, timeline, or current-state
read model.

Queries therefore keep their existing behavior:

- a retained graph is returned at its materialized source version;
- a graph that has never been materialized is unavailable/not found;
- the API never reports `expired` under this contract;
- graph absence must not cause query-time replay, projection activation,
  replacement, repair, or cleanup.

There is no owner TTL, delete-on-completion path, or background graph purge in
this release. `ReplaceOwnerGraphAsync` may remove stale elements while updating
the same active owner, but it is not a retention operation.

## Capacity contract

Indefinite retention makes capacity an explicit operational responsibility.
For every production Neo4j database that stores projection graphs, operators
must record these values at least daily across both the legacy graph objects
and the versioned owner-state objects:

- managed owner, node, and relationship counts plus versioned owner-state,
  owner-event, edge-identity, and pending-edge-identity counts, grouped only
  for offline diagnosis rather than metric labels;
- database store bytes, transaction-log bytes, provisioned disk bytes, and
  replica factor;
- page-cache hit ratio and page-cache usage;
- `replace_owner_graph` and incremental graph-write p50/p90/p99 duration and
  failure totals from `Aevatar.CQRS.Projection.Providers.Neo4j`;
- seven-day and thirty-day net growth for owners, nodes, relationships,
  owner events, edge identities, pending identities, and store bytes.

Capacity policy:

| Signal | Target | Warning | Critical / required action |
|---|---:|---:|---|
| Database volume utilization | `< 70%` | `>= 70%` | `>= 80%`: add capacity before the next forecast crossing |
| Forecast volume utilization, 90 days | `< 70%` | `>= 70%` | `>= 80%`: capacity change is release-blocking |
| Page-cache hit ratio, 15 minute window | `>= 95%` | `< 95%` | `< 90%`: investigate working-set fit and query plans |
| Projection graph write p90 | within the post-#3473 baseline | `> 1.5x` baseline for 15 minutes | `> 2x` baseline for 15 minutes: incident review |

The 90-day forecast is deliberately simple and auditable:

```text
projectedBytes90d = currentStoreBytes + max(growthBytes7d / 7, growthBytes30d / 30) * 90
projectedUtilization90d = projectedBytes90d / provisionedDatabaseBytes
```

A negative growth rate is treated as zero. Forecasts include the configured
replica factor and enough temporary headroom for backup, restore, index/schema
work, and rolling provider migration. Capacity expansion is the normal response
to a forecast breach; deletion is not authorized by this runbook.

## Read-only inventory

Run inventory with a read-only Neo4j identity. Do not return `propertiesJson`,
node ids, edge ids, owner ids, workflow content, or credentials in operational
logs.

Before running the queries, resolve the deployed provider identifiers from
`Projection:Graph:Providers:Neo4j`: `<node-label>` is the normalized
`NodeLabel`, `<rel-type>` is the normalized `EdgeType`, and the versioned labels
are `<node-label>OwnerState`, `<node-label>OwnerEvent`, and
`<node-label>EdgeIdentity`. With the defaults these are
`ProjectionGraphNode`, `PROJECTION_REL`,
`ProjectionGraphNodeOwnerState`, `ProjectionGraphNodeOwnerEvent`, and
`ProjectionGraphNodeEdgeIdentity`. Substitute all five together; mixing the
default main labels with customized versioned labels produces a false capacity
report.

```cypher
MATCH (n:ProjectionGraphNode)
WHERE coalesce(n.projectionManaged, false) = true
RETURN count(DISTINCT [n.scope, n.projectionOwnerId]) AS owners,
       count(n) AS nodes;
```

```cypher
MATCH ()-[r:PROJECTION_REL]->()
WHERE coalesce(r.projectionManaged, false) = true
RETURN count(r) AS relationships;
```

```cypher
MATCH (state:ProjectionGraphNodeOwnerState)
RETURN count(state) AS versionedOwners;
```

```cypher
MATCH (event:ProjectionGraphNodeOwnerEvent)
RETURN count(event) AS versionedOwnerEvents;
```

```cypher
MATCH (identity:ProjectionGraphNodeEdgeIdentity)
RETURN count(identity) AS edgeIdentities,
       sum(CASE WHEN identity.status = 'pending' THEN 1 ELSE 0 END) AS pendingEdgeIdentities;
```

Use database-management metrics for store, transaction-log, disk, and
page-cache values. Validate the resolved labels and relationship type against
the provider's deployed schema objects before recording the sample.

The inventory queries are observational only. Never attach `DELETE`, `DETACH
DELETE`, `SET`, schema mutation, or query-time materialization to this check.

## Growth review

Keep a rolling daily record containing the UTC observation time, deployment
revision, Neo4j database, all legacy and versioned counts above, bytes,
page-cache values, graph-write quantiles, forecast, and capacity ticket when
one is required. Do not include owner identities or graph payloads.

Review the record weekly and before a release that materially changes workflow
fan-out, retained graph shape, or projection volume. A rising total graph size
is expected under indefinite retention; unbounded write latency is not. The
owner indexes and bounded delta path must keep active-owner write work
independent of unrelated retained owners.

## Changing this decision

Finite retention is a future product and data-lifecycle change, not an
operations toggle. It requires all of the following before any deletion code is
enabled:

1. a typed committed retirement/archive fact with one authoritative actor;
2. an explicit retention start instant and duration for workflow and script
   owners;
3. aligned report, timeline, current-state, graph, and export query semantics;
4. a durable actor-owned cleanup/checkpoint flow with idempotent retry;
5. atomic owner deletion that preserves foreign relationships and shared nodes;
6. API tests for the exact expiry boundary and an explicit expired result;
7. backup, restore, audit, legal-hold, and rollback approval.

No service-level owner registry, string status heuristic, query-time cleanup,
event-store side read, replay, or projection priming may substitute for that
contract.
