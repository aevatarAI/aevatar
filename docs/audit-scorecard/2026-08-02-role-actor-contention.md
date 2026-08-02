---
title: Role actor contention inventory and pre-3135 baseline
status: draft
owner: Aevatar Runtime
---

# Role actor contention inventory and pre-3135 baseline

## Scope and comparison contract

This report answers issue #3143 without assuming a new session actor. The only
remaining production surface measured here is `scoped_role`: a Scope static
GAgent chat invocation that explicitly supplies an existing actor identity.
The checked-in configuration pins
`dcb05b683b911db037eb51c071b4495f1195ee28` as the production-code baseline,
before #3135 is integrated.

The raw baseline is
[`raw/2026-08-02-role-actor-contention-baseline-pre-3135.json`](raw/2026-08-02-role-actor-contention-baseline-pre-3135.json).
It was produced by harness commit
`618ba214166df2dd794bbe6d5a5eb2a75ae0dc98`; its config digest is
`AA835BE81AF3B7FBD4184E083380C8B47CDD33DD500A51739D822263A16BB739`.
A post-#3135 result does not exist yet. It must be produced from an aggregate
that contains #3135 using the same config digest; this report does not infer or
fabricate that result.

## Entrypoint inventory

| Entrypoint | Definition/config owner | Run/turn execution owner | Identity creation and reuse | LLM/tool execution location | Contention disposition |
| --- | --- | --- | --- | --- | --- |
| Mainnet `/api/chat`, workflow request | `WorkflowGAgent` definition actor | `WorkflowRunGAgent`; run-owned `WorkflowRoleGAgent` children | Each run creates or ensures a `WorkflowRunGAgent`; role children are keyed by `runActorId + roleId` and are not shared across runs | `WorkflowRoleGAgent.ChatStreamAsync` inside the run-owned child actor turn | No cross-run `RoleGAgent` reuse; excluded from the scoped-role load test |
| Mainnet `/api/chat`, Assistant request | `NyxIdChatConversationGAgent` owns conversation authority | `NyxIdChatTurnGAgent` owns one authorized turn operation | Turn actor address is derived from conversation authority plus server-created turn identity; the controller creates and links it on first dispatch | `NyxIdChatTurnGAgent` calls the operation executor inside its actor turn | Turn-scoped; excluded |
| Channel/Lark chat | `ConversationGAgent` owns conversation state and routing | `AgentRunGAgent` per run | `AgentRunDispatcher` derives one actor address from the required run ID and creates that run actor | `AgentRunReplyGenerationExecutor` reaches `ConversationReplyGenerator -> ChatRuntime.ChatStreamAsync` inside the run actor turn | Run-scoped; excluded |
| Workflow Chat API | `WorkflowGAgent` owns the definition | `WorkflowRunGAgent` and its run-owned role children | New run identity creates a new execution actor; explicit resume reuses the same run authority, not another independent session | `WorkflowRoleGAgent.ChatStreamAsync` inside the role child actor turn | Reuse is bounded to one workflow run; excluded from the remaining shared-session test |
| Scope static GAgent / `scoped_role` | Published service revision and static deployment own the sealed agent kind/config; the activated static actor owns role state | `RoleGAgent` itself handles `ChatRequestEvent` | Without `PreferredActorId`, `GAgentDraftRunInteractionService` creates a fresh opaque actor ID. With an explicit authorized ID, it reuses that existing actor. Static deployment activation also has a stable deployment actor ID | `RoleGAgent.HandleChatRequestCoreAsync -> ExecuteStreamingChatAsync -> ChatStreamAsync`, including tool rounds, all inside one actor handler turn | Concrete remaining shared-inbox surface; measured as `same_actor` versus `distinct_actor` |
| CLI clients | No separate actor owner; CLI calls the Scope/Mainnet contracts | Inherits the selected API execution owner | Inherits explicit actor reuse or fresh-actor behavior from the called API | Inherits the called API path | No separate load target |

The inventory follows `CLAUDE.md`: definition/config facts may be long-lived,
while run/session/task execution defaults to short-lived actors. It also keeps
`actorId` opaque; no result depends on an ID prefix or type-name parse.

## Reproducible workload

The non-gating harness extends
`tools/measurements/Aevatar.RoleStreamingWriteAmplification` and uses the real
`LocalActor` single-reader mailbox plus the real `RoleGAgent` streaming path.
Each sample starts one controlled slow provider turn and eight fast turns.
After the slow turn enters `ChatStreamAsync`, all fast turns are admitted. A
fixed async yield budget then releases the slow provider. There is no latency
pass/fail threshold.

Two otherwise identical scenarios run in alternating order for 2 warmups and
12 measured iterations:

- `same_actor`: 9 concurrent sessions target one actor;
- `distinct_actor`: each session targets its own actor.

Actor/session identities are not metric labels. The only allowed label axes are
`entrypoint`, `scenario`, `turn_kind`, and `outcome`; `actor_id`, `session_id`,
`command_id`, and `correlation_id` are forbidden. Raw samples use only local
ordinals.

## Pre-3135 baseline

Milliseconds use nearest-rank percentiles over 96 fast-turn observations per
scenario.

| Metric | `same_actor` p50/p95/p99 | `distinct_actor` p50/p95/p99 | same - distinct p50/p95/p99 |
| --- | ---: | ---: | ---: |
| Fast mailbox queue | 55.388 / 82.474 / 85.096 | 0.051 / 0.215 / 2.225 | 55.337 / 82.258 / 82.871 |
| Fast completion latency | 55.700 / 83.110 / 85.456 | 0.571 / 2.707 / 2.779 | 55.129 / 80.404 / 82.677 |
| Slow service time | 52.933 / 81.103 / 81.103 | 57.448 / 81.497 / 81.497 | not used for a gate |

| Lifecycle/state observation | `same_actor` | `distinct_actor` |
| --- | ---: | ---: |
| Maximum queue depth per actor | 8 | 1 |
| Activations per iteration | 1 | 9 |
| Actor protobuf state bytes p50 | 2,207 | 324 |
| Aggregate protobuf state bytes p50 | 2,207 | 3,251 |
| Cleanup failures | 0 | 0 |
| Active actor orphans after cleanup | 0 | 0 |

The controlled slow turn is the dominant difference: shared fast turns wait
behind it, while distinct actors run the same fast work independently. The
queue delta closely tracks the slow-turn duration, so the harness attributes
the synthetic delta to single-inbox head-of-line blocking rather than event
store payload size or transport backlog. Distinct actors trade that isolation
for nine activations and higher aggregate state bytes.

## Decision

The baseline proves the expected mechanism for the explicit `scoped_role`
reuse option, but it does not prove a production hotspot. No production
concurrency distribution, provider throttling trace, or mailbox SLO was
available, and #3135 has not yet been applied to the comparison branch.
Therefore this evidence does not authorize a new session actor or a global
`RoleGAgent` refactor.

`MaxTrackedSessions = 128` remains a bounded retained-session-state policy on
`RoleGAgent`; it is not mailbox admission or cross-session capacity control.
Run/turn-scoped entrypoints do not need a copied capacity layer. For explicit
scoped-role reuse, the limit only bounds retained session records after turns
have already shared the inbox.

The next required action is exactly one post-#3135 rerun with the same config.
Only after that result and production-representative traffic evidence exist can
a follow-up issue decide whether the explicit scoped-role owner needs a
server-sealed run snapshot and a narrower execution actor. Until then, the
architecture remains unchanged.

## Reproduction

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  baseline-pre-3135
```

After #3135 is present in the aggregate:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  post-3135
```
