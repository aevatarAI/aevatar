---
title: Role actor contention inventory and post-3135 decision
status: complete
owner: Aevatar Runtime
---

# Role actor contention inventory and post-3135 decision

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
`618ba214166df2dd794bbe6d5a5eb2a75ae0dc98`. The raw post-#3135 result is
[`raw/2026-08-02-role-actor-contention-post-3135.json`](raw/2026-08-02-role-actor-contention-post-3135.json).
It was produced from aggregate commit
`8d3d51c40b813455db4a41c5a4f0b1a8b8d91e98`, which contains #3135 and
#3137. Both results use config digest
`AA835BE81AF3B7FBD4184E083380C8B47CDD33DD500A51739D822263A16BB739`,
so they satisfy the comparison contract.

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

## Post-3135 result

The same workload and config produced the following result after #3135.

| Metric | `same_actor` p50/p95/p99 | `distinct_actor` p50/p95/p99 | same - distinct p50/p95/p99 |
| --- | ---: | ---: | ---: |
| Fast mailbox queue | 49.119 / 86.868 / 90.055 | 0.046 / 0.131 / 0.222 | 49.073 / 86.737 / 89.833 |
| Fast completion latency | 49.491 / 87.250 / 90.540 | 0.593 / 2.486 / 2.633 | 48.898 / 84.765 / 87.907 |
| Slow service time | 45.430 / 85.827 / 85.827 | 44.414 / 76.927 / 76.927 | not used for a gate |

| Lifecycle/state observation | `same_actor` | `distinct_actor` |
| --- | ---: | ---: |
| Maximum queue depth per actor | 8 | 1 |
| Activations per iteration | 1 | 9 |
| Actor protobuf state bytes p50 | 2,207 | 324 |
| Aggregate protobuf state bytes p50 | 2,207 | 3,251 |
| Cleanup failures | 0 | 0 |
| Active actor orphans after cleanup | 0 | 0 |

The head-of-line delta changed as follows. Positive values mean the post run
was higher than the baseline; this diagnostic has no latency pass/fail
threshold.

| Head-of-line delta | Baseline p50/p95/p99 | Post-#3135 p50/p95/p99 | Post - baseline p50/p95/p99 |
| --- | ---: | ---: | ---: |
| Fast mailbox queue | 55.337 / 82.258 / 82.871 | 49.073 / 86.737 / 89.833 | -6.265 / +4.478 / +6.961 |
| Fast completion latency | 55.129 / 80.404 / 82.677 | 48.898 / 84.765 / 87.907 | -6.232 / +4.361 / +5.230 |

#3135 bounds the maximum turn duration and makes deadline cancellation
terminally observable; it does not make a single-reader actor execute two turns
concurrently. This workload releases the controlled slow provider before the
host cap, so the deadline is not expected to reduce its queue time. The mixed
p50 and tail movement is consistent with scheduler variation from the fixed
async-yield budget and is not evidence of either a capacity improvement or a
regression.

The bottleneck attribution remains unchanged: fast service time stays small,
distinct-actor queue time stays near zero, lifecycle/state sizes are unchanged,
and every measured turn completed with zero cleanup failures or active actor
orphans. The synthetic delay is therefore the reused actor's single inbox, not
event-store payload, transport backlog, retained-session state, or cleanup.
The measurement still does not establish production severity because no
production concurrency distribution, provider-throttling trace, or mailbox SLO
was supplied.

## Go/no-go decision

**Go:** close #3143 as a completed measurement and architecture decision. Keep
#3135's host-owned finite deadline as the reliability bound for every shared
actor turn.

**No-go:** do not add a session actor, globally refactor `RoleGAgent`, or open
an implementation issue from this synthetic result alone. The explicit
`scoped_role` reuse option is a concrete shared-inbox surface and the harness
proves its head-of-line mechanism, but the decision threshold is not fully met:
there is no production-representative hotspot evidence and no justified new
fact owner, server-sealed typed snapshot/reference, lifecycle contract, reuse
key, upgrade-forward rule, or cleanup owner.

`MaxTrackedSessions = 128` remains a bounded retained-session-state policy on
`RoleGAgent`; it is not mailbox admission or cross-session capacity control.
Run/turn-scoped entrypoints do not need a copied capacity layer. For explicit
scoped-role reuse, the limit only bounds retained session records after turns
have already shared the inbox.

Only future production evidence that satisfies every issue decision threshold
can justify a narrowly scoped follow-up for the explicit `scoped_role` owner.
Until then, the architecture remains unchanged.

## Reproduction

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  baseline-pre-3135
```

From the aggregate containing #3135 and #3137:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  post-3135
```
