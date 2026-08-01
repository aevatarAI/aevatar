---
title: RoleGAgent streaming write-amplification measurement
status: complete
owner: codex
issue: 3146
source_commit: bd5716b838a6f4638d3ebc06f673fbe1f88760c6
---

# RoleGAgent streaming write-amplification measurement

## Decision

**No-go on changing commit boundaries now.** The benchmark confirms linear
write amplification and measurable Garnet transaction cost, but it does not
establish a production capacity, latency, or storage SLO violation. Weakening
typed committed facts would spend recovery and observation correctness without
a production-backed benefit target.

Keep the current `RoleGAgent` durability boundaries and the single committed
state projection path. A future implementation issue requires production
telemetry, an explicit benefit threshold, and recovery contracts from #3138
plus publication/compaction fences from #3139.

Raw samples, adapter identity, capabilities, and all distributions are in
[`raw/2026-08-02-role-streaming-write-amplification.json`](raw/2026-08-02-role-streaming-write-amplification.json).

## What was measured

The harness executes the real `RoleGAgent` through a real `LocalActor` mailbox
and `IActorDispatchPort`. It injects the real `LocalActorPublisher`, observes
real `CommittedStateEventPublished` envelopes, and sends those envelopes
through a formally registered current-state materializer, projection write
dispatcher, and document store. Provider evidence is captured immediately
before each deterministic chunk is yielded. Harness decorators record scalar
counts, protobuf sizes, and I/O duration; production code is unchanged.

Configuration:

- source commit `bd5716b838a6f4638d3ebc06f673fbe1f88760c6`;
- macOS 26.3 arm64, .NET 10.0.3, workstation GC, 12 logical processors;
- 2 warmups and 12 measured samples per workload and adapter;
- snapshot interval 50, compaction enabled, 5 retained events;
- crash recovery append fences 4, 12, and 24;
- recovery uses 22 text chunks, keeping final event-set reconciliation below
  the version-50 compaction boundary; the 128-chunk workload measures
  compaction separately;
- InMemory and Garnet 2.1.0 over Docker loopback;
- deterministic text, reasoning, tool, media, terminal, cancellation, failure,
  and recovery provider shapes.

Garnet was pinned to
`ghcr.io/microsoft/garnet:2.1.0@sha256:4e298b9b274088cded4156853a32b85fed7b42242eb9ca90216d332e25f2bceb`.
Before samples, the harness fail-closed verified `INFO SERVER`, `HELLO 2`, Lua
read/write, and the production append/compaction scripts. Raw identity reports
`server_name=garnet`, `garnet_version=2.1.0`, RESP2, standalone, and master.
The OCI digest is operator-declared because Redis does not expose container
identity; server name/version are independently observed.

Percentiles use nearest rank. With 12 samples, p95 and p99 both select the
maximum sample, so they indicate run reproducibility and tail direction, not a
production percentile estimate.

## Core results

Values are `p50/p95/p99`. Append counts and bytes are p50. Completion ends at
the actor handler boundary and excludes deactivation and recovery-validation
drain barriers.

### InMemory event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,846 | 0 / 0 | 0.022/0.051/0.051 | 0.209/0.401/0.401 | 0.621/1.265/1.265 |
| long text, 128 chunks | 131 | 53,277 | 2 / 95 | 0.376/5.925/5.925 | 0.143/0.281/0.281 | 6.174/19.374/19.374 |
| reasoning + text | 59 | 22,006 | 1 / 45 | 0.123/0.244/0.244 | 0.135/0.202/0.202 | 2.156/3.025/3.025 |
| single tool call | 7 | 3,472 | 0 / 0 | 0.019/0.064/0.064 | 0.336/2.055/2.055 | 0.637/2.512/2.512 |
| three tool calls | 13 | 6,779 | 0 / 0 | 0.033/0.066/0.066 | 0.521/0.929/0.929 | 0.977/2.358/2.358 |
| four media parts | 11 | 5,465 | 0 / 0 | 0.028/0.047/0.047 | 0.150/0.230/0.230 | 0.663/1.939/1.939 |
| terminal only | 3 | 1,593 | 0 / 0 | 0.009/0.021/0.021 | n/a | 0.309/0.589/0.589 |
| cancellation | 5 | 2,161 | 0 / 0 | 0.037/0.112/0.112 | 0.254/0.685/0.685 | 17.470/22.546/22.546 |
| provider failure | 6 | 2,601 | 0 / 0 | 0.016/0.019/0.019 | 0.120/0.157/0.157 | 0.418/0.509/0.509 |

### Garnet event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,846 | 0 / 0 | 4.973/7.796/7.796 | 1.732/2.825/2.825 | 5.930/9.218/9.218 |
| long text, 128 chunks | 131 | 53,277 | 2 / 95 | 64.375/188.731/188.731 | 1.359/16.046/16.046 | 73.182/200.508/200.508 |
| reasoning + text | 59 | 22,006 | 1 / 45 | 34.642/135.597/135.597 | 1.434/2.963/2.963 | 38.386/137.625/137.625 |
| single tool call | 7 | 3,472 | 0 / 0 | 2.936/6.571/6.571 | 2.085/4.260/4.260 | 3.352/7.084/7.084 |
| three tool calls | 13 | 6,793 | 0 / 0 | 6.166/8.579/8.579 | 4.114/6.232/6.232 | 7.103/9.433/9.433 |
| four media parts | 11 | 5,473 | 0 / 0 | 4.412/10.633/10.633 | 0.965/6.592/6.592 | 4.803/11.151/11.151 |
| terminal only | 3 | 1,593 | 0 / 0 | 2.344/2.916/2.916 | n/a | 2.649/3.248/3.248 |
| cancellation | 5 | 2,161 | 0 / 0 | 5.141/6.991/6.991 | 1.683/3.488/3.488 | 18.789/19.983/19.983 |
| provider failure | 6 | 2,601 | 0 / 0 | 4.815/6.721/6.721 | 1.518/2.974/2.974 | 5.309/7.249/7.249 |

## Recovery sweep

The comparison uses provider-generated semantic evidence, an
append-acknowledged ledger, fresh `baseStore.GetEventsAsync(actorId)` readback,
actual `CommittedStateEventPublished` observations, and an independently read
measurement-only current-state document. Actor initialization is included in
the three final event sets, while turn resource metrics reset after
initialization. The raw schema is version 4.

Generated boundaries are keyed by session, attempt, semantic ordinal, kind,
and payload hash. Phase one deliberately reports the single chunk generated
after the configured append fence rejects the next write as an attempt-local
uncommitted tail; it is not labelled committed progress loss. The successful
recovery attempt must match committed semantics in both directions. Completion
events are expanded through their typed `terminal_progress`, so usage evidence
is not silently omitted. The materialized read model must match committed
publication version, event ID, state-root hash, session counts, final content
hash, and usage hash; duplicate and stale writes must leave it unchanged.

All counts below are deterministic p50 values. Missing/loss columns are zero
for every sample on both adapters, not only at p50.

| Adapter | Fence | Phase generated / committed | Recovery generated / committed | Payload redo / bytes | Final ledger / durable / publication | Attempt tail | Recovery G→C / C→G | Materialized |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 3 / 2 | 23 / 23 | 3 / 1,009 | 29 / 29 / 29 | 1 | 0 / 0 | pass |
| InMemory | 12 | 11 / 10 | 23 / 23 | 11 / 3,597 | 37 / 37 / 37 | 1 | 0 / 0 | pass |
| InMemory | 24 | 23 / 22 | 23 / 23 | 23 / 7,473 | 49 / 49 / 49 | 1 | 0 / 0 | pass |
| Garnet | 4 | 3 / 2 | 23 / 23 | 3 / 1,009 | 29 / 29 / 29 | 1 | 0 / 0 | pass |
| Garnet | 12 | 11 / 10 | 23 / 23 | 11 / 3,608 | 37 / 37 / 37 | 1 | 0 / 0 | pass |
| Garnet | 24 | 23 / 22 | 23 / 23 | 23 / 7,496 | 49 / 49 / 49 | 1 | 0 / 0 | pass |

These are three observed windows, not a maximum. At each tested fence, the
final append ledger, durable readback, and committed-publication event IDs
matched exactly. Recovery did not reuse progress identity or sequence, but it
repeated every previously emitted payload. The recovery attempt itself had no
generated/committed gap, and its 22 text deltas plus usage matched the final
materialized session hashes for all 72 samples. The phase-one payload repeat is
still user-visible redo risk; no tested committed or final user-visible fact
was lost.

## Resource calibration

The append decorator no longer clones events. It records counts and
`CalculateSize()` scalar totals; event references are retained only for crash
reconciliation. Every instrumented sample is paired with a separate matched
control turn that removes append/snapshot decorators. Even iterations run the
control first and odd iterations run it second.

The following values are the matched-control estimate (`p50/p95`; p99 equals
p95). Allocation uses process-wide `GC.GetTotalAllocatedBytes(true)`.

| Adapter / workload | Net CPU ms | Net allocated bytes |
| --- | ---: | ---: |
| InMemory / short text | 1.145/2.022 | 104,760/106,304 |
| InMemory / long text | 12.305/18.976 | 1,563,952/1,569,296 |
| InMemory / reasoning + text | 6.560/8.316 | 711,088/715,256 |
| InMemory / single tool | 1.575/2.203 | 127,096/145,224 |
| InMemory / three tools | 2.467/4.016 | 216,536/234,760 |
| InMemory / media | 1.904/2.884 | 161,056/162,016 |
| InMemory / terminal | 0.825/1.269 | 63,048/63,624 |
| InMemory / cancellation | 1.901/19.414 | 100,432/100,680 |
| InMemory / provider failure | 1.042/2.723 | 110,240/134,808 |
| Garnet / short text | 6.039/8.379 | 128,400/130,504 |
| Garnet / long text | 80.700/248.103 | 2,058,064/2,066,048 |
| Garnet / reasoning + text | 61.115/174.428 | 920,544/931,640 |
| Garnet / single tool | 3.480/10.300 | 150,864/152,856 |
| Garnet / three tools | 15.240/35.145 | 260,712/263,872 |
| Garnet / media | 5.716/16.042 | 200,320/205,880 |
| Garnet / terminal | 2.817/3.415 | 75,040/75,440 |
| Garnet / cancellation | 3.672/9.922 | 114,992/116,624 |
| Garnet / provider failure | 3.352/4.859 | 129,000/130,528 |

Gross-minus-control allocation delta spans about -18.7-25.0 KB for InMemory
normal workloads and -24.1-93.1 KB for Garnet. CPU deltas likewise cross
zero for several workloads and have noisy maxima. The raw file retains gross,
net, and signed deltas for every sample. This calibration prevents decorator
work from being attributed to `RoleGAgent`, but the control is still a
synthetic process-level estimate, not production resource attribution.

## Findings

1. **Transaction count is the synthetic cost center.** A 128-chunk response
   creates 131 append transactions but only about 53 KB of protobuf data.
   Garnet store I/O is 64.4 ms p50 versus 0.38 ms InMemory. This points to
   transaction/script round trips, not payload bytes. The 188.7 ms largest
   Garnet sample also shows this local setup is too variable for an SLO.
2. **Snapshot save is not the dominant cost.** Long text makes two 486-byte
   snapshots and deletes 95 events. Garnet compaction is 1.05 ms p50; typed
   InMemory snapshot save is below 0.01 ms. Orleans snapshot persistence was
   not measured.
3. **Chunk count extends one mailbox turn.** The harness deliberately awaits
   one chat dispatch, so occupancy is one and queued depth is zero. The long
   Garnet turn occupies the actor for 73.2 ms p50 before provider network/model
   latency is added.
4. **Recovery redo is semantic, not identity reuse.** Tested fences produced
   zero progress event-ID and sequence overlap. Final full state-event sets were
   identical across append ledger, durable readback, and committed publication
   for all 72 adapter/fence samples. Each injected attempt had one generated
   but intentionally uncommitted tail; every successful recovery generated
   boundary and final user-visible text/usage fact was present. Payload redo
   still equalled all phase-1 progress: 3, 11, and 23 events.
5. **Tool boundaries remain recovery-sensitive.** One tool call is seven
   append transactions and three calls are thirteen. #3138 must define
   intent/completion/outcome-uncertain recovery before performance work can
   combine or remove those facts.

## Semantic constraints

This measurement does not reclassify text, reasoning, media, tool lifecycle,
usage, or terminal records. They remain typed committed facts consumed through
the single committed-state projection path. Any future transient/coalesced
proposal must change the canonical typed contract explicitly, preserve CQRS
and AGUI observation through one authoritative chain, and state its loss/redo
window.

The measurement must not change:

- tool intent/admission, completion receipt, and outcome-uncertain policy from
  #3138;
- terminal completion and completion-notification facts;
- publication checkpoint and compaction fence from #3139;
- committed source version used by projection/read models.

## Limitations

- Garnet ran locally over Docker loopback, not production topology. No
  production SLO, concurrency, or capacity evidence was available.
- Snapshot persistence used the typed InMemory snapshot store for both
  adapters; the production Orleans snapshot backend was not measured.
- Provider SDK parsing, network latency, model think time, and provider
  backpressure are intentionally excluded.
- Recovery uses deterministic append-failure injection, not OS/container
  termination. Fresh final durable readback, committed-publication event-ID
  reconciliation, attempt-scoped generated evidence, and current-state
  materialization verify the tested adapter state, not host crash mechanics.
- One actor turn at a time measures head-of-line occupancy, not multi-producer
  mailbox saturation.
- CPU/allocation are process-level matched controls. Negative signed deltas and
  large maxima quantify remaining scheduler/GC noise; rerun on
  production-representative hardware before setting a benefit target.

## Reproduction

Use `tools/measurements/Aevatar.RoleStreamingWriteAmplification/README.md`.
Garnet must use the checked-in pinned digest with Lua and Lua transaction mode;
the harness rejects missing/mismatched identity or capabilities before warmup.
