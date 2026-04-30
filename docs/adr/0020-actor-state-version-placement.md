---
title: 0020 — Actor state schema version lives on the runtime envelope
status: accepted
owner: eanzhao
---

# 0020 — Actor state schema version lives on the runtime envelope

## Status

Accepted (Phase 1 landed alongside ADR 0019). Co-issued with issues
[#498](https://github.com/aevatarAI/aevatar/issues/498) and
[#500](https://github.com/aevatarAI/aevatar/issues/500).

## Context

Issue #500 establishes the matrix of actor evolution patterns and identifies
within-actor state migration as one cell of that matrix. The original
sketch placed a `state_version` field directly on each business state proto
("each agent's state proto carries its own schema version"). That coupling
is wrong:

- **Business state protos should be pure domain artifacts.** Adding a
  runtime concern (schema version) to every business state proto bleeds
  infrastructure into the domain layer and forces every domain author to
  reason about schema migration.
- **Different actors with the same state proto would force the same
  version axis.** Schema evolution is per-actor-implementation; locking it
  to the proto pins the wrong dimension.
- **Discoverability.** `RuntimeActorGrain` has the state row in hand at
  activation; reading the version from the runtime envelope keeps the
  migration-detection path local. Reading it from the inner business
  proto requires successful deserialization of the state body, which is
  the very thing migration may need to repair.

## Decision

Place `state_schema_version` on the runtime envelope, alongside `kind` and
`legacy_clr_type_name`, on `RuntimeActorIdentity` (see ADR 0019):

```csharp
[GenerateSerializer]
public sealed class RuntimeActorIdentity
{
    [Id(0)] public string Kind { get; set; } = string.Empty;
    [Id(1)] public int StateSchemaVersion { get; set; }
    [Id(2)] public string? LegacyClrTypeName { get; set; }
}
```

Business state protos themselves stay pure domain artifacts and never
carry a version field.

### Consumer contract (issue #500's lazy migration)

Issue #500 will land an `IActorStateMigration<TState>` interface for
within-actor schema upgrades. That mechanism reads
`Identity.StateSchemaVersion` from the runtime envelope, applies registered
migrations until the version reaches the agent's current schema, persists
the new state with the new version, and only then dispatches commands to
the agent. The interface is defined as:

```csharp
public interface IActorStateMigration<TState>
{
    int FromStateVersion { get; }
    int ToStateVersion { get; }
    TState Apply(TState state);
}
```

Constraints (locked at the contract level, enforced by CI guard when the
interface lands):

- **Pure function** of input state. No I/O, no other-actor calls, no
  random / time-dependent inputs.
- **Idempotent** — applying twice must yield the same result.
- **Total** — must not throw on any well-formed historical state.
- Migrations form a chain (`v1→v2`, `v2→v3`); skipping is forbidden.
- **Zero-dependency constructor**: implementations may not depend on
  `IServiceProvider`, any `IClient*`, any `*Async*` service, `ITimeService`,
  `IRandom`, or anything that performs I/O.

These constraints are doctrine; the actual interface and CI guard land
together with the first concrete migration case (deferred per #500).

### Phase 1 scope (this PR)

- The `StateSchemaVersion` field exists on `RuntimeActorIdentity`.
- Default value is `0`; no migrations are registered yet.
- `RuntimeActorGrain` reads the field but does not yet route through any
  migration pipeline — that infrastructure lands with the first concrete
  case per #500.

This pre-records the field placement so #500's consumer contract reads
from a known location and so the field is sized correctly in Phase 1
state rows.

### Out of scope (deferred to issue #500 + follow-up issues)

- The migration interface + CI guard (zero-dependency constructor).
- The strangler-fig (projection-driven) split / merge / re-key cookbook
  and its `IActorBootstrapPort` prerequisite.
- The event-immutability policy ADR (separate doctrine ADR).
- A general-purpose data-transformation framework (explicit non-goal).

## Consequences

- Within-actor schema upgrades have a single canonical version axis,
  owned by the runtime envelope.
- Business state protos remain free of infrastructure version markers.
- The runtime can probe migration eligibility (`Identity.StateSchemaVersion`)
  without deserializing the state body, decoupling activation safety from
  proto evolution.
- Cross-actor state evolution (split / merge / re-key) is explicitly **not**
  served by this version field — those go through projection-driven
  bootstrap per #500's matrix.

## References

- Issue #498 — `AgentKind` identity (parent decision; runtime envelope
  introduced there).
- Issue #500 — actor evolution pattern matrix (consumer of this field).
- ADR 0019 — companion identity ADR.
