# RoleGAgent streaming write-amplification measurement

This measurement harness runs deterministic streaming shapes through a real
`RoleGAgent`, `LocalActor` mailbox turn, event-sourcing behavior, snapshot
strategy, and event-store adapter. The measurement decorators exist only in
this tool; they do not change production commit, publication, or projection
code.

The same project also contains the targeted `scoped_role` contention
measurement required by issue #3143. That mode compares one explicitly reused
`RoleGAgent` with one actor per turn; it does not add a production session actor
or change runtime dispatch semantics.

The `provider-normalization` mode required by issue #3146 exercises the MEAI,
NyxID, and Tornado streaming adapters through the same real `RoleGAgent`,
`LocalActor`, `IActorDispatchPort`, event store, and committed publication path.
Loopback fixtures provide deterministic provider protocol frames without using
external credentials or network services.

## Provider normalization mode

The fixed provider workload uses two warmups and twelve measured samples per
provider. Correctness is derived from committed typed progress and completion
events, the append ledger, durable event-store readback, and observed
`CommittedStateEventPublished` identities. SDK update or wire-frame counts are
never used as correctness evidence.

| Provider | Text | Reasoning | Media | Tool start/completion | Usage | Terminal |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| MEAI | yes | yes | yes | yes | yes | yes |
| NyxID | yes | yes, raw patch | unsupported | yes | yes | yes |
| Tornado | yes | unsupported | unsupported | unsupported | yes | yes |

Tornado's production `MapRequest` does not map `LLMRequest.Tools` into the
LlmTornado request. LlmTornado emits its streaming tool accumulator only when
that request contains tools, so the adapter currently cannot surface tool
deltas even though its declared capabilities say tool calls are supported.
The harness therefore records `toolsAdvertised=false` and does not manufacture
tool evidence. This is a provider contract gap, not evidence for changing
`RoleGAgent` durability boundaries.

Run the checked-in measurement and regenerate both the raw JSON and SHA-256
sidecar:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-provider-normalization.sh
```

Validate only the fixed config and harness wiring:

```bash
dotnet run \
  --project tools/measurements/Aevatar.RoleStreamingWriteAmplification/Aevatar.RoleStreamingWriteAmplification.csproj \
  --configuration Release -- \
  --measurement provider-normalization \
  --config tools/measurements/Aevatar.RoleStreamingWriteAmplification/provider-normalization.config.json \
  --verify
```

Request facts are deliberately low-cardinality tuples. The checked-in result
contains only these unique shapes:

| Provider | Path | Stream | Usage opt-in | Auth | User-Agent | Tools advertised |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| MEAI | `in-process-ichatclient` | yes | yes | no | no | yes |
| NyxID | `/api/v1/llm/gateway/v1/chat/completions` | yes | yes | yes | no | yes |
| Tornado | `/v1/chat/completions` | yes | yes | yes | yes | no |

Actor, session, command, and tool-call identities are excluded from metric
labels. Assert the checked-in provider result with:

```bash
jq -e '
  .providers as $providers |
  .schemaVersion == 1 and
  .config.warmupIterations == 2 and
  .config.measuredIterations == 12 and
  .provenance.configSha256 ==
    "49d8081c54f0dc270c27dfa3e6de0932f327fa4beab41b175a53fcaaac4c4e1d" and
  ([.providers[].provider] | sort) == ["meai", "nyxid", "tornado"] and
  all(.providers[]; (.samples | length) == 12) and
  all(
    .providers[].samples[];
    .progressSequenceMonotonic and
    .uniqueCompletion and
    .appendLedgerMatchesDurableReadback and
    .durableReadbackMatchesPublication and
    .textObserved and .usageObserved and .terminalObserved
  ) and
  all(.providers[]; all(.summary[]; .p95 == .p99)) and
  ($providers[] | select(.provider == "meai") |
    .coverage == {text:true, reasoning:true, media:true, tool:true, usage:true, terminal:true}) and
  ($providers[] | select(.provider == "nyxid") |
    .coverage.reasoning and .coverage.tool and (.coverage.media | not)) and
  ($providers[] | select(.provider == "tornado") |
    (.coverage.reasoning | not) and (.coverage.media | not) and
    (.coverage.tool | not) and all(.requestFacts[]; .toolsAdvertised | not)) and
  ([$providers[] | {provider, requestFacts:(.requestFacts | unique)}] | sort_by(.provider)) == [
    {provider:"meai", requestFacts:[{path:"in-process-ichatclient", stream:true,
      usageOptIn:true, authPresent:false, userAgentPresent:false, toolsAdvertised:true}]},
    {provider:"nyxid", requestFacts:[{path:"/api/v1/llm/gateway/v1/chat/completions",
      stream:true, usageOptIn:true, authPresent:true, userAgentPresent:false, toolsAdvertised:true}]},
    {provider:"tornado", requestFacts:[{path:"/v1/chat/completions", stream:true,
      usageOptIn:true, authPresent:true, userAgentPresent:true, toolsAdvertised:false}]}
  ] and
  (($providers | map(select(.provider == "meai"))[0]) as $meai |
   ($providers | map(select(.provider == "nyxid"))[0]) as $nyxid |
    ([$meai.samples[].toolArgumentsSha256] | unique | length) == 1 and
    ([$nyxid.samples[].toolArgumentsSha256] | unique | length) == 1 and
    $meai.samples[0].toolArgumentsSha256 != "" and
    $meai.samples[0].toolArgumentsSha256 == $nyxid.samples[0].toolArgumentsSha256)
' docs/audit-scorecard/raw/2026-08-02-role-provider-normalization.json
```

## Scoped-role contention mode

The contention mode starts one controlled slow LLM turn and eight fast turns.
The slow provider is held behind a deterministic completion gate while all fast
turns are admitted, then released after a fixed async yield budget. It runs the
same workload in two scenarios:

- `same_actor`: all nine sessions share one `RoleGAgent` inbox;
- `distinct_actor`: every session owns a separate `RoleGAgent` inbox.

The JSON records per-turn queue, service, first-output, and completion latency;
p50/p95/p99 summaries; the same-minus-distinct head-of-line delta; maximum
per-actor and total queue depth; activation/deactivation counts; protobuf state
bytes; cleanup failures; and remaining active actor orphans. Actor and session
identities are represented only as run-local ordinals. The allowed metric label
set is `entrypoint`, `scenario`, `turn_kind`, and `outcome`; identity labels are
explicitly forbidden.

Run the pre-#3135 baseline:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  baseline-pre-3135
```

After #3135 is integrated, rerun the exact checked-in config from a descendant
of that integration commit:

```bash
bash tools/measurements/Aevatar.RoleStreamingWriteAmplification/run-contention.sh \
  post-3135
```

Only compare outputs whose `configSha256` values match. The baseline config
pins `dcb05b683b911db037eb51c071b4495f1195ee28` as the pre-#3135 production-code
aggregate. `sourceCommit` records the harness checkout used for each run. These
measurements are diagnostic evidence, not wall-clock CI gates or production
SLOs.

Validate the checked-in schema, final runtime-neutral lifecycle patch, and
cleanup observations:

```bash
jq -e '
  .schemaVersion == 2 and
  .sourceDirtyPaths == [
    "tools/measurements/Aevatar.RoleStreamingWriteAmplification/Program.cs",
    "tools/measurements/Aevatar.RoleStreamingWriteAmplification/RoleContentionMeasurement.cs"
  ] and
  all(
    .scenarios[].samples[];
    .deactivationCount == .activationCount and
    .cleanupFailureCount == 0 and
    .orphanedActiveActorCount == 0
  ) and
  all(
    .scenarios[];
    .summary.cleanupFailureCount == 0 and
    .summary.orphanedActiveActorCount == 0
  )
' docs/audit-scorecard/raw/2026-08-02-role-actor-contention-*.json
```

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
same event, snapshot, publication-checkpoint, and secret-vault stores and
re-dispatches the same session. The recovery shape uses 23 text chunks. Fence
24 reaches committed version 50, snapshots that authoritative state, compacts
through version 45 only after publication reaches version 50, and leaves the
configured five events plus any post-snapshot commit as the durable tail.
Fences 4 and 12 remain below the snapshot
boundary. The 128-chunk long-text shape measures steady-state snapshot and
compaction cost separately.

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
- Publication-checkpoint metrics count runtime-owned load, initialization,
  advance, and failure-record calls and mutations. Serialized checkpoint bytes
  use the typed Protobuf state returned by each successful revision advance.
- Mailbox occupancy means in-flight plus queued chat turns. The harness awaits
  one dispatch at a time, so occupancy is one and queued depth is zero; all
  provider chunks execute inside that single actor turn.
- Gross CPU/allocation values include append/snapshot decorators. Net values
  are the matched undecorated control turn; the signed gross-minus-control
  delta quantifies measurement overhead and can be negative under process
  noise. CPU and allocation remain process deltas (`TotalProcessorTime` and
  `GC.GetTotalAllocatedBytes(true)`), so neither gross nor net is a production
  cost attribution. Managed heap and working set are gross diagnostics only.
- Recovery validation records provider-generated text, reasoning, media,
  tool-start identity, and usage immediately before each fake provider chunk
  is yielded. Attempt-scoped operation evidence uses
  `session + attempt + semantic ordinal + kind + payload hash`; terminal
  progress embedded in the completion event is expanded through the same
  extractor. The injected phase-one generated-but-uncommitted tail is reported
  separately from committed progress loss. The successful recovery attempt
  must match committed semantics in both directions without deriving expected
  evidence from configuration or the append decorator.
- The harness subscribes to the actual `CommittedStateEventPublished` stream,
  then sends the same envelope through a formally registered
  `ICurrentStateProjectionMaterializer`, projection write dispatcher, and
  InMemory document projection store. The measurement-only protobuf read model
  is independently read after phase one and final recovery; it is not a
  production `RoleGAgent` read model and is never registered by a host.
- Every sample requires the complete append ledger to equal the complete
  committed-publication identity set. After compaction, durable readback must
  equal only the ledger suffix above the compacted-through version; the deleted
  prefix count must match the adapter's actual delete result. Snapshot state
  must match the committed publication at its version, the runtime-owned
  publication checkpoint must match the latest store version/event, retained
  tail versions must be continuous, and a fresh activation must reconstruct
  the latest committed state. The projection read model must independently
  match version, event ID, state-root SHA-256, and session facts, including
  duplicate-write idempotency and stale-write rejection. The raw output also
  requires the successful recovery attempt's generated text and usage hashes
  to equal the materialized final session facts. The schema is version 5 and
  records source commit plus Program/config SHA-256 provenance. Progress redo
  remains a separate diagnostic based on
  event ID, `session_id + sequence`, and a sequence-free payload SHA-256
  fingerprint; no fence is labelled a maximum.
- Percentiles use nearest rank. With twelve samples, p95 and p99 are both the
  maximum sample and must not be treated as production tail estimates.

The raw result is
`docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json`.

Assert the checked-in final recovery evidence:

```bash
jq -e '
  .schemaVersion == 5 and
  (.sourceCommit | test("^[0-9a-f]{40}$")) and
  (.provenance.programSha256 | test("^[0-9a-f]{64}$")) and
  (.provenance.configSha256 | test("^[0-9a-f]{64}$")) and
  ([.adapters[].adapter] | sort) == ["garnet", "inmemory"] and
  all(.adapters[]; .status == "measured") and
  all(
    .adapters[].workloads[] | select(.streamShape == "crash_recovery");
    (.samples | length) == 12 and
    all(
      .samples[];
      .crashRecovery.ledgerToDurableMissingEvents == 0 and
      .crashRecovery.durableToLedgerUnexpectedEvents == 0 and
      .crashRecovery.durableToCommittedPublicationMissingEvents == 0 and
      .crashRecovery.committedPublicationToDurableUnexpectedEvents == 0 and
      .crashRecovery.finalAppendLedgerEvents == .crashRecovery.finalCommittedPublicationEvents and
      .crashRecovery.durableAuthority.ledgerToPublicationMissingEvents == 0 and
      .crashRecovery.durableAuthority.publicationToLedgerUnexpectedEvents == 0 and
      .crashRecovery.durableAuthority.tailLedgerToDurableMissingEvents == 0 and
      .crashRecovery.durableAuthority.durableToTailUnexpectedEvents == 0 and
      .crashRecovery.durableAuthority.compactedBySnapshotEvents ==
        .crashRecovery.durableAuthority.compactionDeletedEvents and
      .crashRecovery.durableAuthority.snapshotCoversCompaction and
      .crashRecovery.durableAuthority.snapshotStateMatchesCommittedPublication and
      .crashRecovery.durableAuthority.checkpointMatchesAuthority and
      .crashRecovery.durableAuthority.retainedTailVersionsContinuous and
      .crashRecovery.durableAuthority.freshActivationStateMatchesLatestPublication and
      .crashRecovery.phaseOneAttemptLocalGeneratedTailEvents == 1 and
      .crashRecovery.phaseOneCommittedWithoutGeneratedEvidence == 0 and
      .crashRecovery.phaseOneGeneratedSemanticEvents ==
        (.crashRecovery.phaseOneCommittedSemanticEvents + 1) and
      .crashRecovery.recoveryGeneratedSemanticEvents ==
        .crashRecovery.recoveryCommittedSemanticEvents and
      .crashRecovery.recoveryGeneratedToCommittedMissingEvents == 0 and
      .crashRecovery.recoveryCommittedWithoutGeneratedEvidence == 0 and
      .crashRecovery.materializedCurrentState.phaseOne.readModelFound and
      .crashRecovery.materializedCurrentState.phaseOne.durableIdentityMatchesCommittedPublication and
      .crashRecovery.materializedCurrentState.phaseOne.readModelMatchesCommittedPublication and
      .crashRecovery.materializedCurrentState.final.readModelFound and
      .crashRecovery.materializedCurrentState.final.durableIdentityMatchesCommittedPublication and
      .crashRecovery.materializedCurrentState.final.readModelMatchesCommittedPublication and
      .crashRecovery.materializedCurrentState.duplicateWriteIdempotent and
      .crashRecovery.materializedCurrentState.staleWriteDidNotOverwrite and
      .crashRecovery.recoveredUserVisibleSemantics.finalContentMatchesRecoveryGeneration and
      .crashRecovery.recoveredUserVisibleSemantics.finalUsageMatchesRecoveryGeneration
    )
  ) and
  all(
    .adapters[].workloads[] | select(.crashAfterSuccessfulAppends == 24);
    all(
      .samples[];
      .crashRecovery.durableAuthority.storeVersion >= 50 and
      .crashRecovery.durableAuthority.snapshotVersion == 50 and
      .crashRecovery.durableAuthority.publicationCheckpointVersion ==
        .crashRecovery.durableAuthority.storeVersion and
      .crashRecovery.durableAuthority.compactedThroughVersion == 45 and
      .crashRecovery.durableAuthority.publishedVersionAtCompaction == 50 and
      .crashRecovery.durableAuthority.expectedDurableTailEvents ==
        (.crashRecovery.durableAuthority.storeVersion - 45) and
      .crashRecovery.durableAuthority.actualDurableTailEvents ==
        .crashRecovery.durableAuthority.expectedDurableTailEvents
    )
  )
' docs/audit-scorecard/raw/2026-08-02-role-streaming-write-amplification.json
```
