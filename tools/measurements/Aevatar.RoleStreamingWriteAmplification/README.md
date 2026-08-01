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
retained events. Crash recovery injects a persistent append failure after 12
successful turn appends, creates a new actor activation over the same stores,
and re-dispatches the same session.

## Run

InMemory only:

```bash
tools/measurements/Aevatar.RoleStreamingWriteAmplification/run.sh --adapter inmemory
```

InMemory plus a local Garnet 2.1-compatible server:

```bash
docker run -d --name aevatar-role-stream-measure -p 6399:6379 \
  ghcr.io/microsoft/garnet:latest --lua --lua-transaction-mode

AEVATAR_TEST_GARNET_CONNECTION_STRING='localhost:6399,abortConnect=false' \
  tools/measurements/Aevatar.RoleStreamingWriteAmplification/run.sh --adapter all

docker rm -f aevatar-role-stream-measure
```

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
- Committed bytes are protobuf `StateEvent.CalculateSize()` values captured
  before the adapter call.
- Snapshot bytes are the typed `RoleGAgentState` protobuf size. The checked-in
  run uses the InMemory snapshot store for both adapters; Garnet measurements
  cover event append/read/version/compaction I/O, not a production snapshot
  backend.
- Mailbox occupancy means in-flight plus queued chat turns. The harness awaits
  one dispatch at a time, so occupancy is one and queued depth is zero; all
  provider chunks execute inside that single actor turn.
- CPU, allocation, managed heap, and working-set values are process deltas.
- Percentiles use nearest rank. With twelve samples, p95 and p99 are both the
  maximum sample and must not be treated as production tail estimates.

The raw result is
`docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json`.
