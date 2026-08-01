---
title: RoleGAgent streaming write-amplification measurement
status: complete
owner: codex
issue: 3146
source_commit: 84570ce43860ccace9604c0c970737b77604fda4
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

- source commit `84570ce43860ccace9604c0c970737b77604fda4`;
- macOS 26.3 arm64, .NET 10.0.3, workstation GC, 12 logical processors;
- 2 warmups and 12 measured samples per workload and adapter;
- snapshot interval 50, compaction enabled, 5 retained events;
- crash recovery append fences 4, 12, and 24;
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
| short text | 7 | 2,846 | 0 / 0 | 0.017/0.022/0.022 | 0.102/0.149/0.149 | 0.359/0.497/0.497 |
| long text, 128 chunks | 131 | 53,277 | 2 / 95 | 0.197/0.389/0.389 | 0.122/0.149/0.149 | 3.637/5.073/5.073 |
| reasoning + text | 59 | 21,946 | 1 / 45 | 0.098/0.940/0.940 | 0.113/0.171/0.171 | 1.737/3.144/3.144 |
| single tool call | 7 | 3,464 | 0 / 0 | 0.015/0.018/0.018 | 0.209/0.315/0.315 | 0.394/0.547/0.547 |
| three tool calls | 13 | 6,779 | 0 / 0 | 0.024/0.029/0.029 | 0.343/0.574/0.574 | 0.659/1.112/1.112 |
| four media parts | 11 | 5,465 | 0 / 0 | 0.023/0.025/0.025 | 0.090/0.116/0.116 | 0.478/0.543/0.543 |
| terminal only | 3 | 1,597 | 0 / 0 | 0.007/0.013/0.013 | n/a | 0.186/0.251/0.251 |
| cancellation | 5 | 2,161 | 0 / 0 | 0.028/0.036/0.036 | 0.185/0.245/0.245 | 17.421/17.766/17.766 |
| provider failure | 6 | 2,601 | 0 / 0 | 0.014/0.017/0.017 | 0.116/0.212/0.212 | 0.420/2.063/2.063 |

### Garnet event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,846 | 0 / 0 | 3.988/6.529/6.529 | 1.435/2.251/2.251 | 4.756/7.462/7.462 |
| long text, 128 chunks | 131 | 53,243 | 2 / 95 | 132.950/1,351.656/1,351.656 | 2.597/21.782/21.782 | 150.719/1,368.912/1,368.912 |
| reasoning + text | 59 | 21,999 | 1 / 45 | 407.719/827.336/827.336 | 8.467/85.558/85.558 | 419.426/835.614/835.614 |
| single tool call | 7 | 3,472 | 0 / 0 | 39.419/164.527/164.527 | 26.819/83.042/83.042 | 44.315/166.102/166.102 |
| three tool calls | 13 | 6,793 | 0 / 0 | 118.935/301.453/301.453 | 65.617/292.194/292.194 | 141.314/307.729/307.729 |
| four media parts | 11 | 5,477 | 0 / 0 | 129.019/511.848/511.848 | 18.385/304.169/304.169 | 132.573/518.301/518.301 |
| terminal only | 3 | 1,597 | 0 / 0 | 22.431/257.957/257.957 | n/a | 22.996/258.649/258.649 |
| cancellation | 3 | 1,529 | 0 / 0 | 29.196/89.565/89.565 | 11.483/15.526/15.526 | 31.313/90.221/90.221 |
| provider failure | 6 | 2,601 | 0 / 0 | 59.775/333.395/333.395 | 19.723/130.768/130.768 | 60.741/334.743/334.743 |

## Recovery sweep

The comparison uses append-acknowledged committed progress, adapter durable
readback before reactivation, and actual committed-state stream observations.
Payload overlap hashes the typed progress payload after removing session ID and
sequence. Thus it detects repeated content even when recovery assigns new
event IDs and monotonic sequences.

All counts below are deterministic p50 values. Missing/loss columns are zero
for every sample on both adapters, not only at p50.

| Adapter | Fence | Phase-1 committed | Recovery committed | Event ID overlap | Sequence overlap | Payload redo / bytes | Durable missing | Phase-1 projection missing | Final projection missing | Commit-ledger loss |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 3 | 33 | 0 | 0 | 3 / 1,009 | 0 | 0 | 0 | 0 |
| InMemory | 12 | 11 | 33 | 0 | 0 | 11 / 3,597 | 0 | 0 | 0 | 0 |
| InMemory | 24 | 23 | 33 | 0 | 0 | 23 / 7,473 | 0 | 0 | 0 | 0 |
| Garnet | 4 | 3 | 33 | 0 | 0 | 3 / 1,009 | 0 | 0 | 0 | 0 |
| Garnet | 12 | 11 | 33 | 0 | 0 | 11 / 3,608 | 0 | 0 | 0 | 0 |
| Garnet | 24 | 23 | 33 | 0 | 0 | 23 / 7,496 | 0 | 0 | 0 | 0 |

These are three observed windows, not a maximum. At each tested fence, all
pre-failure progress was durable and projection-visible. Recovery did not reuse
event identity or sequence, but it repeated every previously emitted payload.
That is user-visible redo risk even though committed facts were not lost.

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
| InMemory / short text | 1.030/1.213 | 105,616/106,288 |
| InMemory / long text | 12.380/14.709 | 1,573,312/1,578,328 |
| InMemory / reasoning + text | 5.805/7.643 | 713,320/717,864 |
| InMemory / single tool | 1.175/1.343 | 127,464/145,208 |
| InMemory / three tools | 1.794/2.915 | 217,296/218,080 |
| InMemory / media | 1.409/2.454 | 162,008/168,352 |
| InMemory / terminal | 0.575/0.904 | 63,160/63,576 |
| InMemory / cancellation | 1.653/4.603 | 100,576/112,472 |
| InMemory / provider failure | 1.310/1.559 | 110,136/110,520 |
| Garnet / short text | 5.123/7.345 | 128,680/130,880 |
| Garnet / long text | 92.702/584.542 | 2,049,192/2,054,080 |
| Garnet / reasoning + text | 38.715/169.379 | 915,336/916,888 |
| Garnet / single tool | 5.269/14.585 | 150,056/168,056 |
| Garnet / three tools | 8.891/42.408 | 258,776/261,752 |
| Garnet / media | 8.433/23.654 | 199,360/199,744 |
| Garnet / terminal | 2.051/3.132 | 75,040/75,248 |
| Garnet / cancellation | 2.660/4.228 | 52,176/115,936 |
| Garnet / provider failure | 3.906/8.385 | 128,512/130,136 |

Gross-minus-control allocation delta is about 5.2-12.9 KB for InMemory normal
workloads and -3.8-12.3 KB for Garnet. CPU deltas likewise cross
zero for several workloads and have noisy maxima. The raw file retains gross,
net, and signed deltas for every sample. This calibration prevents decorator
work from being attributed to `RoleGAgent`, but the control is still a
synthetic process-level estimate, not production resource attribution.

## Findings

1. **Transaction count is the synthetic cost center.** A 128-chunk response
   creates 131 append transactions but only about 53 KB of protobuf data.
   Garnet store I/O is 133.0 ms p50 versus 0.20 ms InMemory. This points to
   transaction/script round trips, not payload bytes. The 1.35-second largest
   Garnet sample also shows this local setup is too variable for an SLO.
2. **Snapshot save is not the dominant cost.** Long text makes two 486-byte
   snapshots and deletes 95 events. Garnet compaction is 2.44 ms p50; typed
   InMemory snapshot save is below 0.01 ms. Orleans snapshot persistence was
   not measured.
3. **Chunk count extends one mailbox turn.** The harness deliberately awaits
   one chat dispatch, so occupancy is one and queued depth is zero. The long
   Garnet turn occupies the actor for 150.7 ms p50 before provider network/model
   latency is added.
4. **Recovery redo is semantic, not identity reuse.** Tested fences produced
   zero event-ID and sequence overlap, zero committed/projection loss, and
   payload redo equal to all phase-1 progress: 3, 11, and 23 events.
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
  termination. Durable readback verifies the adapter accepted phase-1 events.
- One actor turn at a time measures head-of-line occupancy, not multi-producer
  mailbox saturation.
- CPU/allocation are process-level matched controls. Negative signed deltas and
  large maxima quantify remaining scheduler/GC noise; rerun on
  production-representative hardware before setting a benefit target.

## Reproduction

Use `tools/measurements/Aevatar.RoleStreamingWriteAmplification/README.md`.
Garnet must use the checked-in pinned digest with Lua and Lua transaction mode;
the harness rejects missing/mismatched identity or capabilities before warmup.
