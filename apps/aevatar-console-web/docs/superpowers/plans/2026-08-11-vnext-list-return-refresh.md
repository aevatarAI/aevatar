# vNext List Return Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refresh Workflow Activity vNext collection data whenever users return from an editor or detail surface to Workflows or Activity.

**Architecture:** Keep freshness policy with each list query by forcing a refetch whenever that query mounts. Reproduce the production 30-second cache window in route-level tests so detail-to-list navigation proves a second request is made while cached data is still fresh.

**Tech Stack:** React, TypeScript, TanStack React Query, Jest, Testing Library, pnpm.

---

## File Map

- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`: make catalogue data revalidate on every list entry.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`: apply the same contract to the runs list.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`: cover Workflows route return with fresh cached data.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`: cover Activity remount with fresh cached data.
- Existing `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-vnext-list-return-refresh-design.md`: approved behavior contract.

### Task 1: Specify Workflows Return Freshness

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Add a production-cache route test**

Render with a local `QueryClient` whose `staleTime` is `30_000`, enter the
Workflows route, navigate to `/workflows/wf-alpha`, then return to `/workflows`.
Assert `queryWorkflowCatalogue` has been called twice.

- [ ] **Step 2: Run the exact test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern 'refreshes the workflow catalogue when returning from the editor'
```

Expected: FAIL because the current catalogue remains fresh and is requested
only once.

### Task 2: Specify Activity Return Freshness

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Add a production-cache remount test**

Render Activity with a local `QueryClient` whose `staleTime` is `30_000`, wait
for its first runs result, unmount and remount the page with the same client,
then assert `listRuns` has been called twice.

- [ ] **Step 2: Run the exact test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx --testNamePattern 'refreshes runs when returning to activity'
```

Expected: FAIL because the fresh runs result is reused without a second call.

### Task 3: Implement List-Owned Revalidation

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`

- [ ] **Step 1: Add the explicit mount policy to both queries**

```ts
refetchOnMount: 'always',
```

Place the option beside `retry: false` in the catalogue and runs query options.

- [ ] **Step 2: Run both exact regression tests and verify GREEN**

Run the two commands from Tasks 1 and 2. Expected: PASS.

- [ ] **Step 3: Run the changed test files**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Expected: both suites pass.

### Task 4: Focused Verification And Delivery

**Files:**

- Verify only the files listed in the File Map.

- [ ] **Step 1: Analyze the frontend change scope against the requested base**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run changed-file static checks and repository guards**

Run the analyzer-approved Biome command, `bash tools/ci/test_stability_guards.sh`,
the Workflow Activity vNext design-baseline verifier, and `git diff --check`.
Expected: all pass.

- [ ] **Step 3: Review and deliver**

Stage only this task's files, commit with an imperative single-purpose message,
push the branch, and create a PR targeting
`feat/2026-08-04_workflow-activity-vnext`. Record all focused commands and state
that full frontend validation is delegated to GitHub CI.
