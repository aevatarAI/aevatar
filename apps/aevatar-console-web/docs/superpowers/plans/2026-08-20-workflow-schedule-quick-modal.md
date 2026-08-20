# Workflow Schedule Quick Modal Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Make Workflow Schedule management match the approved Schedule design baseline by opening a management modal from the catalogue, exposing existing Schedules for editing, and sharing one configure/preview/review/create flow with the editor panel.

**Architecture:** Keep `WorkflowScheduleSurface` as the single owner of Schedule query and mutation state. The catalogue entry opens the same list management view as the editor Drawer; `New schedule` enters the surface-local state machine (`configure`, `previewing`, `review`). After create the catalogue closes with a Toast, while the editor returns to list management with the same Toast. Continue using only workflow-scoped Schedule APIs.

**Tech Stack:** React, TypeScript, Ant Design, TanStack Query, Jest Testing Library, existing Workflow vNext CSS tokens and i18n helpers.

---

### Task 1: Lock the intended entry point and state machine with tests

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [x] **Step 1: Add a failing catalogue test for the Schedule management modal**

  Render the published catalogue row, click `Manage schedules for Published workflow`, and assert that the visible dialog shows `Schedules`, `No schedules yet`, and `New schedule`, without entering the creation form.

- [x] **Step 2: Add failing Schedule-surface tests for configure and review states**

  Assert that the configure view renders `How often`, `Run input (optional)`, the repeat controls, and a `Review schedule` button. Select the custom cron path or use the default weekday preset, click review, resolve `workflowScheduleApi.preview`, and assert the returned five fire times plus `Create schedule` are shown.

- [x] **Step 3: Run the focused tests and confirm RED**

  Run:

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern='schedule|Schedule'
  ```

  Expected result: FAIL because the catalogue currently opens the list modal, the surface renders a nested Create schedule Modal, and no review state exists.

### Task 2: Implement the shared configure/review state machine

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [x] **Step 1: Replace the nested form Modal state with explicit creation state**

  Add typed state for `creationStep`, `repeatPreset`, `repeatTime`, `cronMode`, and the existing API form fields. Keep list/detail mutations unchanged. Initialize the modal surface in `configure` and initialize panel creation from the list view.

- [x] **Step 2: Render the design-aligned configure view**

  Render the blue Workflow context block, Schedule name, `How often` repeat/time/timezone controls, `write it as cron instead` disclosure, `Run input (optional)`, and footer actions `Cancel` plus `Review schedule`. Map presets to five-field cron without calculating fire times in the browser.

- [x] **Step 3: Render previewing and review states**

  `Review schedule` calls `workflowScheduleApi.preview(scopeId, workflowId, { cronExpression, timezone, count: 5 })`. Previewing shows a pending state, errors return to configure while preserving fields, review shows the Workflow/name/repeat/timezone/enabled/prompt/five returned fire times, and create submits the existing `WorkflowScheduleConfigurationInput`. After `202 Accepted`, close the catalogue modal or return the editor panel to its list, show a Toast, and continue refreshing the workflow-scoped list.

- [x] **Step 4: Add only the required localized labels and messages**

  Add matching English and Chinese keys for the human repeat builder, context/status labels, review actions, accepted state, and explanation text. Do not add Team/member/service identity copy.

- [x] **Step 5: Run the focused Schedule tests and confirm GREEN**

  Run:

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx
  ```

  Expected result: all Schedule surface tests pass.

### Task 3: Make the catalogue entry open the Schedule management modal

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [x] **Step 1: Pass a direct-create intent to the modal surface**

  Keep the existing workflow identity and availability guard, but make the catalogue `Schedules` action render the surface in `list` mode. The list exposes `New schedule` and existing row actions; the editor panel continues to use the same list state.

- [x] **Step 2: Update the catalogue regression assertion**

  Assert that clicking the published row Schedules action opens exactly one dialog with `Schedules`, `No schedules yet`, and `New schedule`, while `Review schedule` is absent until the user chooses to create one.

- [x] **Step 3: Run the targeted catalogue test**

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern='direct.*schedule|schedule.*quick|Schedule'
  ```

  Expected result: the targeted catalogue tests pass.

### Task 4: Align the modal and panel presentation with the design baseline

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`

- [x] **Step 1: Add dedicated schedule quick-modal tokens and layout rules**

  Match the baseline proportions: 820px modal, compact header, blue context panel, two-column form grid on desktop, stacked controls on mobile, section dividers, review and next-fire panels, and a footer with one primary action.

- [x] **Step 2: Scope portal tokens and focus styles**

  Keep the existing Modal/Drawer portal token scope, but apply it to the new quick surface and make the close control use a restrained 2px focus indicator without the oversized blue rectangle.

- [x] **Step 3: Verify the editor panel reuses the same flow**

  Open the editor Schedule panel, enter New schedule, move to Review schedule, go back, and confirm the canvas remains visible and no nested Modal is created.

### Task 5: Focused verification and delivery

**Files:**
- Verify only the files changed by Tasks 1-4.

- [x] **Step 1: Run the affected test files**

  ```bash
  CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern='schedule|Schedule'
  CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/api/workflowScheduleApi.test.ts
  ```

- [x] **Step 2: Run changed-file static checks**

  ```bash
  pnpm --dir apps/aevatar-console-web exec biome check src/locales/workflowActivityVNextMessages.en-US.ts src/locales/workflowActivityVNextMessages.zh-CN.ts src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/styles.ts src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx
  git diff --check
  ```

- [x] **Step 3: Inspect the existing Chrome tab at `http://127.0.0.1:5175`**

  Verify the direct catalogue quick modal, configure/review transition, mobile stacking if available, and editor panel reuse against the Schedule PNG baseline.

- [x] **Step 4: Review, stage only current-task files, commit, push, and update PR #3498**

  Commit message: `Align workflow schedule creation with design`

  Record exact focused commands and results in PR #3498. Full frontend suite, typecheck, and production build remain delegated to GitHub CI.

### Verification record

- Focused rendered Jest: 2 suites passed, 9 matching tests passed, 113 tests skipped (`CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern='schedule|Schedule'`).
- Workflow Schedule API Jest: 1 suite passed, 3 tests passed (`CI=1 pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/api/workflowScheduleApi.test.ts`).
- Changed-file Biome: 6 files checked with no fixes.
- `bash tools/ci/test_stability_guards.sh`: passed.
- `git diff --check HEAD~3..HEAD`: passed.
- `python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`: passed; 17/17 baseline frames, 6/6 Schedule frames, 6 standalone 1440x900 Schedule PNGs, and byte-identical generator output.
- Chrome visual verification: desktop and 390px mobile Catalogue quick modal, editor Schedule Drawer reuse, and Review state with five returned fire times; no horizontal overflow at 390px. No Schedule was created.
- Accepted create observation: React Query refetches the workflow-scoped list every second after `202 Accepted` and stops only when the receipt `scheduleId` appears in the read model; the regression test covers two misses followed by an observed item.
- Full frontend suite, full typecheck, and production build remain delegated to GitHub CI under the local incremental validation policy.
