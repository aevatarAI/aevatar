# GAgent Registry Ownership Ports Design

Date: 2026-04-27

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

`GAgentActorRegistration` should carry `ScopeId`, `GAgentType`, and `ActorId` as typed fields. The port dispatches commands to the per-scope registry actor. Its synchronous return means the command was accepted for dispatch or failed before dispatch. It must not imply that the registry read model has observed the change.

### Registry Query Port

`IGAgentActorRegistryQueryPort` owns registry listing reads.

Expected operations:

```csharp
Task<GAgentActorRegistrySnapshot> ListActorsAsync(
    string scopeId,
    CancellationToken ct = default);
```

The query port reads `GAgentRegistryCurrentStateDocument`. Its result should be named as a snapshot/read model result, not an ownership verdict. If the current read model exposes source version or refresh timestamp, include it in the snapshot; if not, the API documentation must still state that the result is eventually consistent.

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
- `Unavailable`

The port is deliberately not a registry read port. It must not call registry projection reads to hard-fail ownership. It must not infer ownership from actor id format. It should use a stable authoritative contract for the target capability.

## Admission Semantics

Admission is command-path authorization, not a list query.

For target operations such as StreamingProxy `chat`, `join`, `post message`, `message stream`, or NyxID `stream/approve/delete`, the endpoint should ask the admission port whether the target belongs to the requested scope before dispatching or opening a stream.

Route-supplied actor ids must not trigger implicit target creation. If the client supplies `{actorId}` or `{roomId}`, the operation must resolve through admission first. Missing targets should return `404`; unauthorized targets should return `403`; indeterminate ownership should return `503`.

The admission implementation should be capability-aware. For a resource actor that owns its current scope binding, the long-term model is:

1. Creation command initializes the target actor with a typed `scope_id` field in its authoritative state.
2. Target operations carry the requested scope as typed request context.
3. The actor or its application command port rejects operations whose requested scope does not match the actor-owned binding.
4. Public endpoints map the result to `403`, `404`, or `503` without consulting a lagging projection as a source of truth.

If a capability cannot yet move ownership into target actor state, it may use a dedicated actor-owned ownership contract, such as the per-scope registry actor's authoritative state. That contract must be narrow to ownership/admission, must return only an admission result, and must not expose registry groups or arbitrary actor state. It must not be implemented by direct state reads, generic request/reply, query-time replay, query-time projection refresh, or fallback to the registry projection. It must not be a process-local dictionary.

## Migration Scope

The implementation should remove production use of `IGAgentActorStore`.

Production call sites to migrate:

- `StreamingProxyEndpoints`
  - Create/delete room: registry command port.
  - List rooms: registry query port.
  - Chat, message stream, post message, join, list participants: admission port before target access.
- `NyxIdChatEndpoints`
  - Create conversation: create or ensure the target `NyxIdChatGAgent`, bind its scope or otherwise establish authoritative ownership, then register through the registry command port.
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
2. Endpoint or application service creates/activates the target actor through the existing runtime/command path.
3. Target actor records its authoritative scope binding when this capability owns target scope in its actor state, or the capability establishes its documented authoritative ownership contract.
4. Registry command port registers the actor for list/query visibility only after the target ownership source exists.
5. Response returns an accepted/created result without promising immediate list visibility.

The exact order may vary by capability, but rollback must be explicit when one side succeeds and the other fails.

### List

1. Endpoint validates caller scope.
2. Registry query port reads the registry current-state read model.
3. Endpoint returns the snapshot as eventually consistent list data.

List must not be reused as command admission.

### Operate On Target

1. Endpoint validates caller scope.
2. Endpoint builds a `ScopeResourceTarget`.
3. Admission port authorizes the target using authoritative resource ownership semantics.
4. On `Allowed`, the operation is dispatched to the target actor or service path.
5. On denial/not-found/unavailable, the endpoint maps to an honest HTTP response.

## Error Mapping

- Scope claim mismatch: `403` with `SCOPE_ACCESS_DENIED`.
- Admission `Denied`: `403`.
- Admission `NotFound`: `404`.
- Admission `Unavailable`: `503`, because the system cannot safely decide.
- Registry command dispatch failure during create/delete: `503` or operation-specific failure response.
- Registry query failure during list: preserve current user-facing behavior where appropriate, but log that the list read model is unavailable.

## Testing

Add or update tests around these behaviors:

- Create followed immediately by operate does not depend on registry projection visibility.
- Existing preferred actor reuse in draft-run does not use registry list projection as strong admission.
- Route-supplied actor ids from another scope are denied or not found through admission.
- Registry list endpoints still read from the query port and tolerate eventual consistency honestly.
- Old `IGAgentActorStore` is not registered in production DI.
- Static or architecture guard coverage prevents new production references to `IGAgentActorStore`.

Because tests around eventual consistency can become flaky, prefer deterministic fake ports over polling. If an integration test truly needs eventually consistent observation, it must follow the repository polling allowlist rule.

## Documentation And Guards

- Update issue 348 or linked docs to state that the registry list read model is not an ownership authority.
- Add a small architecture note describing command/query/admission separation for GAgent registry resources.
- Add or extend a guard that fails on production references to `IGAgentActorStore` after removal.
- If new target-owned ownership state is added, define it as typed protobuf fields/events rather than bags or metadata.

## Risks

The largest design risk is making `IScopeResourceAdmissionPort` too generic and letting it become a hidden RPC/query escape hatch. The port should stay narrow: it only answers command admission for a typed target and operation. It should not return arbitrary actor state.

The largest implementation risk is migration breadth. The old interface appears in endpoints, application services, integration tests, AI tests, and CLI adapter tests. The implementation plan should stage edits by call-site family, while still deleting the old production abstraction before the PR is complete.

## Implementation Decisions

- Use `IGAgentActorRegistryCommandPort`, `IGAgentActorRegistryQueryPort`, and `IScopeResourceAdmissionPort` unless implementation reveals a direct naming conflict.
- Keep registry command methods returning `Task` in this work. A command receipt object can be introduced later with the broader command receipt model.
- StreamingProxy should use target-owned room scope binding in its typed state/event contract because `StreamingProxyGAgentState` and `GroupChatRoomInitializedEvent` are capability-owned and can carry `scope_id`.
- NyxID chat create must create or ensure the `NyxIdChatGAgent` target before registering it, because `stream` and `approve` must not implicitly create route-supplied actors. If extending `RoleGAgent` state for typed scope ownership is too broad for this work, use the narrow registry actor ownership contract as an interim authoritative admission source, not projection and not actor id parsing.
- Draft-run preferred actor reuse should use the same admission port. It must not list registry projection groups to decide whether an existing actor may be reused.
