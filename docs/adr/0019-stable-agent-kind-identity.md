---
title: 0019 — Stable AgentKind identity replaces CLR-name actor identity
status: accepted
owner: eanzhao
---

# 0019 — Stable `AgentKind` identity replaces CLR-name actor identity

## Status

Accepted (Phase 1 landed). Tracked in issue [#498](https://github.com/aevatarAI/aevatar/issues/498).

## Context

`RuntimeActorGrainState.AgentTypeName` persisted a CLR fully-qualified type
name and `RuntimeActorGrain.OnActivateAsync` resolved it via
`Type.GetType` + `AppDomain.GetAssemblies()`. Authoritative state therefore
referenced a runtime-incidental implementation detail, so every rename /
move / split of an actor class became a destructive migration concern.

Concrete failure modes:

- **PR #495** had to ship spec-driven cleanup for retired `ChannelRuntime`
  types because old actor rows still pointed at deleted CLR names.
- **PR #497** (skill-runner split) reproduced the same activation-failure
  shape for live business actors, blocking on a long-term identity fix.
- The original governance proposal in #498 (drop / preserve / dormant
  classification, per-PR CI gate) treated this as a process problem; on
  reflection it is the symptom layer of the identity coupling.

## Decision

Replace CLR-name identity with stable, business-meaningful **`AgentKind`**
tokens. Persisted state references the kind; an `IAgentKindRegistry` maps
kind to implementation at activation time. CLR type names disappear from
authoritative state.

### Identity envelope

A new `RuntimeActorIdentity` sub-record on `RuntimeActorGrainState` (Orleans
serialized, `[Id(7)]`) carries:

- `Kind` — the FQ kind token (e.g. `scheduled.skill-runner`,
  `channels.bot-registration`).
- `StateSchemaVersion` — runtime-owned schema marker for the actor's
  persisted business state. Business state protos themselves stay pure
  domain artifacts and never carry a version field. See ADR 0020 for
  rationale and the consumer contract from issue #500.
- `LegacyClrTypeName` — populated only during the Phase 1/2 transition;
  becomes `reserved` in Phase 3.

### Registry contract

```csharp
public interface IAgentKindRegistry
{
    AgentImplementation Resolve(string kind);
    bool TryResolveKindByClrTypeName(string clrFullName, out string kind);
    bool TryGetKind(AgentImplementation implementation, out string kind);
}

public sealed record AgentImplementation(
    Func<IAgent> Factory,
    Type StateContractType,
    AgentImplementationMetadata Metadata);
```

The registry returns an opaque `AgentImplementation` handle —
`AgentImplementation.Factory()` produces an `IAgent`. The implementation
CLR type does not appear in the contract surface, keeping the door open
for scripted / workflow / out-of-process implementations behind the same
kind.

### Kind naming convention

- Format: `<module>.<entity>` — e.g. `scheduled.skill-definition`,
  `channels.bot-registration`. Module prefix is mandatory (avoids
  cross-module collisions); the prefix matches the project namespace it
  lives in.
- Kinds are **never versioned**. `skill-runner-v2` is forbidden — kind
  identifies the business entity; schema evolution within a kind goes
  through proto3 field rules or the state-version migration mechanism
  (see ADR 0020 / issue #500), never through kind rename.
- CI guard `tools/ci/agent_kind_naming_guard.sh` enforces
  `^[a-z0-9]+(\.[a-z0-9]+(-[a-z0-9]+)*)+$` and rejects `-v\d+` tails.

### Attribute surface

- `[GAgent("scheduled.skill-runner")]` — declares the primary kind on an
  agent class. Single-valued.
- `[LegacyAgentKind("scheduled.skill-runner")]` — claims a previously-used
  kind on a new class so persisted state pointing at the old kind resolves
  to the new implementation without state mutation. Multi-valued.
- `[LegacyClrTypeName("...")]` (existing, in
  `Aevatar.Foundation.Abstractions.Compatibility`) — already used by
  payload codec compatibility; reused here for agent-class CLR aliases
  during the transition window.

### Activation flow

`RuntimeActorGrain.OnActivateAsync` resolves identity in this order:

1. `Identity.Kind` non-empty → `IAgentKindRegistry.Resolve(kind)`.
2. Otherwise, if `AgentTypeName` is set, fall back through:
   1. `IAgentKindRegistry.TryResolveKindByClrTypeName(name)` → registered
      class found via current `Type.FullName` or `[LegacyClrTypeName]`
      alias → resolve and **lazy-tag** `Identity.Kind` on the state row.
      `AgentTypeName` is preserved untouched until Phase 3 hard-deprecation
      so mixed-version pods stay compatible.
   2. `ILegacyAgentClrTypeResolver.TryResolve(name)` (transitional fallback)
      — encapsulates the previous `Type.GetType` + `AppDomain` reflection
      probe. Activations resolved through this lane do **not** lazy-tag
      `Identity.Kind` because un-decorated classes have no stable kind to
      record.

The reflection scan that previously lived inline in
`RuntimeActorGrain.ResolveAgentType` is removed. The transitional fallback
lives behind `ILegacyAgentClrTypeResolver`; Phase 3 drops the default
registration so un-decorated classes fail to activate.

### Retire vs migrate (collapsed)

Under kind-token identity the original 3-classification governance scheme
collapses to two cases:

- **Keep** (default, free): identity-only rename / move / class split is
  expressed by adding a `[LegacyAgentKind]` alias on the new class. No
  production state mutation. No spec. No CI-gate firing.
- **Retire** (destructive, requires explicit spec): a kind is removed from
  the registry → spec-driven cleanup destroys persisted state of that
  kind. PR #495's mechanism handles this case unchanged; its specs migrate
  from CLR-name tokens to `AgentKind` tokens in a follow-up.

`dormant/delete` is removed entirely — it was never safe.

**Re-keying** (preserving kind, changing actor id) is **not** in either
bucket. A separate `IActorRedirectSpec` (mapping
`from_kind:from_id` → `to_kind:to_id`) handles it; tracked separately,
out of scope here.

## Consequences

- PR #497 (`SkillRunner` split) ships as a kind-alias registration on
  `SkillDefinitionGAgent` with zero state mutation and natural
  mixed-version rollout safety.
- PR #495 ships unchanged today; its specs migrate from CLR-name tokens
  to `AgentKind` tokens in a follow-up PR — same mechanism, more durable
  keys.
- All future identity-only refactors (rename, move, class split) avoid the
  "destroy or migrate persisted state?" question entirely. State-shape
  refactors still flow through the matrix in ADR 0020.

## Phased rollout

**Phase 1 (this PR)** — additive identity envelope, registry, attributes,
grain wiring, ADRs, CI guard. Existing un-decorated classes continue to
activate via the transitional reflection fallback.

**Phase 2** — modules opportunistically decorate classes with `[GAgent]` /
`[LegacyAgentKind]` as they're touched; PR #497 uses this pattern. PR #495
specs migrate to `AgentKind` tokens.

**Phase 3** — telemetry confirms no recent reads of `AgentTypeName` as
primary identity (recommended grace ≥ 4 weeks). Mark
`RuntimeActorGrainState.AgentTypeName` as `reserved`; remove default
registration of `ILegacyAgentClrTypeResolver`. Un-decorated classes stop
activating.

## Supersedes

This ADR supersedes the original 3-classification (`drop/rebuild`,
`preserve/migrate`, `dormant/delete`) framing of issue #498.
