---
title: 0020 — Actor state schema version lives on the runtime envelope
status: accepted
owner: eanzhao
---

# 0020 — Actor state schema version lives on the runtime envelope

## Status

Accepted. The runtime migration consumer and the first concrete migration
landed with issue
[#3482](https://github.com/aevatarAI/aevatar/issues/3482). The original field
placement landed alongside ADR 0019 and was co-issued with issues
[#498](https://github.com/aevatarAI/aevatar/issues/498) and
[#500](https://github.com/aevatarAI/aevatar/issues/500).

## Context

The actor evolution matrix in
`docs/canon/event-sourcing.md#22-actor-evolution-matrix` identifies
within-actor state migration as one cell of a broader evolution decision.
The original sketch placed a `state_version` field directly on each
business state proto ("each agent's state proto carries its own schema
version"). That coupling is wrong:

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

Place `state_schema_version` on the runtime envelope, alongside `kind`, on
`RuntimeActorIdentity` (see ADR 0019). The
identity envelope is defined as a Protobuf message in
`src/Aevatar.Foundation.Abstractions/runtime_actor_identity.proto`:

```proto
message RuntimeActorIdentity {
  reserved 3;
  reserved "legacy_clr_type_name";
  string kind = 1;
  int32 state_schema_version = 2;
  repeated RuntimeActorStateSchemaAdoptionReceipt state_schema_adoptions = 4;
}
```

Orleans serializes the proto-generated class through `AddProtobufSerializer`
(wired in `AddAevatarFoundationRuntimeOrleans`), so the runtime envelope
stays a single Orleans state row while the identity contract itself
follows the Protobuf-first mandate.

Business state protos themselves stay pure domain artifacts and never
carry a version field.

### Consumer contract (lazy migration)

Within-actor schema upgrades implement `IActorStateMigration<TState>`. The
registry discovers migrations by exact `AgentKind`, requires one complete
consecutive chain from version `0` to the implementation's declared current
version, and rejects migrations for another Protobuf state contract. The
runtime reads
`Identity.StateSchemaVersion` from the runtime envelope, applies registered
migrations until the version reaches the agent's current schema, persists
the new state with the new version, and only then constructs or activates the
agent. The interface is:

```csharp
public interface IActorStateMigration<TState>
{
    int FromStateVersion { get; }
    int ToStateVersion { get; }
    TState Apply(TState state);
}
```

Constraints:

- **Pure function** of input state. No I/O, no other-actor calls, no
  random / time-dependent inputs.
- **Idempotent** — applying twice must yield the same result.
- **Total** — must not throw on any well-formed historical state.
- Migrations form a chain (`v1→v2`, `v2→v3`); skipping is forbidden.
- **Zero-dependency constructor**: implementations may not depend on
  `IServiceProvider`, any `IClient*`, any `*Async*` service, `ITimeService`,
  `IRandom`, or anything that performs I/O.

Each step also declares one exact fleet capability, contract id, and minimum
reader contract version. Adoption is fail-closed unless the authority
current-state read model proves all of the following at the same time:

1. The gate is `Open` and belongs to the fixed capability authority actor.
2. Authority state version and capability epoch are positive.
3. Membership epoch, digest, deployment revision, and validity window match
   the local runtime's current trusted membership identity.
4. Every active member advertises exactly one compatible contract, and the
   exact local member id plus incarnation occurs once in the admitted set.

The deployment revision is a stable `manifest-v1` SHA-256 digest over the
runtime grain module identity and the sorted capability contract, reader
version, and declared reader implementation module identity. A Workflow reader
assembly change therefore changes the revision even when the Orleans runtime
assembly itself did not change.

The read model is eventually consistent. A live membership epoch, digest,
deployment revision, or local incarnation mismatch rejects an old proof
immediately. If Authority revokes while the exact membership proof remains
unchanged, an unprojected stale `Open` document can be accepted only until its
committed `valid_until`; expiry is the hard bound, and admission does not
side-read the Authority actor to simulate query-time strong consistency.

The runtime atomically persists the migrated typed snapshot, state contract
name, `state_schema_version`, and one immutable
`RuntimeActorStateSchemaAdoptionReceipt` per adopted step. Orleans uses its
single persisted grain row; the local runtime uses
`RuntimeActorStateEnvelope` as the same atomic boundary. A failed write does
not leave a new schema marker or receipt attached to an old snapshot.

An adoption receipt proves that one actor row crossed a gate while the exact
fleet evidence was valid. Later expiry or revocation does not downgrade that
row or make its already-normalized state unreadable. Capabilities that begin
a new logical mutation after adoption may additionally require the live gate
to remain valid; that revalidation uses the same admission policy rather than
trusting the historical receipt as current fleet evidence.

Consumers that must persist or act on the live proof use the typed
`RuntimeFleetCapabilityAdmissionGrant`. The validator binds one cloned
admission, one local membership identity, and one validation timestamp in the
same read/validation operation. Such consumers must not first request a Boolean
grant and then re-read the admission, because those two reads could span a
membership, authority, or freshness change.

### Out of scope

- Defining a state-key protocol for projection-driven split / merge / re-key.
- The event-immutability policy ADR (separate doctrine ADR).
- A general-purpose data-transformation framework.

## Consequences

- Within-actor schema upgrades have a single canonical version axis,
  owned by the runtime envelope.
- Business state protos remain free of infrastructure version markers.
- The runtime can probe migration eligibility (`Identity.StateSchemaVersion`)
  without deserializing the state body, decoupling activation safety from
  proto evolution.
- Schema adoption is coupled to exact, freshness-bearing fleet evidence and
  leaves durable proof without turning that proof into a perpetual live-gate
  grant.
- Cross-actor state evolution (split / merge / re-key) is explicitly **not**
  served by this version field — those follow the projection-driven
  bootstrap / retire cleanup / re-key redirect canon in
  `docs/canon/event-sourcing.md` and `docs/canon/cqrs-projection.md`.

## References

- Issue #498 — `AgentKind` identity (parent decision; runtime envelope
  introduced there).
- `docs/canon/event-sourcing.md` — actor evolution matrix and lazy state migration constraints.
- `docs/canon/cqrs-projection.md` — projection-driven split / merge / re-key and query-time replay prohibition.
- Issue #500 — parent actor evolution discussion.
- ADR 0019 — companion identity ADR.
