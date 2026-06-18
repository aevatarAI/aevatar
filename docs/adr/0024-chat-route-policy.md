---
title: Chat Route Policy — Config Actor + Boundary Resolver
status: Accepted
owner: eanzhao
---

# ADR-0024: Chat Route Policy — Config Actor + Boundary Resolver

> Superseded for GAgent/team routing by ADR-0026. The current wire action set
> no longer includes `ForwardToGAgent` or `ForwardToTeam`; policies express
> those targets as `ForwardToModel.tool_set_ref + tool_choice_hint` with
> `aevatar_invoke_gagent` or `aevatar_invoke_team`. Narrowed by issue #2158:
> Mainnet voice ingress is only `/ws/voice`; explicit voice attach identity is
> typed `ForwardToModel.tool_choice_hint.voice_attach_target`.

## Context

Aevatar today has four ingress paths that each hard-route inbound traffic to a
single destination, with no central place a user / scope owner can change the
policy:

| Source | Endpoint | Current target |
|---|---|---|
| Direct chat | `/api/scopes/{scopeId}/nyxid-chat/...` | hard-codes `NyxIdChatGAgent` |
| NyxID relay (Lark / Telegram) | `/api/webhooks/nyxid-relay` → `ConversationGAgent` | runner picked via DI singleton; one path |
| NyxID Responses | `/v1/responses` + `/v1/messages` | `ILLMProviderFactory.GetDefault()` directly; no GAgent |
| Voice | `/ws/voice` | policy-aware entry resolves target; explicit attach identity is typed `voice_attach_target` |

Issues #672 + #674 introduce one user-configurable layer that decides which
target GAgent or LLM model handles each inbound request. The earlier proposal
("an agent chat router GAgent") was rejected during #672 / #608 reviews: a
new intermediary actor would (a) violate the Harness boundary from #568, (b)
duplicate state ownership versus per-entry actors, and (c) add a serial actor
hop on the hot path without changing the policy decision shape.

This ADR records the **shape** that replaces "router actor": one config-only
aggregate per scope, one stateless library function used by entries, and one
read-side projection. v1 scope deliberately stops short of a configuration UI,
workflow-typed actions, and a voice scratch actor.

## Decision

### D1 — Three-part form, no router actor

Routing is split into three objects with disjoint responsibilities:

| Role | Form | Owner | Lifecycle |
|---|---|---|---|
| **Policy authority** | `ChatRoutePolicyGAgent` (config aggregate) | per-scope, long-lived | event-sourced |
| **Decision engine** | `ChatRouteResolver` (library function) | imported by each entry | stateless |
| **Query view** | `ChatRoutePolicyCurrentStateDocument` | projection | committed → readmodel |

`ChatRoutePolicyGAgent` only handles config commands (`Upsert*`,
`RemoveRule*`) — never turn dispatch, never reply tokens, never
audio frames. `ChatRouteResolver.Resolve(snapshot, input) → ChatRouteDecision`
is a pure function each entry calls before its existing dispatch path. The
readmodel is a coverage replica of the actor's current `ChatRoutePolicyState`.

**Therefore:** zero new actor hops on the hot path; the policy actor only
participates in writes; decisions are observed once at the boundary and
discarded.

### D2 — Decisions are transient

`ChatRouteDecision` MUST NOT be persisted to actor state, event stores,
readmodel documents, or persistent logs. Telemetry MAY record
`matched_rule_id`, `used_fallback`, `resolved_at` for observability, but the
decision structure itself stays at the ingress boundary and is dropped after
the calling entry consumes it. The same constraint that #672 review imposed
on `reply_token` applies here.

### D3 — Strong-typed inputs and outputs throughout

Per CLAUDE.md "字段命名与 Metadata 决策树" step 1: anything that influences
control flow, compatibility, or stable lookup is a typed proto field or typed
sub-message. For routing this includes:

- `ChatSourceKind` (enum), `ToolMode` (enum)
- `ChatRouteInput.model` + `ChatRouteMatch.model` for stable model-based rules
- `VoiceCodec`, `VoiceConversationMode`, `VadMode` (enums)
- `VoiceInput` (sub-message) — only valid when `source_kind = VOICE`
- `ForwardToModel` and `Reject` (current oneof variants). GAgent, team,
  Studio member, and workflow targets are expressed as
  `ForwardToModel.tool_set_ref + tool_choice_hint`.
- `VoiceInput.voice_module_name` (typed string) — chooses among
  `voice_presence`, `voice_presence_openai`, `voice_presence_minicpm`,
  `voice_presence_minicpm_o` registered at bootstrap

`map<string, string> metadata` bag is **not allowed** anywhere in this proto.
Caller credentials, reply tokens, and connection identifiers are explicitly
out of scope and never reach `ChatRouteInput`.

### D4 — Endpoint naming: `/ws/voice`, not `/ws/chat`

The existing repository already mounts `/api/ws/chat` for text JSON chat. The
voice transport is binary PCM16 frames + JSON text control frames
(`WebSocketVoiceTransport`); mixing both protocols on `/ws/chat` makes the
upgrade ambiguous to clients and reviewers. The policy-aware voice endpoint
is therefore `/ws/voice` (no `actorId` in route). The later Mainnet hardening
removed the explicit-actor path; policy authors express explicit voice attach
through typed `voice_attach_target`.

### D5 — v1 scope reduction

This section's original v1 wire-action list is superseded by ADR-0026.
The current `ChatRouteAction` oneof exposes only:

- `ForwardToModel`
- `Reject`

GAgent, team, Studio member, and workflow targets are no longer separate
policy action variants. Policies express them through
`ForwardToModel.tool_set_ref + tool_choice_hint`:

- GAgent routing: `tool_choice_hint.tool_name = aevatar_invoke_gagent`
- Team routing: `tool_choice_hint.tool_name = aevatar_invoke_team`
- Workflow routing: `tool_choice_hint.tool_name = aevatar_start_workflow`

The old `ForwardToGAgent`, `ForwardToTeam`, `ForwardToWorkflow`, and `Bypass`
tags/names remain reserved for protobuf compatibility only. They are not live
policy actions and must not be reintroduced as fields.

Mainnet no longer exposes a dev/admin explicit-actor path outside
`ChatRouteAction`. Ordinary `/ws/voice` may attach only when the resolved
`ForwardToModel.tool_choice_hint.voice_attach_target` carries a typed target.
Tool-first `ForwardToModel` execution through a voice session actor is not
implemented in this milestone and remains the later Stage 5 work described by
ADR-0026.

The write side also drops `ResetChatRoutePolicyRequested`: because
`default_target` is REQUIRED whenever the actor exists (per D6 below), a
"wipe and clear" command would leave an invalid persisted state that
neither matches the cold-start fallback path nor a fully-configured one.
Callers that want to start over should issue `UpsertChatRoutePolicyRequested`
with the desired `default_target` and an empty `rules` list — atomic,
single-event, no temporary invalid window.

### D6 — Default target and fallback

`ChatRoutePolicyState.default_target` is **required** when the actor exists.
When the policy actor itself does not exist yet (cold start) or the readmodel
is unavailable, the resolver falls back to a hardcoded
`ForwardToModel(env AEVATAR_DEFAULT_LLM_MODEL)` decision and sets
`used_fallback = true`. This is the single piece of state that ingress code
holds — and only as configuration, not as event-sourced fact. Existing
"user default agent" concepts are out of scope: the policy actor's
`default_target` covers them.

### D7 — Caller identity uses the foundation contract

// Refactor (iter91/cluster-091-owner-scope-foundation):
//   Old: chat routing carried a `ChatRouteCallerScope` mirror of
//        `Aevatar.GAgents.Scheduled.OwnerScope` to avoid depending on the Scheduled agent package.
//   New: `OwnerScope` lives in `Aevatar.Foundation.Abstractions`; chat routing imports
//        that canonical caller identity directly while preserving its containing field tags.

## Boundaries with adjacent issues

- **#568 (Harness boundary)**: this ADR honors it by *not* introducing a
  router actor. `ChatRoutePolicyGAgent` is a single-purpose config aggregate;
  the resolver is a library function; no Harness / Runtime / ChatRuntime
  surface appears.
- **#608 (ChatRuntime / boundary adapter)**: the entry-side resolver call is
  the boundary adapter #608 frames. We add no new actor on top.
- **#596 (run-actor continuation)**: `ChatRouteResolver` runs *before*
  `AgentRunGAgent` — it decides which target run actor to feed. Run actors
  remain authoritative for execution.
- **#560 (StreamSessionGAgent RFC)**: out of scope. The `/ws/voice` policy
  endpoint owns *which actor to attach to*. Cross-host reconnect, frame
  sequencing, replay are #560's concerns and are not solved here. If #560
  later lands `SessionStreamGAgent`, this ADR's wire shape continues to
  hold: the resolver still produces a target actor id; the attach layer
  changes underneath.

## Out of scope

- Configuration UI (Studio / CLI surfaces for `Upsert*` commands).
- Workflow routing as a wire action. Workflow invocation is exposed through
  the `aevatar_start_workflow` tool.
- `ForwardToModel` over `/ws/voice` (would need a scratch voice-enabled
  actor — explicitly deferred per #674 review).
- Telemetry pipeline for `matched_rule_id` (will arrive via existing run
  trace channels in a later issue).
- Voice session actor execution for pure `ForwardToModel` voice decisions.

## Consequences

- One new proto-only project (`Aevatar.ChatRouting.Abstractions`) joins the
  graph at the bottom; nothing in `agents/` or `src/Aevatar.Foundation.*`
  depends on it yet (subsequent phases add dependencies inward).
- Four existing ingress entries (NyxIdChat, Responses, Messages, Relay) will
  each add one resolver call in Phase 3 — no schema breakage at the
  ingress, no new actor in the dispatch path.
- Phase 4 is the first Mainnet host voice mount; Mainnet now uses the
  policy-aware `/ws/voice` endpoint rather than the Foundation generic mapper.
- Reverting to a router-actor shape later would require unwinding every
  resolver call site. The boundary form is intentionally costly to walk
  back from — this is what protects #568's anti-Harness invariant.

## Verification

- proto compiles in the new project; no agent or other src project
  references it during Phase 0.
- `bash tools/docs/lint.sh` passes; `bash tools/docs/build-index.sh` lists
  this ADR.
- subsequent phases verify their own deltas; this ADR ships separately as
  the foundation commit.
