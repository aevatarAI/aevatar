---
title: Issue 462 Ownership And Admission Follow-Up Design
status: history
owner: architecture
---

# Issue 462 Ownership And Admission Follow-Up Design

Date: 2026-04-28

> Non-authoritative design snapshot. The durable rule for registry ownership lives in [GAgent Registry Ownership](../../canon/gagent-registry-ownership.md). This document records the proposed direction for issue 462 follow-ups around StudioMember binding and NyxID relay admission.

## Context

Issue 462 tracks three remaining ownership/admission paths adjacent to the registry ownership cleanup from issue 348:

- `StudioMemberService.BindAsync` reads `IStudioMemberQueryPort` before dispatching binding writes.
- `NyxIdRelayScopeResolver` resolves relay scope by `nyx_agent_api_key_id` through projection-backed channel bot registration queries when the relay JWT has no scope claim.
- `NyxIdChatEndpoints.Relay` hashes `scope_id` into the relay `ConversationGAgent` actor id.

These are not ordinary read-path bugs. They are places where command admission, routing, or ownership still depends on eventually consistent read models or actor id shape. That conflicts with `CLAUDE.md`:

- command paths must follow `Command -> Event`; queries read read models.
- synchronous ACKs must only promise the stage actually reached.
- actor ids are opaque addresses, not business ownership carriers.
- cross-actor reads must not become generic query/reply, request/reply RPC, event-store side reads, or projection priming.
- business progress that spans actors must use command/event continuation with explicit correlation and timeout semantics.

The local checkout does not contain `../NyxID`, so this design cannot verify the real NyxID relay token contract. Any implementation that changes relay token requirements must confirm the NyxID repository or coordinate the external contract before coding.

## Goals

- Remove projection-backed scope fallback from security-sensitive relay routing.
- Remove scope ownership from relay conversation actor id shape.
- Move Studio member bind admission away from read-model freshness.
- Keep StudioMember as the actor-owned authority for member existence, implementation kind, published service id, lifecycle, and binding run status.
- Preserve one projection pipeline: committed actor facts flow through the existing Projection Pipeline into read models.
- Make every synchronous response honest: accepted means accepted, completed means completed, observed means observed.

## Non-Goals

- Do not introduce a generic actor query/reply port to fetch StudioMember state.
- Do not use event-store replay, snapshot reads, state mirror side reads, or query-time projection refresh to make bind synchronous.
- Do not derive ownership from actor id prefixes, suffixes, type names, or hashes.
- Do not add process-local dictionaries or service-level caches as admission facts.
- Do not split StudioMember into separate read/write actors.
- Do not make ChannelBotRegistration projection reads the authority for relay scope routing.

## Design Summary

The recommended design is two implementation phases.

Phase 1 tightens NyxID relay admission:

1. Relay authentication must produce a verified canonical `scope_id`.
2. If the relay JWT lacks a verified scope, the webhook returns `401` or a contract-specific authentication failure.
3. `NyxIdRelayScopeResolver` is removed from the security-sensitive webhook path.
4. Relay conversation actor ids become opaque conversation addresses built from relay identity and conversation key, not from `scope_id`.
5. Scope ownership for relay execution comes from the verified token/admission context carried in typed command payloads.

Phase 2 converts Studio member bind into an actor-owned binding run:

1. `BindAsync` becomes a command surface that dispatches `StudioMemberBindingRequestedCommand` to `StudioMemberGAgent`.
2. The synchronous response returns `accepted + binding_id + member_id + scope_id`, not a completed binding result.
3. `StudioMemberGAgent` validates member existence and implementation kind in its own turn, then persists a typed pending binding event.
4. A binding continuation actor/worker consumes the committed pending-binding fact from a durable event-delivery path that is separate from read-model materialization, performs the existing scope binding upsert, then dispatches a typed completion or failure event back to the same StudioMember actor.
5. Read APIs report bind status from the StudioMember read model after projection observes actor events.

This keeps the member actor as the single authority for member facts and lifecycle while keeping external scope binding work out of the request-time read path.

## Relay Admission Design

### Verified Scope Required

Relay webhook handling should treat the verified relay principal as the admission source. `ResolveRelayScopeIdAsync` should no longer fall back to `INyxIdRelayScopeResolver.ResolveScopeIdByApiKeyAsync` for a missing JWT scope. A missing verified scope is an authentication/admission failure.

This avoids two projection-lag failures:

- a newly provisioned bot callback can be rejected because the registration read model has not caught up;
- stale or duplicate registration documents can route a callback to the wrong scope.

If NyxID cannot yet guarantee a scope claim, the implementation should stop at a documented prerequisite rather than preserving projection fallback in production.

### Opaque Conversation Actor Id

`BuildScopedRelayConversationActorId(scopeId, canonicalKey)` should be replaced with an opaque address builder whose inputs are not interpreted as ownership by callers. Candidate inputs:

- `nyx_agent_api_key_id + canonical_conversation_key`
- `nyx_conversation_route_id + canonical_conversation_key`
- `platform + bot identity + canonical_conversation_key`

The selected identity must be stable for a bot conversation and should be hashed as an opaque address component. `scope_id` must not be included for ownership isolation. If it is carried for diagnostics or downstream command context, it must be a typed field in the relay payload or activity context, not part of the actor id.

The actor id change creates a compatibility question for existing scoped relay conversation actors. Since relay conversations are session-like and issue 462 is an architecture cleanup, the default recommendation is forward-only: new relay callbacks use the new actor id. A migration/backfill path should be added only if production needs existing relay conversation continuity.

### Registration Reads Remain Query Reads

Channel bot registration read models may continue to support admin list/search/debug flows and non-security display. They must not decide relay routing or tenant admission.

If LLM reply configuration still needs bot-owner settings by API key, the implementation must classify that separately:

- If the config affects security-sensitive routing or tenant choice, it must use the same verified scope from the relay principal.
- If it is only optional personalization and may be absent without changing admission, it may fail closed to no override, but should not make security decisions from projection.

## Studio Bind Design

### Why A Narrow Synchronous Prepare Helper Is Rejected

A tempting design is to add `PrepareBindingAsync(scopeId, memberId)` that asks `StudioMemberGAgent` for `PublishedServiceId`, `ImplementationKind`, and `DisplayName`, then lets `StudioMemberService.BindAsync` continue synchronously.

That is rejected. Even if the port is narrow and actor-owned, the shape is still synchronous read-before-write on the command path. It risks becoming a generic actor query/reply escape hatch and violates the `CLAUDE.md` guidance that cross-actor progress should use command/event continuation rather than current-turn waiting or pseudo RPC.

### Binding Run As Actor-Owned State

StudioMember should own binding runs because the member actor already owns the stable facts that bind needs:

- member existence;
- scope id;
- member id;
- implementation kind;
- published service id;
- display name;
- current implementation ref;
- last binding.

Add typed protobuf messages to the StudioMember contract:

- `StudioMemberBindingRequestedCommand`
- `StudioMemberBindingRequestedEvent`
- `StudioMemberBindingCompletedEvent`
- `StudioMemberBindingFailedEvent`
- `StudioMemberBindingRun`
- `StudioMemberBindingStatus`

The request command carries the binding intent as strong typed fields, not a metadata bag. For example, workflow/script/gagent binding specs should be typed sub-messages aligned with the existing `UpdateStudioMemberBindingRequest` shape.

`StudioMemberState` gains a repeated or keyed collection of active/recent binding runs. This state belongs inside the actor because it is the member's own lifecycle and command progress. It is not a middle-layer in-memory registry.

### Command Flow

1. HTTP endpoint validates caller scope and delegates to `IStudioMemberService.BindAsync`.
2. `BindAsync` normalizes route/request data and dispatches `StudioMemberBindingRequestedCommand` to the target `StudioMemberGAgent`.
3. The actor rejects if it has not been created, if the implementation kind does not match the request, or if a conflicting active binding for the same member is already in progress.
4. On acceptance, the actor persists `StudioMemberBindingRequestedEvent` with:
   - `binding_id`
   - `scope_id`
   - `member_id`
   - `published_service_id`
   - `implementation_kind`
   - `display_name`
   - typed binding spec
   - `requested_at_utc`
5. The synchronous API response returns an accepted receipt containing `binding_id`, `scope_id`, `member_id`, and a status such as `accepted`.
6. A continuation processor registered as `ICommittedObservationContinuation<StudioMaterializationContext>` observes the committed `StudioMemberBindingRequestedEvent` through the durable projection runtime. It does not register as a materializer, does not materialize read models, and does not run inside a query call stack; it performs the scope binding upsert through explicit command ports.
7. The continuation dispatches `StudioMemberBindingCompletedEvent` or `StudioMemberBindingFailedEvent` back to the same `StudioMemberGAgent`.
8. The actor validates `binding_id` against active state, ignores stale completions, and persists final lifecycle changes.
9. Query endpoints read the projected StudioMember current-state read model to show binding status, last binding, and failure details.

The continuation processor may be implemented as a task-scoped actor or an application-level event handler backed by durable event delivery, but it must not use a process-local dictionary as the source of binding run truth. It must also not be implemented as a projector or `IProjectionMaterializer`; projection materializers remain responsible for read models/artifacts, while committed-observation continuations are responsible for cross-actor business side effects.

### Missing Member Admission

The bind command dispatcher must not use the same unconditional `EnsureAsync<StudioMemberGAgent>` path that is appropriate for `CreateAsync`. A route-supplied `member_id` is not permission to create a new member actor.

The dispatch contract should resolve the target without creation for bind requests. If the runtime cannot distinguish missing from inactive without creating, bind should be modeled as an accepted command only when the target actor already exists through an authoritative create path, or the command surface should return a non-creating not-found/unavailable result. It must not create an empty StudioMember actor and then rely on an in-actor "not yet created" exception as the normal missing-member path.

This distinction is part of the ownership rule: `CreateStudioMember` is the only path that establishes member identity; `BindStudioMember` only operates on an existing member.

### Completion Event Semantics

`StudioMemberBindingCompletedEvent` should carry the resolved binding result needed to update member state:

- `binding_id`
- `service_id`
- `revision_id`
- `implementation_kind`
- typed resolved implementation ref
- `expected_actor_id`
- `completed_at_utc`

`StudioMemberBindingFailedEvent` should carry:

- `binding_id`
- typed failure code;
- failure summary;
- retryability flag if needed;
- `failed_at_utc`.

These fields are stable business/control-flow semantics and must be protobuf fields.

### API Compatibility

The clean architecture changes the meaning of `BindAsync`. The existing synchronous `StudioMemberBindingResponse` implies that scope binding already completed. Under the corrected model, the bind endpoint should return an accepted response such as:

```csharp
public sealed record StudioMemberBindingAcceptedResponse(
    string ScopeId,
    string MemberId,
    string BindingId,
    string Status,
    DateTimeOffset AcceptedAt);
```

If backward compatibility is not required, replace the old response shape. The repository rules favor deletion over compatibility shells. If a temporary compatibility route is unavoidable, it must not pretend the operation is completed before the completion event is committed.

## Read Models

StudioMember current-state projection should materialize binding run status only because there is a stable consumption scenario:

- Studio UI needs to show pending/completed/failed bind state.
- API callers need to observe async completion.
- Existing binding view needs last completed binding.

The read model version must remain aligned with the StudioMember actor committed version. Projection must consume committed StudioMember events or the same durable feed, not inbound commands.

Relay does not need a new read model for admission. Verified token scope and command context are enough for the webhook command path.

## Error And Status Mapping

Relay:

- verified scope missing: `401`;
- verified scope rejected by route/caller guard: `403`;
- payload lacks canonical conversation key: `400`;
- accepted relay inbox dispatch: `202`.

Studio bind:

- command dispatch accepted: `202`;
- member missing maps to a non-creating not-found/unavailable result before dispatch, or to an actor-owned failure only if the target actor already exists and can authoritatively reject the command; the bind path must not create a new empty member actor to discover missing state;
- invalid binding spec rejected before dispatch may return `400`;
- binding continuation unavailable after acceptance must produce a failed or retryable binding run status, not silently hang behind a process-local queue.

## Alternatives Considered

### Minimal Derived Context Patch

Derive `publishedServiceId` from `memberId` and infer implementation kind from request, then call scope binding without reading `IStudioMemberQueryPort`.

Rejected. It avoids projection lag but can create external scope binding side effects for a member that the authoritative actor never created, or with an implementation kind that the actor would reject.

### Narrow Synchronous Actor Context Port

Ask the member actor for the authoritative bind context and continue in the same HTTP request.

Rejected. This is too close to actor query/reply on a command path and would be easy to reuse as a forbidden synchronous state read. The repository rule prefers continuation events for cross-actor progress.

### Keep Relay Projection Fallback With Better Duplicate Checks

Continue using `IChannelBotRegistrationQueryByNyxIdentityPort.ListByNyxAgentApiKeyIdAsync`, but reject ambiguous projection results.

Rejected. Duplicate checks improve stale-document safety but do not solve projection lag or the fact that security-sensitive routing is using a read model as authority.

## Test Plan

Relay tests:

- webhook rejects callbacks whose validated principal has no scope;
- webhook does not resolve missing scope through `INyxIdRelayScopeResolver`;
- relay actor id builder does not include `scope_id` or a scope hash;
- two scopes using the same relay conversation key do not rely on actor id scope suffix for authorization;
- optional LLM configuration uses the verified scope when security-sensitive.

Studio tests:

- bind dispatch does not call `IStudioMemberQueryPort.GetAsync`;
- bind returns accepted receipt instead of completed binding result;
- uncreated member actor rejects `StudioMemberBindingRequestedCommand`;
- created member actor persists `StudioMemberBindingRequestedEvent` with authoritative `published_service_id` from actor state;
- completion with matching `binding_id` updates implementation ref and last binding;
- completion with stale or unknown `binding_id` is ignored or rejected deterministically;
- failure event updates binding run status without changing last successful binding;
- read model materializes pending/completed/failed binding status from committed events.

Guards and commands:

- `bash tools/ci/test_stability_guards.sh` for test changes.
- `bash tools/ci/query_projection_priming_guard.sh` because query/read and projection-adjacent boundaries change.
- `bash tools/ci/architecture_guards.sh` for the final branch.
- targeted `dotnet test` projects for Studio and channel runtime/AI endpoint tests.

## Implementation Staging

Stage 1: Relay admission cleanup.

- Confirm NyxID token scope contract from `../NyxID` or record the missing prerequisite.
- Remove webhook projection fallback.
- Replace scoped actor id builder with opaque relay conversation id builder.
- Update relay endpoint tests.

Stage 2: Studio binding command protocol.

- Extend StudioMember protobuf messages.
- Update actor state transitions and tests.
- Change application bind command surface to return accepted receipt.
- Add `ICommittedObservationContinuation` processing and completion/failure dispatch.
- Update Studio read model and endpoint tests.

Stage 3: Documentation and guard cleanup.

- Update canonical docs only if the durable architecture rule changes. Otherwise keep this file as the historical implementation plan.
- Extend guards if production code can still introduce projection-backed relay scope fallback or synchronous member bind reads.

## Open Questions

- Does NyxID already guarantee a signed relay scope claim for every channel bot callback?
- Which relay identity is the best stable opaque conversation partition: agent API key id, conversation route id, or another NyxID-owned bot identifier?
- Should Studio keep any synchronous bind endpoint, or should the endpoint be explicitly renamed to indicate async binding?
- How long should completed or failed binding runs remain in StudioMember state before compaction?
