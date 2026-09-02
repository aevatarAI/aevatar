# Projection Version Regression Repair Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a platform-admin, code-only repair path that conditionally removes the exact regressed Workspace or NyxID catalog Elasticsearch replica and rebuilds it from an authoritative source.

**Architecture:** A separate internal Elasticsearch repair adapter exposes an opt-in repair lease only for explicitly registered read-model types. Studio and GAgentService Application services own repair ordering through narrow Infrastructure ports. The Mainnet Host only authorizes, validates, and maps typed results.

**Tech Stack:** .NET 10, C#, Protobuf, ASP.NET Core minimal APIs, Elasticsearch HTTP optimistic concurrency, xUnit, FluentAssertions, NSubstitute.

## Global Constraints

- Never allow a lower `StateVersion` to overwrite a higher `StateVersion`.
- Never hydrate actor state from Elasticsearch.
- Never perform replay, projection priming, or replica deletion from a normal query path.
- EventStore version `0` is not eligible for replica deletion.
- Repair is limited to `StudioWorkspaceCurrentStateDocument` and `NyxIdAuthorizationCatalogDocument`.
- Every apply request must fence the exact authority version, document version, document last event ID, actor identity, repair request ID, and operator reason.
- Workspace repair returns dispatch acceptance only; it must not claim read-model visibility.
- Catalog repair can use the caller bearer only for the same verified caller subject.
- No bearer, Agent Key, API key, refresh token, Vault reference, or catalog contents may be logged or returned.

## Review Hardening Semantics

- The ordinary Elasticsearch projection store does not implement the repair
  interface. A separate internal adapter is registered only by the explicit
  repair opt-in, and the in-memory provider exposes no repair capability.
- Workspace and Catalog stores read the authoritative EventStore version again
  immediately before deletion, after the document fingerprint has matched.
- A transport-ambiguous Elasticsearch delete is reconciled by one bounded exact
  reinspection of the leased index, document ID, sequence number, and primary
  term. `AlreadyAbsent` is returned only when that exact revision is proven
  absent.
- Workspace repair carries the inspected source version as a minimum. The actor
  republishes its actual latest committed state at a version greater than or
  equal to that minimum.
- Catalog repair uses separate repair command/refresh adapters. The actor checks
  the minimum source version and uses its own lifecycle state; the repair path
  never queries the deleted read model for a lifecycle fence.
- After `Deleted` or `AlreadyAbsent`, Workspace dispatch and Catalog refresh use
  a cancellation token independent of the HTTP request, so client disconnect
  cannot cancel authoritative recovery.
- Unexpected downstream inspection/apply exceptions map to a bodyless,
  sanitized HTTP `503`. Cancellation propagates only when the supplied request
  token is actually canceled; authorization remains fail-closed as `403`.
- The existing already-absent continuation remains an operator/audit rule. A
  signed inspection token or durable repair-request-ID record is explicitly
  deferred.
- This review hardening adds no secret, configuration setting, infrastructure
  operation, or operator step.

---

### Task 1: Opt-in Elasticsearch repair lease

**Files:**

- Create: `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/IElasticsearchProjectionDocumentRepairStore.cs`
- Modify: `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs`
- Modify: `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `test/Aevatar.CQRS.Projection.Core.Tests/ElasticsearchProjectionDocumentStoreBehaviorTests.cs`

**Interfaces:**

- Produces:

```csharp
public interface IElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    Task<ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>?> InspectAsync(
        TKey key,
        CancellationToken ct = default);

    Task<ElasticsearchProjectionDocumentRepairDeleteDisposition> DeleteIfUnchangedAsync(
        ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey> lease,
        CancellationToken ct = default);
}

public sealed class ElasticsearchProjectionDocumentRepairLease<TReadModel, TKey>
    where TReadModel : class, IProjectionReadModel<TReadModel>, new()
{
    internal ElasticsearchProjectionDocumentRepairLease(
        TKey key,
        TReadModel document,
        string concreteIndexName,
        long sequenceNumber,
        long primaryTerm);

    public TKey Key { get; }
    public TReadModel Document { get; }
    internal string ConcreteIndexName { get; }
    internal long SequenceNumber { get; }
    internal long PrimaryTerm { get; }
}

public enum ElasticsearchProjectionDocumentRepairDeleteDisposition
{
    Deleted = 0,
    AlreadyAbsent = 1,
    RevisionConflict = 2,
}
```

- Produces the explicit registration:

```csharp
public static IServiceCollection AddElasticsearchDocumentProjectionRepairStore<TReadModel, TKey>(
    this IServiceCollection services)
    where TReadModel : class, IProjectionReadModel<TReadModel>, new();
```

- [ ] **Step 1: Write failing repair-lease tests**

Add tests that use the existing fake HTTP handler:

```csharp
[Fact]
public async Task RepairInspectAsync_ReturnsDocumentAndOpaqueRevisionLease()
{
    var handler = Handler(
        JsonResponse(HttpStatusCode.OK, """
        {
          "_index":"aevatar-mainnet-test-v1",
          "_seq_no":12,
          "_primary_term":3,
          "_source":{
            "id":"doc-1",
            "actor_id":"actor-1",
            "state_version":"7",
            "last_event_id":"event-7",
            "updated_at_utc_value":"2026-07-25T00:00:00Z"
          }
        }
        """));
    using var store = CreateStore(
        new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
        handler);

    var lease = await ((IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>)store)
        .InspectAsync("doc-1");

    lease.Should().NotBeNull();
    lease!.Document.ActorId.Should().Be("actor-1");
    lease.Document.StateVersion.Should().Be(7);
}

[Fact]
public async Task RepairDeleteIfUnchangedAsync_UsesConcreteIndexAndOccRevision()
{
    var handler = RecordingHandler(
        JsonResponse(HttpStatusCode.OK, ExistingDocumentJson()),
        JsonResponse(HttpStatusCode.OK, """{"result":"deleted"}"""));
    using var store = CreateStore(
        new ElasticsearchProjectionDocumentStoreOptions { AutoCreateIndex = true },
        handler);
    var repair = (IElasticsearchProjectionDocumentRepairStore<TestStoreReadModel, string>)store;
    var lease = await repair.InspectAsync("doc-1");

    var result = await repair.DeleteIfUnchangedAsync(lease!);

    result.Should().Be(ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted);
    handler.Requests[1].RequestUri!.PathAndQuery.Should().Be(
        "/aevatar-mainnet-test-v1/_doc/doc-1?if_seq_no=12&if_primary_term=3");
}
```

Also cover:

- inspection `404` returns `null`;
- delete `404` returns `AlreadyAbsent`;
- delete `409` returns `RevisionConflict`;
- the ordinary `AddElasticsearchDocumentProjectionStore` does not register the repair interface;
- calling the new opt-in registration does register it.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~ElasticsearchProjectionDocumentStoreBehaviorTests"
```

Expected: compilation fails because the repair interface and registration do not exist.

- [ ] **Step 3: Implement the separate opaque lease adapter and OCC deletion**

Keep `ElasticsearchProjectionDocumentStore<TReadModel,TKey>` on the ordinary
projection-store contract. Add a separate internal
`ElasticsearchProjectionDocumentRepairStore<TReadModel,TKey>` adapter that
implements the repair interface by using internal store operations. Add a
private read method that parses `_index`, `_seq_no`, `_primary_term`, and
`_source`.
Deletion must be:

```csharp
using var response = await _httpClient.DeleteAsync(
    $"{lease.ConcreteIndexName}/_doc/{Uri.EscapeDataString(keyValue)}" +
    $"?if_seq_no={lease.SequenceNumber}&if_primary_term={lease.PrimaryTerm}",
    ct);
```

Map only:

```csharp
if (response.StatusCode == HttpStatusCode.NotFound)
    return ElasticsearchProjectionDocumentRepairDeleteDisposition.AlreadyAbsent;
if (response.StatusCode == HttpStatusCode.Conflict)
    return ElasticsearchProjectionDocumentRepairDeleteDisposition.RevisionConflict;
await ElasticsearchProjectionDocumentStoreHttpSupport.EnsureSuccessAsync(
    response,
    "repair-delete",
    ct);
return ElasticsearchProjectionDocumentRepairDeleteDisposition.Deleted;
```

Keep the lease revision properties `internal`; callers may compare the typed document but cannot manufacture an Elasticsearch revision.

If delete transport fails or times out after the request may have reached
Elasticsearch, perform one timeout-bounded exact reinspection of the leased
revision. Return `AlreadyAbsent` only when the exact index, document ID,
sequence number, and primary term are proven absent; otherwise preserve the
ambiguous failure.

- [ ] **Step 4: Add explicit opt-in DI**

The new extension must construct the separate adapter over an already
registered concrete store:

```csharp
services.AddSingleton<IElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>>(provider =>
    new ElasticsearchProjectionDocumentRepairStore<TReadModel, TKey>(
        provider.GetRequiredService<
            ElasticsearchProjectionDocumentStore<TReadModel, TKey>>()));
```

It must not be called by the ordinary store registration.

- [ ] **Step 5: Run tests and verify GREEN**

Run the Task 1 command. Expected: all `ElasticsearchProjectionDocumentStoreBehaviorTests` pass.

- [ ] **Step 6: Commit**

```bash
git add \
  src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/IElasticsearchProjectionDocumentRepairStore.cs \
  src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs \
  src/Aevatar.CQRS.Projection.Providers.Elasticsearch/DependencyInjection/ServiceCollectionExtensions.cs \
  test/Aevatar.CQRS.Projection.Core.Tests/ElasticsearchProjectionDocumentStoreBehaviorTests.cs
git commit -m "Add guarded Elasticsearch projection repair lease"
```

---

### Task 2: Studio Workspace actor re-publication and Application repair

**Files:**

- Modify: `src/workflow/Aevatar.Workflow.Studio/Workspace/studio_workspace_messages.proto`
- Modify: `src/workflow/Aevatar.Workflow.Studio/Workspace/StudioWorkspaceGAgent.cs`
- Create: `src/Aevatar.Studio.Application.Abstractions/Studio/ProjectionRecovery/StudioWorkspaceVersionRegressionRepairContracts.cs`
- Create: `src/Aevatar.Studio.Application/Studio/ProjectionRecovery/StudioWorkspaceVersionRegressionRepairService.cs`
- Create: `src/Aevatar.Studio.Infrastructure/ActorBacked/ActorDispatchStudioWorkspaceProjectionRepublishPort.cs`
- Create: `src/Aevatar.Studio.Infrastructure/ProjectionRecovery/ElasticsearchStudioWorkspaceVersionRegressionStorePort.cs`
- Modify: `src/Aevatar.Studio.Infrastructure/Aevatar.Studio.Infrastructure.csproj`
- Modify: `src/Aevatar.Studio.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Modify: `src/Aevatar.Studio.Hosting/StudioProjectionReadModelServiceCollectionExtensions.cs`
- Create: `test/Aevatar.Studio.Tests/StudioWorkspaceProjectionRebuildTests.cs`
- Create: `test/Aevatar.Studio.Tests/StudioWorkspaceVersionRegressionRepairServiceTests.cs`
- Modify: `test/Aevatar.Studio.Tests/StudioHostingNyxIdAuthorizationCatalogCompositionTests.cs`

**Interfaces:**

- Produces the typed actor command:

```protobuf
message RepairStudioWorkspaceProjectionCommand {
  string workspace_id = 1;
  string scope_id = 2;
  int64 minimum_state_version = 3;
  string repair_request_id = 4;
}
```

- Produces Application contracts:

```csharp
public interface IStudioWorkspaceVersionRegressionRepairService
{
    Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<StudioWorkspaceVersionRegressionRepairResult> RepairAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IStudioWorkspaceVersionRegressionStorePort
{
    Task<StudioWorkspaceVersionRegressionInspection> InspectAsync(
        string scopeId,
        CancellationToken ct = default);

    Task<StudioWorkspaceReplicaDeleteDisposition> DeleteIfMatchesAsync(
        StudioWorkspaceVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface IStudioWorkspaceProjectionRepublishPort
{
    Task<StudioWorkspaceProjectionRepublishReceipt> DispatchAsync(
        string scopeId,
        long minimumStateVersion,
        string repairRequestId,
        CancellationToken ct = default);
}

public sealed record StudioWorkspaceVersionRegressionRepairRequest(
    string ScopeId,
    string ExpectedActorId,
    long ExpectedSourceStateVersion,
    long ExpectedDocumentStateVersion,
    string ExpectedDocumentLastEventId,
    string RepairRequestId,
    string RepairReason,
    string RequestedBySubjectId);
```

- The inspection includes:

```csharp
public sealed record StudioWorkspaceVersionRegressionInspection(
    string ScopeId,
    string ActorId,
    long SourceStateVersion,
    long? DocumentStateVersion,
    string DocumentLastEventId,
    string DocumentActorId,
    bool Repairable,
    string Detail);
```

- [ ] **Step 1: Write failing actor re-publication tests**

Build an actor with an in-memory event store, commit an inspected version and
then a later draft event, capture committed-fact publications, clear the
capture, and dispatch the repair command with the earlier version as its
minimum.

Assert:

```csharp
eventsAfter.Should().HaveCount(eventsBefore);
publication.StateEvent.Version.Should().Be(2);
publication.StateEvent.EventId.Should().Be($"rebuild:{actor.Id}:2");
publication.StateRoot.Unpack<StudioWorkspaceState>()
    .Drafts.Should().ContainKey("wf-alpha");
```

Add a second test asserting a minimum version above the actor's current version
publishes nothing and throws.

- [ ] **Step 2: Run actor tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~StudioWorkspaceProjectionRebuildTests"
```

Expected: compilation fails because the command and handler do not exist.

- [ ] **Step 3: Implement the actor maintenance command**

The handler must validate canonical identity and version, then route the current state using a typed settings event:

```csharp
[EventHandler(EndpointName = "repairWorkspaceProjection")]
public async Task HandleProjectionRepairAsync(RepairStudioWorkspaceProjectionCommand command)
{
    ArgumentNullException.ThrowIfNull(command);
    ValidateWorkspace(command.WorkspaceId, command.ScopeId);
    var currentVersion = EventSourcing?.CurrentVersion
        ?? throw new InvalidOperationException("Workspace event sourcing is unavailable.");
    if (command.MinimumStateVersion <= 0 ||
        currentVersion < command.MinimumStateVersion)
        throw new InvalidOperationException("Workspace projection repair source version changed.");
    if (string.IsNullOrWhiteSpace(State.WorkspaceId) ||
        !string.Equals(State.WorkspaceId, command.WorkspaceId, StringComparison.Ordinal) ||
        !string.Equals(State.ScopeId, command.ScopeId, StringComparison.Ordinal))
    {
        throw new InvalidOperationException("Workspace projection repair identity does not match current state.");
    }

    await RepublishCommittedStateAsync(new StudioWorkspaceSettingsUpdated
    {
        WorkspaceId = State.WorkspaceId,
        ScopeId = State.ScopeId,
        Settings = State.Settings?.Clone() ?? new StudioWorkspaceSettings(),
        UpdatedAtUtc = State.UpdatedAtUtc?.Clone() ?? new Timestamp(),
        ExpectedVersion = State.LastAppliedEventVersion,
    });
}
```

Do not persist a repair event.

- [ ] **Step 4: Write failing Application service tests**

Use fake store and republish ports. Cover:

- inspection classifies `documentVersion > sourceVersion > 0` as repairable;
- source version `0` is not repairable;
- equal or lower document version is not repairable;
- actor ID mismatch is not repairable;
- apply mismatch never deletes or dispatches;
- `Deleted` and `AlreadyAbsent` both dispatch the typed republish command;
- delete revision conflict never dispatches.

- [ ] **Step 5: Run Application tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~StudioWorkspaceVersionRegressionRepairServiceTests"
```

Expected: compilation fails because the contracts and service do not exist.

- [ ] **Step 6: Implement Application classification and ordering**

`RepairAsync` must:

1. normalize and validate request fields;
2. inspect current source/document state;
3. require exact expected source/document/event values when the document exists;
4. allow a missing document only as an idempotent continuation with an unchanged positive expected source version;
5. call `DeleteIfMatchesAsync`;
6. dispatch republish only for `Deleted` or `AlreadyAbsent`;
7. return `Accepted` with the command ID.

No polling or query visibility check belongs in this service.

- [ ] **Step 7: Implement Studio Infrastructure adapters**

The store adapter derives the actor ID only with:

```csharp
var actorId = StudioWorkspaceConventions.BuildActorId(scopeId);
```

It reads source version from `IEventStore.GetVersionAsync(actorId)`, obtains an opaque Elasticsearch repair lease, maps the typed document to the inspection, and on apply:

```csharp
if (sourceVersion != request.ExpectedSourceStateVersion)
    return StudioWorkspaceReplicaDeleteDisposition.SourceChanged;
if (lease is null)
    return StudioWorkspaceReplicaDeleteDisposition.AlreadyAbsent;
if (!FingerprintMatches(lease.Document, request))
    return StudioWorkspaceReplicaDeleteDisposition.DocumentChanged;
sourceVersion = await _eventStore.GetVersionAsync(actorId, ct);
if (sourceVersion != request.ExpectedSourceStateVersion)
    return StudioWorkspaceReplicaDeleteDisposition.SourceChanged;
```

Then map the provider delete disposition.

The republish adapter uses `IStudioActorBootstrap` and `StudioActorCommandDispatch` to send `RepairStudioWorkspaceProjectionCommand`. Do not extend the ordinary `IStudioWorkspaceCommandPort`.
Once deletion returns `Deleted` or `AlreadyAbsent`, dispatch with
`CancellationToken.None`; a client disconnect must not strand the document
after deletion. The actor treats the inspected source version as a minimum and
republishes its actual latest committed version.

- [ ] **Step 8: Opt in only the Workspace ES read model**

After registering `StudioWorkspaceCurrentStateDocument` with Elasticsearch:

```csharp
services.AddElasticsearchDocumentProjectionRepairStore<
    StudioWorkspaceCurrentStateDocument,
    string>();
services.TryAddSingleton<
    IStudioWorkspaceVersionRegressionStorePort,
    ElasticsearchStudioWorkspaceVersionRegressionStorePort>();
services.TryAddSingleton<
    IStudioWorkspaceVersionRegressionRepairService,
    StudioWorkspaceVersionRegressionRepairService>();
```

Register `IStudioWorkspaceProjectionRepublishPort` in Studio Infrastructure.
Do not register the repair service in the in-memory branch.

- [ ] **Step 9: Run tests and verify GREEN**

Run both Task 2 filters. Expected: all pass.

- [ ] **Step 10: Commit**

```bash
git add \
  src/workflow/Aevatar.Workflow.Studio/Workspace \
  src/Aevatar.Studio.Application.Abstractions/Studio/ProjectionRecovery \
  src/Aevatar.Studio.Application/Studio/ProjectionRecovery \
  src/Aevatar.Studio.Infrastructure/ActorBacked/ActorDispatchStudioWorkspaceProjectionRepublishPort.cs \
  src/Aevatar.Studio.Infrastructure/ProjectionRecovery \
  src/Aevatar.Studio.Infrastructure/Aevatar.Studio.Infrastructure.csproj \
  src/Aevatar.Studio.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs \
  src/Aevatar.Studio.Hosting/StudioProjectionReadModelServiceCollectionExtensions.cs \
  test/Aevatar.Studio.Tests/StudioWorkspaceProjectionRebuildTests.cs \
  test/Aevatar.Studio.Tests/StudioWorkspaceVersionRegressionRepairServiceTests.cs \
  test/Aevatar.Studio.Tests/StudioHostingNyxIdAuthorizationCatalogCompositionTests.cs
git commit -m "Repair regressed Studio workspace projection"
```

---

### Task 3: NyxID catalog delete-and-refresh repair

**Files:**

- Create: `src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairContracts.cs`
- Create: `src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairService.cs`
- Create: `src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Infrastructure/Aevatar.GAgentService.Infrastructure.csproj`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/NyxIdAuthorizationCatalogHostingServiceCollectionExtensions.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
- Create: `test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests.cs`
- Modify: `test/Aevatar.GAgentService.Integration.Tests/NyxIdAuthorizationCatalogHostingCompositionTests.cs`

**Interfaces:**

```csharp
public interface INyxIdAuthorizationCatalogVersionRegressionRepairService
{
    Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default);

    Task<NyxIdAuthorizationCatalogVersionRegressionRepairResult> RepairPersonalAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogVersionRegressionStorePort
{
    Task<NyxIdAuthorizationCatalogVersionRegressionInspection> InspectPersonalAsync(
        string verifiedOwnerSubject,
        CancellationToken ct = default);

    Task<NyxIdAuthorizationCatalogReplicaDeleteDisposition> DeleteIfMatchesAsync(
        NyxIdAuthorizationCatalogVersionRegressionRepairRequest request,
        CancellationToken ct = default);
}

public sealed record NyxIdAuthorizationCatalogVersionRegressionRepairRequest(
    string VerifiedOwnerSubject,
    string ExpectedActorId,
    string BearerToken,
    long ExpectedSourceStateVersion,
    long ExpectedDocumentStateVersion,
    string ExpectedDocumentLastEventId,
    string RepairRequestId,
    string RepairReason,
    string RequestedBySubjectId);
```

- [ ] **Step 1: Write failing catalog repair service tests**

Use fake store, repair-refresh, and visibility ports. Cover:

- repairable classification requires `documentVersion > sourceVersion > 0`;
- source/document mismatch does not delete or refresh;
- `Deleted` and `AlreadyAbsent` invoke the repair-specific
  `RefreshPersonalAsync` exactly once;
- refresh receives the exact verified owner subject and bearer;
- refresh failure returns `Failed` without fabricating visibility;
- observed refresh calls visibility with the committed refresh version;
- ready visibility maps to `Ready`;
- projection-pending visibility maps to `ProjectionPending`.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests"
```

Expected: compilation fails because the repair contracts and service do not exist.

- [ ] **Step 3: Implement Application repair flow**

After guarded delete, call the repair-specific refresh port with the inspected
source version as a minimum and detach authoritative recovery from request
cancellation:

```csharp
var refresh = await _refreshPort.RefreshPersonalAsync(
    request.VerifiedOwnerSubject,
    request.BearerToken,
    request.ExpectedSourceStateVersion,
    request.RepairRequestId,
    CancellationToken.None);
if (!refresh.Success)
    return NyxIdAuthorizationCatalogVersionRegressionRepairResult.Failed(inspection, refresh);

var owner = new AuthorizationOwnerIdentity
{
    Authority = NyxIdAuthorizationAuthorities.NyxId,
    OwnerKind = AuthorizationOwnerKind.Personal,
    OwnerSubject = request.VerifiedOwnerSubject,
};
var visibility = await _visibilityPort.ResolveAsync(owner, refresh.StateVersion, ct);
```

Never pass document services, lifecycle fence, digest, or timestamps into the actor.
The repair begin command must be admitted against the Catalog actor's current
version and must use the actor-owned `State.LifecycleFence`; it must not query
the deleted read model for lifecycle state.

- [ ] **Step 4: Implement the catalog Infrastructure store adapter**

Build the actor ID only through:

```csharp
var owner = new AuthorizationOwnerIdentity
{
    Authority = NyxIdAuthorizationAuthorities.NyxId,
    OwnerKind = AuthorizationOwnerKind.Personal,
    OwnerSubject = normalizedSubject,
};
var actorId = NyxIdAuthorizationCatalogActorIds.Build(owner);
```

Use `IEventStore.GetVersionAsync(actorId)` and the opt-in repair store for
`NyxIdAuthorizationCatalogDocument`. Enforce the same exact source/document
fingerprint as Workspace. After the fingerprint matches, read the authoritative
EventStore version again immediately before deletion and reject any drift.

- [ ] **Step 5: Add opt-in registration only in the ES provider branch**

After registering `NyxIdAuthorizationCatalogDocument` with Elasticsearch:

```csharp
services.AddElasticsearchDocumentProjectionRepairStore<
    NyxIdAuthorizationCatalogDocument,
    string>();
services.AddNyxIdAuthorizationCatalogVersionRegressionRepairPorts();
services.TryAddSingleton<
    INyxIdAuthorizationCatalogVersionRegressionStorePort,
    ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort>();
services.TryAddSingleton<
    INyxIdAuthorizationCatalogVersionRegressionRepairService,
    NyxIdAuthorizationCatalogVersionRegressionRepairService>();
```

Do not register it for the in-memory provider.
The ordinary Catalog command and refresh concrete types must not implement the
repair interfaces; the Elasticsearch branch resolves distinct repair adapters.

- [ ] **Step 6: Add composition tests**

Verify ES configuration resolves the repair service and in-memory configuration does not expose it.

- [ ] **Step 7: Run tests and verify GREEN**

Run the Task 3 unit filter and the focused composition test.

- [ ] **Step 8: Commit**

```bash
git add \
  src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairContracts.cs \
  src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairService.cs \
  src/platform/Aevatar.GAgentService.Infrastructure/Schedules/Authorization/ElasticsearchNyxIdAuthorizationCatalogVersionRegressionStorePort.cs \
  src/platform/Aevatar.GAgentService.Infrastructure/Aevatar.GAgentService.Infrastructure.csproj \
  src/platform/Aevatar.GAgentService.Hosting/DependencyInjection \
  test/Aevatar.GAgentService.Tests/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests.cs \
  test/Aevatar.GAgentService.Integration.Tests/NyxIdAuthorizationCatalogHostingCompositionTests.cs
git commit -m "Repair regressed NyxID catalog projection"
```

---

### Task 4: Platform-admin repair endpoints

**Files:**

- Create: `src/Aevatar.Mainnet.Host.Api/ProjectionRecovery/ProjectionVersionRegressionRepairAdminEndpoints.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs`
- Create: `test/Aevatar.Capabilities.Tests/MainnetProjectionVersionRegressionRepairAdminEndpointsTests.cs`

**Interfaces:**

Routes:

```text
POST /api/admin/scheduled-agent-key/projection-repair/workspace
POST /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog
```

Requests:

```csharp
internal sealed record WorkspaceRepairRequest(
    [property: JsonPropertyName("scope_id")] string ScopeId,
    [property: JsonPropertyName("apply")] bool Apply,
    [property: JsonPropertyName("expected_actor_id")] string ExpectedActorId,
    [property: JsonPropertyName("expected_source_state_version")] long ExpectedSourceStateVersion,
    [property: JsonPropertyName("expected_document_state_version")] long ExpectedDocumentStateVersion,
    [property: JsonPropertyName("expected_document_last_event_id")] string ExpectedDocumentLastEventId,
    [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
    [property: JsonPropertyName("repair_reason")] string RepairReason);

internal sealed record CatalogRepairRequest(
    [property: JsonPropertyName("apply")] bool Apply,
    [property: JsonPropertyName("expected_actor_id")] string ExpectedActorId,
    [property: JsonPropertyName("expected_source_state_version")] long ExpectedSourceStateVersion,
    [property: JsonPropertyName("expected_document_state_version")] long ExpectedDocumentStateVersion,
    [property: JsonPropertyName("expected_document_last_event_id")] string ExpectedDocumentLastEventId,
    [property: JsonPropertyName("repair_request_id")] string RepairRequestId,
    [property: JsonPropertyName("repair_reason")] string RepairReason);
```

- [ ] **Step 1: Write failing endpoint tests**

Cover both endpoints:

- missing authorizer returns `503`;
- missing bearer returns `403`;
- identity-resolution failure returns `403`;
- cancellation propagates only when the supplied request token is actually
  canceled;
- an uncanceled authorization `OperationCanceledException` remains a fail-closed
  `403`;
- an uncanceled downstream `OperationCanceledException` returns sanitized
  `503`;
- non-elevated caller returns `403`;
- missing repair service returns `503`;
- invalid apply manifest returns `400`;
- `apply=false` calls only inspection;
- Workspace `Accepted` returns `202`;
- Workspace conflict returns `409`;
- catalog owner is always `PlatformCaller.UserId`;
- catalog ready returns `200`;
- catalog projection pending returns `202`;
- catalog conflict returns `409`;
- bearer text never appears in serialized responses.

- [ ] **Step 2: Run endpoint tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~MainnetProjectionVersionRegressionRepairAdminEndpointsTests"
```

Expected: compilation fails because the endpoint does not exist.

- [ ] **Step 3: Implement fail-closed authorization**

Use the same shape as `ScheduledAgentCredentialRepairAdminEndpoints`:

```csharp
var authorization = http.Request.Headers.Authorization.ToString();
var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
    ? authorization[7..].Trim()
    : string.Empty;
if (string.IsNullOrEmpty(bearer))
    return Results.Forbid();
var caller = await authorizer.ResolveCallerAsync(bearer, ct);
if (!caller.IsElevated || string.IsNullOrWhiteSpace(caller.UserId))
    return Results.Forbid();
```

Catalog repair must construct the service request with:

```csharp
VerifiedOwnerSubject: caller.UserId,
BearerToken: bearer,
RequestedBySubjectId: caller.UserId
```

There is no catalog owner field in the HTTP DTO.

- [ ] **Step 4: Map typed results honestly**

- Workspace inspection: `200`.
- Workspace accepted dispatch: `202`.
- Catalog inspection: `200`.
- Catalog ready: `200`.
- Catalog projection pending: `202`.
- Changed source/document: `409`.
- Missing provider or downstream unavailable: `503`.
- Unexpected inspection/apply exceptions: bodyless sanitized `503`, without
  exception text, bearer/credential values, or catalog contents.

Responses may include actor ID, versions, last event ID, request ID, command ID, refresh status, and visibility versions. They must not include bearer or catalog contents.

- [ ] **Step 5: Map endpoints in Mainnet Host**

Add:

```csharp
app.MapProjectionVersionRegressionRepairAdminEndpoints();
```

next to the existing scheduled credential repair endpoint.

- [ ] **Step 6: Run tests and verify GREEN**

Run the Task 4 command. Expected: all endpoint tests pass.

- [ ] **Step 7: Commit**

```bash
git add \
  src/Aevatar.Mainnet.Host.Api/ProjectionRecovery/ProjectionVersionRegressionRepairAdminEndpoints.cs \
  src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs \
  test/Aevatar.Capabilities.Tests/MainnetProjectionVersionRegressionRepairAdminEndpointsTests.cs
git commit -m "Expose guarded projection regression repair"
```

---

### Task 5: Runbook, regression guards, and final verification

**Files:**

- Create: `docs/operations/2026-07-25-projection-version-regression-repair.md`
- Modify: `docs/adr/0040-current-state-readmodel-dr-rebuild.md`
- Modify: `docs/README.md`
- Modify: `docs/canon/scheduled-skill-runners.md`

- [ ] **Step 1: Write the operator runbook**

Document:

1. call Workspace inspection with `apply=false`;
2. copy the returned source/document versions and last event ID into `apply=true`;
3. verify `202 Accepted`, then query the normal Workspace API;
4. call catalog inspection/apply using the same elevated owner bearer;
5. verify refresh `observed/ready`;
6. run scheduled Agent Key preflight and canary;
7. stop on any `409`; re-inspect instead of editing the manifest;
8. never use direct Elasticsearch deletion or lower-version writes.

Use distinct example identities:

```text
scopeId = scope-alpha
workspaceActorId = studio-workspace:scope-alpha
catalogOwnerSubject = user-alpha
```

- [ ] **Step 2: Update the DR ADR**

Clarify two separate recovery cases:

- read model missing while authority survives: republish current committed state;
- read model version exceeds authority after lineage loss: only a target-specific,
  operator-gated conditional delete followed by an authoritative rebuild source.

Explicitly reject generic replica deletion and ES-to-actor hydration.

- [ ] **Step 3: Update scheduled Agent Key canonical semantics**

State that catalog version regression repair rebuilds authorization evidence only
through a fresh NyxID observation and is not part of normal preflight/query behavior.

- [ ] **Step 4: Run documentation and architecture guards**

Run:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/architecture_guards.sh
bash tools/docs/lint.sh
```

Expected: every guard exits `0`.

- [ ] **Step 5: Run focused test suites**

Run:

```bash
dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~ElasticsearchProjectionDocumentStoreBehaviorTests"

dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~StudioWorkspaceProjectionRebuildTests|FullyQualifiedName~StudioWorkspaceVersionRegressionRepairServiceTests"

dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~NyxIdAuthorizationCatalogVersionRegressionRepairServiceTests"

dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo --tl:off -m:1 -p:UseSharedCompilation=false -p:NuGetAudit=false \
  --filter "FullyQualifiedName~MainnetProjectionVersionRegressionRepairAdminEndpointsTests"
```

Expected: all tests pass.

- [ ] **Step 6: Build the repository**

Run:

```bash
dotnet build aevatar.slnx --nologo --tl:off -m:1 \
  -p:UseSharedCompilation=false -p:NuGetAudit=false
```

Expected: build succeeds with no new warnings attributable to this change.

- [ ] **Step 7: Request code review and address findings**

Run a specification review and a code-quality review. Re-run affected tests after every accepted finding.

- [ ] **Step 8: Commit documentation**

```bash
git add \
  docs/operations/2026-07-25-projection-version-regression-repair.md \
  docs/adr/0040-current-state-readmodel-dr-rebuild.md \
  docs/README.md \
  docs/canon/scheduled-skill-runners.md
git commit -m "Document projection regression repair"
```

- [ ] **Step 9: Rebase and push**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
git push origin HEAD:feature/integrate
git ls-remote origin refs/heads/feature/integrate
```

Expected: the remote SHA equals local `HEAD`.

- [ ] **Step 10: Production acceptance after automatic deployment**

Using the local NyxID CLI and non-secret output only:

1. confirm the deployed source SHA;
2. inspect and repair the known Workspace regression;
3. verify the hidden draft is visible, then delete it normally;
4. inspect and repair the current caller's catalog regression;
5. verify catalog refresh is `observed/ready`;
6. create a temporary workflow/member/service/schedule with distinct IDs;
7. verify preflight accepts the exact non-wildcard UserService grant;
8. verify a dedicated Agent Key is created;
9. run now and verify `simple_qa` completes;
10. delete the schedule and verify the exact Agent Key and Vault reference are revoked;
11. clean up the workflow/member/team resources;
12. save a redacted canary evidence artifact and update issue `#2963`.
