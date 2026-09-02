---
title: 0047 - Kafka fixed-assignment ownership follows Orleans queue balancing
status: accepted
owner: eanzhao
supersedes: "ADR-0003 shared-group, rebalance, and revoke ownership claims"
---

# 0047 - Kafka fixed-assignment ownership follows Orleans queue balancing

## Context

ADR-0003 correctly records the deterministic `QueueId <-> PartitionId` mapping,
but describes the resulting multi-silo behavior as Kafka shared-group
consumption, rebalance, and revoke handling. The implemented receiver uses
manual `Assign(partition)` rather than `Subscribe`. It therefore does not use
Kafka consumer-group assignment or revocation as its ownership protocol.

The distinction is operationally significant. A periodic `Consume(timeout)` on
a paused manually assigned partition advances librdkafka broker and protocol
work, but is not evidence that Kafka group heartbeats or group rebalances own
the receiver lifecycle.

## Decision

Orleans Persistent Streams queue balancing is the only partition-ownership
authority for this provider:

1. `KafkaQueuePartitionMapper` maintains a one-to-one mapping between Orleans
   `QueueId` values and Kafka partitions.
2. Orleans creates one queue receiver for each locally owned queue. That
   receiver creates its Kafka consumer on its owner loop and manually assigns
   the mapped fixed partition.
3. Consumer create, assign, pause, resume, consume, seek, commit, close, and
   dispose operations stay serialized on that owner loop.
4. When Orleans moves queue ownership, it shuts down the old receiver and
   initializes a new receiver, which creates a new consumer and assigns the
   same mapped partition. This is an Orleans receiver handoff, not a Kafka group
   rebalance or revoke callback.
5. `ConsumerGroup` identifies the committed-offset namespace and lag observed
   for the manually assigned consumer. It does not create a second ownership
   authority.
6. During backpressure the owner loop continues bounded polling while the fixed
   partition is paused. This keeps librdkafka protocol processing alive without
   claiming Kafka group-heartbeat semantics.
7. A non-fatal librdkafka consume error is observable and retried on the same
   owner loop. A fatal consume error terminates that loop and is surfaced by
   receiver read, acknowledgement, and shutdown operations until Orleans
   explicitly rebuilds the receiver lifecycle with a new consumer. Each
   lifecycle generation owns its cancellation source, initialization task,
   owner-loop task, shutdown task, and fault. Shutdown publishes one shared
   cleanup task, cancels and awaits in-flight initialization, and then stops the
   owner loop. Initialization which overlaps shutdown publishes one shared
   successor generation but does not enter transport readiness until predecessor
   cleanup completes. The predecessor continuation rechecks generation identity
   and cancellation on the owner-loop thread, under the lifecycle lock, before
   it may create or assign a consumer. A delegate which was queued before
   shutdown but runs afterward therefore cannot start the old generation. Only
   explicit initialization starts a successor generation and leaves the
   predecessor task and fault behind.

Receiver handoff can produce at-least-once redelivery around failures or
overlap. Kafka committed offsets are the restart cursor, while delivery success
and poison-message behavior remain governed by the acknowledgement semantics
defined and validated under issue #3136. Manual assignment does not itself
provide exactly-once delivery or broker-enforced exclusive ownership.

## Superseded ADR-0003 statements

This ADR supersedes ADR-0003 wording which claims:

- multi-pod correctness is provided by shared-group rebalance;
- the manual-assignment lifecycle receives Kafka revoke callbacks;
- periodic paused polling proves group-heartbeat ownership;
- manual assignment alone establishes an end-to-end at-least-once guarantee.

ADR-0003 remains the historical record for explicit producer partitioning and
the canonical queue/partition mapping. Those decisions are unchanged.

## Consequences

- Runtime and documentation use Orleans queue ownership terminology for this
  provider and reserve Kafka rebalance/revoke terminology for `Subscribe`-based
  consumers.
- Tests model shutdown and reinitialization of a fixed receiver instead of fake
  assignment changes. Gated transport-readiness tests cover shutdown during
  initialization, initialization during shutdown, and concurrent callers on
  both lifecycle operations. A separate gated owner-loop starter covers the
  exact queued-before-shutdown, executed-after-shutdown window.
- Receiver owner-loop failure must surface through `IQueueAdapterReceiver`
  operations so Orleans can observe lifecycle failure; an unsupervised failed
  task must not masquerade as a healthy empty queue.
- Operators must diagnose queue ownership in Orleans and offset/lag state in
  Kafka as separate concerns.
