# RoleGAgent streaming write-amplification measurement

This measurement harness runs deterministic streaming shapes through a real
`RoleGAgent`, `LocalActor` mailbox turn, event-sourcing behavior, snapshot
strategy, and event-store adapter. The measurement decorators exist only in
this tool; they do not change production commit, publication, or projection
code.

## Fixed configuration

`streaming-write-amplification.config.json` is the checked-in workload and
runtime configuration. It covers short/high-chunk text, reasoning, one and
multiple tool calls, media, terminal-only completion, cancellation, provider
failure, and crash recovery. Each adapter receives two warmups and twelve
measured samples per workload.

The actor is initialized outside the measured turn. The fixed snapshot policy
uses an interval of 50 committed versions, compaction enabled, and five
retained events. Crash recovery sweeps failure fences after 4, 12, and 24
successful turn appends. Each fence creates a new actor activation over the
same stores and re-dispatches the same session. The recovery shape uses 22 text
chunks, so even the fence-24 final reconciliation remains below version 50 and
does not confuse expected compaction deletion with durability loss. The
128-chunk long-text shape independently measures snapshot and compaction cost.

Each instrumented resource sample has a matched control turn with the same
workload, adapter, actor lifecycle, and committed-state publisher but without
the append/snapshot measurement decorators. Even iterations run control first;
odd iterations run it second. This alternating order reduces systematic order
bias without claiming to remove process-level runtime noise.

## Run

InMemory only:

```bash
tools/measurements/Aevatar.RoleStreamingWriteAmplification/run.sh --adapter inmemory
```

InMemory plus the pinned Garnet 2.1.0 image used by the checked-in result:

```bash
docker run -d --name aevatar-role-stream-measure -p 6399:6379 \
  ghcr.io/microsoft/garnet:2.1.0@sha256:4e298b9b274088cded4156853a32b85fed7b42242eb9ca90216d332e25f2bceb \
  --lua --lua-transaction-mode

AEVATAR_TEST_GARNET_CONNECTION_STRING='localhost:6399,abortConnect=false' \
AEVATAR_TEST_GARNET_IMAGE_REFERENCE='ghcr.io/microsoft/garnet:2.1.0@sha256:4e298b9b274088cded4156853a32b85fed7b42242eb9ca90216d332e25f2bceb' \
  tools/measurements/Aevatar.RoleStreamingWriteAmplification/run.sh --adapter all

docker rm -f aevatar-role-stream-measure
```

Garnet selection is fail-closed. Before warmups, the harness requires the
exact declared image reference above, verifies `server_name=garnet` and
`garnet_version=2.1.0` through `INFO SERVER`, verifies RESP2 standalone/master
identity through `HELLO 2`, executes a Lua `redis.call` read/write probe, and
executes the production `GarnetEventStore` append and compaction scripts. The
image reference remains operator-declared because the Redis protocol does not
expose an OCI digest; the independently observed server identity and all
verified capabilities are recorded beside it in raw output.

Validate the fixed config without running samples:

```bash
dotnet run \
  --project tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj \
  --configuration Release -- \
  --config tools/measurements/Aevatar.RoleStreamingWriteAmplification/streaming-write-amplification.config.json \
  --verify
```

## Metric semantics

- A non-empty `ConfirmEventsAsync` maps one-to-one to `AppendAsync` on the
  measured path. Failed append attempts are reported separately.
- Committed bytes are protobuf `StateEvent.CalculateSize()` scalar totals. The
  append decorator does not clone committed events. It retains event
  references only for crash-recovery samples, whose gross resource values are
  paired with the same uninstrumented control as every other workload.
- Snapshot bytes are the typed `RoleGAgentState` protobuf size. The checked-in
  run uses the InMemory snapshot store for both adapters; Garnet measurements
  cover event append/read/version/compaction I/O, not a production snapshot
  backend.
- Mailbox occupancy means in-flight plus queued chat turns. The harness awaits
  one dispatch at a time, so occupancy is one and queued depth is zero; all
  provider chunks execute inside that single actor turn.
- Gross CPU/allocation values include append/snapshot decorators. Net values
  are the matched undecorated control turn; the signed gross-minus-control
  delta quantifies measurement overhead and can be negative under process
  noise. CPU and allocation remain process deltas (`TotalProcessorTime` and
  `GC.GetTotalAllocatedBytes(true)`), so neither gross nor net is a production
  cost attribution. Managed heap and working set are gross diagnostics only.
- Recovery validation subscribes to the actual `CommittedStateEventPublished`
  stream and performs a fresh `baseStore.GetEventsAsync(actorId)` read after
  recovery completes. Every sample reconciles the full `StateEvent` ID sets in
  four fail-closed directions: append ledger to durable missing, durable to
  ledger unexpected, durable to projection missing, and projection to durable
  unexpected. The raw output schema is version 3 and records all four counts.
  Progress redo is a separate diagnostic based on event ID,
  `session_id + sequence`, and a sequence-free payload SHA-256 fingerprint;
  no fence is labelled a maximum.
- Percentiles use nearest rank. With twelve samples, p95 and p99 are both the
  maximum sample and must not be treated as production tail estimates.

The raw result is
`docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json`.

Assert the checked-in final recovery evidence:

```bash
jq -e '
  .schemaVersion == 3 and
  ([.adapters[].adapter] | sort) == ["garnet", "inmemory"] and
  all(.adapters[]; .status == "measured") and
  all(
    .adapters[].workloads[] | select(.streamShape == "crash_recovery");
    (.samples | length) == 12 and
    all(
      .samples[];
      .crashRecovery.ledgerToDurableMissingEvents == 0 and
      .crashRecovery.durableToLedgerUnexpectedEvents == 0 and
      .crashRecovery.durableToProjectionMissingEvents == 0 and
      .crashRecovery.projectionToDurableUnexpectedEvents == 0 and
      .crashRecovery.finalAppendLedgerEvents == .crashRecovery.finalDurableReadbackEvents and
      .crashRecovery.finalDurableReadbackEvents == .crashRecovery.finalProjectionVisibleEvents
    )
  )
' docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json
```
