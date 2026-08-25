# Activity Filter Spacing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate URL-backed Activity scope chips from editable query controls with a conditional 12px vertical interval.

**Architecture:** Keep the existing filter state, URL ownership, query payload, and toolbar unchanged. Give the already-conditional scope-chip `Space` an Activity-specific class and let that class own the inter-group interval, so no chips means no extra element or blank space. Lock the contract in the Activity component test and verify the rendered geometry in the user's existing Chrome tab.

**Tech Stack:** React, TypeScript, Ant Design `Space`, CSS-in-TypeScript, Jest, Testing Library, Chrome browser verification.

---

### Task 1: Prove The Missing Context Interval

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Add a rendered-structure assertion for scoped Activity**

Extend `filters by Schedule without inventing a Run source` after the two scope buttons become visible:

```tsx
const scopeContext = document.querySelector(
  '.wa-vnext__activity-filter-context',
);
expect(scopeContext).toBeInTheDocument();
expect(scopeContext).toContainElement(
  screen.getByRole('button', { name: 'Remove workflow filter wf-alpha' }),
);
expect(scopeContext).toContainElement(
  screen.getByRole('button', {
    name: 'Remove schedule filter schedule-alpha',
  }),
);
```

This asserts the semantic owner of the spacing rather than a browser-specific computed pixel value.

- [ ] **Step 2: Assert the CSS interval contract**

Import `workflowActivityVNextCss` from `../styles` and add one focused assertion:

```tsx
expect(workflowActivityVNextCss).toContain(
  '.wa-vnext__activity-filter-context { margin-bottom: 12px; }',
);
```

- [ ] **Step 3: Run the exact Activity test and confirm RED**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Expected: FAIL because the context `Space` has no Activity-specific class and the CSS does not define the 12px interval.

### Task 2: Implement The Conditional Spacing Owner

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] **Step 1: Classify the existing conditional scope group**

Change only the existing `filterContext` root:

```tsx
<Space className="wa-vnext__activity-filter-context" wrap>
```

Do not wrap the chips in another card or container and do not change their clear actions.

- [ ] **Step 2: Add the stable interval**

Add beside the shared toolbar rules:

```css
.wa-vnext__activity-filter-context { margin-bottom: 12px; }
```

Do not add margin to `.wa-vnext__toolbar`; Workflows and editor toolbars do not own Activity scope context. Because `filterContext` remains conditionally rendered, unscoped Activity reserves no extra space.

- [ ] **Step 3: Run the exact Activity test and confirm GREEN**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Expected: PASS with the class present around both chips and the 12px CSS contract defined.

### Task 3: Focused Verification And Delivery

**Files:**
- Review all files changed by Tasks 1-2 and the committed Activity specification.

- [ ] **Step 1: Analyze affected frontend scope**

Run from the repository root:

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run related and changed tests**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest --findRelatedTests src/pages/workflow-activity-vnext/activity/ActivityPage.tsx src/pages/workflow-activity-vnext/styles.ts --runInBand
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Do not run a bare package test command, full typecheck, or production build.

- [ ] **Step 3: Run changed-file static checks and guards**

```bash
pnpm exec biome check src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx src/pages/workflow-activity-vnext/activity/ActivityPage.tsx src/pages/workflow-activity-vnext/styles.ts
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5175/
```

Expected: all commands exit 0 and the frontend returns HTTP 200. Full frontend suite, typecheck, and production build remain delegated to GitHub CI.

- [ ] **Step 4: Verify geometry in the existing Chrome tab**

Reuse the claimed local Activity tab. Confirm the URL and filter behavior are unchanged, measure 12px between `.wa-vnext__activity-filter-context` and `.wa-vnext__toolbar`, inspect the desktop screenshot, and verify no new tab/window/browser was opened.

- [ ] **Step 5: Commit, push, and update PR #3498**

Stage only the implementation plan, Activity component/test, and styles. Commit with an imperative message, push `feat/2026-08-20_workflow-schedule-frontend`, and append exact verification evidence to PR #3498 while preserving Draft, `[DO NOT MERGE]`, and base `feat/2026-08-04_workflow-activity-vnext`.
