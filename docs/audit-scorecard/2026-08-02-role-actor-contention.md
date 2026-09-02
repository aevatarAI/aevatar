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
`54bbc8d9f72d5b528c4bde55aad45bef2a91a79c`, which contains #3135 and
#3137. Both results use config digest
`AA835BE81AF3B7FBD4184E083380C8B47CDD33DD500A51739D822263A16BB739`,
so they satisfy the comparison contract.

Both runs use schema 2 of the final runtime-neutral, cleanup-corrected
measurement. They were run in isolated detached worktrees whose only tracked
dirty paths were
`tools/measurements/Aevatar.RoleStreamingWriteAmplification/Program.cs` and
`tools/measurements/Aevatar.RoleStreamingWriteAmplification/RoleContentionMeasurement.cs`.
The latter is byte-identical to commit `80887623f`; the former contains only
that commit's `SingleActorRuntime` availability and `DestroyAsync` lifecycle
patch. Production `RoleGAgent` code therefore remains pinned to each declared
source commit.

The raw files record the exact binaries used:

| Run | Measurement assembly SHA-256 | `Aevatar.AI.Core` assembly SHA-256 |
| --- | --- | --- |
| Baseline | `6774D81C8DCCD7D1E5705A62A099DCA7ED3FD1A74AF428FC06B4840AF99B7CA1` | `904996AAFE9601838928FB84885BFC33A227E92E7C749FB49B3939C1954D1A80` |
| Post-#3135 | `6DE872879419D6F832BB0893515EDFA9F1123CC87542B0312AD3DCBBEE67E520` | `9646C138D3FA09DE2F36BD8F22B5876A22E6170E90BAD561D30094D9891A3C6A` |

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

Each sample is constructed only after cleanup completes. `deactivationCount`
increments only when `IActorRuntime.DestroyAsync` returns after deactivating the
real `LocalActor`. Deactivation, service-provider disposal, and stream-drain
errors increment `cleanupFailureCount`. A post-deactivation probe goes through
`IActorDispatchPort` and independently verifies whether the runtime still
accepts the actor address, supplying `orphanedActiveActorCount`. If the workload
fails, cleanup still runs and its three observed counts are included in the
thrown failure. The `--verify` mode executes both a successful lifecycle and an
injected deactivation-hook failure; the latter must report `deactivations=0`,
`failures=1`, and `active_orphans=0`.

## Pre-3135 baseline

Milliseconds use nearest-rank percentiles over 96 fast-turn observations per
scenario.

| Metric | `same_actor` p50/p95/p99 | `distinct_actor` p50/p95/p99 | same - distinct p50/p95/p99 |
| --- | ---: | ---: | ---: |
| Fast mailbox queue | 26.973 / 38.804 / 39.754 | 0.299 / 2.775 / 3.167 | 26.674 / 36.029 / 36.587 |
| Fast completion latency | 27.701 / 39.022 / 40.026 | 1.229 / 6.319 / 6.614 | 26.472 / 32.703 / 33.412 |
| Slow service time | 25.207 / 39.805 / 39.805 | 26.272 / 53.575 / 53.575 | not used for a gate |

| Lifecycle/state observation | `same_actor` | `distinct_actor` |
| --- | ---: | ---: |
| Maximum queue depth per actor p50/p95/p99 | 8 / 8 / 8 | 1 / 1 / 1 |
| Activations per iteration | 1 | 9 |
| Successful deactivations per iteration | 1 | 9 |
| Actor protobuf state bytes p50/p95/p99 | 2,207 / 2,216 / 2,216 | 324 / 659 / 660 |
| Aggregate protobuf state bytes p50/p95/p99 | 2,207 / 2,216 / 2,216 | 3,251 / 3,260 / 3,260 |
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
| Fast mailbox queue | 36.151 / 66.405 / 68.153 | 0.423 / 2.039 / 2.566 | 35.727 / 64.366 / 65.588 |
| Fast completion latency | 37.400 / 66.834 / 68.785 | 2.004 / 5.619 / 6.288 | 35.396 / 61.215 / 62.497 |
| Slow service time | 33.671 / 65.369 / 65.369 | 43.340 / 62.288 / 62.288 | not used for a gate |

| Lifecycle/state observation | `same_actor` | `distinct_actor` |
| --- | ---: | ---: |
| Maximum queue depth per actor p50/p95/p99 | 8 / 8 / 8 | 1 / 1 / 1 |
| Activations per iteration | 1 | 9 |
| Successful deactivations per iteration | 1 | 9 |
| Actor protobuf state bytes p50/p95/p99 | 2,207 / 2,216 / 2,216 | 324 / 659 / 660 |
| Aggregate protobuf state bytes p50/p95/p99 | 2,207 / 2,216 / 2,216 | 3,251 / 3,260 / 3,260 |
| Cleanup failures | 0 | 0 |
| Active actor orphans after cleanup | 0 | 0 |

The head-of-line delta changed as follows. Positive values mean the post run
was higher than the baseline; this diagnostic has no latency pass/fail
threshold.

| Head-of-line delta | Baseline p50/p95/p99 | Post-#3135 p50/p95/p99 | Post - baseline p50/p95/p99 |
| --- | ---: | ---: | ---: |
| Fast mailbox queue | 26.674 / 36.029 / 36.587 | 35.727 / 64.366 / 65.588 | +9.054 / +28.337 / +29.001 |
| Fast completion latency | 26.472 / 32.703 / 33.412 | 35.396 / 61.215 / 62.497 | +8.924 / +28.511 / +29.085 |

#3135 bounds the maximum turn duration and makes deadline cancellation
terminally observable; it does not make a single-reader actor execute two turns
concurrently. This workload releases the controlled slow provider before the
host cap, so the deadline is not expected to reduce its queue time. The post
run's higher p50 and tail deltas are not evidence of a capacity regression: the
controlled slow-turn durations also moved under the fixed async-yield budget.
The diagnostic has no latency pass/fail threshold.

The bottleneck attribution remains unchanged: median fast service and
distinct-actor queue times remain small, the per-actor maximum queue depth is
still one versus eight, lifecycle/state sizes are unchanged, and every
measured turn completed with zero cleanup failures or active actor orphans.
The synthetic median delay remains attributable to the reused actor's single
inbox, not event-store payload, transport backlog, retained-session state, or
cleanup.
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

Validate configuration plus both successful and injected-failure cleanup
accounting:

```bash
dotnet run \
  --project tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj \
  --configuration Release -- \
  --measurement role-contention \
  --adapter inmemory \
  --config tools/measurements/Aevatar.RoleStreamingWriteAmplification/role-contention.config.json \
  --verify
```

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  baseline-pre-3135
```

From the aggregate containing #3135 and #3137:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  post-3135
```

Validate the checked-in cleanup observations:

```bash
jq -e '
  .schemaVersion == 2 and
  .sourceDirtyPaths == [
    "tools/measurements/Aevatar.RoleStreamingWriteAmplification/Program.cs",
    "tools/measurements/Aevatar.RoleStreamingWriteAmplification/RoleContentionMeasurement.cs"
  ] and
  all(
    .scenarios[].samples[];
    .deactivationCount == .activationCount and
    .cleanupFailureCount == 0 and
    .orphanedActiveActorCount == 0
  ) and
  all(
    .scenarios[];
    .summary.cleanupFailureCount == 0 and
    .summary.orphanedActiveActorCount == 0
  )
' docs/audit-scorecard/raw/2026-08-02-role-actor-contention-*.json
```
