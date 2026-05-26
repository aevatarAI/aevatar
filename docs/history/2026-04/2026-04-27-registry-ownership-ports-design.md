---
title: GAgent Registry Ownership Ports Design
status: history
owner: architecture
---

# GAgent Registry Ownership Ports Design

Date: 2026-04-27

> Non-authoritative design snapshot. The durable architecture rule for this topic lives in [GAgent Registry Ownership](../../canon/gagent-registry-ownership.md); this file records the implementation direction for issue 348.

## Context

Issue 348 started as a request to refine `IGAgentActorStore` into clearer registry command and query ports. The review discussion around StreamingProxy and NyxID exposed a deeper problem: the current abstraction combines three different meanings behind one `Store` interface.

- Registry lifecycle commands: register or unregister an actor for a scope.
- Registry listing queries: list actors in a scope from the registry current-state read model.
- Command admission: decide whether a caller may operate on a concrete target actor under a requested scope.

The default implementation writes by dispatching commands to `GAgentRegistryGAgent`, but reads from `GAgentRegistryCurrentStateDocument`, a CQRS projection. That read model is eventually consistent. Using `GetAsync(scopeId)` as a synchronous ownership check can reject a valid actor immediately after registration when projection has not caught up.

The tactical alternative of deriving ownership from the actor id format is also not acceptable as a framework-level solution. It makes `actorId` carry business facts, depends on string parsing, is forgeable by callers, and conflicts with the rule that `actorId` is an opaque address.

## Goals

- Replace `IGAgentActorStore` with explicit command, query, and admission ports.
- Make read consistency honest: registry list queries are read-model queries and are eventually consistent.
- Keep command admission out of projection reads and actor id parsing.
- Give StreamingProxy, NyxID chat, and draft-run actor preparation a stable ownership/admission contract.
- Delete `IGAgentActorStore` as a production abstraction instead of preserving it as a compatibility facade.
- Keep API/Host endpoints as composition surfaces; business routing and admission rules live behind application/domain-level ports.

## Non-Goals

- Do not introduce generic actor query/reply as a fallback for reading another actor's internal state.
- Do not implement query-time projection priming or synchronous read-model refresh.
- Do not make `GAgentRegistryGAgent` a high-throughput central RPC service for every target operation.
- Do not encode scope ownership into actor id syntax.
- Do not add a second registry authority backed by process memory.

## Proposed Ports

### Registry Command Port

`IGAgentActorRegistryCommandPort` owns lifecycle writes.

Expected operations:

```csharp
Task RegisterActorAsync(
    GAgentActorRegistration registration,
    CancellationToken ct = default);

Task UnregisterActorAsync(
    GAgentActorRegistration registration,
    CancellationToken ct = default);
```

`GAgentActorRegistration` should carry `ScopeId`, `GAgentType`, and `ActorId` as typed fields. The port dispatches commands to the per-scope registry actor. A bare `Task` return means the command was accepted for dispatch or failed before dispatch. It must not imply that the registry read model has observed the change, and it must not imply that the membership is committed or admission-visible.

Create-then-immediately-operate flows need a stronger contract than bare accepted dispatch. The implementation should introduce an explicit registry command receipt, or an equivalent operation-specific method, that can distinguish at least `AcceptedForDispatch` from `Committed` or `AdmissionVisible`. Callers may only treat a newly created target as immediately operable after the committed/admission-visible stage has been reached through the registry ownership contract.

### Registry Query Port

`IGAgentActorRegistryQueryPort` owns registry listing reads.

Expected operations:

```csharp
Task<GAgentActorRegistrySnapshot> ListActorsAsync(
    ScopeId scopeId,
    CancellationToken ct = default);
```

The query port reads `GAgentRegistryCurrentStateDocument`. Its result should be named as a snapshot/read model result, not an ownership verdict. The snapshot must expose the source version or the read model observation timestamp so callers can be honest about freshness. If the current read model cannot provide that value, the implementation work must add one instead of hiding the gap in API prose.

### Scope Resource Admission Port

`IScopeResourceAdmissionPort` owns command-admission decisions for concrete targets.

Expected operation:

```csharp
Task<ScopeResourceAdmissionResult> AuthorizeTargetAsync(
    ScopeResourceTarget target,
    CancellationToken ct = default);
```

`ScopeResourceTarget` should include:

- `ScopeId`
- `ResourceKind`
- `GAgentType`
- `ActorId`
- `Operation`

`ResourceKind` and `Operation` must be typed values, such as enums or constrained value objects, not open string bags. They affect authorization and routing semantics, so they fall under the repository's strong-type rule for stable control-flow data.

The result should distinguish at least:

- `Allowed`
- `Denied`
- `NotFound`
- `ScopeMismatch`
- `Unavailable`

`Denied` means the caller/action is not allowed even when the target belongs to the requested scope. `ScopeMismatch` means the target exists but is authoritatively bound to a different scope, and may only be returned when that verdict comes from the registry ownership contract or an explicitly modeled distributed ownership index. The current per-scope registry actor implementation may return `NotFound` for route-supplied ids registered under another scope, because discovering cross-scope existence would otherwise require a forbidden side read or a second authority.

The port is deliberately not a registry read port. It must not call registry projection reads to hard-fail ownership. It must not infer ownership from actor id format. It should use the single authoritative scope-membership source defined by the registry ownership contract.

## Admission Semantics

Admission is command-path authorization, not a list query.

For target operations such as StreamingProxy `chat`, `join`, `post message`, `message stream`, `delete`, or NyxID `stream/approve/delete`, the HTTP endpoint should delegate to an application command/admission surface that asks the admission port whether the target belongs to the requested scope before dispatching or opening a stream. The endpoint remains an HTTP composition layer; it must not own the business admission workflow itself.

Route-supplied actor ids must not trigger implicit target creation. If the client supplies `{actorId}` or `{roomId}`, the operation must resolve through admission first. Missing targets should return `404`; unauthorized targets should return `403`; indeterminate ownership should return `503`.

Admission must be non-creating. An implementation must not hide a `GetAsync(actorId) ?? CreateAsync(actorId)` path behind `AuthorizeTargetAsync`, and actor activation alone is not ownership evidence. If the underlying runtime only exposes get-or-create addressing, the admission port must use another authoritative non-creating ownership source before target dispatch: the narrow registry actor ownership/admission contract, or an explicitly modeled distributed authoritative ownership index.

The registry ownership model has one authority: the per-scope registry actor, or a future explicitly modeled distributed authoritative ownership index. Target actors may carry capability-local initialization data, but they must not become a second authority for the same `scope_id -> resource` membership. If a target actor stores a scope-shaped value for local validation or diagnostics, that value is a derived mirror and cannot be used to override or contradict the registry ownership contract.

The long-term model is:

1. Creation establishes target resource state through the capability's command path.
2. Registration submits the `scope_id + resource kind + actor_id` membership to the registry ownership authority.
3. Target operations carry the requested scope as typed request context.
4. If the create response or immediate follow-up operation requires the target to be usable right away, the application command surface waits for an explicit committed/admission-visible registry receipt.
5. Application command/admission surfaces call the admission port before target dispatch.
6. Public endpoints map the result to `403`, `404`, or `503` without consulting a lagging projection as a source of truth.

The registry actor admission contract must be narrow to ownership/admission, must return only an admission result, and must not expose registry groups or arbitrary actor state. It must not be implemented by direct state reads, actor state side reads, snapshot reads, event-store reads, generic request/reply, query-time replay, query-time projection refresh, fallback to the registry projection, implicit actor activation, get-or-create runtime lookup, or process-local dictionaries. The admission port is not a generic actor query surface; implementations must be registered per capability/resource kind and may only answer the typed admission verdict.

## Migration Scope

The implementation should remove production use of `IGAgentActorStore`.

Production call sites to migrate:

- `StreamingProxyEndpoints`
  - Create room: application command path plus registry command port.
  - Delete room: admission port first, then capability delete and registry command port rollback/unregister.
  - List rooms: registry query port.
  - Chat, message stream, post message, join, list participants: admission port before target access.
- `NyxIdChatEndpoints`
  - Create conversation: create or ensure the target `NyxIdChatGAgent` through the capability command path, then register authoritative scope membership through the registry command port.
  - Delete/restore conversation registration: registry command port plus the capability's cleanup/rollback contract.
  - List conversations: registry query port.
  - Stream/approve/delete target operations: admission port where the target actor is supplied by the route.
- `GAgentDraftRunActorPreparationService`
  - New actor registration/rollback: registry command port.
  - Existing preferred actor reuse: admission port, not registry list query.
- `ScopeGAgentEndpoints`
  - Actor admin CRUD endpoints map to command/query ports.

After these migrations, remove:

- `IGAgentActorStore`
- `GAgentActorGroup` from the old store contract if no longer used
- `ActorBackedGAgentActorStore`
- DI registration for the old store
- production tests and fakes that only exist to satisfy the old interface

Tests should use fakes for the new ports directly.

## Data Flow

### Create

1. Endpoint validates caller scope with `AevatarScopeAccessGuard`.
2. Endpoint delegates creation to an application command surface.
3. The application command path creates or activates the target resource through the capability command path.
4. Registry command port submits authoritative scope membership to the registry ownership authority.
5. If the response implies the target can be operated on immediately, the application command surface obtains a committed/admission-visible registry receipt. Accepted-for-dispatch alone is insufficient.
6. Response returns an accepted/created result without promising immediate list visibility.

The exact order may vary by capability, but rollback must be explicit when one side succeeds and the other fails.

### List

1. Endpoint validates caller scope.
2. Registry query port reads the registry current-state read model.
3. Endpoint returns the snapshot as eventually consistent list data.

List must not be reused as command admission.

### Operate On Target

1. Endpoint validates caller scope.
2. Endpoint delegates the operation to an application command/admission surface.
3. The application surface builds a typed `ScopeResourceTarget`.
4. Admission port authorizes the target using authoritative registry ownership semantics.
5. On `Allowed`, the operation is dispatched to the target actor or service path.
6. On denial/not-found/unavailable, the endpoint maps to an honest HTTP response.

## Error Mapping

- Scope claim mismatch: `403` with `SCOPE_ACCESS_DENIED`.
- Admission `Denied`: `403`.
- Admission `NotFound`: `404`.
- Admission `ScopeMismatch`: `403`, with a distinct internal result for tests and diagnostics only when the authority can distinguish it without side reads.
- Admission `Unavailable`: `503`, because the system cannot safely decide.
- Registry command dispatch failure during create/delete: `503` or operation-specific failure response.
- Registry query failure during list: preserve current user-facing behavior where appropriate, but log that the list read model is unavailable.

## Testing

Add or update tests around these behaviors:

- Create followed immediately by operate does not depend on registry projection visibility.
- Create followed immediately by operate uses an explicit committed/admission-visible registry receipt, not a bare accepted dispatch result.
- Existing preferred actor reuse in draft-run does not use registry list projection as strong admission.
- Route-supplied actor ids from another scope return `ScopeMismatch` only when the authority can distinguish it without side reads; otherwise they return `NotFound` or an equivalent non-leaking typed admission result, not a generic list-query miss.
- Route-supplied missing actor ids return `NotFound` without creating a target actor.
- Registry list endpoints still read from the query port and tolerate eventual consistency honestly.
- Old `IGAgentActorStore` is not registered in production DI.
- Static or architecture guard coverage prevents new production references to `IGAgentActorStore`.

Because tests around eventual consistency can become flaky, prefer deterministic fake ports over polling. If an integration test truly needs eventually consistent observation, it must follow the repository polling allowlist rule.

## Documentation And Guards

- Update issue 348 or linked docs to state that the registry list read model is not an ownership authority.
- Keep `docs/canon/gagent-registry-ownership.md` updated with the command/query/admission separation for GAgent registry resources.
- Add or extend a guard that fails on production references to `IGAgentActorStore` after removal.
- If any derived target-side mirror is added, define it as typed protobuf fields/events rather than bags or metadata, and document that it is not the ownership authority.

## Risks

The largest design risk is making `IScopeResourceAdmissionPort` too generic and letting it become a hidden RPC/query escape hatch. The port should stay narrow: it only answers command admission for a typed target and operation. It must not return arbitrary actor state, registry groups, readmodel snapshots, or target details.

The largest implementation risk is migration breadth. The old interface appears in endpoints, application services, integration tests, AI tests, and CLI adapter tests. The implementation plan should stage edits by call-site family, while still deleting the old production abstraction before the PR is complete.

## Implementation Decisions

- Use `IGAgentActorRegistryCommandPort`, `IGAgentActorRegistryQueryPort`, and `IScopeResourceAdmissionPort` unless implementation reveals a direct naming conflict.
- Do not rely on bare `Task` registry command methods for immediate usability. If issue 348 keeps a simple accepted-dispatch method for lifecycle writes, it must also add an explicit committed/admission-visible receipt path for create-then-immediately-operate flows.
- StreamingProxy should keep room-specific facts in `StreamingProxyGAgentState`, but scope membership is authorized by the registry ownership authority. A target-side `scope_id` mirror, if added later, is derived and cannot replace the registry admission contract.
- NyxID chat create must create or ensure the `NyxIdChatGAgent` target before registering it, because `stream` and `approve` must not implicitly create route-supplied actors. Admission must use the narrow registry actor ownership contract as the authoritative source for this work, not projection, actor id parsing, target-owned duplicate ownership, or get-or-create target activation.
- Draft-run preferred actor reuse should use the same admission port. It must not list registry projection groups to decide whether an existing actor may be reused.
