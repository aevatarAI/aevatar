---
title: RoleGAgent streaming write-amplification measurement
status: complete
owner: codex
issue: 3146
source_commit: 80887623f7e8b418e3fb90024ff9bce12ee46670
---

# RoleGAgent streaming write-amplification measurement

## Decision

**No-go on changing commit boundaries now.** The benchmark confirms linear
write amplification and measurable Garnet transaction cost, but it does not
establish a production capacity, latency, or storage SLO violation. Weakening
typed committed facts would spend recovery and observation correctness without
a production-backed benefit target.

Keep the current `RoleGAgent` durability boundaries and the single committed
state projection path. The measured code includes #3138's typed recovery
checkpoints and #3139's runtime-owned publication/compaction fence. A future
implementation issue still requires production telemetry, an explicit benefit
threshold, and preservation of both contracts.

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

- source commit `80887623f7e8b418e3fb90024ff9bce12ee46670`;
- Program SHA-256 `b20be0740f1b25d246693ac1014f8676101e68dc5596e6ed4a205230a07ebb78`;
- config SHA-256 `e79890b6d747fb51898ff47235588d70eeeb6cbb8dc8e3fd4674f3fb41f86a3b`;
- macOS 26.3 arm64, .NET 10.0.3, workstation GC, 12 logical processors;
- 2 warmups and 12 measured samples per workload and adapter;
- snapshot interval 50, compaction enabled, 5 retained events;
- crash recovery append fences 4, 12, and 24;
- recovery uses 23 text chunks so fence 24 reaches snapshot version 50,
  compacts through the publication-fenced version 45, and verifies the retained
  tail; the 128-chunk workload measures steady-state compaction separately;
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

Values are `p50/p95/p99` for every displayed metric. Completion ends at the
actor handler boundary and excludes deactivation and recovery-validation drain
barriers.

### InMemory event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7/7/7 | 2,878/2,898/2,898 | 0/0/0 / 0/0/0 | 0.0252/0.0952/0.0952 | 0.2359/1.1949/1.1949 | 1.0310/1.6321/1.6321 |
| long text, 128 chunks | 131/131/131 | 53,317/53,585/53,585 | 2/2/2 / 95/95/95 | 0.5114/7.3959/7.3959 | 0.2295/0.6481/0.6481 | 14.7766/21.4282/21.4282 |
| reasoning + text | 59/59/59 | 22,046/22,110/22,110 | 1/1/1 / 45/45/45 | 0.2154/0.4808/0.4808 | 0.3742/0.8637/0.8637 | 4.5463/9.2725/9.2725 |
| single tool call | 9/9/9 | 6,948/6,983/6,983 | 0/0/0 / 0/0/0 | 0.0311/0.0755/0.0755 | 0.8153/1.3687/1.3687 | 1.4367/2.0432/2.0432 |
| three tool calls | 17/17/17 | 22,470/22,560/22,560 | 0/0/0 / 0/0/0 | 0.0515/0.1722/0.1722 | 1.3748/4.4493/4.4493 | 2.1330/5.0625/5.0625 |
| four media parts | 11/11/11 | 5,517/5,554/5,554 | 0/0/0 / 0/0/0 | 0.0285/0.0575/0.0575 | 0.1880/0.4745/0.4745 | 0.8710/1.4842/1.4842 |
| terminal only | 3/3/3 | 1,637/1,649/1,649 | 0/0/0 / 0/0/0 | 0.0098/0.0259/0.0259 | n/a | 0.3754/0.6599/0.6599 |
| cancellation | 5/5/5 | 2,209/2,224/2,224 | 0/0/0 / 0/0/0 | 0.0345/0.0635/0.0635 | 0.2499/0.5505/0.5505 | 16.5327/17.0362/17.0362 |
| provider failure | 6/6/6 | 2,634/2,651/2,651 | 0/0/0 / 0/0/0 | 0.0191/0.0496/0.0496 | 0.2333/0.3041/0.3041 | 0.6964/0.9046/0.9046 |

### Garnet event store

| Workload | Appends | Bytes | Snapshot saves / deleted | Store I/O ms | First output ms | Completion ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| short text | 7/7/7 | 2,878/2,906/2,906 | 0/0/0 / 0/0/0 | 4.2262/7.7453/7.7453 | 1.8269/3.1220/3.1220 | 5.1940/9.3947/9.3947 |
| long text, 128 chunks | 131/131/131 | 53,317/53,585/53,585 | 2/2/2 / 95/95/95 | 67.7159/87.1258/87.1258 | 1.4397/3.2698/3.2698 | 78.6963/98.9983/98.9983 |
| reasoning + text | 59/59/59 | 22,046/22,170/22,170 | 1/1/1 / 45/45/45 | 26.3611/33.8960/33.8960 | 1.3311/3.3872/3.3872 | 29.3472/36.7882/36.7882 |
| single tool call | 9/9/9 | 6,959/6,983/6,983 | 0/0/0 / 0/0/0 | 4.7181/5.9032/5.9032 | 4.0618/4.9367/4.9367 | 5.6912/7.1300/7.1300 |
| three tool calls | 17/17/17 | 22,446/22,560/22,560 | 0/0/0 / 0/0/0 | 7.3259/9.0548/9.0548 | 6.3859/9.0638/9.0638 | 8.7334/11.9876/11.9876 |
| four media parts | 11/11/11 | 5,517/5,554/5,554 | 0/0/0 / 0/0/0 | 5.3941/11.2019/11.2019 | 1.4435/3.3634/3.3634 | 6.2685/12.3596/12.3596 |
| terminal only | 3/3/3 | 1,637/1,649/1,649 | 0/0/0 / 0/0/0 | 1.5358/2.5629/2.5629 | n/a | 1.8606/2.9569/2.9569 |
| cancellation | 5/5/5 | 2,203/2,224/2,224 | 0/0/0 / 0/0/0 | 7.0980/16.2082/16.2082 | 2.6230/14.1068/14.1068 | 17.8385/20.9787/20.9787 |
| provider failure | 6/6/6 | 2,641/2,658/2,658 | 0/0/0 / 0/0/0 | 3.0256/7.2776/7.2776 | 1.2064/2.8144/2.8144 | 3.5721/7.8715/7.8715 |

### Snapshot and compaction distributions

The compact table below expands the persistence metrics that are easy to hide
behind a median-only summary. Every cell is `p50/p95/p99`; zeros are measured
results, not omitted values.

| Adapter / workload | Snapshot saves | Snapshot bytes | Snapshot ms | Compaction calls | Deleted events | Compaction ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory / short text | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / long text, 128 chunks | 2/2/2 | 568/570/570 | 0.0173/0.0407/0.0407 | 2/2/2 | 95/95/95 | 0.0098/0.0118/0.0118 |
| InMemory / reasoning + text | 1/1/1 | 280/281/281 | 0.0069/0.0351/0.0351 | 1/1/1 | 45/45/45 | 0.0036/0.0084/0.0084 |
| InMemory / single tool call | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / three tool calls | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / four media parts | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / terminal only | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / cancellation | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory / provider failure | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / short text | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / long text, 128 chunks | 2/2/2 | 568/570/570 | 0.0163/0.0275/0.0275 | 2/2/2 | 95/95/95 | 1.0606/1.8342/1.8342 |
| Garnet / reasoning + text | 1/1/1 | 280/281/281 | 0.0088/0.0174/0.0174 | 1/1/1 | 45/45/45 | 0.4419/0.7919/0.7919 |
| Garnet / single tool call | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / three tool calls | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / four media parts | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / terminal only | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / cancellation | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet / provider failure | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |

## Recovery sweep

The comparison uses provider-generated semantic evidence, the complete
append-acknowledged ledger, actual `CommittedStateEventPublished` observations,
the runtime-owned durable publication checkpoint, typed snapshots, fresh
durable-tail readback, a fresh activation, and an independently read
measurement-only current-state document. Actor initialization is included in
the complete ledger/publication sets. After compaction, durable readback is the
authoritative suffix rather than a second copy of deleted history. The raw
schema is version 5.

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

| Adapter | Fence | Phase generated / committed | Recovery generated / committed | Payload redo / bytes | Final ledger / durable tail / publication | Attempt tail | Recovery G→C / C→G | Materialized |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 3 / 2 | 24 / 24 | 3 / 1,006 | 30 / 30 / 30 | 1 | 0 / 0 | pass |
| InMemory | 12 | 11 / 10 | 24 / 24 | 11 / 3,608 | 39 / 39 / 39 | 1 | 0 / 0 | pass |
| InMemory | 24 | 23 / 22 | 24 / 24 | 23 / 7,496 | 50 / 5 / 50 | 1 | 0 / 0 | pass |
| Garnet | 4 | 3 / 2 | 24 / 24 | 3 / 1,009 | 30 / 30 / 30 | 1 | 0 / 0 | pass |
| Garnet | 12 | 11 / 10 | 24 / 24 | 11 / 3,608 | 38 / 38 / 38 | 1 | 0 / 0 | pass |
| Garnet | 24 | 23 / 22 | 24 / 24 | 23 / 7,496 | 50 / 5 / 50 | 1 | 0 / 0 | pass |

These are three observed windows, not a maximum. At every fence, the complete
append ledger and committed-publication identities matched. Before compaction,
durable readback matched both. At fence 24, the snapshot-covered 45-event prefix
was deleted and durable readback matched the continuous five- or six-event
suffix, depending on whether a post-snapshot completion committed. Recovery did
not reuse progress identity or sequence, but repeated every previously emitted
payload. The successful attempt's 23 text deltas plus usage matched the final
materialized session hashes for all 72 samples. The phase-one payload repeat is
still user-visible redo risk; no tested committed or final user-visible fact
was lost.

### Durable authority fence

Ranges below cover all 12 samples. In every row the publication checkpoint
equalled the latest store version/event ID, ledger equalled publication,
snapshot state matched the publication at that version, and fresh activation
reconstructed the latest publication state.

| Adapter | Fence | Store version | Snapshot | Compacted through | Published at compaction | Durable tail | Checkpoint version |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 30-31 | 0 | 0 | 0 | 30-31 | 30-31 |
| InMemory | 12 | 38-39 | 0 | 0 | 0 | 38-39 | 38-39 |
| InMemory | 24 | 50-51 | 50 | 45 | 50 | 5-6 | 50-51 |
| Garnet | 4 | 30 | 0 | 0 | 0 | 30 | 30 |
| Garnet | 12 | 38-39 | 0 | 0 | 0 | 38-39 | 38-39 |
| Garnet | 24 | 50-51 | 50 | 45 | 50 | 5-6 | 50-51 |

### Recovery resource distributions

The following tables retain the full `p50/p95/p99` distribution for every
crash-recovery fence. Append attempts include rejected and retried writes;
committed event/byte counts describe the final successful durable turn.

| Adapter | Fence | Append attempts | Committed events | Committed bytes | Event-store I/O ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 7/32/32 | 4/29/29 | 1,380/11,008/11,008 | 0.0767/0.1463/0.1463 |
| InMemory | 12 | 41/41/41 | 38/38/38 | 14,788/14,873/14,873 | 0.1539/0.2754/0.2754 |
| InMemory | 24 | 27/53/53 | 24/50/50 | 7,918/18,676/18,676 | 0.1418/0.2690/0.2690 |
| Garnet | 4 | 8/8/8 | 4/4/4 | 1,380/1,389/1,389 | 3.5091/4.6105/4.6105 |
| Garnet | 12 | 16/41/41 | 12/37/37 | 3,981/13,776/13,776 | 5.8385/14.1163/14.1163 |
| Garnet | 24 | 28/53/53 | 24/49/49 | 7,869/17,533/17,533 | 10.5933/22.7178/22.7178 |

Fence 24 always produced authoritative snapshot version 50 and compaction
through version 45. The distributions below count work inside the timed handler
window; snapshot/compaction completed after that window in some samples, so p50
is zero while the durable-authority table above remains the correctness proof.

| Adapter | Fence | Snapshot saves | Snapshot bytes | Snapshot ms | Compaction calls | Deleted events | Compaction ms |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory | 12 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| InMemory | 24 | 0/1/1 | 0/847/847 | 0/0.0064/0.0064 | 0/1/1 | 0/45/45 | 0/0.0043/0.0043 |
| Garnet | 4 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet | 12 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 | 0/0/0 |
| Garnet | 24 | 0/1/1 | 0/846/846 | 0/0.0041/0.0041 | 0/1/1 | 0/45/45 | 0/0.4366/0.4366 |

Publication checkpoint calls are real runtime durable-delivery operations over
the shared checkpoint store; failure-record counts were zero in every sample.

| Adapter | Fence | Loads | Advances | Serialized write bytes | Checkpoint ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 6/31/31 | 4/29/29 | 484/3,509/3,509 | 0.0095/0.0540/0.0540 |
| InMemory | 12 | 39/40/40 | 37/38/38 | 4,551/4,674/4,674 | 0.0577/0.0836/0.0836 |
| InMemory | 24 | 26/53/53 | 24/50/50 | 2,952/6,150/6,150 | 0.0355/0.0895/0.0895 |
| Garnet | 4 | 6/6/6 | 4/4/4 | 488/492/492 | 0.0087/0.0137/0.0137 |
| Garnet | 12 | 14/39/39 | 12/37/37 | 1,476/4,588/4,588 | 0.0128/0.0510/0.0510 |
| Garnet | 24 | 26/52/52 | 24/49/49 | 2,952/5,978/5,978 | 0.0241/0.0482/0.0482 |

Gross metrics include the measurement decorators; net CPU and allocation come
from the matched undecorated control turn with alternating execution order.

| Adapter | Fence | Gross CPU ms | Gross allocated bytes | Net CPU ms | Net allocated bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 4.087/7.662/7.662 | 301,360/721,440/721,440 | 3.550/7.212/7.212 | 222,952/593,088/593,088 |
| InMemory | 12 | 8.574/9.628/9.628 | 1,013,304/1,033,464/1,033,464 | 4.776/9.791/9.791 | 370,912/748,064/748,064 |
| InMemory | 24 | 9.114/13.368/13.368 | 996,000/1,449,232/1,449,232 | 7.705/11.975/11.975 | 592,816/968,072/968,072 |
| Garnet | 4 | 7.779/16.559/16.559 | 334,760/344,856/344,856 | 6.943/17.300/17.300 | 244,096/255,752/255,752 |
| Garnet | 12 | 9.442/19.169/19.169 | 657,224/1,176,816/1,176,816 | 8.159/24.470/24.470 | 427,192/885,576/885,576 |
| Garnet | 24 | 15.736/27.306/27.306 | 1,140,136/1,659,992/1,659,992 | 12.567/29.198/29.198 | 701,304/1,168,536/1,168,536 |

Heap and working-set deltas are gross process diagnostics. Mailbox occupancy is
in-flight plus queued turns and remains one because each recovery sample
dispatches a single turn at a time.

| Adapter | Fence | Managed heap bytes | Working set bytes | First output ms | Completion ms | Mailbox occupancy |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| InMemory | 4 | 301,792/737,256/737,256 | 0/196,608/196,608 | 0.2211/0.2886/0.2886 | 2.2755/3.8160/3.8160 | 1/1/1 |
| InMemory | 12 | 1,013,936/1,030,536/1,030,536 | 0/16,384/16,384 | 0.2073/0.2605/0.2605 | 3.6661/4.3677/4.3677 | 1/1/1 |
| InMemory | 24 | 998,520/1,440,696/1,440,696 | 0/360,448/360,448 | 0.2099/0.4978/0.4978 | 3.5470/5.8597/5.8597 | 1/1/1 |
| Garnet | 4 | 327,352/384,552/384,552 | 0/16,384/16,384 | 1.2941/1.7940/1.7940 | 5.3742/6.5944/6.5944 | 1/1/1 |
| Garnet | 12 | 651,648/671,040/671,040 | 0/32,768/32,768 | 1.0085/2.3351/2.3351 | 7.6605/16.5963/16.5963 | 1/1/1 |
| Garnet | 24 | 1,142,952/1,733,128/1,733,128 | 0/16,384/16,384 | 1.0944/1.4482/1.4482 | 13.0226/26.3610/26.3610 | 1/1/1 |

## Resource calibration

The append decorator no longer clones events. It records counts and
`CalculateSize()` scalar totals; event references are retained only for crash
reconciliation. Every instrumented sample is paired with a separate matched
control turn that removes append/snapshot decorators. Even iterations run the
control first and odd iterations run it second.

The following values are the matched-control and process-resource distributions
(`p50/p95/p99`). Allocation uses process-wide
`GC.GetTotalAllocatedBytes(true)`. Heap and working-set values are gross
process deltas because there is no meaningful per-turn subtraction contract;
mailbox occupancy is the measured maximum in-flight plus queued turns.

| Adapter / workload | Net CPU ms | Net allocated bytes | Heap delta bytes | Working-set delta bytes | Mailbox occupancy |
| --- | ---: | ---: | ---: | ---: | ---: |
| InMemory / short text | 0.754/9.649/9.649 | 122,528/128,512/128,512 | 131,584/139,680/139,680 | 0/131,072/131,072 | 1/1/1 |
| InMemory / long text | 13.975/21.856/21.856 | 1,850,896/1,856,152/1,856,152 | 1,946,560/1,965,960/1,965,960 | 16,384/311,296/311,296 | 1/1/1 |
| InMemory / reasoning + text | 4.134/9.440/9.440 | 840,408/843,320/843,320 | 888,192/896,368/896,368 | 0/163,840/163,840 | 1/1/1 |
| InMemory / single tool | 2.813/4.772/4.772 | 222,432/240,176/240,176 | 230,008/255,624/255,624 | 0/0/0 | 1/1/1 |
| InMemory / three tools | 4.196/9.667/9.667 | 559,864/567,112/567,112 | 572,176/584,392/584,392 | 0/0/0 | 1/1/1 |
| InMemory / media | 2.638/3.119/3.119 | 188,288/188,840/188,840 | 204,776/213,272/213,272 | 0/0/0 | 1/1/1 |
| InMemory / terminal | 0.913/1.075/1.075 | 72,376/72,664/72,664 | 81,856/90,280/90,280 | 0/0/0 | 1/1/1 |
| InMemory / cancellation | 2.081/19.439/19.439 | 117,624/117,816/117,816 | 122,864/139,472/139,472 | 0/1,343,488/1,343,488 | 1/1/1 |
| InMemory / provider failure | 1.689/3.748/3.748 | 130,688/152,888/152,888 | 145,688/156,008/156,008 | 0/147,456/147,456 | 1/1/1 |
| Garnet / short text | 4.567/6.276/6.276 | 146,480/148,440/148,440 | 155,392/172,296/172,296 | 0/114,688/114,688 | 1/1/1 |
| Garnet / long text | 79.617/285.201/285.201 | 2,351,776/2,361,664/2,361,664 | 2,416,544/2,497,288/2,497,288 | 49,152/1,327,104/1,327,104 | 1/1/1 |
| Garnet / reasoning + text | 51.590/75.482/75.482 | 1,060,376/1,162,904/1,162,904 | 1,093,296/1,120,360/1,120,360 | 81,920/212,992/212,992 | 1/1/1 |
| Garnet / single tool | 7.836/14.238/14.238 | 256,160/258,664/258,664 | 270,448/323,664/323,664 | 0/16,384/16,384 | 1/1/1 |
| Garnet / three tools | 8.906/25.180/25.180 | 633,736/639,536/639,536 | 649,600/665,800/665,800 | 0/147,456/147,456 | 1/1/1 |
| Garnet / media | 11.753/16.461/16.461 | 227,336/228,216/228,216 | 237,352/246,424/246,424 | 16,384/49,152/49,152 | 1/1/1 |
| Garnet / terminal | 4.601/7.679/7.679 | 84,624/85,136/85,136 | 95,264/106,728/106,728 | 0/0/0 | 1/1/1 |
| Garnet / cancellation | 4.678/22.079/22.079 | 133,712/147,752/147,752 | 144,232/167,520/167,520 | 0/245,760/245,760 | 1/1/1 |
| Garnet / provider failure | 3.603/4.619/4.619 | 149,712/169,688/169,688 | 163,688/172,448/172,448 | 0/32,768/32,768 | 1/1/1 |

Gross-minus-control CPU and allocation still cross zero in some samples and
show scheduler/GC noise. The raw file retains gross, net, and signed deltas for
every sample. This calibration prevents decorator work from being attributed
to `RoleGAgent`, but the control remains a synthetic process-level estimate,
not production resource attribution.

## Findings

1. **Transaction count is the synthetic cost center.** A 128-chunk response
   creates 131 append transactions but only about 53 KB of protobuf data.
   Garnet store I/O is 67.7 ms p50 versus 0.51 ms InMemory. This points to
   transaction/script round trips, not payload bytes. The 87.1 ms largest
   Garnet sample also shows this local setup is too variable for an SLO.
2. **Publication checkpoints are measurable but not dominant.** Long text made
   131 checkpoint advances and wrote 16,648 typed checkpoint bytes; checkpoint
   work was 0.386 ms p50 on the Garnet event-store run. Event-store I/O remained
   two orders of magnitude larger. The checkpoint store itself is InMemory in
   this harness, so production durable-backend cost still needs telemetry.
3. **Snapshot save is not the dominant cost.** Long text makes two snapshots
   and deletes 95 events. Fence 24 also proves compaction only after publication
   version 50 and reconstructs from snapshot plus retained tail. Orleans
   snapshot persistence was not measured.
4. **Chunk count extends one mailbox turn.** The harness deliberately awaits
   one chat dispatch, so occupancy is one and queued depth is zero. The long
   Garnet turn occupies the actor for 78.7 ms p50 before provider network/model
   latency is added.
5. **Recovery redo is semantic, not identity reuse.** Tested fences produced
   zero progress event-ID and sequence overlap. Complete ledger/publication and
   compaction-aware durable-tail checks passed for all 72 adapter/fence samples.
   Each injected attempt had one generated but intentionally uncommitted tail;
   every successful recovery boundary and final text/usage fact was present.
   Payload redo still equalled all phase-one progress: 3, 11, and 23 events.
6. **Tool checkpoint cost is now visible.** One real tool execution is nine
   appends and about 6.95 KB; three executions are seventeen appends and about
   22.5 KB. The harness fail-closed asserts exactly one and three terminal tool
   invocations. These current #3138 intent/completion checkpoints are recovery
   authority, not candidates for an unqualified performance merge.

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
