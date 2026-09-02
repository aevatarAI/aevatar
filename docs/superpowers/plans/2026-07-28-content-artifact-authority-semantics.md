# ContentArtifact Authority Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Complete issue #3011 on PR #2870 so ContentArtifact authorization, idempotency, query visibility, HTTP errors, and provider behavior match the confirmed product contract.

**Architecture:** Keep `ContentArtifactGAgent` as the sole write authority and current-state projection documents as the sole query source. Append becomes actor-numbered and `dedup_key`-idempotent without CAS; the other mutations remain actor-CAS guarded and require read capability. Projection stores perform ACL filtering before cursor paging, while Application maps artifact ACL denial to typed not-found results.

**Tech Stack:** .NET 10, C# 13, Protobuf, xUnit, FluentAssertions, ASP.NET Core minimal APIs, Aevatar CQRS projection stores.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` layering; HTTP endpoints only adapt typed Application outcomes.
- `ContentArtifactGAgent` owns artifact facts and write decisions; no write fact may be derived from a read model.
- Queries read current-state read models only; no event replay, projection priming, query-time materialization, or second ACL index.
- Internal persisted and transported contracts remain Protobuf.
- Principal ACL identity is `principal_id`; `principal_kind` remains descriptive and must not change matching.
- Append-only writers can append and retry blindly but cannot read, list, advance, redact, expire, tombstone, or attach to a run.
- The synchronous command receipt remains accepted-only.
- Store filtering precedes cursor paging; no in-process page membership post-filter is allowed.
- Do not add compatibility shims for the removed append CAS/revision-number inputs.
- Update ContentArtifact canon and run all architecture, projection, test-stability, and docs guards required by `AGENTS.md`.

---

### Task 1: Projection filter parity

**Files:**
- Modify: `src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionDocumentStore.cs`
- Modify: `test/Aevatar.CQRS.Projection.Core.Tests/InMemoryProjectionDocumentStoreBehaviorTests.cs`

**Interfaces:**
- Consumes: `ProjectionDocumentFilter.FieldPath`, Protobuf `IMessage.Descriptor`, and repeated `IEnumerable` values.
- Produces: InMemory `Eq` semantics matching Elasticsearch `term`: snake_case proto paths resolve and a scalar equals any repeated-field element.

- [ ] **Step 1: Add failing provider tests**

Add one test that filters `TestStoreReadModel` by `actor_id`, and one that filters its repeated `tags` field by scalar equality:

```csharp
new ProjectionDocumentFilter
{
    FieldPath = "actor_id",
    Operator = ProjectionDocumentFilterOperator.Eq,
    Value = ProjectionDocumentValue.FromString("actor-1"),
}

new ProjectionDocumentFilter
{
    FieldPath = "tags",
    Operator = ProjectionDocumentFilterOperator.Eq,
    Value = ProjectionDocumentValue.FromString("reader-1"),
}
```

- [ ] **Step 2: Verify the tests fail for the intended reasons**

Run:

```bash
dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~InMemoryProjectionDocumentStoreBehaviorTests'
```

Expected: the snake_case and repeated-element tests return no items.

- [ ] **Step 3: Resolve proto field names and repeated scalar equality**

In `ResolveFieldValue`, when `current is IMessage`, resolve each segment through `message.Descriptor.FindFieldByName(segment)` before CLR reflection and read it through the field accessor. In `MatchesFilter`, compare scalar operators against every element when `actualValue` is a non-string `IEnumerable`; keep scalar behavior unchanged.

- [ ] **Step 4: Verify provider behavior**

Run the Task 1 command. Expected: all InMemory behavior tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionDocumentStore.cs test/Aevatar.CQRS.Projection.Core.Tests/InMemoryProjectionDocumentStoreBehaviorTests.cs
git commit -m "Fix projection filter parity"
```

### Task 2: Actor-safe create and append semantics

**Files:**
- Modify: `src/Aevatar.ContentArtifacts.Abstractions/content_artifact_messages.proto`
- Modify: `agents/Aevatar.GAgents.ContentArtifacts/ContentArtifactGAgent.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Contracts/ContentArtifactContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Abstractions/IContentArtifactCommandPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ContentArtifactService.cs`
- Modify: `src/Aevatar.Studio.Projection/CommandServices/ActorDispatchContentArtifactCommandService.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactGAgentTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactCommandServiceTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactServiceTests.cs`

**Interfaces:**
- Produces: `AppendContentArtifactRevisionRequest(ContentArtifactRevisionWriteRequest Revision)`.
- Produces: `IContentArtifactCommandPort.AppendRevisionAsync(string scopeId, string artifactId, AppendContentArtifactRevisionRequest request, ContentArtifactPrincipalContract requester, CancellationToken ct = default)`.
- Produces: `AppendContentArtifactRevision` with reserved protobuf field 2 and no expected concurrency version.
- Consumes: client revision `dedup_key`; the actor assigns `revision_number`, `revision_id`, `created_at_utc`, and `availability`.

- [ ] **Step 1: Add failing Actor regression tests**

Cover these observable behaviors:

```csharp
await unauthorizedRetry.Should().ThrowAsync<InvalidOperationException>()
    .WithMessage("*not authorized*");
agent.State.Revisions.Should().HaveCount(2);
agent.State.Revisions.Values.Should().ContainSingle(x => x.DedupKey == "revision-two");
```

The tests must show: authorization precedes append duplicate detection; a writer-only principal appends without CAS; retrying the same `dedup_key` and facts emits no new event; conflicting facts under the same key fail; and actor-assigned revision identity is monotonic.

- [ ] **Step 2: Add the failing create retry test**

Create from backing content, make the backing port throw on subsequent reads, then retry the same logical create with different server-assigned timestamp/availability fields. Assert the retry succeeds and the port's open count does not increase. A changed title or content fact must still fail as a logical identity conflict.

- [ ] **Step 3: Verify Actor tests fail**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifactGAgentTests'
```

Expected: failures expose pre-authorization no-ops, backing re-open, CAS-coupled append, and caller-assigned revision identity.

- [ ] **Step 4: Make create canonicalization retry-stable**

Add one canonicalizer used by both first commit and retry hashing. It clones `CreateContentArtifact`, clears `requested_at_utc` and `first_revision.created_at_utc`, and forces `first_revision.availability` to `AVAILABLE`. In `HandleCreateAsync`, if state exists, compare this hash before `ValidateCreateAsync`; only first creation validates and reads backing content.

- [ ] **Step 5: Make append actor-numbered and dedup-key idempotent**

Reserve protobuf field 2, remove append CAS from DTOs/ports/dispatcher, and send revision facts without a revision id/number. In the Actor: authorize first; then validate artifact lifecycle; locate duplicates by normalized `revision.dedup_key`; compare only client-controlled semantic facts; otherwise set the next number and canonical id from authoritative state immediately before validation and commit.

- [ ] **Step 6: Remove Application write-fact side reads**

`ContentArtifactService.AppendRevisionAsync` may normalize and advisory-check provenance, but must neither require readable current state nor derive `Max(RevisionNumber) + 1`. Dispatch directly with the normalized route scope/artifact id and requester so a writer-only principal can append.

- [ ] **Step 7: Update command/service tests and verify**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifactGAgentTests|FullyQualifiedName~ContentArtifactCommandServiceTests|FullyQualifiedName~ContentArtifactServiceTests'
```

Expected: all selected tests pass and no append test supplies expected CAS or revision number.

- [ ] **Step 8: Commit**

```bash
git add src/Aevatar.ContentArtifacts.Abstractions agents/Aevatar.GAgents.ContentArtifacts src/Aevatar.Studio.Application/Studio src/Aevatar.Studio.Projection/CommandServices test/Aevatar.Studio.Tests/ContentArtifacts
git commit -m "Enforce content artifact write authority"
```

### Task 3: Read-capable mutation and store-side list ACL

**Files:**
- Modify: `agents/Aevatar.GAgents.ContentArtifacts/ContentArtifactGAgent.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionContentArtifactQueryPort.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ContentArtifactService.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactGAgentTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactProjectionTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactServiceTests.cs`

**Interfaces:**
- Consumes: `ProjectionDocumentQuery.Filters` for scope/user filters and `AnyOfFilters` for owner-or-reader ACL.
- Produces: advance/redact/expire authority = owner or a principal present in both writer and reader lists; tombstone remains owner-only.

- [ ] **Step 1: Add failing mutation authorization/CAS tests**

For advance, redact, expire, and tombstone, assert authorization occurs before lifecycle/revision/no-op probes and that even an exact duplicate with a stale expected version fails. Add writer-only denials and writer+reader success for advance/redact/expire.

- [ ] **Step 2: Add failing list ACL and paging tests**

Assert the generated query contains required `scope_id` and user filters in `Filters`, plus exactly these OR filters in `AnyOfFilters`:

```csharp
Equal("owner_principal_id", requesterPrincipalId)
Equal("reader_principal_ids", requesterPrincipalId)
```

Use the real InMemory store with owner, reader, writer-only, unrelated, and tombstoned documents. Request page size 1 and prove page 2 returns the other readable document rather than advancing past an inaccessible row.

- [ ] **Step 3: Verify the selected tests fail**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifactGAgentTests|FullyQualifiedName~ContentArtifactProjectionTests|FullyQualifiedName~ContentArtifactServiceTests'
```

- [ ] **Step 4: Enforce authorization before probes and CAS before no-op**

For each non-create mutation, validate `requested_by` against authoritative ACL before calling `EnsureActiveArtifact`, `GetRevision`, or checking duplicates. Advance/redact/expire require owner or writer+reader; then check the expected actor concurrency version before classifying an exact no-op. Tombstone authorizes owner and checks CAS before its duplicate branch.

- [ ] **Step 5: Move list membership entirely into the store query**

Build owner and repeated-reader membership as `AnyOfFilters`, retain scope and caller filters in `Filters`, and return `result.Items.Select(ToResponse)` directly. Delete `MatchesQuery` and its helper-only predicates so cursor membership has one source of truth.

- [ ] **Step 6: Align Application mutation pre-checks**

Treat writer-only as append-only. Advance/redact/expire advisory pre-checks require both write and read capability; tombstone requires owner. Convert artifact ACL denial to typed not-found in Task 4 rather than exposing the authorization reason.

- [ ] **Step 7: Verify and commit**

Run the Task 3 test command, then:

```bash
git add agents/Aevatar.GAgents.ContentArtifacts src/Aevatar.Studio.Application/Studio/Services src/Aevatar.Studio.Projection/QueryPorts test/Aevatar.Studio.Tests/ContentArtifacts
git commit -m "Align content artifact ACL semantics"
```

### Task 4: Typed non-leaking Application and HTTP outcomes

**Files:**
- Modify: `src/Aevatar.Studio.Application/Studio/Contracts/ContentArtifactContracts.cs`
- Modify: `src/Aevatar.Studio.Application/Studio/Services/ContentArtifactService.cs`
- Modify: `src/Aevatar.Studio.Projection/QueryPorts/ProjectionContentArtifactQueryPort.cs`
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/ContentArtifactEndpoints.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactServiceTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactProjectionTests.cs`
- Modify: `test/Aevatar.Studio.Tests/ContentArtifacts/ContentArtifactEndpointsTests.cs`

**Interfaces:**
- Produces: artifact ACL denial and nonexistent revision as `ContentArtifactNotFoundException`.
- Produces: `ContentArtifactIdentityConflictException` for occupied `scope + dedupKey` with a different logical create request.
- Keeps: `ContentArtifactContentUnavailableException` exclusively for authorized redacted, expired, or tombstoned content.

- [ ] **Step 1: Add failing service and endpoint status tests**

Cover 404 for ACL denial on metadata, revision, content, append, advance, redact, expire, tombstone, and attach-to-run. Cover 404 for an authorized reader's nonexistent revision and 410 only for redacted/expired/tombstoned content. Retain 401 for missing authentication and 403 for scope denial. Assert denied error bodies contain neither lifecycle facts nor concurrency versions.

- [ ] **Step 2: Add the create occupancy conflict test**

Seed the deterministic artifact id in the query port. A create by a different principal must throw `ContentArtifactIdentityConflictException` before dispatch; an exact same-owner retry may still dispatch so the Actor decides semantic equality. Endpoint mapping returns a typed conflict response containing only dedup-key occupancy.

- [ ] **Step 3: Verify status tests fail**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifactServiceTests|FullyQualifiedName~ContentArtifactProjectionTests|FullyQualifiedName~ContentArtifactEndpointsTests'
```

- [ ] **Step 4: Implement typed translations at the narrow boundaries**

Use `ContentArtifactNotFoundException` whenever the caller cannot read the artifact. `FindRevision` throws not-found rather than unavailable. The projection content reader authorizes before availability checks and also maps ACL denial to not-found. Add the advisory deterministic-id occupancy read in `CreateAsync` without replaying, priming, or querying actor state.

- [ ] **Step 5: Map HTTP responses uniformly**

Catch not-found before generic invalid requests on every read/command/attach handler. Catch unavailable as 410 in any handler where it can surface. Catch the typed occupancy conflict as HTTP 409 with a stable code and a message that reveals only key occupancy. Leave malformed requests and readable state/CAS conflicts as 400.

- [ ] **Step 6: Verify and commit**

Run the Task 4 command, then:

```bash
git add src/Aevatar.Studio.Application/Studio src/Aevatar.Studio.Projection/QueryPorts src/Aevatar.Studio.Hosting/Endpoints test/Aevatar.Studio.Tests/ContentArtifacts
git commit -m "Hide content artifact ACL existence"
```

### Task 5: Canon, regression, and integration delivery

**Files:**
- Modify: `docs/canon/content-artifacts.md`
- Verify: all changed production and test files

**Interfaces:**
- Documents: Team ownership versus ACL; append-only writer; Actor CAS versus advisory read-model checks; shared dedup namespace versus protected artifact facts; store-side ACL paging.

- [ ] **Step 1: Update canon**

State explicitly that append has no CAS and is `dedup_key` idempotent, writer-only is append-only, advance/redact/expire require read+write, tombstone is owner-only, create collisions expose occupancy only, and list filtering/paging occurs in the projection provider.

- [ ] **Step 2: Run focused tests**

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifact'
dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~InMemoryProjectionDocumentStoreBehaviorTests|FullyQualifiedName~ElasticsearchProjectionDocumentStoreBehaviorTests'
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --no-restore --filter 'FullyQualifiedName~ContentArtifact'
```

- [ ] **Step 3: Run required guards**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/projection_route_mapping_guard.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/docs/lint.sh
bash tools/ci/architecture_guards.sh
git diff --check
```

- [ ] **Step 4: Run fresh full verification**

```bash
dotnet restore aevatar.slnx --nologo
dotnet build aevatar.slnx --nologo --no-restore
dotnet test aevatar.slnx --nologo --no-restore
```

- [ ] **Step 5: Commit canon and final test adjustments**

```bash
git add docs/canon/content-artifacts.md test src agents
git commit -m "Document content artifact authority semantics"
```

- [ ] **Step 6: Rebase, re-run affected verification, and deliver through PR #2870**

Fetch `origin/feature/integrate`, rebase the PR branch if it advanced, re-run focused tests and required guards, push the PR branch with an explicit force-with-lease if rebased, wait for required CI, and merge PR #2870. Confirm `origin/feature/integrate` contains the merged head. Do not push directly around branch protection or required review policy.
