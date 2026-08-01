---
title: RoleGAgent streaming write-amplification measurement
status: complete
owner: codex
issue: 3146
source_commit: 86071cc86fd4c881648206750fc365d657194a96
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
real `CommittedStateEventPublished` envelopes, and preserves production event
sourcing, snapshot, and compaction behavior. Harness decorators record scalar
counts, protobuf sizes, and I/O duration; production code is unchanged.

Configuration:

- source commit `86071cc86fd4c881648206750fc365d657194a96`;
- macOS 26.3 arm64, .NET 10.0.3, workstation GC, 12 logical processors;
- 2 warmups and 12 measured samples per workload and adapter;
- snapshot interval 50, compaction enabled, 5 retained events;
- crash recovery append fences 4, 12, and 24;
- recovery uses 22 text chunks, keeping final three-way reconciliation below
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
| short text | 7 | 2,846 | 0 / 0 | 0.022/0.078/0.078 | 0.194/2.557/2.557 | 0.579/3.410/3.410 |
| long text, 128 chunks | 131 | 53,145 | 2 / 95 | 0.393/1.024/1.024 | 0.261/0.783/0.783 | 7.561/21.970/21.970 |
| reasoning + text | 59 | 22,006 | 1 / 45 | 0.217/1.003/1.003 | 0.344/0.650/0.650 | 4.327/6.679/6.679 |
| single tool call | 7 | 3,472 | 0 / 0 | 0.052/0.175/0.175 | 1.051/2.055/2.055 | 1.837/4.043/4.043 |
| three tool calls | 13 | 6,793 | 0 / 0 | 0.073/0.413/0.413 | 1.085/1.999/1.999 | 1.811/3.772/3.772 |
| four media parts | 11 | 5,465 | 0 / 0 | 0.049/0.193/0.193 | 0.232/5.322/5.322 | 1.634/15.644/15.644 |
| terminal only | 3 | 1,597 | 0 / 0 | 0.012/0.040/0.040 | n/a | 0.454/1.830/1.830 |
| cancellation | 5 | 2,161 | 0 / 0 | 0.036/2.598/2.598 | 0.242/2.959/2.959 | 17.831/22.910/22.910 |
| provider failure | 6 | 2,594 | 0 / 0 | 0.035/0.090/0.090 | 0.300/3.566/3.566 | 1.199/4.197/4.197 |

### Garnet event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,846 | 0 / 0 | 49.135/278.364/278.364 | 12.464/66.882/66.882 | 51.654/286.927/286.927 |
| long text, 128 chunks | 131 | 53,277 | 2 / 95 | 249.157/409.989/409.989 | 3.618/6.401/6.401 | 265.745/435.356/435.356 |
| reasoning + text | 59 | 22,006 | 1 / 45 | 267.059/852.242/852.242 | 10.360/84.235/84.235 | 274.429/860.924/860.924 |
| single tool call | 7 | 3,472 | 0 / 0 | 32.292/74.625/74.625 | 14.316/67.456/67.456 | 33.354/75.719/75.719 |
| three tool calls | 13 | 6,784 | 0 / 0 | 80.799/355.067/355.067 | 42.733/218.407/218.407 | 83.102/356.788/356.788 |
| four media parts | 11 | 5,477 | 0 / 0 | 16.463/156.709/156.709 | 3.940/111.149/111.149 | 18.012/158.731/158.731 |
| terminal only | 3 | 1,597 | 0 / 0 | 3.666/6.072/6.072 | n/a | 4.029/6.560/6.560 |
| cancellation | 5 | 2,161 | 0 / 0 | 6.485/10.441/10.441 | 3.036/3.387/3.387 | 18.694/25.447/25.447 |
| provider failure | 6 | 2,601 | 0 / 0 | 7.619/9.745/9.745 | 2.726/5.449/5.449 | 8.315/12.255/12.255 |

## Recovery sweep

The comparison uses an append-acknowledged ledger, a fresh final
`baseStore.GetEventsAsync(actorId)` readback after recovery, and actual
`CommittedStateEventPublished` observations. Actor initialization is observed
and included in all three final sets, while turn resource metrics reset after
initialization. The raw schema is version 3. Every sample fails closed on four
full-state-event ID differences: ledger to durable missing, durable to ledger
unexpected, durable to projection missing, and projection to durable
unexpected. Payload overlap separately hashes typed progress after removing
session ID and sequence.

All counts below are deterministic p50 values. Missing/loss columns are zero
for every sample on both adapters, not only at p50.

| Adapter | Fence | Phase-1 / recovery progress | ID / sequence overlap | Payload redo / bytes | Final ledger / durable / projection | L→D | D→L | D→P | P→D |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 3 / 23 | 0 / 0 | 3 / 1,006 | 29 / 29 / 29 | 0 | 0 | 0 | 0 |
| InMemory | 12 | 11 / 23 | 0 / 0 | 11 / 3,608 | 37 / 37 / 37 | 0 | 0 | 0 | 0 |
| InMemory | 24 | 23 / 23 | 0 / 0 | 23 / 7,473 | 49 / 49 / 49 | 0 | 0 | 0 | 0 |
| Garnet | 4 | 3 / 23 | 0 / 0 | 3 / 1,009 | 29 / 29 / 29 | 0 | 0 | 0 | 0 |
| Garnet | 12 | 11 / 23 | 0 / 0 | 11 / 3,608 | 37 / 37 / 37 | 0 | 0 | 0 | 0 |
| Garnet | 24 | 23 / 23 | 0 / 0 | 23 / 7,496 | 49 / 49 / 49 | 0 | 0 | 0 | 0 |

These are three observed windows, not a maximum. At each tested fence, the
final append ledger, durable readback, and projection event IDs matched exactly.
Recovery did not reuse progress identity or sequence, but it repeated every
previously emitted payload. That is user-visible redo risk even though the
tested committed facts were not lost.

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
| InMemory / short text | 0.701/1.719 | 104,552/106,272 |
| InMemory / long text | 7.164/16.306 | 1,557,688/1,561,144 |
| InMemory / reasoning + text | 3.326/6.901 | 706,952/709,768 |
| InMemory / single tool | 1.679/2.612 | 125,992/143,768 |
| InMemory / three tools | 2.484/3.957 | 214,624/215,880 |
| InMemory / media | 1.258/3.786 | 160,272/166,616 |
| InMemory / terminal | 0.828/2.298 | 62,672/62,904 |
| InMemory / cancellation | 2.428/18.539 | 99,992/100,328 |
| InMemory / provider failure | 1.158/2.445 | 109,512/109,936 |
| Garnet / short text | 9.000/102.627 | 128,576/131,072 |
| Garnet / long text | 115.928/247.332 | 2,034,696/2,133,448 |
| Garnet / reasoning + text | 37.698/52.931 | 916,192/918,072 |
| Garnet / single tool | 4.540/11.050 | 151,856/167,472 |
| Garnet / three tools | 9.248/43.678 | 259,032/262,328 |
| Garnet / media | 10.219/12.457 | 200,032/206,088 |
| Garnet / terminal | 2.678/3.564 | 75,080/75,464 |
| Garnet / cancellation | 5.418/12.611 | 114,888/116,616 |
| Garnet / provider failure | 5.426/6.755 | 128,800/130,312 |

Gross-minus-control allocation delta spans about 4.9-31.2 KB for InMemory
normal workloads and -2.9-32.2 KB for Garnet. CPU deltas likewise cross
zero for several workloads and have noisy maxima. The raw file retains gross,
net, and signed deltas for every sample. This calibration prevents decorator
work from being attributed to `RoleGAgent`, but the control is still a
synthetic process-level estimate, not production resource attribution.

## Findings

1. **Transaction count is the synthetic cost center.** A 128-chunk response
   creates 131 append transactions but only about 53 KB of protobuf data.
   Garnet store I/O is 249.2 ms p50 versus 0.39 ms InMemory. This points to
   transaction/script round trips, not payload bytes. The 410.0 ms largest
   Garnet sample also shows this local setup is too variable for an SLO.
2. **Snapshot save is not the dominant cost.** Long text makes two 486-byte
   snapshots and deletes 95 events. Garnet compaction is 3.66 ms p50; typed
   InMemory snapshot save is below 0.01 ms. Orleans snapshot persistence was
   not measured.
3. **Chunk count extends one mailbox turn.** The harness deliberately awaits
   one chat dispatch, so occupancy is one and queued depth is zero. The long
   Garnet turn occupies the actor for 265.7 ms p50 before provider network/model
   latency is added.
4. **Recovery redo is semantic, not identity reuse.** Tested fences produced
   zero progress event-ID and sequence overlap. Final full state-event sets were
   identical across append ledger, durable readback, and projection for all 72
   adapter/fence samples; payload redo still equalled all phase-1 progress: 3,
   11, and 23 events.
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
  termination. Fresh final durable readback and three-way event-ID
  reconciliation verify the tested adapter state, not host crash mechanics.
- One actor turn at a time measures head-of-line occupancy, not multi-producer
  mailbox saturation.
- CPU/allocation are process-level matched controls. Negative signed deltas and
  large maxima quantify remaining scheduler/GC noise; rerun on
  production-representative hardware before setting a benefit target.

## Reproduction

Use `tools/measurements/Aevatar.RoleStreamingWriteAmplification/README.md`.
Garnet must use the checked-in pinned digest with Lua and Lua transaction mode;
the harness rejects missing/mismatched identity or capabilities before warmup.
