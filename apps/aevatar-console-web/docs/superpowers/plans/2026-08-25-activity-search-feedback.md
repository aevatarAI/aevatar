# Activity Search Feedback Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Activity Search command immediate, request-owned loading feedback without hiding committed ledger rows or confusing background fetching with user intent.

**Architecture:** Keep URL parameters as the committed Activity filter source and React Query as the request owner. Track only the normalized query string submitted by the current Search command; same-query searches await `refetch()`, while changed-query searches remain pending until the URL reaches that target and its query settles. The existing page skeleton continues to own initial loading, and the button alone acknowledges Search over committed content.

**Tech Stack:** React, TypeScript, TanStack React Query, Ant Design Button, Jest, Testing Library, Chrome remote-backed verification.

---

### Task 1: Prove The Missing Same-Query Feedback

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Add a deferred same-query Search test**

Render a committed Activity row, make the second feed request a deferred promise, press Search without changing filters, and assert that the Search button immediately has `aria-busy="true"`, `ant-btn-loading`, and `disabled` while the committed row remains visible.

- [ ] **Step 2: Assert duplicate submission is blocked**

Press the pending Search button again and assert `listActivityRuns` is still called only twice: one initial load and one explicit Search request.

- [ ] **Step 3: Assert completion restores the command**

Resolve the deferred response and assert the button returns to `aria-busy="false"`, loses `ant-btn-loading`, and becomes enabled.

- [ ] **Step 4: Run the exact test and confirm RED**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx --testNamePattern 'shows pending feedback for an unchanged Activity Search'
```

Expected: FAIL because Search has no command-owned pending state.

### Task 2: Preserve Feedback Across URL-Backed Search

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Add a deferred changed-filter test**

Make the test history mock apply `history.replace()` to `mockSearch` and rerender the page. Change the draft search value, press Search, and defer the feed request for the new URL-backed filter.

- [ ] **Step 2: Assert target-owned lifecycle**

Assert the URL contains `q=support`, the button remains loading and disabled during the new feed request, and it returns to idle only after the target request settles.

- [ ] **Step 3: Run the focused test and confirm RED**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx --testNamePattern 'keeps Search pending while URL-backed Activity filters load'
```

Expected: FAIL because the current button does not retain feedback across URL navigation.

### Task 3: Implement Command-Owned Search Pending State

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Track the submitted normalized query string**

Add `pendingSearchTarget` state. Ignore another submission while it is non-null, set it before issuing Search, and use the normalized `URLSearchParams.toString()` value as the command target.

- [ ] **Step 2: Own both request paths**

For unchanged filters, await `runs.refetch()` and clear the target in `finally`. For changed filters, replace the URL and clear only after the current URL matches the submitted target and `runs.isFetching` becomes false.

- [ ] **Step 3: Expose honest button feedback**

Set the Search button's `loading`, `disabled`, and `aria-busy` from `pendingSearchTarget !== null`. Keep its stable Search label and preserve the committed ledger content.

- [ ] **Step 4: Run the exact Activity test and confirm GREEN**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Expected: all Activity tests pass.

### Task 4: Focused Verification And Delivery

**Files:**
- Review the specification, plan, Activity component, and Activity test changed by Tasks 1-3.

- [ ] **Step 1: Analyze affected frontend scope**

Run from the repository root:

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run related and explicit tests**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest --findRelatedTests src/pages/workflow-activity-vnext/activity/ActivityPage.tsx --runInBand
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Do not run a bare package test, full typecheck, or production build.

- [ ] **Step 3: Run changed-file checks and repository guards**

```bash
pnpm exec biome check src/pages/workflow-activity-vnext/activity/ActivityPage.tsx src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5175/
```

Full frontend suite, typecheck, and production build remain delegated to GitHub CI.

- [ ] **Step 4: Verify in the existing Chrome tab**

Reuse the user's existing local Activity tab. Press Search, confirm immediate spinner/disabled feedback, confirm committed rows stay mounted, confirm a second click cannot submit, and confirm the button returns to idle after the remote request settles. Do not open a browser, window, or duplicate tab.

- [ ] **Step 5: Commit, push, and update PR #3498**

Stage only this task's specification, plan, Activity component, and Activity test. Commit with an imperative message, push `feat/2026-08-20_workflow-schedule-frontend`, append the audit and exact verification commands to PR #3498, and verify that it remains Draft, `[DO NOT MERGE]`, based on `feat/2026-08-04_workflow-activity-vnext`, with matching local/remote/PR head SHAs.
