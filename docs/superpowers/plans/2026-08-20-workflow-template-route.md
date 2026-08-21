# Workflow Template Route Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Make the vNext workflow template browser addressable at `/workflows/new/templates`, remove duplicated page copy, and present a truthful product message when the deployed backend does not expose the template contract.

**Architecture:** Add a route and navigation helper for the template creation surface. Keep `NewWorkflowPage` responsible for the creation chooser and its existing Describe/Import flows; render a dedicated `WorkflowTemplatesPage` for the template shell and compose the existing browser without its duplicate heading. Classify an initial catalogue 404 at the page boundary while preserving the raw error in technical details.

**Tech Stack:** React, TypeScript, Umi route config, React Testing Library, Jest, TanStack Query, Ant Design.

---

### Task 1: Lock the canonical route and URL builder

**Files:**
- Modify: `apps/aevatar-console-web/config/routes.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.ts`
- Test: `apps/aevatar-console-web/src/routesConfig.test.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/navigation.test.ts`

- [ ] **Step 1: Write the failing route and navigation tests**

  Add a route assertion for `/scopes/:scopeId/workflow-activity-vnext/workflows/new/templates`, assert it uses `./workflow-activity-vnext`, and assert its index is before `/workflows/:workflowId`. Add a navigation test asserting `buildWorkflowActivityTemplatesHref('scope with space')` returns `/scopes/scope%20with%20space/workflow-activity-vnext/workflows/new/templates`.

- [ ] **Step 2: Run the focused tests and verify they fail for missing route/helper**

  Run:

  ```bash
  pnpm --dir apps/aevatar-console-web exec jest src/routesConfig.test.ts src/pages/workflow-activity-vnext/navigation.test.ts --runInBand
  ```

  Expected result: the route lookup and missing helper assertions fail before production changes.

- [ ] **Step 3: Add the route and helper**

  Insert the explicit template route immediately after the existing `/workflows/new` route and before `/workflows/:workflowId`. Add `buildWorkflowActivityTemplatesHref` by appending `/templates` to `buildWorkflowActivityNewHref`.

- [ ] **Step 4: Run the focused tests and verify they pass**

  Re-run the command from Step 2 and expect all route and navigation assertions to pass.

### Task 2: Make template selection navigate instead of changing local mode

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowTemplatesPage.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`

- [ ] **Step 1: Write failing navigation and hierarchy tests**

  In the existing creation tests, change the template selection assertion to expect `history.push('/scopes/scope-alpha/workflow-activity-vnext/workflows/new/templates')` and render the template surface by changing `mockLocation` to that path. Add an index test that renders the template pathname, waits for the template fixture, and asserts exactly one `Start from a template` heading plus one `Browse public templates, inspect details, or create a draft directly.` paragraph. Add an assertion that `Change method` pushes the existing `/workflows/new` URL.

- [ ] **Step 2: Run the affected tests and verify the expected failures**

  Run:

  ```bash
  pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx --runInBand
  ```

  Expected result: template selection still uses local state and the dedicated route does not render.

- [ ] **Step 3: Implement route-owned rendering**

  In `index.tsx`, check `pathname.endsWith('/workflows/new/templates')` before `/workflows/new` and render `WorkflowTemplatesPage`. In `NewWorkflowPage`, remove the template browser import, the `template` creation mode branch, and the template-specific shell title/description. Keep the chooser item but make `selectMode('template')` push `buildWorkflowActivityTemplatesHref(scopeId)` and return without setting local mode. The new page composes `WorkflowActivityVNextShell`, uses the single title and description from the spec, renders `WorkflowTemplateBrowser`, and maps `Change method` to `buildWorkflowActivityNewHref(scopeId)`.

- [ ] **Step 4: Remove duplicate browser-owned heading and description**

  Keep `WorkflowTemplateBrowser` responsible for its toolbar, list, pagination, alerts, detail modal, and actions. Remove only its inner `Typography.Title`, inner supporting paragraph, and the obsolete page-level `Change method` wrapper so the dedicated page owns the hierarchy.

- [ ] **Step 5: Run the affected tests and verify they pass**

  Re-run the command from Step 2. All existing template list/detail/instantiate assertions must pass, with the new route and one-heading assertions green.

### Task 3: Present a contextual 404 state without hiding the technical error

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowTemplateBrowser.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`

- [ ] **Step 1: Write the failing 404 presentation test**

  Configure `listWorkflowTemplates` to reject with an error carrying `status: 404`, render the canonical template pathname, and assert the visible alert message is `Templates are not available in this environment.` while `HTTP 404 Not Found` remains visible inside the technical details disclosure. Assert the retry action remains available.

- [ ] **Step 2: Run the focused test and verify it fails with the generic 404 copy**

  Run:

  ```bash
  pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx --runInBand
  ```

- [ ] **Step 3: Add a narrow 404 classifier and render the contextual message**

  Add a local `isStatus(error, 404)` check using the existing `isStudioApiStatus` helper. For a browser list failure with status 404, render the product message as the alert `message`, place `errorMessage(browserFailure.message)` in `TechnicalDetails`, and keep `Retry`. Leave non-404 errors on the existing generic failure message and description.

- [ ] **Step 4: Run the focused tests and verify the 404 behavior passes**

  Re-run the focused Jest command and confirm the contextual message, technical detail, and retry assertions pass.

### Task 4: Focused validation, browser verification, and PR update

**Files:**
- Modify: affected source and test files from Tasks 1-3 only

- [ ] **Step 1: Run the frontend scope analyzer against the PR base**

  ```bash
  python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
  ```

- [ ] **Step 2: Read the affected runner instructions and run only affected tests/static checks**

  Run every changed/new Jest file reported by the analyzer and the changed-file Biome/static checks. Do not run the full frontend test suite, package-wide typecheck, or production build.

- [ ] **Step 3: Verify the real page in the existing Chrome tab**

  Navigate the already open localhost tab to the creation chooser, click `Use template`, confirm the address becomes `/workflows/new/templates`, confirm one page heading, use `Change method`, then return to the template URL and confirm the authenticated remote request still reports 404 until backend PR #3484 is deployed.

- [ ] **Step 4: Review the complete diff and stage only current-task files**

  Exclude the pre-existing untracked `findings.md`, `progress.md`, and `task_plan.md`. Run `git diff --check` and inspect the complete staged diff.

- [ ] **Step 5: Commit, push, and update PR #3495**

  ```bash
  git commit -m "Fix workflow template route and unavailable state"
  git push origin feat/2026-08-18_workflow-template-browser
  gh pr edit 3495 --repo aevatarAI/aevatar --body-file /tmp/workflow-template-pr-body.md
  ```

  Include the exact focused commands and state that the full frontend suite/build is delegated to GitHub CI.
