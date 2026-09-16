---
title: Tool-First Chat Ingress — Collapse Forward Actions to Model + Tools
status: Accepted
owner: eanzhao
---

# ADR-0026: Tool-First Chat Ingress — Collapse Forward Actions to Model + Tools

## Implementation status

Accepted for the current `ChatRouteAction` contract: the active oneof variants
are `ForwardToModel` and `Reject`; legacy
`ForwardToGAgent`/`ForwardToTeam`/`ForwardToWorkflow`/`Bypass` names are
reserved only. The `ForwardToModel.tool_set_ref + tool_choice_hint` fields
express tool availability, tool prefill, and the typed voice attach target.
Tool prefilled arguments must not be interpreted as actor addressing.

D5/D6 describe the later session-owned execution topology. `ChatRunActor` is
not implemented in the current v1 slice; `/v1/responses` `wait=complete`
therefore returns the accepted/streaming invocation receipt and clients observe
terminal completion through typed `aevatar_observe_run` targets.
`aevatar_invoke_member` does not advertise or accept `wait=complete`: one
successful dispatch retires that tool for the rest of the current chat turn,
while `aevatar_observe_run` remains available for the returned
`service_id + run_id`. This turn-local execution policy prevents a new model
call ID from creating a duplicate member run while preserving readmodel-based
completion observation.
`VoiceSessionActor` is also not implemented by this ADR slice. Until that topology
exists, ordinary `/ws/voice` supports only typed
`tool_choice_hint.voice_attach_target` attachment; pure model forwarding
remains fail-closed.

## Context

ADR-0024 introduced `ChatRoutePolicy` with several `ChatRouteAction` oneof
variants — including `ForwardToModel`, `ForwardToGAgent`, `ForwardToTeam`,
`ForwardToWorkflow`, and `Reject` — and shipped `ForwardToModel`,
`ForwardToGAgent`, and `ForwardToTeam` in v1. ADR-0025 extended the policy
to voice using `ForwardToGAgent(actor_id, voice_module_name)` because voice
v1 was framed as "pick a voice-enabled actor and attach a module".

Two facts make the v1 shape no longer the right shape:

1. **The tool-calling backbone is already present and load-bearing.**
   `ToolCallLoop` in `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs` plus
   `IAgentToolSource` (30+ live implementations covering Scripting, Skills,
   NyxID, Lark, ChronoStorage, …) already drives "LLM decides which tool to
   call, dispatcher executes, result flows back". `/v1/responses` already
   composes this on top of caller scope: substitute tools (`TodoWrite`,
   `WebFetch`, `WebSearch`, `Task`) and additive tools (`use_skill`,
   `ornn_search_skills`) are routed through the same caller's NyxID bearer
   via NyxID proxy (see `nyxid-responses-direct.md` §5 and
   `ResponsesAevatarToolProvider`). This is the architecture the policy was
   incidentally working around, not a future state.

2. **The remaining forward variants are a parallel routing dialect, not a
   distinct capability.** `ForwardToGAgent` and `ForwardToTeam` on
   `/v1/responses` (`ResponsesEndpoints.cs:779-927`) ultimately fan
   `IStaticGAgentStreamInvocationPort<AGUIEvent>` and adapt back to SSE.
   `ForwardToGAgent` on NyxIdChat (`AgentRunGAgent.cs:1108-1141`) is
   implemented by mutating `NeedsLlmReplyEvent.TargetActorId`. Both shapes
   could equally well be expressed as the LLM choosing to call a tool
   `aevatar_invoke_gagent(actor_id, payload)` or
   `aevatar_invoke_team(team_id, endpoint_id, payload)` that performs the
   same dispatch.

The status quo violates CLAUDE.md §"顶级架构约束 — 统一投影链路，禁止双轨实现"
and §"单一主干，插件扩展". The cost is paid in three places: a parallel
adapter chain on `/v1/responses` and `/ws/voice`, `TargetActorId` override
plumbing in `NyxidChat`, and `/v1/messages` returning HTTP 501 because it
cannot host the parallel dialect.

## Decision

### D1 — Collapse `ChatRouteAction` to two variants

`ChatRoutePolicy` actions reduce to:

- `Reject` (governance boundary, unchanged from ADR-0024)
- `ForwardToModel` (extended; see D2)

Removed actions:

- `ForwardToGAgent` — replaced by tool `aevatar_invoke_gagent`
- `ForwardToTeam` — replaced by tool `aevatar_invoke_team`
- `ForwardToWorkflow` — replaced by tool `aevatar_start_workflow`
  (the old wire slot is reserved and must not be reused)

The "config actor + boundary resolver + readmodel" three-part form from
ADR-0024 D1 is preserved. Only the action set narrows.

### D2 — `ForwardToModel` carries tool injection

`ForwardToModel` gains two typed fields:

- `tool_set_ref` — a typed reference to a tool set the resolver should
  inject for this ingress turn. Tool sets are named compositions (e.g.
  `"workspace.default"`, `"lark.self_notify"`, `"voice.realtime"`) maintained
  outside the policy proto so the action stays small.
- `tool_choice_hint` — optional pinning of a particular tool plus
  pre-filled named arguments. It configures the tool call input for ingress
  paths that actually execute that tool loop. Its
  `voice_attach_target { actor_id, voice_module_name }` sub-message is the
  only `/ws/voice` attach target. `prefilled_arguments` is not actor addressing
  and must not be used by `/ws/voice` to choose a WebSocket attach target.

Per CLAUDE.md §"字段命名与 Metadata 决策树" step 1, both fields are typed
sub-messages, not `map<string, string>` bags.

### D3 — New tool sources expose orchestration

The following `IAgentToolSource` implementations are introduced in this
repository (no external repo changes — CLAUDE.md §"外部仓库无改动权"):

| Tool | Input | Output | Default `wait` |
|---|---|---|---|
| `aevatar_invoke_gagent` | `actor_id`/`actor_name`, typed `payload` | `{run_id, status, stream_topic?, result?}` | `stream` |
| `aevatar_invoke_team` | `team_id`, `endpoint_id`, typed `payload` | same | `stream` |
| `aevatar_start_workflow` | `workflow_id`, typed `inputs` | same | `stream` |
| `aevatar_observe_run` | typed oneof target: `service_run`, `gagent_terminal_correlation`, `gagent_terminal_session`, or `workflow_current_state` | `{status, recent_events, partial_output}` | — |

`aevatar_start_workflow` is still a top-level accepted-only ingress when
called without workflow runtime context. When a workflow run calls it through
`llm_call` or `tool_call`, the host stamps typed workflow runtime context on
`AgentToolExecutionContext`; the dispatcher converts the call to a
`SubWorkflowInvokeRequestedEvent` addressed to the parent run actor. Parent,
root, depth, and fanout are not tool arguments and are rejected if supplied by
the user or LLM.

Payloads are typed proto, not free-form JSON. The dispatcher validates the
proto schema before executing; a malformed call returns a structured error
back to the LLM so it can self-correct rather than fail the turn.

The existing additive-tool pattern from `/v1/responses` is the model: tools
execute under the caller's scope through `AgentToolRequestContext`, never
accepting credentials as arguments (CLAUDE.md §"NyxID credential isolation"
memory).

### D4 — Long-running work uses continuation events, not synchronous waits

LLM tool-call protocol is request/response, but a GAgent run can take
seconds to minutes. The default `wait=stream` returns `{run_id,
stream_topic}` immediately; the session actor (D5) subscribes to the
stream and folds events back into the LLM context for the next turn. The
LLM can then continue with `aevatar_observe_run`, cancel, or proceed on
the partial.

Until D5 exists, `aevatar_invoke_member` treats its accepted receipt as the
single successful dispatch for one chat turn. Later model rounds cannot invoke
the member tool again and must use the returned typed service-run observation
identity. `wait=complete` is rejected for this tool because no terminal result
is observed in its call stack.

This applies CLAUDE.md §"Actor 执行模型 — self continuation 事件化" and
§"跨 actor 等待 continuation 化" at the chat-session layer: there is no
synchronous await across actor boundaries.

### D5 — `ChatRunActor` and `VoiceSessionActor` own session state as actors

The later topology would introduce session-scoped actors:

- **`ChatRunActor`** for `/v1/responses` and `/v1/messages` SSE sessions.
- **`VoiceSessionActor`** for `/ws/voice` Realtime sessions.

Both own:

- LLM context for the session
- Tool-call history
- Active sub-run subscriptions as a typed list in `State` (not a
  middle-layer `Dictionary<runId, context>` — see CLAUDE.md §"中间层状态约束")
- The state machine that folds sub-run committed events into the next
  LLM turn

Per CLAUDE.md §"Actor 即业务实体", these are named for the business entity
(chat run, voice session), not the technical role.

Issue #1748's first slice does not introduce `VoiceSessionActor`; it only
removes the Host-owned voice session shell and moves voice control/transcript
onto the shared realtime/projection path. `ChatRunActor`/`LlmSessionGAgent` and
the voice `RoleGAgent` remain separate authoritative actors; they must not be
merged to share implementation convenience.

### D6 — Voice converges on the same ingress shape

`/ws/voice` may attach to a voice-enabled actor at WebSocket upgrade time only
when the resolved `ForwardToModel.tool_choice_hint.voice_attach_target` carries
the typed attach target. `actor_id` and `voice_module_name` in
`prefilled_arguments` are ignored for voice attachment. Until a route-scoped
`VoiceSessionActor` exists, ordinary `/ws/voice` still fails closed for pure
model-forward `ForwardToModel` decisions before WebSocket accept. The later
session actor can run `ChatRouteResolver` once at session establishment,
declare the resolved tool set to the OpenAI Realtime provider, and feed
function calls through the same `ToolCallLoop` without treating tool prefill as
actor addressing.

`/ws/voice/{actorId}` (dev/admin bypass from ADR-0024 D4) stays. It
short-circuits the resolver, so its semantics are unaffected.

This supersedes ADR-0025's "voice v1 supports `ForwardToGAgent` only;
`ForwardToModel` returns HTTP 501". Voice v2 supports `ForwardToModel`
exclusively; `ForwardToGAgent` ceases to be a wire-level concept for voice.

**Prerequisite, not in this ADR:** `Aevatar.Foundation.VoicePresence.OpenAI`
must complete its migration from beta-shape `session.update` to GA shape
before voice can drive tool calls end-to-end (per the
`reference_openai_realtime_beta_ga_shape_mismatch` operational record).
This migration is tracked as an independent prerequisite issue.

### D7 — Caller-scoped tools enable the "NyxID-direct user routes Lark to
self" use case

Because the additive-tool pattern from `nyxid-responses-direct.md` §5
already executes tools under the caller's NyxID bearer (`use_skill` and
`ornn_search_skills` work this way today), exposing Lark outbound as an
`IAgentToolSource` lets a user calling `/v1/responses` directly through
NyxID — without ever opening the Aevatar UI — say "push this to my Lark"
and have the LLM call `lark_message_send`. The tool dispatcher reads
`AgentToolRequestContext.NyxIdUserId`, calls NyxID's user-scoped Lark
relay outbound endpoint with the caller's credentials, and the message
lands in the caller's Lark account.

This is not a new capability added by this ADR; it is the natural
consequence of the unified shape. Two implementation details must be
verified in Phase 1 and tracked as Phase 1 sub-tasks rather than
deferred:

1. NyxID's Lark relay outbound endpoint supports user-scoped push (not
   only service-bot push). If it does not, CLAUDE.md §"外部仓库无改动权"
   forbids us patching NyxID; the use case is then deferred until NyxID
   ships it independently.
2. Aevatar's Lark outbound tool (if any) propagates caller scope through
   `AgentToolRequestContext` rather than using a service-level Lark
   identity.

## Boundaries

- `IActorDispatchPort`, `IActorRuntime`, and internal actor-to-actor
  dispatch surfaces are **not affected**. Only user-facing ingress
  collapses. Internal system code that invokes a GAgent directly keeps its
  direct path.
- `ChatRoutePolicy` three-part form from ADR-0024 D1 is preserved.
  `ChatRouteDecision` remains transient per ADR-0024 D2.
- Workflow definition / editing endpoints
  (`/api/scopes/{scopeId}/workflows`, `IWorkflowDraftStore`, etc.) are
  unaffected. Only workflow *triggering* moves to the tool path. The
  Studio member invoke surface
  (`POST /api/scopes/{scopeId}/members/{memberId}/invoke/{endpointId}`)
  remains as a direct system-to-system surface for callers that explicitly
  do not want LLM mediation.
- The existing `/v1/responses` Aevatar substitute and additive tool model
  (`nyxid-responses-direct.md` §5) is extended, not replaced. The new
  invocation tools join the additive-tool category.
- `/ws/voice/{actorId}` dev bypass is preserved.
- This ADR does not redesign the read-path for sub-run state observation;
  `aevatar_observe_run` reads through the existing readmodel projection
  surfaces, one typed target per call. Ordinary workflow queries stay on
  the workflow-owned `workflow_actor_current_state`, `workflow_status`, and
  `event_query` tools. No new readmodel is introduced.

## Verification

Static checks (CI guards):

- `chat_route_policy.proto`: policy snapshots MUST NOT emit
  `ForwardToGAgent`, `ForwardToTeam`, or `ForwardToWorkflow`; those tags and
  names are reserved. A guard script asserts emission paths only produce
  `ForwardToModel` or `Reject`.
- `ResponsesEndpoints.cs` lines 779-927 deleted after Phase 4. A guard
  script asserts the file does not contain `IStaticGAgentStreamInvocationPort`
  references in `/v1/responses` handler bodies.
- `AgentRunGAgent.cs:1108-1141` `TargetActorId` override deleted after
  Phase 4. A guard script asserts `NeedsLlmReplyEvent.TargetActorId` is no
  longer mutated outside the actor that owns the field's authoritative
  state.
- later `ChatRunActor` and `VoiceSessionActor` substate that tracks sub-run IDs
  is a typed proto `repeated` field on actor State, not a
  `Dictionary<,>` field. Existing middleware-state guard (`tools/ci/`)
  scope extends to the new actors.

Runtime checks:

- `/v1/responses` policy resolves `ForwardToModel(tool_set_ref=...)`,
  injects the resolved tool set, ToolCallLoop dispatches
  `aevatar_invoke_gagent(actor_id=X)`, sub-run starts, stream events fold
  back into the response SSE — observed end-to-end without the legacy
  `ForwardToGAgent` adapter.
- `/v1/messages` no longer returns HTTP 501 for any case the legacy
  policy would have routed to `ForwardToGAgent` / `ForwardToTeam`. The
  Anthropic facade can host the same orchestration because the
  orchestration lives in the tool layer, not the wire shape.
- `/ws/voice` resolves a policy and attaches when the decision carries typed
  `voice_attach_target`; `ForwardToModel` decisions without that typed target
  return HTTP 501 before WebSocket accept.
- NyxID-direct caller (no Aevatar UI session) hits `/v1/responses` with
  a NyxID bearer that has a Lark connection in NyxID; says "push X to
  my Lark"; the Lark tool dispatches under caller scope; the message
  appears in the caller's Lark account.

Behavior preserved:

- Caller scope semantics from `nyxid-responses-direct.md` §2 (scope
  resolution, response session visibility) unchanged.
- Per-user NyxID binding from ADR-0018 unchanged.
- ADR-0024 D1/D2/D3/D4/D6 unchanged. Only D5 (action set in v1) is
  superseded.

## Stage Plan

Each stage ships independently. Stages 1 and 5 can run in parallel after
Stage 1's tool sources are merged.

| Stage | Scope | Breaking? |
|---|---|---|
| **1** | Implement `aevatar_invoke_gagent` / `_team` / `_workflow` / `_observe_run` as `IAgentToolSource`. Wire into existing ToolCallLoop. Verify Lark outbound user-scoped path (D7 prerequisite). | No |
| **2** | Extend `ForwardToModel` proto with `tool_set_ref` + `tool_choice_hint`. Policy authors express GAgent, team, and workflow targets directly as tool-first `ForwardToModel` actions. ChatRun-owned SSE session continuation remains deferred. | No |
| **3** | Delete legacy wire actions and migration path. Reserve old proto tags/names for `ForwardToGAgent`, `ForwardToTeam`, `ForwardToWorkflow`, and `Bypass`; no new policy writer may emit them. | Yes (clients still using legacy actions) |
| **4** | Remove code paths: `ResponsesEndpoints.cs:779-927`, `AgentRunGAgent.cs:1108-1141`, resolver branches for legacy actions. `/v1/messages` 501 fallback for these actions deleted. | Yes (clients still using legacy actions) |
| **5** | `VoiceSessionActor` implementation. `/ws/voice` switches from typed attach target to session-owned `ForwardToModel + tool_set_ref` execution. `/ws/voice/{actorId}` dev bypass unaffected. **Blocked by:** VoicePresence.OpenAI GA migration. | Yes (voice clients) |

## Supersedes

- **ADR-0024 §D5** (v1 action set: `ForwardToGAgent`, `ForwardToModel`,
  `ForwardToTeam` all implemented) — superseded by D1 of this ADR. The
  rest of ADR-0024 (D1/D2/D3/D4/D6) stands.
- **ADR-0025** (voice v1 routes to `ForwardToGAgent` only;
  `ForwardToModel` returns 501) — superseded by D6 of this ADR. The
  boundary constraints in ADR-0025 §"Boundaries" stand.

## References

- ADR-0018: Per-user NyxID binding via OAuth broker
- ADR-0024: Chat Route Policy — Config Actor + Boundary Resolver
- ADR-0025: Voice Router Integration — Policy-Aware WebSocket Boundary
- `docs/canon/nyxid-responses-direct.md` — additive tools and caller
  scope contract
- `docs/canon/chat-api.md`
- `docs/canon/llm-streaming.md`
- Issue #672, #674, #588 — preceding routing work
- `reference_openai_realtime_beta_ga_shape_mismatch` (operational memory)
  — Voice Stage 5 prerequisite signal
