# Workflow Schedule Recent Fires Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the approved Schedule detail state so users can inspect authoritative recent fire history before changing a Schedule.

**Architecture:** Keep `WorkflowScheduleSurface` as the single Schedule management state machine and add a `detail` view between the collection and edit form. The detail view reads `workflowScheduleApi.get(scopeId, workflowId, scheduleId)` through TanStack Query, renders only returned summary and `recentFires` facts, and reuses the existing mutation functions. Editing starts from the observed detail and Cancel returns to detail.

**Tech Stack:** React 19, TypeScript, Ant Design, TanStack Query, Jest, Testing Library, existing Workflow vNext tokens and locale catalogues.

---

### Task 1: Protect Schedule detail and recent-fire behavior

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Extend the existing Schedule fixture with an authoritative detail response**

  Add one reusable `WorkflowScheduleSummary` fixture and configure `workflowScheduleApi.get` to return:

  ```ts
  {
    schedule: scheduleSummary,
    recentFires: [
      {
        scheduledFireAt: '2026-08-20T01:00:00Z',
        completedAt: '2026-08-20T01:02:00Z',
        idempotencyKey: 'schedule-alpha:fire:1',
        error: '',
        manual: false,
      },
      {
        scheduledFireAt: '2026-08-19T03:00:00Z',
        completedAt: '2026-08-19T03:01:00Z',
        idempotencyKey: 'schedule-alpha:manual:1',
        error: 'Workflow invocation failed',
        manual: true,
      },
    ],
  }
  ```

- [ ] **Step 2: Write the failing detail-to-edit integration test**

  Render the management modal with `initialView="list"`, click `View Daily workflow run`, and assert:

  ```ts
  expect(workflowScheduleApi.get).toHaveBeenCalledWith(
    'scope-alpha',
    'wf-alpha',
    'schedule-alpha',
  );
  expect(screen.getByText('Schedule details')).toBeVisible();
  expect(screen.getByText('Recent fires')).toBeVisible();
  expect(screen.getByText('Succeeded')).toBeVisible();
  expect(screen.getByText('Failed')).toBeVisible();
  expect(screen.getByText('Scheduled')).toBeVisible();
  expect(screen.getByText('Manual')).toBeVisible();
  expect(screen.getByText('Workflow invocation failed')).toBeVisible();
  ```

  Then click `Change schedule`, assert the editable form is visible, click `Cancel`, and assert the same Schedule detail and recent fires are visible again.

- [ ] **Step 3: Write the failing empty/error distinction test**

  For a successful detail response with `recentFires: []`, assert `No fires yet`. For a rejected detail request, assert `Schedule details couldn't be loaded`, a `Retry` action, and absence of `No fires yet`; clicking Retry must call the exact detail API again.

- [ ] **Step 4: Run the new tests and verify RED**

  Run:

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest \
    --runInBand \
    --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx \
    --testNamePattern='detail|recent fires'
  ```

  Expected: FAIL because the current list exposes `Edit`, never calls `workflowScheduleApi.get`, and has no detail/history view.

### Task 2: Restore the Schedule detail state

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Add typed detail selection and query state**

  Extend the local state without adding another data owner:

  ```ts
  type ScheduleSurfaceView = 'list' | 'detail' | 'form';

  const [selectedSchedule, setSelectedSchedule] =
    React.useState<WorkflowScheduleSummary | null>(null);

  const scheduleDetail = useQuery({
    enabled: open && available && Boolean(selectedSchedule),
    queryKey: [...queryKey, 'detail', selectedSchedule?.scheduleId],
    queryFn: () =>
      workflowScheduleApi.get(
        scopeId,
        workflowId,
        selectedSchedule?.scheduleId ?? '',
      ),
    retry: false,
  });
  ```

  Reset `selectedSchedule` when the surface opens for another Workflow. `openDetail(schedule)` selects the exact summary and enters `detail`; `openEdit()` hydrates from `scheduleDetail.data.schedule` and enters `form`.

- [ ] **Step 2: Render backend-honest detail states**

  Replace the list's pencil action with an eye action named `View {name}`. The detail view must render:

  ```text
  Schedule details
  Workflow and enabled state
  Schedule name
  Next fire / Last fire
  Total fires / Failed fires
  Cron expression / Timezone
  Run input
  Recent fires
  ```

  Each recent fire uses `error.trim()` to choose `Failed` or `Succeeded`, uses `manual` to choose `Manual` or `Scheduled`, and formats only `scheduledFireAt` and `completedAt` returned by the API. Do not display the idempotency key or derive a Run URL.

  Loading uses a stable status region. A successful empty array renders `No fires yet`. A rejected detail query renders an inline retryable error and never the empty state.

- [ ] **Step 3: Keep detail, edit, and mutation transitions coherent**

  `Change schedule` enters edit from `scheduleDetail.data.schedule`. Cancel from edit returns to detail. Back from detail returns to the Schedule list. Update returns to detail and refreshes the list/detail query prefix. Enable/disable and run-now refresh detail in place. Delete refreshes authoritatively and then returns to the list.

- [ ] **Step 4: Add localized operational copy**

  Add matching English and Chinese entries for:

  ```text
  Schedule details
  View {name}
  Change schedule
  Loading schedule details…
  Schedule details couldn't be loaded
  Recent fires
  No fires yet
  Total fires
  Failed fires
  Last fire
  Scheduled / Manual
  Succeeded / Failed
  Scheduled {date}
  Completed {date}
  Back to schedules
  ```

- [ ] **Step 5: Add compact detail styles using existing tokens**

  Add `wa-vnext__schedule-detail-*` classes for a two-column facts grid, a bordered recent-fire list, status/source rows, wrapped error text, and a footer. Collapse the facts grid to one column below the existing mobile breakpoint; do not add nested cards or new color tokens.

- [ ] **Step 6: Run the focused component tests and verify GREEN**

  Run:

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest \
    --runInBand \
    --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx
  ```

  Expected: one suite passes, including detail success, empty, failure/retry, edit/Cancel, creation, and mutation behavior.

### Task 3: Verify the owning route and frontend boundaries

**Files:**
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `apps/aevatar-console-web/src/shared/api/workflowScheduleApi.test.ts`
- Verify: changed source, test, locale, style, spec, and plan files

- [ ] **Step 1: Run the catalogue integration test file**

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest \
    --runInBand \
    --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
  ```

  Expected: the published Workflow still opens one Schedule management modal and all tests pass.

- [ ] **Step 2: Run the existing Workflow Schedule adapter test**

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest \
    --runInBand \
    --runTestsByPath src/shared/api/workflowScheduleApi.test.ts
  ```

  Expected: the exact Workflow-scoped detail route and `recentFires` decoder remain passing without adapter changes.

- [ ] **Step 3: Run changed-file static and repository checks**

  ```bash
  pnpm --dir apps/aevatar-console-web exec biome check \
    src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx \
    src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx \
    src/pages/workflow-activity-vnext/styles.ts \
    src/locales/workflowActivityVNextMessages.en-US.ts \
    src/locales/workflowActivityVNextMessages.zh-CN.ts
  bash tools/ci/test_stability_guards.sh
  bash tools/docs/lint.sh
  git diff --check
  ```

  Expected: all commands exit 0. Do not run full frontend tests, package-wide typecheck, or production build locally; GitHub CI owns those under the incremental frontend policy.

- [ ] **Step 4: Review, commit, push, and update the existing PR**

  Run the frontend scope analyzer with base `origin/feat/2026-08-04_workflow-activity-vnext`, inspect the complete diff, stage only this task's files, and commit:

  ```bash
  git commit -m "Show workflow schedule recent fires"
  git push origin feat/2026-08-20_workflow-schedule-frontend
  ```

  Append the exact focused commands and results to PR #3498 while preserving its Draft and `[DO NOT MERGE]` state. Leave `.planning/` untracked.
