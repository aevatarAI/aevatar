# Workflow Archive Boundary Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give authenticated scope users a correct Workflow Archive command and make Workflow Activity show exactly one destructive list action for each draft or published row.

**Architecture:** A new scope-owned Application port resolves published service and deployment identities from the authoritative Workflow read model, then dispatches the existing deployment deactivation command. The frontend feature branch calls only that scope contract and applies a published-dominant menu policy while retaining catalogue-based completion observation.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Protobuf service commands, xUnit, FluentAssertions, React, TypeScript, React Query, Jest, Testing Library, Biome.

---

### Task 1: Document The Archive Boundary

**Files:**
- Create: `docs/superpowers/specs/2026-08-10-workflow-archive-boundary-design.md`
- Create: `docs/superpowers/plans/2026-08-10-workflow-archive-boundary.md`
- Modify: `docs/canon/workflow-catalog-visibility.md`

- [ ] **Step 1: Record the approved product semantics**

Document these exact invariants:

```text
draft-only -> Delete draft
published with or without draft -> Archive
archived -> no Archive action
Archive -> deactivate deployment and preserve committed history
```

- [ ] **Step 2: Record the identity boundary**

Document the endpoint and source of every identity:

```http
POST /api/scopes/{scopeId}/workflows/{workflowId}:archive
```

```text
browser: scopeId + workflowId
read model: publishedServiceId + serviceAppId + serviceNamespace + deploymentId
```

- [ ] **Step 3: Commit the backend design with the implementation**

Stage the design, plan, canonical documentation, and backend source only after
the backend focused tests pass.

### Task 2: Add The Backend Archive Application Contract

**Files:**
- Create: `src/platform/Aevatar.GAgentService.Abstractions/Ports/IScopeWorkflowArchiveCommandPort.cs`
- Modify: `src/platform/Aevatar.GAgentService.Abstractions/ScopeWorkflows/ScopeWorkflowModels.cs`
- Create: `src/platform/Aevatar.GAgentService.Application/Workflows/ScopeWorkflowArchiveApplicationService.cs`
- Test: `test/Aevatar.GAgentService.Tests/Application/ScopeWorkflowArchiveApplicationServiceTests.cs`

- [ ] **Step 1: Write the failing identity-resolution test**

Add a test that supplies deliberately distinct identities:

```csharp
var workflow = new ScopeWorkflowSummary(
    ScopeId: "scope-alpha",
    WorkflowId: "wf-alpha",
    DisplayName: "Alpha",
    ServiceKey: "opaque-key",
    WorkflowName: "alpha",
    ActorId: "m-alpha",
    ActiveRevisionId: "rev-alpha",
    DeploymentId: "dep-alpha",
    DeploymentStatus: "Active",
    UpdatedAt: DateTimeOffset.UtcNow)
{
    PublishedServiceId = "svc-alpha",
    ServiceAppId = "workflow-app",
    ServiceNamespace = "workflow-namespace",
};

var result = await service.ArchiveAsync(
    new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));

commands.DeactivateCommand!.Identity.ServiceId.Should().Be("svc-alpha");
commands.DeactivateCommand.DeploymentId.Should().Be("dep-alpha");
result.WorkflowId.Should().Be("wf-alpha");
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScopeWorkflowArchiveApplicationServiceTests
```

Expected: compilation fails because the archive request, port, result, and
Application service do not exist.

- [ ] **Step 3: Add the narrow contract and accepted result**

Create:

```csharp
public interface IScopeWorkflowArchiveCommandPort
{
    Task<ScopeWorkflowArchiveAcceptedResult> ArchiveAsync(
        ScopeWorkflowArchiveRequest request,
        CancellationToken ct = default);
}
```

Add strongly typed models:

```csharp
public sealed record ScopeWorkflowArchiveRequest(string ScopeId, string WorkflowId);

public sealed record ScopeWorkflowArchiveAcceptedResult(
    string ScopeId,
    string WorkflowId,
    string DeploymentId,
    ScopeWorkflowCommandAcceptedHandle CommandHandle,
    string ReadModelUrl,
    string AcceptanceStage = "accepted",
    string PropagationStage = "readmodel_propagating");
```

- [ ] **Step 4: Implement minimal Application dispatch**

The implementation must:

```csharp
var lookup = await _workflowQueryPort.LookupByWorkflowIdAsync(scopeId, workflowId, ct);
if (!lookup.IsRunnable)
    throw ScopeWorkflowArchiveRejectedException.FromLookup(lookup);

var workflow = lookup.Workflow!;
EnsureActive(workflow.DeploymentStatus);
var receipt = await _serviceCommandPort.DeactivateServiceDeploymentAsync(
    new DeactivateServiceDeploymentCommand
    {
        Identity = new ServiceIdentity
        {
            TenantId = workflow.ScopeId,
            AppId = workflow.ServiceAppId,
            Namespace = workflow.ServiceNamespace,
            ServiceId = workflow.PublishedServiceId,
        },
        DeploymentId = workflow.DeploymentId,
    },
    ct);
```

Return a standard command handle with stage `deactivate_deployment` and the
read-model URL `/api/scopes/{scopeId}/workflows/{workflowId}`.

- [ ] **Step 5: Verify GREEN**

Run the same filtered test command. Expected: the identity-resolution test
passes.

- [ ] **Step 6: Write rejection tests**

Add separate tests proving:

```text
NotFound lookup -> archive rejection and zero service commands
Stale lookup -> archive rejection and zero service commands
Deactivated status -> WORKFLOW_NOT_ACTIVE and zero service commands
blank publishedServiceId -> WORKFLOW_ARCHIVE_IDENTITY_UNAVAILABLE and zero commands
```

- [ ] **Step 7: Run rejection tests and verify RED**

Run the filtered Application test command. Expected: new rejection assertions
fail until validation and typed rejection categories are implemented.

- [ ] **Step 8: Implement rejection categories and validation**

Add `ScopeWorkflowArchiveRejectedException` with stable `Code` and a failure
kind independent of HTTP. Normalize status with case-insensitive comparison to
`ServiceDeploymentStatus.Active.ToString()`. Validate every identity component
and the deployment ID before dispatch.

- [ ] **Step 9: Verify GREEN**

Run the filtered Application test command. Expected: all archive Application
tests pass with no command dispatched for any rejection case.

### Task 3: Expose The Scope-Owned HTTP Endpoint

**Files:**
- Modify: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs`
- Modify: `src/platform/Aevatar.GAgentService.Hosting/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs`

- [ ] **Step 1: Write the failing accepted endpoint test**

Add a recording `IScopeWorkflowArchiveCommandPort` and assert:

```csharp
var result = await ScopeWorkflowEndpoints.HandleArchiveWorkflowAsync(
    http,
    "scope-alpha",
    "wf-alpha",
    port,
    CancellationToken.None);

await result.ExecuteAsync(http);
http.Response.StatusCode.Should().Be(StatusCodes.Status202Accepted);
port.Request.Should().Be(new ScopeWorkflowArchiveRequest("scope-alpha", "wf-alpha"));
```

The test caller has `scope_id=scope-alpha` but deliberately has no service
identity claims.

- [ ] **Step 2: Run the endpoint test and verify RED**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter FullyQualifiedName~HandleArchiveWorkflowAsync
```

Expected: compilation fails because the handler does not exist.

- [ ] **Step 3: Map the route and handler**

Register:

```csharp
group.MapPost("/{scopeId}/workflows/{workflowId}:archive", HandleArchiveWorkflowAsync)
    .Produces<ScopeWorkflowArchiveAcceptedResult>(StatusCodes.Status202Accepted)
    .Produces(StatusCodes.Status400BadRequest)
    .Produces(StatusCodes.Status403Forbidden)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status409Conflict);
```

The handler must call `AevatarScopeAccessGuard` first, delegate to the archive
port, return `Results.Accepted`, and map typed Application rejection kinds.
It must not call `ServiceIdentityEndpointAccess`.

- [ ] **Step 4: Register the Application port**

Add:

```csharp
services.TryAddSingleton<
    IScopeWorkflowArchiveCommandPort,
    ScopeWorkflowArchiveApplicationService>();
```

- [ ] **Step 5: Verify GREEN**

Run the filtered endpoint test command. Expected: accepted route test passes.

- [ ] **Step 6: Add endpoint failure tests**

Add focused tests for scope mismatch, not found, and inactive conflict. Assert
that scope mismatch never calls the archive port and that error bodies contain
stable codes.

- [ ] **Step 7: Run endpoint tests**

Run the same filtered endpoint test command. Expected: all Archive handler
tests pass.

### Task 4: Add The Frontend Scope API

**Files (frontend-only branch):**
- Modify: `apps/aevatar-console-web/src/shared/models/scopes.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/scopesApi.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/scopesApi.test.ts`

- [ ] **Step 1: Write the failing API wrapper test**

Test the exact call:

```ts
const result = await scopesApi.archiveWorkflow('scope alpha', 'wf/alpha');

expect(mockFetch).toHaveBeenCalledWith(
  '/api/scopes/scope%20alpha/workflows/wf%2Falpha:archive',
  expect.objectContaining({ method: 'POST' }),
);
expect(result.workflowId).toBe('wf-alpha');
```

Use distinct `workflowId`, `deploymentId`, and command identity fixture values.

- [ ] **Step 2: Run the API test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/shared/api/scopesApi.test.ts --runInBand
```

Expected: test fails because `archiveWorkflow` is undefined.

- [ ] **Step 3: Add models, decoder, and API wrapper**

Add a `ScopeWorkflowArchiveAcceptedResult` model matching the backend response.
Decode all stable fields with structured decoder helpers. Implement a POST with
no service identity request body:

```ts
archiveWorkflow(scopeId: string, workflowId: string) {
  return requestJson(
    `/api/scopes/${encodeURIComponent(scopeId)}/workflows/${encodeURIComponent(workflowId)}:archive`,
    decodeScopeWorkflowArchiveAcceptedResult,
    { method: 'POST' },
  );
}
```

- [ ] **Step 4: Verify GREEN**

Run the focused API test. Expected: all `scopesApi.test.ts` tests pass.

### Task 5: Switch Archive And Normalize Destructive Menu Actions

**Files (frontend-only branch):**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Write failing menu-policy tests**

Create three rows with distinct identities and assert:

```text
draft-only: Delete draft present, Archive absent
published + draft: Delete draft absent, Archive present
published-only: Delete draft absent, Archive present
```

- [ ] **Step 2: Write the failing scope Archive call test**

Confirming Archive must assert:

```ts
expect(mockScopesApi.archiveWorkflow).toHaveBeenCalledWith(
  'scope-alpha',
  'wf-alpha',
);
expect(mockScopesApi.getWorkflowDetail).not.toHaveBeenCalled();
expect(mockServicesApi.deactivateDeployment).not.toHaveBeenCalled();
```

- [ ] **Step 3: Run Workflow Activity tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand
```

Expected: published-with-draft still exposes Delete draft and Archive still
calls the generic service API.

- [ ] **Step 4: Implement the minimal frontend change**

Remove the `servicesApi` import and detail lookup. Submit:

```ts
await scopesApi.archiveWorkflow(scopeId, target.workflowId);
```

Compute the list-level draft delete policy once per row:

```ts
const canDeleteDraft =
  row.capabilities.delete.available && !row.hasCommittedSource;
```

Use `canDeleteDraft` for the divider and Delete draft menu item. Keep the
existing `canArchiveWorkflow(row)` policy and observation-only retry behavior.

- [ ] **Step 5: Verify GREEN**

Run the focused Workflow Activity test. Expected: all tests pass.

### Task 6: Focused Validation And Delivery

**Files:**
- Review all files changed in each worktree independently.

- [ ] **Step 1: Run backend focused checks**

Run:

```bash
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter FullyQualifiedName~ScopeWorkflowArchiveApplicationServiceTests
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter FullyQualifiedName~HandleArchiveWorkflowAsync
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
```

Expected: all commands exit 0.

- [ ] **Step 2: Run frontend scope and focused checks**

Run the frontend incremental scope guard against
`origin/feat/2026-08-04_workflow-activity-vnext`, then run only:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/shared/api/scopesApi.test.ts src/pages/workflow-activity-vnext/index.test.tsx --runInBand
bash tools/ci/test_stability_guards.sh
```

Run Biome only for changed frontend files. Do not run full frontend Jest,
lint, `tsc`, or production build locally; GitHub CI owns full verification.

- [ ] **Step 3: Review backend and frontend diffs separately**

Use:

```bash
git diff --check
git diff --stat
git diff
```

Confirm the frontend branch contains only `apps/aevatar-console-web/**` files
and the backend branch contains no Workflow Activity frontend source.

- [ ] **Step 4: Commit and push the backend branch**

Stage only backend task files and commit:

```bash
git commit -m "Add scope workflow archive command"
git push -u origin fix/2026-08-10_workflow-archive-command
```

Create a PR targeting `feature/integrate` with focused validation results.

- [ ] **Step 5: Commit and push the frontend branch**

Stage only frontend task files and commit:

```bash
git commit -m "Fix workflow archive actions"
git push -u origin fix/2026-08-10_workflow-archive-boundary
```

Create a PR targeting `feat/2026-08-04_workflow-activity-vnext`, state its
dependency on the backend PR, list exact focused commands, and delegate the
full frontend suite/build to GitHub CI.
