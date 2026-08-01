# Kafka Receiver Backpressure Benchmark Raw Evidence

Date: 2026-08-02

Issue: #3140

## Environment

- macOS 26.3, arm64
- .NET SDK 10.0.103
- Debug build
- Each recorded run used a dedicated `dotnet test` process.

## Method

The receiver-shape comparison runs the old unbounded `ConcurrentQueue` and the new fixed-capacity
SPSC buffer through the same two-thread path:

1. owner thread calls a deterministic fake `Consume`;
2. owner thread decodes the routing headers, parses the Protobuf `EventEnvelope`, and constructs the
   `StreamId`, sequence token, and `KafkaProviderBatchContainer`;
3. owner thread enqueues into the selected buffer;
4. Orleans-shaped puller thread drains the selected buffer and checks the Kafka offset sequence.

This models the production ownership boundary: Orleans Persistent Streams assigns a `QueueId` to one receiver
lifecycle, `KafkaQueuePartitionMapper` maps that queue to one fixed partition, and that receiver manually assigns
the partition. The backpressure path therefore pauses/resumes that fixed partition; the benchmark does not claim
to measure Kafka subscription, group heartbeat, or group rebalance behavior.

The buffer capacity equals the operation count during this measurement, and the test asserts zero rejected
writes so CPU scheduling cannot silently turn the steady-state comparison into an overload/backpressure run.
Each implementation is warmed up, then measured five times with alternating order; the test reports the
median elapsed-time sample. The regression gate requires bounded throughput to remain at least 80% of
unbounded throughput. CPU is process CPU time and
allocation is process-wide allocation during the isolated sample, so both are comparative diagnostics,
not production capacity promises.

The retained-memory curve constructs a fresh buffer for each backlog depth. Allocation measurement begins
after buffer construction so it reports overload-time allocation, while retained messages report the live
references held after the offered backlog.

## Commands

```bash
dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo --no-restore \
  --filter 'FullyQualifiedName~KafkaReceiverShape_SteadyState_ShouldNotRegressMateriallyAgainstUnboundedQueue|FullyQualifiedName~KafkaReceiverMessageBuffer_ShouldBoundRetentionAndRetainTransportHeadroom' \
  --logger 'console;verbosity=detailed'
```

## Receiver-shape results

Each row is the median of 5 x 100,000 messages.

| Run | Buffer | Throughput (msg/s) | CPU (us/msg) | Allocation (B/msg) | Bounded/unbounded throughput |
| --- | --- | ---: | ---: | ---: | ---: |
| 1 | Old unbounded | 1,680,763 | 0.86 | 969.3 | - |
| 1 | New bounded | 1,714,143 | 0.96 | 968.0 | 102.0% |
| 2 | Old unbounded | 1,860,157 | 0.90 | 968.7 | - |
| 2 | New bounded | 2,238,303 | 0.77 | 968.0 | 120.3% |
| 3 | Old unbounded | 1,353,739 | 1.67 | 990.8 | - |
| 3 | New bounded | 2,326,225 | 0.61 | 968.0 | 171.8% |

All three runs pass the 80% relative gate. No receiver-shape throughput, CPU, or allocation regression is
visible beyond normal local benchmark variation.

## Retained-memory curve

Capacity is 1,024 messages.

| Offered backlog | Old retained | Old overload allocation | New retained | New overload allocation |
| ---: | ---: | ---: | ---: | ---: |
| 256 | 256 | 8,512 B | 256 | 0 B |
| 1,024 | 1,024 | 33,984 B | 1,024 | 0 B |
| 4,096 | 4,096 | 133,184 B | 1,024 | 0 B |
| 16,384 | 16,384 | 527,296 B | 1,024 | 0 B |
| 32,768 | 32,768 | 1,052,032 B | 1,024 | 0 B |

The old retained curve is `N`; the new retained curve is `min(N, capacity)`. This measures buffer-held
references and overload-time queue allocation, not the object graph retained by each message payload.

## Pure buffer diagnostic

The same run measured 1,000,000 concurrent enqueue/dequeue pairs:

| Buffer | Pairs/s |
| --- | ---: |
| Old unbounded `ConcurrentQueue` | 140,692,488 |
| New fixed-capacity SPSC ring | 26,921,814 |

This isolated ring result is intentionally reported rather than hidden. It does not include the receiver's
decode, Protobuf parse, batch construction, or Kafka poll costs, so it is not used as the receiver steady-state
acceptance result. The receiver-shape comparison above is the relative performance gate; the pure buffer test
retains an absolute 500,000 pairs/s transport-headroom guard as a secondary diagnostic.
