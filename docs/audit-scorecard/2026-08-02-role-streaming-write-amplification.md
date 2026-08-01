---
title: RoleGAgent streaming write-amplification measurement
status: complete
owner: codex
issue: 3146
source_commit: 362a12b3ee5739fd5781488545a134351c2fc08e
---

# RoleGAgent streaming write-amplification measurement

## Decision

**No-go on changing commit boundaries now.** The benchmark confirms linear
write amplification and a measurable transaction cost on Garnet, but it does
not establish an observed production capacity, latency, or storage bottleneck.
Changing durability semantics would therefore spend recovery and observation
correctness without a production-backed benefit target.

Keep the current typed committed facts and the single committed-state
projection path. If production telemetry later shows an SLO or capacity
violation, open a linked implementation issue only after #3138 fixes the tool
intent/completion recovery policy and #3139 fixes publication/compaction
fences. That issue must carry this baseline plus an explicit benefit threshold
and maximum loss/redo window.

Raw samples and all distributions are checked in at
[`raw/2026-08-02-role-streaming-write-amplification.json`](raw/2026-08-02-role-streaming-write-amplification.json).

## What was measured

The harness executes the real `RoleGAgent` streaming handler inside one
`LocalActor` mailbox turn. The normal event-sourcing behavior builds typed
`StateEvent` protobuf records, publishes the normal committed-state facts, and
runs the configured snapshot/compaction decision after every commit. Only
harness-owned decorators record calls, bytes, and elapsed time.

Configuration:

- source commit `362a12b3ee5739fd5781488545a134351c2fc08e`;
- macOS 26.3 arm64, .NET 10.0.3, workstation GC, 12 logical processors;
- 2 warmups and 12 measured samples per workload and adapter;
- snapshot interval 50, compaction enabled, retain 5 events;
- InMemory event store and Garnet 2.1.0 in Docker over loopback;
- deterministic provider shapes for text, reasoning, tool deltas, media,
  terminal, cancellation, failure, and recovery;
- low-cardinality dimensions are only adapter and workload. Actor/session IDs
  are unique sample data, not metric labels.

Percentiles use nearest rank. With 12 samples, p95 and p99 both select the
maximum sample; they are reproducibility/tail-direction indicators, not
production percentile estimates.

## Core results

Values use `p50/p95/p99`. Bytes and append counts are deterministic except for
protobuf ID lengths, so the table reports their p50.

### InMemory event store

| Workload | Appends | Bytes | Snapshot saves / deleted events | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,826 | 0 / 0 | 0.022/0.056/0.056 | 0.131/0.291/0.291 | 0.478/1.008/1.008 |
| long text, 128 chunks | 131 | 53,009 | 2 / 95 | 0.476/0.584/0.584 | 0.146/0.211/0.211 | 5.510/9.548/9.548 |
| reasoning + text | 59 | 21,882 | 1 / 45 | 0.193/0.252/0.252 | 0.112/0.397/0.397 | 2.128/2.713/2.713 |
| single tool call | 7 | 3,452 | 0 / 0 | 0.017/0.031/0.031 | 0.241/0.409/0.409 | 0.440/0.636/0.636 |
| three tool calls | 13 | 6,761 | 0 / 0 | 0.029/0.033/0.033 | 0.373/1.564/1.564 | 0.648/1.856/1.856 |
| four media parts | 11 | 5,441 | 0 / 0 | 0.024/0.037/0.037 | 0.098/0.142/0.142 | 0.497/0.642/0.642 |
| terminal only | 3 | 1,585 | 0 / 0 | 0.007/0.009/0.009 | n/a | 0.184/0.247/0.247 |
| cancellation | 5 | 2,145 | 0 / 0 | 0.026/0.050/0.050 | 0.145/0.364/0.364 | 17.441/17.818/17.818 |
| provider failure | 6 | 2,584 | 0 / 0 | 0.018/0.022/0.022 | 0.116/0.152/0.152 | 0.429/0.526/0.526 |
| crash recovery | 49 attempts | 16,705 committed | 0 / 0 | 0.153/0.187/0.187 | 0.112/0.148/0.148 | 2.539/4.426/4.426 |

### Garnet event store

| Workload | Appends | Bytes | Snapshot saves / deleted events | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7 | 2,826 | 0 / 0 | 4.362/9.678/9.678 | 1.735/4.400/4.400 | 5.298/10.915/10.915 |
| long text, 128 chunks | 131 | 53,009 | 2 / 95 | 80.263/129.199/129.199 | 1.380/3.680/3.680 | 90.438/145.105/145.105 |
| reasoning + text | 59 | 21,882 | 1 / 45 | 47.359/110.979/110.979 | 1.911/5.550/5.550 | 51.874/119.584/119.584 |
| single tool call | 7 | 3,452 | 0 / 0 | 4.126/6.248/6.248 | 2.770/4.299/4.299 | 4.850/7.361/7.361 |
| three tool calls | 13 | 6,761 | 0 / 0 | 5.382/6.851/6.851 | 3.961/5.689/5.689 | 6.227/8.077/8.077 |
| four media parts | 11 | 5,441 | 0 / 0 | 4.879/6.431/6.431 | 1.089/2.635/2.635 | 5.640/8.616/8.616 |
| terminal only | 3 | 1,581 | 0 / 0 | 1.421/2.495/2.495 | n/a | 1.711/3.014/3.014 |
| cancellation | 5 | 2,145 | 0 / 0 | 3.668/7.135/7.135 | 1.315/3.118/3.118 | 18.282/19.981/19.981 |
| provider failure | 6 | 2,584 | 0 / 0 | 6.300/9.692/9.692 | 1.958/3.702/3.702 | 6.805/10.355/10.355 |
| crash recovery | 49 attempts | 16,705 committed | 0 / 0 | 21.994/40.132/40.132 | 1.117/1.416/1.416 | 24.721/44.548/44.548 |

## Resource curve

The following values are `p50/p95/p99` for CPU milliseconds and allocated
bytes. Managed-heap and working-set deltas remain in the raw file because their
short-window values are dominated by GC scheduling and page allocation.

| Adapter / workload | CPU ms | Allocated bytes |
| --- | ---: | ---: |
| InMemory / short text | 1.067/1.728/1.728 | 92,960/94,744/94,744 |
| InMemory / long text | 9.419/14.946/14.946 | 1,247,536/1,252,040/1,252,040 |
| InMemory / reasoning + text | 4.168/5.626/5.626 | 573,504/574,384/574,384 |
| InMemory / single tool | 0.922/1.211/1.211 | 113,288/114,328/114,328 |
| InMemory / three tools | 1.275/2.721/2.721 | 186,488/204,424/204,424 |
| InMemory / media | 1.050/1.570/1.570 | 137,048/137,128/137,128 |
| InMemory / terminal | 0.413/0.531/0.531 | 59,920/59,976/59,976 |
| InMemory / cancellation | 1.140/19.987/19.987 | 93,904/94,552/94,552 |
| InMemory / provider failure | 0.793/0.903/0.903 | 100,984/101,032/101,032 |
| InMemory / crash recovery | 4.636/6.640/6.640 | 623,600/636,504/636,504 |
| Garnet / short text | 3.876/12.323/12.323 | 118,704/120,488/120,488 |
| Garnet / long text | 73.605/175.014/175.014 | 1,727,048/1,732,944/1,732,944 |
| Garnet / reasoning + text | 34.860/139.145/139.145 | 787,920/790,840/790,840 |
| Garnet / single tool | 4.543/5.870/5.870 | 140,336/157,464/157,464 |
| Garnet / three tools | 4.903/14.820/14.820 | 234,248/237,552/237,552 |
| Garnet / media | 4.610/15.123/15.123 | 179,328/185,184/185,184 |
| Garnet / terminal | 1.489/2.518/2.518 | 74,984/74,984/74,984 |
| Garnet / cancellation | 3.455/22.971/22.971 | 114,360/114,368/114,368 |
| Garnet / provider failure | 10.176/16.102/16.102 | 124,048/140,632/140,632 |
| Garnet / crash recovery | 23.027/66.963/66.963 | 800,104/806,944/806,944 |

## Findings

1. **Transaction count is the dominant variable.** A 128-chunk response
   creates 131 append transactions but only about 53 KB of committed protobuf
   data. Garnet I/O is 80 ms p50 and 129 ms at the largest sample, while the
   same InMemory I/O is below 1 ms. This points to round trips/script execution,
   not bytes, as the current synthetic cost center.
2. **Snapshot save is not the cost center in this run.** Long text triggers two
   snapshots and removes 95 events; reasoning + text triggers one and removes
   45. Typed state serialization plus InMemory snapshot save remains below
   0.04 ms. Garnet compaction is 1.25 ms p50 for long text and 0.70 ms p50 for
   reasoning plus text; the production Orleans snapshot backend was not
   measured.
3. **Mailbox growth is not caused by chunks.** Every sample is one awaited chat
   dispatch, so maximum in-flight-plus-queued occupancy is one and queued depth
   is zero. The relevant actor risk is head-of-line time: the long Garnet turn
   occupies the actor for 90 ms p50 and 145 ms at the largest sample before any
   real provider latency is added.
4. **Crash recovery currently re-sends committed progress.** With the failure
   fence after 12 successful turn appends, recovery restarts the provider and
   can repeat 11 already committed progress events, about 3.5 KB. The harness
   observed no loss of those committed facts. This is a measured existing redo
   window, not permission to weaken tool or terminal boundaries.
5. **Tool boundaries are small in count but semantically expensive.** One tool
   call is seven append transactions and three calls are thirteen. #3138 must
   define intent/completion/outcome-uncertain recovery before any performance
   work can combine or remove these facts.

## Stable facts versus execution signals

This measurement does not reclassify any event. Current text, reasoning,
media, tool lifecycle, usage, and terminal records remain typed committed facts
consumed through the single committed-state projection path. A future proposal
to make any of them transient must first change the typed canonical contract,
state the user-visible replay consequence, and prove that CQRS and AGUI still
observe one authoritative chain. Performance code must not make that semantic
decision implicitly.

The recovery-sensitive boundaries that cannot be changed by this issue are:

- tool intent/admission, completion receipt, and outcome-uncertain policy from
  #3138;
- terminal session completion and completion notification facts;
- publication checkpoint and compaction fence from #3139;
- committed source version used by projection/read models.

## Limitations

- Garnet ran in a local Docker container over loopback, not in the production
  network/storage topology. No production SLO, concurrency, or capacity data
  was available.
- The production-representative adapter is the Garnet event store. Snapshot
  persistence used the InMemory typed snapshot store; the Orleans runtime
  snapshot backend was not measured.
- Provider shapes are deterministic and provider-agnostic. Vendor SDK parsing,
  network latency, model think time, and provider backpressure are excluded on
  purpose so persistence cost is visible.
- The harness serializes one actor turn at a time. It measures occupancy for
  that contract, not a multi-producer mailbox saturation curve.
- CPU, managed heap, and working set are process-level deltas. The raw results
  should be rerun on production-like hardware before setting a benefit target.

## Reproduction

Run the checked-in tool described in
`tools/measurements/Aevatar.RoleStreamingWriteAmplification/README.md`. Garnet
must enable Lua and Lua transaction mode because the production adapter uses
atomic append and compaction scripts. The checked-in raw file was produced by
the exact `--adapter all` command in that README; the temporary container was
removed after measurement.
