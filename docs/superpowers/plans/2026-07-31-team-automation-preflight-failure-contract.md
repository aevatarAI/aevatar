# Team Automation Preflight Failure Contract Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every failed Team automation preflight an honest typed HTTP error with a sanitized user message and a production-safe stable failure code.

**Architecture:** Keep planner and application result types unchanged. Translate failed results once at `StudioMemberAutomationEndpoints`, reuse the frontend `TeamAutomationApiError` boundary, and show its sanitized message at the owning page while retaining the existing retry and NyxID-binding recovery paths.

**Tech Stack:** .NET 9 minimal APIs, xUnit, FluentAssertions, React 19, TypeScript, Jest, Testing Library.

## Global Constraints

- Return HTTP 200 only when `StudioMemberWorkflowAuthorizationResult.Success` is true.
- Do not expose or log planner `Detail`, bearer tokens, external bindings, catalog contents, permission digests, or credential material.
- Keep `memberId`, `workflowId`, and `publishedServiceId` distinct in fixtures and code.
- Reuse the existing Team automation error envelope and `TeamAutomationApiError`; add no dependency or new error hierarchy.
- Preserve the existing typed retry and NyxID binding recovery behavior.

---

### Task 1: Backend preflight HTTP contract

**Files:**
- Modify: `test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs`
- Modify: `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`

**Interfaces:**
- Consumes: `IStudioMemberWorkflowSchedulePort.PreflightForWriteAsync(...)` returning `StudioMemberWorkflowAuthorizationResult`.
- Produces: HTTP 200 for success or the existing JSON error shape `{ code, message, retryable }` for every failed result.

- [ ] **Step 1: Write failing endpoint tests**

Add tests that configure `StubSchedules.PreflightResult` with literal failure results and assert:

```csharp
new StudioMemberWorkflowAuthorizationResult(
    false,
    null,
    ScheduledInvocationAuthorizationFailureCode.ServiceAccessDenied,
    "private-service-id")
```

returns 403 with `TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED`, a sanitized message, `retryable=false`, and no `private-service-id`; and that `DurableAuthorizationUnavailable` returns 503 with `retryable=true`.

- [ ] **Step 2: Run tests and verify RED**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~StudioMemberAutomationEndpointsTests.Preflight_WhenPlanner'
```

Expected: both tests fail because the endpoint currently returns HTTP 200 and the result object.

- [ ] **Step 3: Implement the minimal endpoint mapping**

Store the application result, return it with `Results.Ok` only when successful, otherwise map the enum in one private switch. Use 400 for `TargetInvalid`, `OwnerInvalid`, and `UnknownEnum`; 403 for `OwnerMismatch`, `ServiceNotFound`, `ServiceAmbiguous`, `ServiceAccessDenied`, and `NodeGrantMissing`; 409 for `AuthorizationPlanChanged`; and 503 with `retryable=true` for `SnapshotNotFound`, `SnapshotStale`, `DurableAuthorizationUnavailable`, and `CatalogProjectionPending`. The default must be a sanitized non-retryable 400 response.

Inject `ILoggerFactory` into `HandlePreflightAsync` and log only route identities plus `FailureCode` when mapping a failed result.

- [ ] **Step 4: Run endpoint tests and verify GREEN**

Run:

```bash
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --filter 'FullyQualifiedName~StudioMemberAutomationEndpointsTests'
```

Expected: all endpoint tests pass.

### Task 2: Frontend typed failure presentation

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx`

**Interfaces:**
- Consumes: existing `TeamAutomationApiError(message, status, code, options)` from `teamAutomationApi.ts`.
- Produces: the backend sanitized message in the shared toast for a typed non-recoverable preflight failure; untyped failures retain the generic fallback.

- [ ] **Step 1: Write failing adapter and page tests**

Change the adapter test to invoke `preflightCreate` against a 403 envelope and assert the thrown `TeamAutomationApiError` preserves status, code, message, and `retryable=false`. Change the existing page failure test to reject with:

```typescript
new TeamAutomationApiError(
  "This automation is not authorized to use one or more required services.",
  403,
  "TEAM_AUTOMATION_AUTHORIZATION_SERVICE_ACCESS_DENIED",
)
```

and assert that exact sanitized message is sent to `message.error` while raw catalog detail is never rendered.

- [ ] **Step 2: Run tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/api/teamAutomationApi.test.ts src/pages/teams/tabs/TeamAutomationsTab.test.tsx --testNamePattern 'typed preflight|authorization preflight failures'
```

Expected: the page test fails because it currently always passes the generic localized fallback to `message.error`.

- [ ] **Step 3: Implement the minimal page mapping**

In the existing preflight catch block, preserve binding recovery first, then pass `error.message` only when `error instanceof TeamAutomationApiError`; otherwise keep `copy("teams.automations.authorization.error", "Authorization could not continue")`.

- [ ] **Step 4: Run focused frontend tests and verify GREEN**

Run the same focused Jest command and then:

```bash
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web exec biome lint src/shared/api/teamAutomationApi.ts src/shared/api/teamAutomationApi.test.ts src/pages/teams/tabs/TeamAutomationsTab.tsx src/pages/teams/tabs/TeamAutomationsTab.test.tsx
```

Expected: focused tests, TypeScript, and affected-file lint pass.

### Task 3: Repository verification and delivery

**Files:**
- Verify all modified files from Tasks 1 and 2 plus the approved spec and this plan.

**Interfaces:**
- Consumes: completed backend and frontend behavior.
- Produces: one reviewed commit pushed to `origin/feature/integrate`.

- [ ] **Step 1: Run mandatory guards and builds**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo --no-build
```

Run the frontend production build only if the changed surface or static checks expose a bundling risk; the frontend testing policy otherwise requires focused Jest plus `tsc` and affected-file lint.

- [ ] **Step 2: Review the exact diff and secret boundaries**

```bash
git diff --check
git diff --stat origin/feature/integrate...HEAD
git diff origin/feature/integrate...HEAD -- src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx
```

Confirm the diff contains no planner detail in responses/logs and no credentials or identity conflation.

- [ ] **Step 3: Commit and push the authorized target**

```bash
git add docs/superpowers/plans/2026-07-31-team-automation-preflight-failure-contract.md src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs test/Aevatar.Studio.Tests/StudioMemberAutomationEndpointsTests.cs apps/aevatar-console-web/src/shared/api/teamAutomationApi.test.ts apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.tsx apps/aevatar-console-web/src/pages/teams/tabs/TeamAutomationsTab.test.tsx
git commit -m "Fix Team automation preflight error contract"
git fetch origin feature/integrate
git merge --no-edit origin/feature/integrate
git push origin HEAD:feature/integrate
```

If the remote moved, merge once, rerun affected verification, and push without force.
