---
title: "Agent Profiles"
status: active
owner: architecture
---

# Agent Profiles

## 1. Purpose And Scope

An Agent Profile is an owner-scoped, versioned resource for an agent's purpose,
instructions, tool-policy restriction, and exact Ornn skill bindings. It is not
a workflow identity, a service identity, a channel registration, or a credential
container.

This canon describes the Phase 1 authority and management delivery. Phase 1
does not make Chat, WebSocket, NyxID conversation, Studio, member, or channel
runtime paths select a Profile. Those integrations remain later rollout phases
and must reuse the authority and read models defined here.

## 2. Identity And Authority

The identity model keeps human lookup, authorization, and actor addressing
separate:

| Identity | Meaning | Authority use |
|---|---|---|
| `ownerHandle/profileSlug` | Human-readable reference carried as two typed fields. | Lookup and display only. It never grants authority. |
| `profileId` | Opaque immutable Profile identity. | Stable resource and Profile Actor identity input. Callers do not derive it from a route or prefix. |
| `AgentProfileOwnerIdentity.user` | Stable authenticated user identity. | Authorizes an ordinary owner Profile together with its separate `owningScopeId`. |
| `AgentProfileOwnerIdentity.system` | Platform-owned system identity. | Reserved for built-in Profiles. Ordinary principals cannot claim `system/*`. |
| `owningScopeId` | Scope boundary for an ordinary Profile. | Compared independently from owner identity. It is empty for system Profiles and cannot equal the shared reserved platform scope. |

`AgentProfileNamespaceGAgent` is the single authority for owner-handle and
reference uniqueness, provisioning status, and published discovery summaries.
`AgentProfileGAgent` is the authority for one Profile's immutable identity,
draft, exact bindings, mutation outcomes, and published snapshot. Committed
event store plus actor state is the truth; every read model is a query replica.

The management route derives owner and scope from the authenticated caller.
Bodies and tool schemas cannot supply an owner subject, scope id, Profile id,
system authority, sealed content, or credentials. Discovery by human reference
is authenticated and returns not found when the caller cannot see the resource.
After caller and entry normalization, an ordinary user Profile is visible only
when the caller scope equals the entry's `owningScopeId` using ordinal equality.
The query path does not read the protected execution model for a hidden entry.
A fully valid `system/*` Profile remains globally discoverable.

## 3. Architecture

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    H["HTTP and agent_profiles"] --> A["Profile Application"]
    A --> N["Namespace GAgent"]
    A --> P["Profile GAgent"]
    A --> O["Exact Ornn publish adapter"]
    N --> C["Committed EventEnvelope"]
    P --> C
    C --> X["Projection Pipeline"]
    X --> R1["Namespace catalog"]
    X --> R2["Owner management"]
    X --> R3["Protected execution"]
```

Host endpoints parse HTTP, authenticate, and map boundary contracts. Application
normalizes input, resolves read-side identity, validates or seals publish input,
builds typed commands, and returns an honest dispatch receipt. Actors alone
commit Profile facts. The existing Projection Pipeline fans those facts out to
the declared read models and audit artifacts.

The five Application-originated mutation commands carry a typed signed ingress
proof. Infrastructure signs after the final target Actor id is known; Core
verifies before operation parsing, deduplication, or persistence. The proof
binds the key id, target id, exact Protobuf command TypeUrl, and SHA-256 digest of
deterministic command bytes with the proof cleared. Signatures use RSA-PSS with
SHA-256 and keys of at least 2,048 bits. Hosts configure one current PKCS#8
private key and a key-id-indexed SubjectPublicKeyInfo public-key ring so previous
public keys may remain during rotation. Missing, malformed, unknown, revoked, or
mismatched proof material fails closed. Create target resolution is pure; signing
must succeed before runtime lookup, creation, materialization, or dispatch.
Proofs, signatures, and private keys do not enter events, Actor state, read
models, audit, responses, labels, or logs.

The four internal provisioning and published-summary messages do not carry an
Application proof. Their authority comes from the runtime-owned typed
`EventEnvelope.runtime.delivery_provenance.authenticated_actor_id`. Raw
`IActorDispatchPort` admission clones the envelope and clears any caller-authored
authenticated Actor origin; Actor-bound `SendToAsync` overwrites that origin
from its bound Actor id after propagation. `Route.PublisherActorId` and
`Runtime.SourceActorId` remain routing/propagation claims and cannot authenticate
an Actor by themselves. The Profile and Namespace handlers require both the
expected route publisher and the matching runtime-authenticated Actor origin
before operation parsing, replay lookup, state mutation, or continuation
effects. Local and Orleans implement the same admission and publishing
semantics, and dispatch ACKs remain accepted-only.

The authority-order governance fact is derived from Roslyn syntax rather than
shell text matching. For each governed message, the guard requires one top-level
target Actor class and exactly one actual `[EventHandler]` direct member. That
registered member must be the expected named method with the fixed signature and
block body, the canonical pre-authority statements, the exact authority call,
and the immediate operation parse. Source-like text in strings, inactive code,
nested types, local functions, or other classes is not valid evidence.

## 4. Phase 1 Management Contract

### HTTP API

| Method and route | Contract |
|---|---|
| `POST /api/scopes/{scopeId}/agent-profiles` | Create an ordinary owner Profile. Requires an idempotency key. |
| `GET /api/scopes/{scopeId}/agent-profiles/{profileSlug}` | Read the authenticated owner's management view and strong ETag. |
| `PUT /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft` | Replace the mutable draft surface under `If-Match`. |
| `PUT /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}` | Upsert one exact skill binding under `If-Match`. |
| `DELETE /api/scopes/{scopeId}/agent-profiles/{profileSlug}/draft/skills/{bindingId}` | Remove one binding under `If-Match`. |
| `POST /api/scopes/{scopeId}/agent-profiles/{profileSlug}:validate` | Validate the complete canonical draft without committing a mutation. |
| `POST /api/scopes/{scopeId}/agent-profiles/{profileSlug}:publish` | Resolve, seal, and dispatch a publish mutation under `If-Match`. |
| `GET /api/agent-profiles/{ownerHandle}/{profileSlug}` | Resolve the authenticated caller's visible discovery summary. |

The strong ETag is the Profile Actor's committed authority state version. It is
not a local projection counter. A known-stale management mutation is rejected;
the actor remains the final authority for version and idempotency decisions.

Draft structure and publish validity are distinct. Create, full draft update,
and skill upsert may commit more than one
`DEFAULT_FOR_UNMATCHED_TURN` binding so an incomplete draft can be represented
and repaired. `:validate` and `:publish` reject that shape with
`MULTIPLE_DEFAULT_SKILLS`; publish performs no Ornn resolution for it, and the
Profile Actor repeats the same defense before accepting sealed content.

### `agent_profiles` tool

The tool actions are exactly `create`, `get`, `update_draft`, `upsert_skill`,
`remove_skill`, `validate`, and `publish`. Every action takes `profile_slug`.
The caller owner and scope are implicit. Mutations use the quoted strong `etag`
returned by `get`; create uses an idempotency key.

An upsert carries a `skill` object with exactly these stable facts:

- `skill_guid`
- `literal_version`
- `expected_name`
- `expected_publisher_id`

The tool never accepts a name-only or `latest` reference, inline skill content,
sealed package content, credentials, another owner, or another scope. Caller
credentials needed at publish time stay in transient caller context rather than
model-authored arguments.

## 5. Exact Ornn Publish Path

Ornn is a publish-side provider boundary, not a Profile Actor dependency. The
Application sealing service accepts only `ExactOrnnSkillReference` and invokes
`IExactOrnnSkillResolver`. The concrete provider reads both exact upstream forms
through the NyxID proxy:

```text
/api/v1/skills/{guid}?version={literalVersion}
/api/v1/skills/{guid}/json?version={literalVersion}
```

The adapter verifies the returned GUID, literal version, canonical name, and
publisher id against the requested reference. It validates and maps the package,
computes deterministic content and snapshot digests, and returns typed sealed
content to Application. Only then may the Profile Actor commit a published
snapshot and authoritative published revision.

The path never calls skill search, `IRemoteSkillFetcher`, or the name-capable
`GetSkillJsonAsync`. It does not fall back to another version. A missing token,
inaccessible exact version, identity mismatch, publisher mismatch, or invalid
package produces a bounded safe diagnostic and no publish commit.

## 6. Accepted And Observed Semantics

A successful command response means `accepted for dispatch` and carries stable
operation, command, correlation, actor, and resource identifiers. It does not
claim that an event committed or that a read model is current.

The completion sequence is:

1. Application validates the request and dispatches a typed command.
2. The target actor serially decides and commits a typed domain event.
3. The committed `EventEnvelope<CommittedStateEventPublished>` enters the
   shared Projection Pipeline.
4. Projectors monotonically overwrite read models using the actor's committed
   state version.
5. Clients reread the canonical management or discovery surface until the
   expected operation, version, revision, or digest is visible.

Every committed Profile mutation outcome carries one typed
`AgentProfileCommittedStateTransition`. Its `before` and `after` messages contain
the authoritative draft revision/digest and published revision/snapshot digest
captured by the Profile Actor before commit. Applied outcomes change the relevant
facts; no-change and rejected outcomes carry equal `before` and `after` facts.
The legacy current fields on `AgentProfileMutationOutcome` are aliases populated
from `transition.after`, never a second fact source. Initialization uses the same
transition contract with no `before` message and an `after` message containing
draft revision one plus the explicit unpublished state.

Queries never create actors, activate or prime projections, replay the event
store, or synchronously wait for a command reply. Eventual visibility is stated
honestly through authority state versions and committed digests.

Actor idempotency is a documented count-bounded window. A Profile retains its
single initialization recovery record plus the 256 newest mutation and publish
operation records. After successful initialization, later initialization
rejections still send their continuation from the committed typed event but are
not retained and do not consume that rolling window. The Namespace retains the
1,024 newest terminal create and published-summary records, and additionally
pins a provisioning-start record while its matching entry is `PROVISIONING` or
`FAILED`; activation releases the pin. Compaction runs only in state-event
appliers and preserves insertion order. Inside the retained window, exact replay
and payload-drift conflict behavior are unchanged. After eviction, the operation
id is outside the idempotency guarantee and is evaluated as a new command against
current identity, uniqueness, and expected-version invariants.

## 7. Read Models And Audit

| Read model | Stable consumer | Exposed content |
|---|---|---|
| Namespace catalog | Owner lookup, discovery resolution, provisioning, and system readiness. | Reference, typed owner, owning scope, provisioning status, Profile id, and safe published summary. |
| Owner management | Authenticated management API/tool and system reconciliation. | Canonical draft, exact references, revisions, digests, safe diagnostics, and authority state version. |
| Protected execution | Future shared runtime resolver and current system readiness. | Server-sealed published snapshot, exact packages, published revision, digest, and authority state version. It is not returned by public management or discovery APIs. |

All three models are actor-scoped current-state replicas. Namespace events feed
the namespace catalog. Profile events feed owner management and protected
execution. Projectors do not recompute a second Profile state machine and do not
read an older same-kind model before writing.

Committed-fact audit translators use exact event TypeUrls and record only safe
identity, operation, reference, exact-reference, revision, digest, outcome, and
stable failure-code facts. Mutation audit annotations expose explicit
`old_draft_*`, `new_draft_*`, `old_published_*`, and `new_published_*` facts from
the committed transition. They do not replay events, query another model, or
infer a previous value. Audit materialization is attached to namespace and owner
contexts only. The execution fan-out does not create duplicate audit records.
Draft instructions, sealed instructions/assets, owner subject ids, credentials,
and raw dependency errors are structurally omitted.

Audit partitioning does not rewrite domain identity. A valid user Profile keeps
its `owningScopeId` as the audit scope. The fully valid canonical
`system/aevatar` identity uses the reserved `platform:aevatar` audit scope.
Committed Profile failures whose identity is missing or invalid are quarantined
in that same reserved audit scope so the governance fact is retained; quarantine
does not grant system authority or repair the invalid domain identity. A
quarantine record uses a stable non-input-derived target and omits every
unvalidated identity/reference field while retaining the stable failure code.
`PlatformScopeSemantics.ReservedPlatformScopeId` in Foundation Abstractions is
the single owner of the `platform:aevatar` literal;
`AuditContractSemantics.PlatformAuditScopeId` is the audit-facing alias. The
reserved value is never an ordinary Profile `owningScopeId`: contract validation,
Application admission, and the Namespace Actor reject it with
`RESERVED_OWNING_SCOPE_ID`. Rejected committed facts are quarantined without
retaining an unvalidated user identity. Query and export access to the partition
always requires platform-admin authorization, even when a caller's `scope_id`
claim has the same literal value.

## 8. System Profile Bootstrap And Health

Hosts may contribute `ISystemAgentProfileDefinitionSource` implementations.
The bootstrap hosted service performs one reconciliation pass at startup and
then wakes on a bounded signal. Each pass rereads definitions and all three read
models and dispatches at most the next required command. The signal is only a
wake mechanism; no service-level id-to-state registry becomes authoritative.

Reconciliation mutation and publish operation ids include
`authority-version:<observedVersion>`. Repeating a pass against the same
management snapshot replays the same operation, while a newly projected
authority version derives a new operation id and can converge after a committed
version conflict. Create identity remains stable because no Profile authority
version exists before creation.

`ISystemAgentProfileOrnnAccessTokenProvider` is replaceable by the host. The
default provider returns unavailable. Exact skills are published only when the
host supplies a token through that boundary.

The readiness service independently compares each required definition with its
namespace, owner, and protected execution replicas. A required Profile is ready
only when the desired draft and published digests match and the protected
execution snapshot has the same published revision and digest. No registered
definitions means ready, so Phase 1 does not change startup behavior by itself.
Capability health reports bounded reference/status/reason details and never
reports credentials, sealed content, or raw remote errors.

## 9. Security And Observability Boundary

- Internal state, commands, events, snapshots, and read models use Protobuf.
- Profile and skill content cannot grant tools, OAuth scopes, permissions, or
  credentials. Tool policy only intersects the caller and route maximum.
- Owner-authored draft text is returned only on the authorized management
  surface. Protected sealed content remains server-side.
- Audit and health use allowlisted safe fields; raw diagnostics are excluded.
- Ingress proof material and signing keys are transport-boundary secrets and are
  never committed, projected, audited, logged, or used as metric labels.
- Runtime-authenticated Actor delivery provenance is transient envelope context.
  It is not copied into Profile state, events, projections, audit, logs, metrics,
  or responses.
- Activity spans may carry resource correlation facts. Metric labels are
  limited to ingress, operation, outcome/failure class, activation mode, and
  required-system readiness; Profile/resource ids are not metric labels.
- Core and Projection do not reference Ornn/HTTP implementations. Application
  depends on exact typed ports. Concrete provider selection belongs to Host.

## 10. Rollout Boundary

| Phase | Status in this canon | Included | Explicitly not active |
|---|---|---|---|
| Phase 1: authority and management | Current | Typed contracts, two authorities, three read models, HTTP/tool management, exact Ornn publish adapter, committed audit, telemetry surface, system bootstrap/readiness, and boundary guards. | No existing runtime ingress consumes a Profile. |
| Phase 2: unified runtime and Studio | Later | Shared resolver, immutable turn stamp, prompt/tool composition, typed Chat/NyxID inputs, and `system/studio`. | Not implemented or implied by Phase 1 routes, DI, or health. |
| Phase 3: channel binding and migration | Later | Typed channel binding, stop-new-write policy, and durable migration from legacy default-skill data. | No channel/member binding or legacy inference exists in Phase 1. |
| Phase 4: removal and enforcement | Later | Resolve migration blocks, remove and reserve legacy fields, enable later-phase forbidden-term checks, and update runtime canon. | Phase 1 guards do not prematurely forbid legacy paths still owned by later migrations. |

Every later phase must consume the Phase 1 identity, authority, exact publish,
and read-model contracts. It must not introduce a second Profile model, infer
identity from strings, or restore runtime Ornn lookup.
