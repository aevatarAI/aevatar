# Run Detail Refresh Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make manual Run Detail refresh visibly acknowledge the request and report whether all authoritative sources refreshed successfully.

**Architecture:** Keep React Query as the owner of detail, graph, and history data. Add one page-local grouped refresh command that awaits all three existing `refetch()` calls, owns only the manual-action pending state, and reports a single success or failure toast while leaving committed content mounted.

**Tech Stack:** React, TypeScript, TanStack React Query, Ant Design Button, ConsoleToast, Jest, Testing Library.

---

### Task 1: Lock The Refresh Interaction Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [x] **Step 1: Add a failing pending/success test**

Render the loaded page, defer the second `getRun`, `getRunGraph`, and `listRuns` calls, click `Refresh`, and assert that the button becomes disabled with the accessible name `Refreshing…`. Resolve all three requests and assert one `Run details refreshed` success toast before the button returns to `Refresh`.

- [x] **Step 2: Add a failing partial-failure test**

Render the loaded page, make the second graph request reject, click `Refresh`, and assert one `Some run details couldn't be refreshed` error toast with no success toast.

- [x] **Step 3: Run the focused test and verify RED**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: both new tests fail because the existing Refresh button never changes state and never emits completion feedback.

### Task 2: Implement The Grouped Refresh Command

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [x] **Step 1: Add page-local manual refresh state**

Add `refreshing` state and an async `refreshRunDetail` callback. Guard duplicate execution, start pending state, await all three `refetch()` calls with `Promise.allSettled`, and inspect both rejected promises and fulfilled React Query results with `isError`.

- [x] **Step 2: Report one completion result**

When every source succeeds, call `toast.success('Run details refreshed', { key: 'run-detail-refresh' })`. When any source fails, call `toast.error("Some run details couldn't be refreshed", { key: 'run-detail-refresh' })`. Clear pending state in `finally`.

- [x] **Step 3: Connect every manual refresh entry point**

Use the grouped callback for the loaded header button, fallback header button, and failure-action retry/reload path. While pending, set the header button `loading` prop and render `Refreshing…`; otherwise render `Refresh`.

Keep the header action width stable across both labels and provide matching English and Chinese feedback messages.

- [x] **Step 4: Run the focused test and verify GREEN**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: all Run Detail tests pass with no warnings.

### Task 3: Run Incremental Verification And Deliver

**Files:**
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Verify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`

- [x] **Step 1: Analyze the frontend change scope**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base HEAD
```

- [x] **Step 2: Run dependency-related tests and exact static checks**

```bash
pnpm exec jest --findRelatedTests src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx --runInBand
pnpm exec biome check src/locales/workflowActivityVNextMessages.en-US.ts src/locales/workflowActivityVNextMessages.zh-CN.ts src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx src/pages/workflow-activity-vnext/styles.ts
```

Expected: every related Jest suite passes and Biome reports no diagnostics. Do not run a full frontend suite, typecheck, or production build.

- [x] **Step 3: Run applicable repository guards**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5175/
```

- [x] **Step 4: Review, commit, push, and update PR #3498**

Stage only the Run Detail component/test, its shared vNext styles, the English and Chinese vNext messages, and this plan. Commit with `Improve Run detail refresh feedback`, push `feat/2026-08-20_workflow-schedule-frontend`, and append the exact focused commands and results to the existing Draft PR. Confirm its `[DO NOT MERGE]` title and `feat/2026-08-04_workflow-activity-vnext` base remain unchanged.
