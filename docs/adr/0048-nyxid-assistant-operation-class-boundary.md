---
title: "NyxID Assistant Operation-Class Boundary"
status: accepted
owner: eanzhao
---

# ADR-0048: NyxID Assistant Operation-Class Boundary

## Context

Milestone 40 needs one answer for every supported NyxID intent, but one transport cannot safely describe every authority boundary. Management reads, browser journeys, connected-service effects, local commands, and unsupported intents have different descriptor sources, credentials, approval authorities, and failure semantics.

Treating them as one global MCP-versus-REST choice causes two concrete failures:

1. a generic proxy can become a model-visible capability even though it has no admitted exact operation; and
2. generic `tool_approval` can be mistaken for authorization to call an exact connected service.

The decision is based on the evidence pinned in the [Milestone 40 Gate 0 inventory](../audit-scorecard/2026-08-07-milestone-40-gate-0-inventory.md), including NyxID `fa157bc4160c27922f49f8f498ccac755843a15a`, assistant registry `nyxid-assistant-actions.v4`, and support-contract gist `f45febb057a7182dab2495d4c739d2bb8d7026f5`.

[Issue #3317](https://github.com/AevatarAI/aevatar/issues/3317) selects `origin/feature/integrate` as the only Milestone 40 delivery, production, and acceptance baseline. This decision is accepted against `origin/feature/integrate@6cf0da4cc53311e27dcc29887b60b330587bcf3c`; later evidence pins the exact integration SHA containing each implementation.

## Decision

NyxID Assistant uses five operation classes. The class is a semantic and authority boundary, not merely a UI label or transport selection.

| Class | Meaning | Descriptor authority | Credential source | Approval authority | Execution adapter | Failure semantics |
|---|---|---|---|---|---|---|
| R | NyxID management and read control plane | Narrow Aevatar typed contract mapped to NyxID REST | Authenticated principal mapped once to typed `NyxIdAuthority` | None for reads; mutations are not Class-R assistant intents | Narrow typed REST adapter | Missing authority, discovery failure, or absent fact returns typed cannot-check; never guess from scope, route, or display IDs |
| A | Browser-owned NyxID action journey | Pinned assistant action registry descriptor plus its Aevatar producer, wire mapper, and typed postcondition | Authenticated browser/user session at the NyxID boundary | NyxID owns authentication and any journey-local confirmation | Typed browser action/continuation; in M40 only `service.connect` is executable | Missing artifact or unavailable verb becomes honest not-yet-executable; no dark fallback to REST mutation or proxy |
| P | Exact connected-service operation | NyxID normalized MCP catalog | Current authenticated caller/delegation authority bound to the exact service | NyxID exact-service policy; a returned exact tool approval remains separately actor-owned and resolvable only for that invocation | Request-local admitted typed operation selected by `user_service_id + service_slug + endpoint_id + operation_contract_digest`; the root catalog digest remains observation provenance | Missing exact identity, service/slug/endpoint/contract drift, credential failure, or policy denial fails closed before effect; root catalog drift alone is diagnostic; generic proxy is never model-visible |
| L | Exact local command handoff | Repository-owned conformance row containing the exact CLI command | User's local NyxID CLI session, outside Aevatar execution | User explicitly chooses to run the command locally | Copyable command plus a precise reason and prerequisites | Report handoff only; never emit an execution receipt or imply verification |
| X | Honest decline or milestone exclusion | Repository-owned conformance row and availability predicate | None | None | No tool is exposed | State that the intent is unavailable/not yet executable; never fabricate a result or silently choose a broader mechanism |

### R and A collision rule

R is read-only on the NyxID Assistant route. Existing typed connected-service management mutations such as `nyxid_service_update`, `nyxid_service_route`, and `nyxid_service_delete` may remain available to an explicitly authorized administrative surface, but they are not mounted as a second mechanism for Assistant Class-A intents.

Assistant action intents resolve through the pinned Class-A registry. Milestone 40 makes only `service.connect` executable. Other registry verbs, including `service.update`, `service.route`, and `service.delete`, resolve to Class X until their complete descriptor/producer/wire/postcondition artifacts and owning milestone are present. A prompt, display name, REST route, or existing admin tool cannot promote them.

### P exposure rule

The generic `nyxid_proxy` remains excluded from NyxID Assistant. Class-P tools are generated request-locally only from a current, exact MCP catalog observation and admitted as typed operations. Exposure first requires an exact ordinal `/keys` and MCP intersection on both `user_service_id` and route slug; an ID match with a different slug exposes no operation. Their selector is the complete tuple:

`user_service_id + service_slug + endpoint_id + canonical operation-contract digest`

HTTP method/path, display name, tool name, or model-supplied metadata cannot replace any member of the tuple. The request-local authority also retains the root `catalog_digest` as source-observation provenance. A different live root digest records a diagnostic and continues exact service/endpoint revalidation; it does not invalidate an unchanged admitted operation. The final typed schema shown to the model and the terminal dispatch must refer to the same frozen admitted operation.

R/A route tools and P admitted operation tools are distinct sets. A host or profile may compose both for one turn, but registration of one set never implies exposure of the other.

Milestone 40 uses **request-local dynamic operation tools** as the only canonical model-visible Class-P selection contract. For every operation admitted by the server-owned exposure policy, Aevatar generates one bounded tool schema and one opaque request-local tool name. The server resolves that name to the frozen exact selector, operation contract, root catalog observation digest, schema, risk, and argument contract before dispatch; the model cannot provide or rewrite any selector member. A fixed `search_connected_service_operations` plus `invoke_connected_service_operation(candidate_ref)` pair is non-canonical for this milestone and must not be added as a parallel selection path.

Milestone 40 owns a closed exposure policy rather than treating every MCP entry as model authority. A current exact service must also be active and credential-allowed in the caller's inventory. `GET/HEAD/OPTIONS` operations are exposed as safe reads without local approval; `POST/PUT/PATCH` are exposed only as non-destructive effects that enter the canonical approval port; `DELETE`, generic proxy entries, unknown risks, invalid schemas, and policy contradictions are not exposed. The policy is server-sealed and cannot be widened by prompt, profile text, tool arguments, or catalog labels.

Read and effect outcomes remain different contracts. A read response uses a headers-first bounded network read and then becomes a maximum-16-KiB complete typed projection, measured as UTF-8 bytes, with bounded service/operation provenance labels and an explicit `untrusted_external_data_only` instruction boundary. A network or final-projection overflow returns only a deterministic typed rejection, never partial/raw provider content. An effect returns a typed receipt projection and the provider-owned `AgentToolReceipt`; provider response bodies are not evidence and are not forwarded as the effect result.

### Special controls that are not normal Class-A actions

`mfa verify` is not a separate action-registry row. Verification is absorbed into the NyxID-owned MFA setup/browser journey so the user does not receive two competing continuations for one authentication state machine.

Approval `approve` and `deny` are controls over one exact pending approval fact, not ordinary Class-A registry actions. A decision must bind the pending request identity, owner, expected version/state, and original operation correlation. It cannot authorize another request or service.

## Milestone 40 approval tier

Milestone 40 selects [#3311](https://github.com/AevatarAI/aevatar/issues/3311) Option 2, the no-NyxID-change fallback, as **Tier B**. This is an Aevatar scope decision; it does not claim that the support-contract owner accepted the corresponding gist correction.

At the pinned NyxID revision, two approval mechanisms must remain separate:

1. `POST /api/v1/approvals/requests` creates generic `tool_approval`, hardcoded `PerRequest` with no service grant. Approving it cannot authorize `svc-lark`, `svc-github`, or any other exact service.
2. Connected-service approvals are created inside effect handlers with the real service and operation, then waited on synchronously. Aevatar does not receive the request identity before the NyxID operation returns.

Tier B therefore has the following binding behavior:

- Aevatar starts the admitted effect as a long-running actor-owned tool operation.
- Before NyxID returns, Aevatar may show only running/waiting and then threshold-derived stalled. It cannot claim that approval is pending and cannot render a reconstructible pre-effect approval card.
- A typed approval fact may be committed only after NyxID returns error 7000/7001 with a non-empty `approval_request_id`.
- NyxID approval mode is a separate typed value: `per_request`, `grant`, or `unknown`. Missing or invalid boundary data remains `unknown`; Aevatar local approval mode never fills that gap.
- A later approve/deny decision and retry are best effort. A grant-mode decision may allow a new retry; a per-request retry may create a new request. The UI and actor protocol promise neither reuse nor successful resumption of the returned request.
- No synthetic Aevatar approval, advisory card, or generic `tool_approval` decision is represented as NyxID exact-service authorization.

Tier A remains a future cross-repository option. It requires NyxID to expose a non-blocking exact-service approval contract that creates and observes a request bound to the service, endpoint, operation digest, and authorization mode, then honors that decision on the later effect. Tier A acceptance is explicitly not claimed by M40 under this ADR.

## Actor and observation consequences

- The command path remains `Normalize -> Resolve Target -> Build Context -> Build Envelope -> Dispatch -> Receipt -> Observe`.
- Accepted dispatch is not committed effect and not read-model visibility.
- Pending action, running/waiting, stalled, post-return approval, and terminal outcome are actor-owned facts published through the existing committed-state projection pipeline.
- The complete exact `AgentToolOperationAdmission` is persisted as a typed Protobuf actor checkpoint fact and restored before resumed execution; credential material is excluded and resolved again for the recovery request.
- A plan is read-only progress, not executable authority. The conversation actor dispatches a server-sealed exact operation directly; the turn actor persists the exact admission, idempotency key, and effect-dispatch waterline before execution. Ambiguous delivery uses the ordinary operation probe/tombstone protocol, and delayed delivery cannot resurrect a fenced operation.
- Effect retry authority is derived only from the exact delivered operation whose committed evidence is `not_applied`. The checkpoint stores a credential-free authorization snapshot and exact tool-definition fingerprint, then rematerialization re-matches the current profile tool and complete admission contract. Operation generation, a stale plan receipt, or generic approval is never sufficient authority.
- If operation dispatch throws after delivery may have begun, the conversation actor commits a secret-free pending delivery probe and cannot start recovery, read-back, or generation N+1. The turn actor either reports the exact committed operation and its effect-dispatch waterline, or first commits an exact delivery tombstone and then reports `not admitted`; a delayed command matching that tombstone cannot execute.
- A successful effect receipt persists only its provider resource identity and safe typed receipt fields. Frozen verification uses that identity plus the admitted typed read-back contract; it never compares mutable content or reuses a raw provider response.
- An Aevatar-internal mutation may set the typed receipt mutation stage to `read_model_observed` only after the tool itself has queried the canonical read model and matched the exact accepted resource revision. For workflow start, that means the caller scope, accepted actor ID, accepted command ID, and a positive authoritative state version must all match one workflow current-state read model. The Assistant may continue from that evidence without scheduling a duplicate connected-service read-back; an accepted-only receipt, an unspecified stage, or any ordinary mutation without admitted read-back remains unresolved and fails closed.
- The recovery credential is a vault reference owned by the turn actor. It is retained and renewed while effect truth is unresolved, hydrated only for the frozen verification dispatch, and revoked only after a terminal `applied` or `not_applied` result. A bounded-list miss remains `unavailable`, because absence from one page is not proof of non-application.
- Stop and steering physically cancel the exact in-flight execution session and commit a fence. A late effect result can refine truth only through the same frozen verification; it cannot resume or advance the superseded operation. A parked steering continuation is dispatched only after that committed verification removes the uncertainty.
- LLM text and reasoning progress preserve source order but use bounded, timer-driven batches outside the conversation actor turn. The first delta is immediate; later deltas flush at one second or 64 KiB UTF-8, and terminal or cancellation forces the accepted tail to flush, so progress cannot saturate the actor inbox and starve controls.
- Timers and remote callbacks publish typed internal events carrying the minimum correlation keys; they do not mutate task state directly.
- Query paths read actor-scoped current-state read models and never prime, replay, or reconstruct an approval from transport text.
- Tool result text, assistant prose, and Studio card presence are not completion evidence. Typed committed state and its authoritative version are.

## Conformance consequences

The machine-readable matrix owned by [#3313](https://github.com/AevatarAI/aevatar/issues/3313) must give every intent:

- one outcome class;
- its descriptor and execution mechanism;
- an availability predicate;
- its approval authority; and
- the evidence type needed to claim completion.

At minimum, fixtures must prove:

- generic `tool_approval` approval never authorizes an exact connected service;
- lost plan-admission delivery/ACK and revoke delivery/ACK recover from actor-owned outboxes without executing a rejected plan;
- ambiguous operation delivery cannot begin conversation-owned reconciliation or admit generation N+1 before the turn actor has committed either exact admission or an exact delivery tombstone;
- late effect success, cancellation, restart, and result-delivery loss all preserve the frozen verification payload and require committed read-back evidence before retry or steering can proceed;
- an admitted Class-P operation cannot be dispatched with a changed selector, digest, or argument set;
- R cannot-check, L handoff, and X decline never produce an effect receipt;
- unknown registry verbs and incomplete Class-A artifact sets fail closed; and
- Tier B never emits a pre-effect exact-approval fact before NyxID returns its request ID.

## Consequences

The hybrid boundary keeps stable control-plane reads on typed REST while using NyxID's authoritative MCP catalog for exact connected-service execution. It prevents a generic proxy, prompt, or management mutation from becoming accidental chat authority.

The cost is explicit composition and tiered product behavior. The profile must mount R/A routes separately from request-local P operations, and Studio must present Tier B as running/waiting/stalled rather than the full prototype approval card. Wave 1 action verbs remain honest exclusions until their external dependencies and artifacts exist.

## Status and rollout

This ADR is `accepted` for Milestone 40. Implementation lands only on `origin/feature/integrate`; issue closure still requires the focused tests and, where the behavior is online-verifiable, evidence from the exact deployed integration image.
