# Architecture Audit Report - 2026-08-02

## Sync Status

- **Repository:** `aevatarAI/aevatar`
- **Audit worktree:** `/private/tmp/aevatar-m38-completion`
- **Audit branch:** `refactor/2026-08-02_milestone-38-completion`
- **Target branch:** `origin/feature/integrate`
- **Changes detected:** Yes. Milestone 38 was implemented on the audit branch and reconciled with the current integration history before final verification.
- **Scope:** GitHub Milestone 38, issues #3135 through #3146.

## Executive Result

Milestone 38 is **12/12 complete** on the audited branch. The work closes the
runtime-hardening gaps found by the original audit without adding a second
projection path, process-local correctness source, query-time replay, or JSON
fact storage.

The result is material to Aevatar's product position as an actor-native agent
runtime: user-facing agent turns now have a host-owned execution bound,
incomplete turns recover from typed durable checkpoints, uncertain external
tool outcomes fail closed, Kafka ingress has honest failure/redelivery and
bounded buffering semantics, committed-state publication is recoverable, and
the remaining performance decisions are backed by reproducible measurements
instead of architectural speculation.

## Scan Summary

| Category | Status | Details |
|---|---|---|
| `GetAwaiter().GetResult()` | Clean | No confirmed production violation |
| `TypeUrl.Contains()` | Clean | No confirmed production violation |
| `Workflow.Core -> AI.Core` dependency | Clean | Dependency remains inverted through abstractions |
| Mid-layer ID-mapping `Dictionary` | Clean | No service-level fact-state registry introduced |
| Actor execution state | Clean | Deadline, recovery, retries, and continuations remain actor-owned and event-serialized |
| JSON in fact storage | Clean | New durable contracts use typed Protobuf |
| Query-time replay/priming | Clean | No normal query path reads or rebuilds write-side state |
| Projection pipeline | Clean | Committed facts continue through the single CQRS/AGUI observation path |
| Process-local dedup correctness | Removed | Generic envelope deduplicator and DI seam were deleted |
| CI guards | Required before delivery | Aggregate and affected specialized guards are recorded in final verification |

## Milestone 38 Completion Matrix

| Issue | Status | Acceptance evidence |
|---|---|---|
| #3135 | Complete | `15860900a` adds host-owned default/cap, typed timeout/cancellation outcomes, durable terminal completion, registration tests, and focused actor tests. |
| #3136 | Complete | `3236965d3` hardens terminal receiver failure propagation; `ff10034e1` proves redelivery across a real pre-ack process exit; later receiver lifecycle commits retain the contract. |
| #3137 | Complete | `15860900a` plus the terminal-reconciliation series resumes/finalizes incomplete sessions, protects incomplete state from trim, and durably retries completion delivery; `9b67cd587` closes nested workflow reconciliation. |
| #3138 | Complete | `697414a21` introduces typed durable tool checkpoints; follow-up recovery commits preserve call, operation, idempotency, direct-parent, approval, source-fact, and `OUTCOME_UNCERTAIN` semantics across replay. |
| #3139 | Complete | `efe78004b` provides runtime-owned Protobuf publication checkpoints, OCC, compaction fencing, activation recovery, and ADR 0045. |
| #3140 | Complete | `7a5491298` adds a bounded receiver buffer, configurable watermarks, consumer-thread pause/resume, metrics, stress coverage, and benchmark evidence; `dcb05b683` fences queued generations. |
| #3141 | Complete | `2ba5e7679` makes transcript sequence and retention actor-owned, keeps conversations appendable, and materializes the complete transcript readmodel. |
| #3142 | Complete | `88713bcfb` establishes typed ownership for transcript, execution state, prompt context, and user memory, with readmodel-only query access. |
| #3143 | Complete | `70646f3ee` records reproducible baseline/post-#3135 contention runs from historical source commits. Every sample has balanced activation/deactivation, zero cleanup failures, and zero orphaned actors. |
| #3144 | Complete | `a40682719` adds processing counters, durable unresolved backlog, trim alerts, per-source versions, Kafka-native lag, readmodel/dashboard projection, and focused tests. |
| #3145 | Complete | `1215ca6b9` deletes the misleading process-local envelope deduplicator, its interface/DI seam, and pre-handler suppression behavior; correctness remains in actor-owned idempotency and ingress redelivery. |
| #3146 | Complete | `80887623f` supplies the fixed harness/config and provider normalization; `04a8186cd` records schema-5 raw results for InMemory and pinned Garnet plus MEAI/NyxID/Tornado normalization and an explicit no-go on durability-boundary changes. |

## Architecture Decisions Preserved

1. `accepted`, handler success, committed fact, transport acknowledgement, and
   readmodel visibility remain distinct stages. No weak ACK claims a stronger
   guarantee.
2. Chat recovery advances only from actor-owned typed state. Activation does
   not replay event storage from a query path or depend on a process-local
   session registry.
3. Tool-call identity is single-purpose: provider `CallId`, durable
   `OperationId`, and `IdempotencyKey` are not aliases or fallback values.
4. An external side effect without a committed completion is represented as
   `OUTCOME_UNCERTAIN`; recovery does not silently execute it again.
5. Kafka backpressure owns fixed buffer capacity and changes consumer
   assignment only on the consumer thread. Generation fencing prevents stale
   callbacks from mutating current receiver state.
6. Measurement did not authorize a durability shortcut. The #3146 conclusion
   is no-go for changing commit boundaries until a production bottleneck and a
   separate semantic implementation issue justify it.

## Measurement Evidence

### Role contention (#3143)

- Baseline source: `618ba2141`; parent source under test: `dcb05b683`.
- Post-deadline source: `54bbc8d9f`, including #3135 commit `15860900a`.
- Shared configuration digest:
  `AA835BE81AF3B7FBD4184E083380C8B47CDD33DD500A51739D822263A16BB739`.
- Raw results:
  `docs/audit-scorecard/raw/2026-08-02-role-actor-contention-baseline-pre-3135.json`
  and
  `docs/audit-scorecard/raw/2026-08-02-role-actor-contention-post-3135.json`.
- Every sample satisfies `deactivationCount == activationCount`,
  `cleanupFailureCount == 0`, and `orphanedActiveActorCount == 0`.

### Streaming write amplification (#3146)

- Source commit: `80887623f7e8b418e3fb90024ff9bce12ee46670`.
- Schema 5 raw results contain 12 measured samples for every workload on both
  InMemory and the pinned Garnet 2.1.0 image.
- Provider normalization contains 12 measured samples for each of MEAI, NyxID,
  and Tornado with normalized payload and durable/publication reconciliation.
- Reports and raw data:
  `docs/audit-scorecard/2026-08-02-role-streaming-write-amplification.md`,
  `docs/audit-scorecard/2026-08-02-role-provider-normalization.md`, and
  `docs/audit-scorecard/raw/`.
- Result: keep current typed committed boundaries. Local Garnet shows measurable
  transaction cost, but the evidence does not establish a production capacity
  or SLO bottleneck that justifies weakening recovery semantics.

## Processing Results

| Work unit | Result | Review/verification |
|---|---|---|
| Role deadline, incomplete-session recovery, replay-safe tools | Integrated | Focused Role recovery/approval/replay tests passed; merge conflict review retained upstream tool context and milestone checkpoint semantics. |
| Kafka ingress terminal failure and local dedup removal | Integrated | Real pre-ack exit/redelivery evidence and runtime failure tests cover the correctness boundary. |
| Kafka bounded buffering | Integrated | Watermark, assignment, generation, shutdown, and stress tests cover consumer-thread ownership. |
| Projection publication, retention, ownership, telemetry | Integrated | Focused projection/runtime tests and architecture guards cover state ownership, authoritative versions, and query boundaries. |
| Contention and write-amplification measurement | Integrated | Raw source SHA, config digest, checksums, sample cardinality, reconciliation invariants, and reports are checked in and reproducible. |

## Final Verification

The delivery run executes the repository-wide build/test plus the aggregate and
affected specialized guards before pushing to `origin/feature/integrate`:

```text
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
bash tools/ci/architecture_guards.sh
bash tools/ci/test_stability_guards.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/docs/lint.sh
```

The checked-in measurement README validations additionally verify sample
cardinality, source identity, reconciliation invariants, provider normalization,
and raw-result sidecar checksums.

## Known Exclusions

- The checked-in Garnet run uses the pinned production adapter over local Docker
  loopback, not a production network topology; it is reproducibility evidence,
  not an SLO claim.
- With twelve measured samples, nearest-rank p95 and p99 are the maximum sample.
  The report treats them as run-tail evidence, not population tail estimates.
- Environment-dependent cluster and mainnet smoke scripts are outside this
  source-level milestone closeout. Their absence does not weaken the tested
  actor, transport, projection, or persistence contracts above.
- Existing package advisory warnings are repository dependency risks, not
  Milestone 38 regressions.

## Previous Audit Comparison

The initial 2026-08-02 audit found eight open gaps after #3139, #3141, #3142,
and #3144. This final cycle closes those gaps with code, typed contracts,
failure tests, real adapter evidence, and reproducible measurements. No issue
was closed solely because a document or unit test asserted completion.
