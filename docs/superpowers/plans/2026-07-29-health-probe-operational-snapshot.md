# Health Probe Operational Snapshot Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop recurring health probes from writing Garnet event-store events, projection watermarks, or durable callback state while preserving the existing `/api/status` contract.

**Architecture:** Persist only `HealthProbeConfigured`; keep sampling state inside the target actor and overwrite a typed operational snapshot. Mainnet stores snapshots in a dedicated Elasticsearch alias reconciled at startup; development/tests use an explicit in-memory adapter. Ephemeral delayed callbacks publish typed self-messages and are canceled on deactivation.

**Tech Stack:** .NET 10, C# 14, Google Protobuf, xUnit, FluentAssertions, Elasticsearch HTTP API, existing Aevatar actor/event publisher contracts.

## Global Constraints

- Internal state, commands, callbacks, and snapshots remain Protobuf; JSON is allowed only in the Elasticsearch Host adapter.
- No health sampling path may call `PersistDomainEventAsync`, `PersistDomainEventsAsync`, `ScheduleSelfDurableTimeoutAsync`, or start a projection scope.
- Runtime state is mutated only in actor handler turns; delayed callbacks only publish typed self-messages.
- No process-level target/session registry and no generic storage abstraction.
- Preserve replay compatibility for historical health events; stop producing them.
- Preserve the public `/api/status` JSON fields and aggregation behavior.
- Tests use distinct literal ids and deterministic `FakeTimeProvider`; no polling delays.
- Do not modify or include unrelated changes from the primary checkout.

---

### Task 1: Operational Snapshot Contract And Query Store

**Files:**
- Modify: `agents/Aevatar.GAgents.StatusDashboard/protos/status_dashboard.proto`
- Create: `agents/Aevatar.GAgents.StatusDashboard/IHealthProbeOperationalSnapshotStore.cs`
- Create: `agents/Aevatar.GAgents.StatusDashboard/InMemoryHealthProbeOperationalSnapshotStore.cs`
- Modify: `agents/Aevatar.GAgents.StatusDashboard/IHealthStatusQueryPort.cs`
- Modify: `agents/Aevatar.GAgents.StatusDashboard/HealthStatusQueryPort.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Status/StatusEndpoints.cs`
- Modify: `test/Aevatar.GAgents.StatusDashboard.Tests/HealthStatusQueryPortTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/MainnetStatusEndpointsTests.cs`

**Interfaces:**
- Produces: `Task UpsertAsync(HealthProbeOperationalSnapshot snapshot, CancellationToken ct = default)`
- Produces: `Task<HealthProbeOperationalSnapshot?> GetAsync(string slug, CancellationToken ct = default)`
- Produces: `IHealthStatusQueryPort` methods returning `HealthProbeOperationalSnapshot`.

- [x] **Step 1: Write failing query and endpoint tests**

Change the query fixture to a real snapshot store and assert only current manifest slugs are returned:

```csharp
var store = new InMemoryHealthProbeOperationalSnapshotStore();
await store.UpsertAsync(new HealthProbeOperationalSnapshot
{
    Target = new HealthProbeTargetDescriptor { Slug = "self-liveness" },
});
var port = new HealthStatusQueryPort(store, Options.Create(new StatusDashboardOptions()));
(await port.ListAllAsync()).Select(x => x.Target.Slug).Should().Equal("self-liveness");
```

Update endpoint fixtures to use `HealthProbeOperationalSnapshot` and keep literal assertions for `overall`, counts, status, history, and timestamps.

- [x] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgents.StatusDashboard.Tests/Aevatar.GAgents.StatusDashboard.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~HealthStatusQueryPortTests
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter FullyQualifiedName~MainnetStatusEndpointsTests
```

Expected: compilation fails because `HealthProbeOperationalSnapshot` and its store do not exist.

- [x] **Step 3: Add the minimal typed snapshot and store**

Add this Protobuf shape and remove `HealthProbeTargetDocument`:

```proto
message HealthProbeOperationalSnapshot {
  HealthProbeTargetDescriptor target = 1;
  HealthProbeOutcome last_outcome = 2;
  int32 consecutive_failures = 3;
  google.protobuf.Timestamp last_success_at = 4;
  google.protobuf.Timestamp last_check_at = 5;
  repeated HealthProbeOutcome recent_outcomes = 6;
  google.protobuf.Timestamp updated_at = 7;
}
```

Define the narrow store port and an in-memory implementation that clones on read/write. Update the query port to read each manifest slug from this store and update `StatusEndpoints` to map from `snapshot.Target` and `snapshot.LastOutcome` without changing JSON names.

- [x] **Step 4: Run tests and verify GREEN**

Run the two commands from Step 2. Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add agents/Aevatar.GAgents.StatusDashboard src/Aevatar.Mainnet.Host.Api/Status test/Aevatar.GAgents.StatusDashboard.Tests/HealthStatusQueryPortTests.cs test/Aevatar.Capabilities.Tests/MainnetStatusEndpointsTests.cs
git commit -m "Model health status as operational snapshots"
```

### Task 2: Actor-Owned Ephemeral Probe Loop

**Files:**
- Modify: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeTargetGAgent.cs`
- Modify: `agents/Aevatar.GAgents.StatusDashboard/DependencyInjection/StatusDashboardServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.GAgents.StatusDashboard.Tests/HealthProbeTargetGAgentTests.cs`
- Modify: `test/Aevatar.GAgents.StatusDashboard.Tests/StatusDashboardServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: `IHealthProbeOperationalSnapshotStore` from Task 1.
- Preserves: typed self handlers for `HealthProbeTickRequested`, `HealthProbeCompletedEvent`, and `HealthProbeTimeoutFiredEvent`.
- Stops producing: `HealthProbeObserved`, `HealthProbeExecutionStarted`, and `HealthProbeExecutionCleared`.

- [x] **Step 1: Write failing zero-write and scheduling tests**

Replace the old "two events per tick" assertion with:

```csharp
var eventVersionBeforeTick = _agent.EventSourcing!.CurrentVersion;
await RunSuccessfulTickAsync();
_agent.EventSourcing.CurrentVersion.Should().Be(eventVersionBeforeTick);
_eventStore.CountEvents(HealthProbeObserved.Descriptor).Should().Be(0);
_snapshotStore.GetRequired("nyxid-auth").LastOutcome.Status.Should().Be(HealthOutcomeStatus.Ok);
_scheduler.ScheduledTimeouts.Should().Be(0);
```

Add focused tests for timeout, immediate executor failure, stale completion,
duplicate tick while active, history cap, activation reset, old-event replay,
and deactivation cancellation. Use `FakeTimeProvider.Advance` to fire delayed
signals; never poll or call `Task.Delay` in tests.

- [x] **Step 2: Run actor tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgents.StatusDashboard.Tests/Aevatar.GAgents.StatusDashboard.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~HealthProbeTargetGAgentTests
```

Expected: old actor increments event version, uses the durable scheduler, and has no snapshot store interaction.

- [x] **Step 3: Implement the minimal actor runtime state**

Add private runtime-only fields for the reset `HealthProbeTargetState`, actor-lifetime cancellation, next tick cancellation, and execution-timeout cancellation. On activation:

```csharp
_runtimeState = new HealthProbeTargetState { Spec = State.Spec?.Clone() };
await TryWriteOperationalSnapshotAsync();
await TryPurgeLegacyDurableCallbacksAsync(ct);
ScheduleNextTick(initial: true);
```

Use `Task.Delay(dueTime, ResolveTimeProvider(), token)` in a fire-and-publish helper. After the delay call `EventPublisher.PublishAsync(message, TopologyAudience.Self, CancellationToken.None, sourceEnvelope: null)`; do not use actor state in that continuation.

In handler turns, update `_runtimeState`, reconcile the exact operation id,
write the snapshot best-effort, and schedule the next ephemeral tick. Persist
only a changed `HealthProbeConfigured` event. Keep old reducers for replay.

- [x] **Step 4: Run actor and registration tests and verify GREEN**

Run:

```bash
dotnet test test/Aevatar.GAgents.StatusDashboard.Tests/Aevatar.GAgents.StatusDashboard.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~HealthProbeTargetGAgentTests|FullyQualifiedName~StatusDashboardServiceCollectionExtensionsTests'
```

Expected: PASS with zero post-configuration event commits and zero durable callback registrations.

- [x] **Step 5: Commit**

```bash
git add agents/Aevatar.GAgents.StatusDashboard test/Aevatar.GAgents.StatusDashboard.Tests
git commit -m "Run health probes without durable sampling writes"
```

### Task 3: Mainnet Elasticsearch Operational Store And Projection Removal

**Files:**
- Create: `src/Aevatar.Mainnet.Host.Api/Status/ElasticsearchHealthProbeOperationalSnapshotStore.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetAgentProjectionDocumentStoresExtensions.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeCommittedStateProjectionActivationPlanProvider.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeMaterializationContext.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeMaterializationRuntimeLease.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeTargetProjector.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeTargetDocument.Partial.cs`
- Delete: `agents/Aevatar.GAgents.StatusDashboard/HealthProbeTargetDocumentMetadataProvider.cs`
- Delete: `test/Aevatar.GAgents.StatusDashboard.Tests/HealthProbeCommittedStateProjectionActivationPlanProviderTests.cs`
- Delete: `test/Aevatar.GAgents.StatusDashboard.Tests/HealthProbeTargetProjectorTests.cs`
- Create: `test/Aevatar.Capabilities.Tests/ElasticsearchHealthProbeOperationalSnapshotStoreTests.cs`
- Modify: `test/Aevatar.Architecture.Tests/Rules/MainnetAgentProjectionDocumentStoreTests.cs`
- Modify: `test/Aevatar.GAgents.StatusDashboard.Tests/StatusDashboardServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Produces: Host adapter implementing `IHealthProbeOperationalSnapshotStore` and `IProjectionIndexReconcileTarget`.
- Production alias: `{IndexPrefix}-health-probe-operational-snapshots`.
- Removes: all health committed-state projection registrations and readmodel inventory entries.

- [x] **Step 1: Write failing Elasticsearch adapter and composition tests**

Use a scripted `HttpMessageHandler` to assert:

```text
PUT /test-health-probe-operational-snapshots/_doc/self-liveness
GET /test-health-probe-operational-snapshots/_doc/self-liveness
```

Assert the PUT body is the Protobuf JSON snapshot, GET returns a clone-equivalent
snapshot, 404 document returns null, missing index is an error, and
`ReconcileIndexAsync` provisions a versioned alias through the existing
lifecycle manager. Update composition tests to expect ES in Mainnet, InMemory
when the in-memory provider is selected, and no health readmodel descriptor.

- [x] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~ElasticsearchHealthProbeOperationalSnapshotStoreTests|FullyQualifiedName~MainnetHostCompositionTests'
dotnet test test/Aevatar.Architecture.Tests/Aevatar.Architecture.Tests.csproj --nologo --filter FullyQualifiedName~MainnetAgentProjectionDocumentStoreTests
```

Expected: adapter is missing and composition still registers the old projection document store.

- [x] **Step 3: Implement the Host adapter and delete the old projection path**

The adapter owns one `HttpClient`, Protobuf `JsonFormatter/JsonParser`,
metadata, and `ElasticsearchIndexLifecycleManager(autoCreate: true)`.
`ReconcileIndexAsync` calls `ReconcileWithReindexAsync`; read/write methods
never call lifecycle APIs. Register it as the snapshot store and reconcile
target in the Elasticsearch branch; register the in-memory store in the
in-memory branch. Remove all health projection/readmodel registrations and
files listed above.

- [x] **Step 4: Run tests and verify GREEN**

Run the commands from Step 2 plus the entire StatusDashboard project. Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add -A agents/Aevatar.GAgents.StatusDashboard src/Aevatar.Mainnet.Host.Api test/Aevatar.Capabilities.Tests test/Aevatar.Architecture.Tests test/Aevatar.GAgents.StatusDashboard.Tests
git commit -m "Replace health projection with operational storage"
```

### Task 4: Canonical Documentation And Verification

**Files:**
- Modify: `docs/canon/status-dashboard.md`
- Modify: `docs/canon/architecture.md`
- Modify: `docs/superpowers/specs/2026-07-29-health-probe-operational-snapshot-design.md`
- Track: `docs/superpowers/plans/2026-07-29-health-probe-operational-snapshot.md`

**Interfaces:**
- Documents: one committed configuration event, ephemeral actor sampling, operational snapshot overwrite, restart reset, and production verification evidence.

- [x] **Step 1: Update canonical documentation**

Replace the health committed-state projection diagram and prose with the
operational path. State explicitly that the latest/history snapshot is not a
readmodel or authoritative business fact, and that restarting resets history.
Document one-time cleanup of old durable callbacks/scopes.

- [x] **Step 2: Run focused verification**

```bash
dotnet test test/Aevatar.GAgents.StatusDashboard.Tests/Aevatar.GAgents.StatusDashboard.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --filter 'FullyQualifiedName~MainnetStatus|FullyQualifiedName~HealthProbe|FullyQualifiedName~ElasticsearchHealthProbe'
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/docs/lint.sh
```

Expected: every command exits 0.

- [x] **Step 3: Commit documentation and plan**

```bash
git add docs/canon docs/superpowers
git commit -m "Document ephemeral health probe sampling"
```

- [ ] **Step 4: Run final repository verification**

```bash
bash tools/ci/architecture_guards.sh
bash tools/ci/solution_split_guards.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo
git diff --check origin/feature/integrate...HEAD
```

Expected: all commands exit 0 and the working tree is clean.

- [ ] **Step 5: Integrate without a PR**

Fetch and require a fast-forward-safe remote base, then push without force:

```bash
git fetch origin feature/integrate
git merge-base --is-ancestor origin/feature/integrate HEAD
git push origin HEAD:feature/integrate
```

If the ancestor check fails, merge the new remote tip, rerun the complete final
verification, and only then retry the normal push. Never force push.
