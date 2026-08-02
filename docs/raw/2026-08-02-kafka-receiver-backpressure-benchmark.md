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

The buffer capacity equals the operation count during this measurement. Every sample asserts zero rejected
writes and the exact same offset checksum so CPU scheduling cannot silently turn the steady-state comparison
into an overload/backpressure run. Each implementation receives three warmup passes, then nine samples are
measured with alternating order. The test prints every sample and each implementation's median elapsed-time
sample. Throughput, CPU, allocation, and their ratios are non-gating diagnostics: none determines unit-test or
CI success. CPU is process CPU time and allocation is process-wide allocation during the isolated sample, so
neither is a production capacity promise.

The controlled measurement does not use the unit suite's five-second timeout. Its default watchdog is 600
seconds and exists only to detect a stuck harness. Set
`AEVATAR_KAFKA_RECEIVER_PERFORMANCE_WATCHDOG_SECONDS` to a larger positive integer on slower or constrained
machines; elapsed time below that watchdog is never an acceptance threshold.

The retained-memory curve constructs a fresh buffer for each backlog depth. Allocation measurement begins
after buffer construction so it reports overload-time allocation, while retained messages report the live
references held after the offered backlog.

## Commands

```bash
AEVATAR_KAFKA_RECEIVER_PERFORMANCE_DIAGNOSTICS=1 \
  AEVATAR_KAFKA_RECEIVER_PERFORMANCE_WATCHDOG_SECONDS=600 \
  dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj \
  --nologo --no-restore \
  --filter 'FullyQualifiedName~KafkaReceiverShape_ControlledMeasurement_ShouldReportNonGatingDiagnostics' \
  --logger 'console;verbosity=detailed'
```

Without the environment variable the diagnostic returns immediately, so ordinary unit and CI runs do not
perform or gate on wall-clock measurements.

## Receiver-shape results

The following is the raw output of one isolated run after 3 warmups. Each sample transferred 250,000 messages.

| Sample | Old msg/s | Old CPU us/msg | Old B/msg | New msg/s | New CPU us/msg | New B/msg | New/old |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 167,438 | 1.79 | 995.1 | 146,539 | 1.73 | 968.1 | 87.5% |
| 2 | 224,127 | 0.92 | 972.2 | 205,056 | 1.44 | 968.0 | 91.5% |
| 3 | 260,050 | 1.09 | 970.1 | 814,878 | 0.89 | 968.0 | 313.4% |
| 4 | 437,914 | 1.01 | 970.1 | 313,996 | 1.08 | 968.1 | 71.7% |
| 5 | 1,192,609 | 0.78 | 970.2 | 1,226,416 | 0.92 | 968.0 | 102.8% |
| 6 | 255,202 | 1.21 | 972.3 | 426,349 | 1.16 | 968.0 | 167.1% |
| 7 | 284,001 | 1.15 | 972.2 | 576,028 | 1.34 | 968.1 | 202.8% |
| 8 | 907,315 | 0.86 | 970.1 | 416,680 | 0.93 | 968.0 | 45.9% |
| 9 | 1,005,629 | 0.94 | 970.1 | 1,493,673 | 0.85 | 968.0 | 148.5% |
| Median | 284,001 | 1.15 | 972.2 | 426,349 | 1.16 | 968.0 | 150.1% |

The 45.9%-313.4% single-sample ratio range demonstrates why this local wall-clock comparison is unsuitable
as a per-run acceptance gate. The stable assertions are zero rejected writes and the exact offset checksum;
the numeric measurements remain review evidence only.

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
| Old unbounded `ConcurrentQueue` | 125,123,560 |
| New fixed-capacity SPSC ring | 24,789,906 |

This isolated ring result is intentionally reported rather than hidden. It does not include the receiver's
decode, Protobuf parse, batch construction, or Kafka poll costs, so it is not used as the receiver steady-state
acceptance result. It has no absolute throughput assertion and is only emitted by the opt-in controlled
diagnostic.
