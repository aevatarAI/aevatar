# Admin Observability Epic Completion Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` and `superpowers:test-driven-development` task-by-task.

**Goal:** Complete #3018 and #3019 so `/admin` has one authoritative workflow observatory UI and CQRS operators can inspect materialized projection-scope status plus recent committed-envelope metadata without replay or priming.

**Architecture:** `/admin#/observatory` embeds the existing `/workflow/observatory` asset and forwards the parent deep-link state, deleting the second renderer. Projection-scope actors retain a bounded typed metadata window in authoritative state; the existing current-state projector copies it into `ProjectionScopeStatusDocument`, and a read-only query port serves admin-only endpoints.

**Tech Stack:** .NET 10, Orleans GAgents, Protobuf, ASP.NET Core minimal APIs, vanilla HTML/CSS/JS, xUnit, FluentAssertions.

## Global Constraints

- Preserve `Domain / Application / Infrastructure / Host` dependency direction; Host only adapts HTTP.
- Query endpoints read materialized read models only: no event-store replay, rebuild, projection activation, or priming.
- Projection scope state and metadata are Protobuf typed fields owned by the scope actor.
- Recent metadata is bounded and payload-free; StateVersion comes from the committed source envelope.
- CQRS introspection stays platform-admin-only and GET-only.
- Preserve unrelated dirty-worktree files and stage only paths named below.

---

### Task 1: Make the standalone workflow observatory authoritative

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html`
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Modify: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowConsoleStaticAssetEndpointTests.cs`
- Modify: `docs/canon/backend-console.md`

**Interfaces:**
- Consumes: `/workflow/observatory` query parameters `scope`, `status`, `origin`, `schedule`, `from`, `to`, `run`, `tab`.
- Produces: `/admin#/observatory?...` iframe source with the same observation intent and one workflow-observatory renderer.

- [ ] Replace admin-renderer regression assertions with a failing contract that `/admin#/observatory` embeds the authoritative asset and forwards canonical deep-link values.
- [ ] Run the focused static-asset tests and confirm they fail because admin still dispatches `viewObservatory()`.
- [ ] Implement the smallest iframe/deep-link bridge and remove the unused admin observatory renderer/state/styles.
- [ ] Run both static-asset test classes and confirm pass.
- [ ] Document `/workflow/observatory` as the single renderer with `/admin` as its shell host.

### Task 2: Materialize bounded projection-scope introspection

**Files:**
- Modify: `src/Aevatar.CQRS.Projection.Core/projection_scope_messages.proto`
- Modify: `src/Aevatar.CQRS.Projection.Core/projection_scope_status_readmodel.proto`
- Modify: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs`
- Modify: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStateApplier.cs`
- Modify: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStatusProjector.cs`
- Create: `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionScopeIntrospectionQueryPort.cs`
- Create: `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeIntrospectionQueryPort.cs`
- Modify: `src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionScopeStatusRuntimeRegistration.cs`
- Modify: `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionScopeGAgentBaseTests.cs`
- Modify: `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionScopeStatusProjectorTests.cs`
- Create: `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionScopeIntrospectionQueryPortTests.cs`

**Interfaces:**
- Produces: `IProjectionScopeIntrospectionQueryPort.GetAsync(scopeActorId)` and `ListRecentEnvelopesAsync(scopeActorId, take)`.
- Produces metadata only: `eventId`, `typeUrl`, `stateVersion`, `timestamp`; no envelope payload.

- [ ] Write failing actor-state tests for successful metadata capture, payload exclusion, and bounded retention.
- [ ] Write failing projector/query tests proving only `ProjectionScopeStatusDocument` is read.
- [ ] Add minimal Protobuf fields, actor transition, projector mapping, query port, and DI registration.
- [ ] Run CQRS Projection Core tests and confirm pass.

### Task 3: Expose and render admin-only read endpoints

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/Cqrs/CqrsObservatoryApiEndpoints.cs`
- Modify: `src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html`
- Modify: `test/Aevatar.Capabilities.Tests/CqrsObservatoryApiEndpointsAuditTests.cs`
- Modify: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`
- Modify: `docs/canon/backend-console.md`

**Interfaces:**
- Produces: `GET /api/cqrs/scopes/{scopeActorId}`.
- Produces: `GET /api/cqrs/scopes/{scopeActorId}/recent-envelopes?take=20`.

- [ ] Write failing HTTP tests for detail, bounded recent metadata, 403-before-query, and missing scope.
- [ ] Write a failing asset contract for selecting a scope and rendering real metadata or an honest empty state.
- [ ] Add GET-only audited endpoint adapters and wire the existing CQRS page.
- [ ] Run capability tests and confirm pass.
- [ ] Document eventual consistency, StateVersion, and payload-free metadata.

### Task 4: Verify, publish, and close the epic

**Files:** all changed paths above only.

- [ ] Run focused test projects.
- [ ] Run `bash tools/ci/test_stability_guards.sh`, `bash tools/ci/query_projection_priming_guard.sh`, `bash tools/ci/projection_state_version_guard.sh`, `bash tools/ci/projection_state_mirror_current_state_guard.sh`, `bash tools/ci/backend_console_static_asset_guard.sh`, `bash tools/ci/workflow_observatory_readonly_guard.sh`, and `bash tools/ci/architecture_guards.sh`.
- [ ] Run `dotnet build aevatar.slnx --nologo` and the relevant solution tests.
- [ ] Inspect the rendered `/admin#/observatory` and `/cqrs` surfaces at desktop and narrow widths.
- [ ] Commit only epic files, push `feature/integrate`, and verify `origin/feature/integrate` resolves to the local commit.
- [ ] Comment with commit and verification evidence, close #3018 and #3019, then close #3013 after confirming all child issues are closed.
