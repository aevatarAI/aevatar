# Workflow Template Tooltips Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove non-informative template catalogue content and establish one accessible tooltip component across console-web.

**Architecture:** Add `AevatarTooltip` under shared UI as the sole Ant Tooltip adapter, migrate all business consumers without changing their existing content or placement overrides, and enforce that boundary with a TypeScript-AST test. Simplify the template table to its distinguishing facts and use the shared adapter for the step-count explanation.

**Tech Stack:** React 19, TypeScript, Ant Design 6, Jest, Testing Library, Biome.

---

### Task 1: Add The Shared Tooltip Adapter

**Files:**
- Create: `apps/aevatar-console-web/src/shared/ui/AevatarTooltip.tsx`
- Create: `apps/aevatar-console-web/src/shared/ui/AevatarTooltip.test.tsx`

- [ ] **Step 1: Write the failing component tests**

Test that hover and focus display the supplied content and that an explicit placement override remains accepted.

- [ ] **Step 2: Run the component test and verify RED**

Run: `pnpm exec jest src/shared/ui/AevatarTooltip.test.tsx --runInBand --runTestsByPath`

Expected: FAIL because `AevatarTooltip` does not exist.

- [ ] **Step 3: Implement the minimal adapter**

Wrap Ant Design `Tooltip`, preserve `TooltipProps`, and default `trigger` to `['hover', 'focus']`, `placement` to `top`, `mouseEnterDelay` to `0.2`, and the content container to a readable maximum width while merging consumer styles.

- [ ] **Step 4: Run the component test and verify GREEN**

Run the Task 1 Jest command and expect all tests to pass.

### Task 2: Migrate Existing Tooltip Consumers

**Files:**
- Create: `apps/aevatar-console-web/src/shared/ui/AevatarTooltipImportGuard.test.ts`
- Modify: every console-web source file that directly imports `Tooltip` from `antd`

- [ ] **Step 1: Write the failing import-boundary test**

Parse TypeScript and TSX source imports with the TypeScript compiler API and fail when any file except `AevatarTooltip.tsx` imports the named `Tooltip` export from `antd`.

- [ ] **Step 2: Run the guard and verify RED**

Run: `pnpm exec jest src/shared/ui/AevatarTooltipImportGuard.test.ts --runInBand --runTestsByPath`

Expected: FAIL and list the current direct-import files.

- [ ] **Step 3: Mechanically migrate consumers**

Replace JSX `Tooltip` elements with `AevatarTooltip`, remove `Tooltip` from Ant imports, add the shared adapter import, and update `AevatarHelpTooltip` to compose the adapter.

- [ ] **Step 4: Run the guard and direct shared tests**

Run both shared tooltip test files by path and expect them to pass.

### Task 3: Simplify The Template Catalogue And Add Step Help

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowTemplateBrowser.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Update the page regression and verify RED**

Require five table columns, no `Reads` or `Workflow inputs`, no decorative marker, and a step-count tooltip that opens on hover and focus.

- [ ] **Step 2: Run the focused page test and verify RED**

Run: `pnpm exec jest src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx --runInBand --runTestsByPath -t "renders only decision-useful template facts with step help"`

Expected: FAIL against the six-column marker implementation.

- [ ] **Step 3: Implement the table and copy change**

Delete marker selection and icon imports, remove the reads header/cell/column, change the identity layout to text-only, rebalance the five columns, and wrap the step summary in `AevatarTooltip` with localized explanatory content.

- [ ] **Step 4: Run the complete changed page test**

Run the complete `NewWorkflowPage.test.tsx` by path and expect all tests to pass.

### Task 4: Focused Verification And Delivery

**Files:** all changed task files only.

- [ ] **Step 1: Analyze affected frontend scope**

Run the `frontend_change_scope.py` analyzer against `feat/2026-08-18_workflow-template-browser`.

- [ ] **Step 2: Run dependency-related and directly changed tests**

Use Jest `--findRelatedTests` for changed source files and run every changed/new test file explicitly.

- [ ] **Step 3: Run changed-file static checks and repository test guard**

Run Biome only on analyzer `staticCheckFiles`, `bash tools/ci/test_stability_guards.sh`, and `git diff --check`.

- [ ] **Step 4: Verify in the existing Chrome tab**

Reuse the user's existing browser tab, verify desktop and 390px layouts, removed content, hover/focus tooltip, scrolling, and absence of document-level horizontal overflow.

- [ ] **Step 5: Commit, push, and create the pull request**

Stage only task files, commit with an imperative message, push `fix/2026-08-21_workflow-template-tooltips`, create a stacked PR against `feat/2026-08-18_workflow-template-browser`, and record exact focused verification while delegating the full frontend suite/build to GitHub CI.
