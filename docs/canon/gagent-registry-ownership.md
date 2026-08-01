---
title: "GAgent Registry Ownership"
status: active
owner: architecture
---

# GAgent Registry Ownership

This document is the durable architecture rule for issue 348. It defines how GAgent registry command, query, and command-admission semantics are separated.

## Core Rule

GAgent scope membership has one authoritative owner.

For the current architecture, the authority is the per-scope `GAgentRegistryGAgent` state reached through the registry command/admission contract. A future implementation may replace this with an explicitly modeled distributed authoritative ownership index, but it must still be a single authority.

Registry and admission membership is keyed by canonical `AgentKind`. Runtime implementation shape is diagnostic only. `ImplementationClrTypeName`, local class names, actor proxy types, and legacy `GAgentType` spellings must not be used as registry keys, admission keys, or draft-run target identity.

Legacy registry rows that were persisted under CLR type names are canonicalized only by the registry authority. The registry may ask an actor-owned kind probe for the actor's canonical kind and then commit a single canonicalization event for that actor. If a legacy row cannot be mapped by that actor-owned contract, it is quarantined for diagnostics and must not be admitted through a CLR-name fallback.

The registry current-state read model is a query replica. It is useful for list/search/display flows, but it is eventually consistent and must not be used as command admission or security-sensitive target authorization.

Target actors may own their capability-local business facts. They must not independently own the same `scope_id -> resource` membership fact that the registry owns. If a target actor stores a scope-shaped value for validation, diagnostics, or event payload completeness, that value is a derived mirror and cannot override or contradict registry ownership.

## Ports

`IGAgentActorRegistryCommandPort` owns registry lifecycle writes:

- register actor membership for a scope
- unregister actor membership for a scope
- return only an honest dispatch/acceptance result unless a stronger receipt is explicitly modeled
- expose a committed or admission-visible receipt when a caller needs create-then-immediately-operate semantics
- never report removal from an ordinary unregister dispatch receipt

`IGAgentActorRegistryUnregistrationPort` is the narrow continuation contract for
workflows that must observe committed removal or authoritative absence:

- carry one stable operation ID plus exact registry Actor, scope, `AgentKind`,
  target Actor, and completion Actor identities
- require the completion Actor to equal the target Actor, so an unregister
  operation cannot redirect lifecycle authority to another Actor
- let the registry Actor commit removal or authoritative absence before it emits
  `GAgentRegistryUnregistrationCompleted`
- emit the completion with the actual registry Actor as the direct-envelope
  publisher; consumers must match both payload identity and envelope publisher
- redeliver the immutable completion for an exact duplicate and fail closed for
  a changed tuple
- retain a typed deterministic order of at most 256 operations; only callbacks
  already admitted to their completion Actor inbox are evictable, while rejected
  callbacks remain pinned for exact redrive
- reject a new operation before commit when all retained entries are still
  pending, rather than dropping active recovery authority

`IGAgentActorRegistryQueryPort` owns registry listing reads:

- read the registry current-state read model
- return a snapshot that exposes source version or observation timestamp
- never return an ownership verdict

`IScopeResourceAdmissionPort` owns command-path target admission:

- answer whether a typed target can be operated on under the requested scope
- return a typed result such as `Allowed`, `Denied`, `NotFound`, `ScopeMismatch`, or `Unavailable`
- never return registry groups, target state, readmodel documents, or arbitrary actor data

The admission port is not a generic actor query/reply escape hatch. Implementations must be scoped by capability or resource kind and may only answer the admission verdict.

## Required Flow

Create:

1. HTTP endpoint validates the caller scope and delegates to an application command surface.
2. Application command path creates or activates the target resource through the capability command path.
3. Registry command port submits authoritative scope membership to the registry ownership authority.
4. If the response or follow-up command path promises that the target is immediately operable, the application command surface must obtain a committed or admission-visible registry receipt through the registry ownership contract. An accepted-for-dispatch receipt is not enough.
5. The response must not promise immediate registry list visibility.

Unregister with committed continuation:

1. The lifecycle owner commits the deterministic registry Actor ID and one
   stable unregistration operation ID.
2. The narrow unregistration port dispatches the exact typed tuple to that
   per-scope registry Actor and returns only accepted-for-dispatch.
3. The registry Actor commits removal or authoritative absence and the immutable
   completion tuple before callback dispatch.
4. A rejected callback remains retained and exact-redrivable. Accepted callback
   admission makes an old terminal entry eligible for bounded eviction; it does
   not rewrite the completion fact.
5. The lifecycle owner advances only when payload identity and direct-envelope
   publisher both match its committed tuple.

List:

1. HTTP endpoint validates the caller scope and delegates to an application query surface.
2. Registry query port reads the current-state read model.
3. The response is explicitly eventually consistent and includes freshness information.

Operate on target:

1. HTTP endpoint validates the caller scope and delegates to an application command/admission surface.
2. Application surface builds a typed `ScopeResourceTarget`.
3. Admission port checks the target against the authoritative registry ownership contract.
4. Only `Allowed` dispatches to the target capability.
5. `Denied` maps to `403`, `ScopeMismatch` maps to `403`, `NotFound` maps to `404`, and `Unavailable` maps to `503`. In the current per-scope registry implementation, a route-supplied id that is registered under another scope may be returned as `NotFound` because discovering cross-scope existence would require a second authority or a forbidden side read. Implementations may return `ScopeMismatch` only when that verdict comes from the registry ownership contract or an explicitly modeled distributed ownership index.

Route-supplied actor ids must not create targets implicitly. Actor activation alone is not ownership evidence.

Admission freshness is separate from list freshness. Admission may be stronger than the registry read model, but that strength must come from the registry ownership command/admission contract or an explicitly modeled distributed ownership index, not from a side read.

## Forbidden Paths

- using `GAgentRegistryCurrentStateDocument` or `ListActorsAsync` as command admission
- parsing actor id prefixes, suffixes, type names, or hashes as ownership facts
- query-time projection priming, replay, or readmodel refresh before admission
- direct reads of actor state, event store, snapshots, or state mirror payloads in application query or admission paths
- implementing admission by side-reading `GAgentRegistryGAgent` state, registry actor snapshots, event-store history, or state mirror payloads
- mapping `AgentKind` back to `ImplementationClrTypeName` or a CLR class name for registry/admission identity
- accepting request or tool identity aliases such as `actorTypeName`, `gagentType`, `gagent_type`, or Aevatar invocation `actor_name`
- generic actor query/reply or request/reply RPC as a fallback read path
- implicit actor activation or get-or-create runtime lookup as ownership evidence
- process-local dictionaries, caches, or registries as scope membership fact state
- target-owned duplicate scope membership authority beside the registry ownership authority

## Tests And Guards

Changes in this area must cover:

- create followed immediately by operate does not depend on registry projection visibility
- create followed immediately by operate has an explicit committed or admission-visible registration receipt, not just accepted dispatch
- list reads remain eventually consistent and expose freshness
- route-supplied targets from another scope return `ScopeMismatch` only when the authoritative ownership contract can distinguish it without side reads; otherwise they return `NotFound` or an equivalent non-leaking admission result
- missing route-supplied targets return `NotFound` without creating a target
- production code no longer depends on `IGAgentActorStore`
- architecture guards prevent new production references to `IGAgentActorStore` after removal
- registry, admission, draft-run, tool, and frontend runtime paths use `AgentKind`/`agent_kind`
- legacy identity aliases are absent from positive request schemas and are rejected at HTTP/tool boundaries
- old CLR-keyed rows are canonicalized only through actor-owned kind facts, and unmappable rows are not admitted
- ordinary unregister receipts never claim committed removal
- typed unregistration commits before callback, exact duplicates redeliver, and
  changed tuples or redirected completion Actors fail closed
- completion payload identity is bound to the actual registry Actor publisher
- the 256-entry operation ledger evicts only dispatch-admitted callbacks,
  preserves rejected callbacks for exact redrive, and rejects all-pending
  overflow without growing Actor state
