# Health Probe Operational Snapshot Design

## Problem

Issue #3030 reports that recurring health probes dominate Garnet event-store
writes. Production verification against commit `eb966787` confirmed that the
first mitigation did not remove the source: every 60-second probe tick still
commits `HealthProbeExecutionStarted` and `HealthProbeObserved`, and the
health projection scope commits a watermark for each event.

The status board is operational telemetry. Its latest sample and two-hour
history are not business facts, audit evidence, or actor state that must survive
a backend restart. Persisting every sample as a domain event gives temporary
telemetry permanent retention semantics.

## Decision

Keep one `HealthProbeTargetGAgent` per configured target, but split its state by
meaning:

- `HealthProbeConfigured` remains a committed domain event because the actor
  needs its typed descriptor after reactivation.
- Active execution, latest outcome, failure count, success/check timestamps,
  and the bounded 120-sample history become actor-owned runtime state.
- Recurring ticks and execution timeouts become ephemeral delayed callbacks.
  The callback only publishes a typed self-message; all reconciliation and
  state changes still happen in an actor handler turn.
- Each configuration or terminal result overwrites one
  `HealthProbeOperationalSnapshot` keyed by target slug.
- `/api/status` reads these operational snapshots directly.
- Backend restart or actor reactivation clears the operational history and
  starts sampling again. This reset semantics is explicitly approved.

The snapshot is not a CQRS readmodel. It has no `StateVersion`, event id,
projection scope, reducer, committed-state activation plan, or watermark.

## Selected Architecture

`IHealthProbeOperationalSnapshotStore` is a capability-specific port with
`UpsertAsync` and `GetAsync`. The actor depends only on this port.

- Production uses an Elasticsearch adapter in the Mainnet Host. It performs an
  unconditional exact-key overwrite in a dedicated
  `health-probe-operational-snapshots` index. JSON exists only at this external
  adapter boundary; the internal contract remains generated Protobuf. The
  existing index-reconcile hosted service provisions/reconciles the alias at
  startup; actor turns never perform index lifecycle operations.
- Development and tests use an explicit
  `InMemoryHealthProbeOperationalSnapshotStore`. This is infrastructure-only
  ephemeral state and is not a production fact source.

The new index is separate from the historical `health-probe-targets`
projection index. During a rolling deployment, an old projector can therefore
write only its retired index and cannot overwrite the new operational
snapshots. No historical document migration is required.

The actor remains the single writer for each slug. Handler turns update runtime
state serially, then await the snapshot overwrite. Completion and timeout
signals carry the operation id; the first matching terminal signal clears the
active execution, and stale signals are ignored.

The delayed callback uses the platform `TimeProvider` overload of
`Task.Delay` and an actor-lifetime cancellation token. It introduces no timer
registry and writes no reminder state. Activation recreates the tick chain;
deactivation cancels outstanding tick/timeout delays and the active executor.
Callback continuations
only call the standard event publisher and never read or mutate actor state.
Activation also best-effort purges durable callbacks left by the old
implementation. This is bounded migration cleanup, not the new scheduling path.

## Data Flow

The canonical path is:

1. `HealthProbeStartupService` dispatches the typed configuration command at
   startup and reconciles the same command once per minute. The reconcile pass
   executes no probe and owns no target runtime state; it reactivates actors
   whose process-local tick was lost during a rolling deployment.
2. A changed descriptor commits one `HealthProbeConfigured` event; an
   unchanged descriptor commits nothing.
3. Actor activation initializes an empty operational snapshot from the
   committed descriptor and rearms an ephemeral delayed self-tick.
4. A tick starts one runtime execution and invokes the registered executor.
   A duplicate tick while an execution is active is ignored.
5. Completion or timeout re-enters the actor inbox as a typed self-message.
6. The actor reconciles the operation id, updates its bounded runtime history,
   and overwrites the operational snapshot.
7. `HealthStatusQueryPort` reads snapshots for the current manifest slugs.

Normal sampling never publishes `CommittedStateEventPublished`, never starts
or advances a projection scope, and never registers a durable callback. An
unchanged periodic configure commits no event.

## Compatibility And Cleanup

The existing `HealthProbeObserved`, `HealthProbeExecutionStarted`, and
`HealthProbeExecutionCleared` Protobuf messages and reducers remain readable
so actors can replay old streams. New code stops producing them. After replay,
activation intentionally ignores historical samples and initializes empty
runtime telemetry.

The health-specific projector, materialization context and lease, activation
plan provider, metadata provider, readmodel inventory entry, and associated
tests are deleted. The existing startup cleanup for legacy nested status scopes
stays in place so already-created scopes can be released without recreating
them.

The old Elasticsearch projection index is no longer queried. Deleting that
index is an operational cleanup outside this code change.

## Failure Semantics

- A snapshot write failure is logged and does not create a fallback domain
  event. The actor keeps its runtime state, rearms the next tick, and a later
  successful overwrite catches the query copy up.
- A snapshot read failure propagates through the status endpoint rather than
  fabricating a healthy result.
- Missing snapshots are omitted, so the aggregate status remains honest while
  probes initialize.
- A late completion after timeout, or a late timeout after completion, is
  ignored by operation-id reconciliation.
- Deactivation cancels delayed signals. Reactivation starts a fresh loop and
  does not recover an interrupted execution or publish its late completion.
- Failure to purge a legacy durable callback is logged; operation-id and
  active-execution guards prevent it from creating parallel probe executions.
- Configuration persistence failure still fails the command because the actor
  cannot safely run without an authoritative descriptor.

## Alternatives Rejected

### Snapshot and compact the event stream

This bounds retained data but still performs append, snapshot, and delete work
for every sample. Production commit `eb966787` demonstrates that compaction
does not remove the write source.

### Write operational snapshots to Garnet

This removes event-store keys but continues feeding the same Garnet AOF and
checkpoint workload. It relocates the write amplification instead of fixing
it.

### Keep a process-level registry

A singleton dictionary is smaller but violates the actor ownership and
distributed-state rules. Only the explicit development/test store may use an
in-memory implementation.

## Verification

Tests must prove:

1. A changed configuration commits exactly one configuration event.
2. Successful, failed, immediate-error, and timed-out probe turns commit zero
   additional event-store events.
3. Completion and timeout update one operational snapshot and reject stale
   operation ids.
4. Tick and timeout scheduling write no durable callback and delayed callbacks
   only publish typed self-messages.
5. Deactivation cancels an active executor and prevents a late completion self-message.
6. Runtime history is bounded to 120 samples and resets on activation.
7. Old event streams still replay without activation failure.
8. Status queries include only current manifest slugs and read no projection
   lifecycle service.
9. Mainnet composition selects Elasticsearch while development selects the
   in-memory adapter.
10. Static guards find no health committed-state activation provider or health
   projection materializer.

Before pushing, run the StatusDashboard and Mainnet capability tests, test
stability guard, query/projection guards, projection state-version guards,
architecture guards, documentation lint, full solution build, and full solution
tests.

Production acceptance uses the signed-in NyxID CLI plus read-only logs:

- `nyxid proxy request aevatar /api/status ...` shows advancing check times;
- logs show operational snapshot overwrites;
- across at least two probe intervals, logs show no event-store commit for
  `health-probe::*` and no health projection watermark commit.

## Non-Goals

- Do not make status history durable across restart.
- Do not add a generic cache, artifact, or key-value abstraction.
- Do not change probe cadence, executor behavior, endpoint JSON, or status
  aggregation rules.
- Do not delete production data or old Elasticsearch indices automatically.
- Do not change event-sourcing or projection behavior for business actors.
