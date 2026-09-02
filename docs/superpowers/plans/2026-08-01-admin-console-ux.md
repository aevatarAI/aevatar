# Admin Console UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore immersive observation and preserve operator context across refreshes while eliminating avoidable admin iframe reloads.

**Architecture:** The canonical Workflow Observatory owns run UI state and immersive behavior; the admin asset remains a thin module shell. Both use small route-keyed `sessionStorage` view-state helpers, and the shell reuses a same-route embedded view instead of rebuilding it.

**Tech Stack:** Checked-in embedded HTML/CSS/JavaScript, ASP.NET Core static asset endpoints, xUnit, FluentAssertions, Node `vm`.

## Global Constraints

- Keep `/workflow/observatory` as the only Workflow Observatory renderer and data client.
- Do not add a frontend build chain, dependency, API, read model, query priming, or process-local business state.
- Immersive mode is session-local and must not change canonical route state.
- Refresh and polling preserve scope, run, tab, inputs, and applicable scroll positions.
- Tests must use distinct identity fixtures and execute shipped behavior where practical.

---

### Task 1: Canonical observatory view state and immersive mode

**Files:**
- Modify: `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html`
- Test: `test/Aevatar.Workflow.Host.Api.Tests/WorkflowConsoleStaticAssetEndpointTests.cs`

**Interfaces:**
- Produces: `observatoryRouteKey(route)`, `readObservatoryViewState(storage, key, route)`, `writeObservatoryViewState(storage, key, route, position)`, `setImmersive(enabled)`, and `refreshObservatory()` JavaScript behavior.
- Emits: same-origin `{ source, type: "observatory-immersive", enabled }` messages when embedded.

- [x] **Step 1: Write failing shipped-JavaScript tests**

Cover route-isolated list/detail positions, malformed storage fallback, immersive session persistence and parent notification, selective scroll resets, and the manual refresh request path.

- [x] **Step 2: Run the focused test and verify RED**

Run: `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~WorkflowConsoleStaticAssetEndpointTests`

Expected: failures because the new view-state and immersive functions do not exist.

- [x] **Step 3: Implement the minimum canonical behavior**

Add the compact workspace/admin-tools surface, immersive CSS/state, Escape handling, real refresh, focus preservation, and route-keyed run-list/detail scroll capture/restore. Preserve the existing API and polling path.

- [x] **Step 4: Run the focused test and verify GREEN**

Run the Step 2 command. Expected: all focused tests pass.

### Task 2: Admin shell view lifecycle and route scroll restoration

**Files:**
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Test: `test/Aevatar.Capabilities.Tests/BackendConsoleStaticAssetEndpointTests.cs`

**Interfaces:**
- Consumes: observatory `observatory-immersive` messages.
- Produces: `adminRouteKey()`, `captureAdminViewState(routeKey)`, `restoreAdminViewState(routeKey)`, `canReuseEmbeddedView(currentKey, nextKey)`, and persistent-view metadata from `viewObservatoryFrame()`/other suite frames.

- [x] **Step 1: Write failing shell behavior tests**

Execute the shipped helpers with fake session storage and assert per-route scroll isolation, same-route persistent-view reuse, cross-route replacement, immersive shell state, and Escape forwarding.

- [x] **Step 2: Run the focused capability tests and verify RED**

Run: `dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo --no-restore --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests`

Expected: failures because the shell lifecycle helpers and message contract do not exist.

- [x] **Step 3: Implement stable shell rendering**

Factor shell header markup, record/restore `.view-scroll` positions per hash route, update same-route embedded shell chrome without replacing its iframe, and synchronize the immersive class/message.

- [x] **Step 4: Run the focused capability tests and verify GREEN**

Run the Step 2 command. Expected: all focused tests pass.

### Task 3: Canonical docs and release verification

**Files:**
- Modify: `docs/canon/backend-console.md`
- Modify: `docs/superpowers/specs/2026-07-24-admin-observatory-ui-design.md`

**Interfaces:**
- Documents the renderer/view-state ownership consumed by future admin changes.

- [x] **Step 1: Update canonical ownership and state contracts**

Record immersive ownership, parent-shell synchronization, scroll restoration semantics, and persistent iframe lifecycle. Mark the earlier approved observatory design as implemented/restored.

- [ ] **Step 2: Run required verification**

Run focused tests, `bash tools/ci/backend_console_static_asset_guard.sh`, `bash tools/ci/workflow_observatory_readonly_guard.sh`, `bash tools/ci/test_stability_guards.sh`, `bash tools/docs/lint.sh`, `bash tools/ci/architecture_guards.sh`, and `dotnet build aevatar.slnx --nologo --no-restore`.

- [ ] **Step 3: Browser-verify the actual page**

Verify desktop and mobile normal/immersive layouts, Escape, manual and polling refresh, same-route state, F5 restoration, focus, and text/control overlap.

- [ ] **Step 4: Rebase, reverify, commit, and push**

Fetch and rebase on the latest `origin/feature/integrate`, repeat affected verification, then push `HEAD:feature/integrate` without force.
