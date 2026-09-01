# Workflow vNext Home Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the fixed-scope Workflow Activity vNext workflow catalogue the console home without modifying the existing Teams page implementation or breaking explicit login return targets.

**Architecture:** Keep `src/shared/navigation/consoleHome.ts` as the single semantic owner of default console navigation. Make the unscoped home aliases in `config/routes.ts` consume that constant, while leaving scoped Team routes and all Teams page files unchanged.

**Tech Stack:** TypeScript, Umi route configuration, Jest, Biome

---

### Task 1: Lock The Home Navigation Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/navigation/consoleHome.test.ts`
- Modify: `apps/aevatar-console-web/src/routesConfig.test.ts`

- [x] **Step 1: Update the shared home test**

Assert that `WORKFLOW_ACTIVITY_VNEXT_HOME_ROUTE`, `getConsoleHomeRoute()`, and `CONSOLE_HOME_ROUTE` all equal the fixed workflow catalogue path.

- [x] **Step 2: Update the route contract test**

Import `CONSOLE_HOME_ROUTE` and assert that `/`, `/overview`, and `/scopes` redirect to it, `/scopes` is hidden and does not mount `./teams`, and `/scopes/:scopeId/teams` still mounts `./teams`.

- [x] **Step 3: Run the changed tests and verify RED**

Run:

```bash
pnpm exec jest src/shared/navigation/consoleHome.test.ts src/routesConfig.test.ts --runInBand
```

Expected: FAIL because the shared home still equals `/scopes` and `/scopes` still mounts `./teams`.

### Task 2: Point Default Navigation At Workflow Activity vNext

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/navigation/consoleHome.ts`
- Modify: `apps/aevatar-console-web/config/routes.ts`

- [x] **Step 1: Define the fixed Workflow Activity home**

Replace the Teams home constant with a named Workflow Activity vNext home constant and retain `CONSOLE_HOME_ROUTE` as its public default-navigation alias.

- [x] **Step 2: Redirect unscoped home aliases**

Import `CONSOLE_HOME_ROUTE` into `config/routes.ts`. Redirect `/`, `/overview`, and `/scopes` to it; make `/scopes` hidden and remove its Teams name/component fields.

- [x] **Step 3: Run the changed tests and verify GREEN**

Run:

```bash
pnpm exec jest src/shared/navigation/consoleHome.test.ts src/routesConfig.test.ts --runInBand
```

Expected: PASS with both changed test suites green.

### Task 3: Focused Validation And Delivery

**Files:**
- Review all files changed from `feat/2026-08-04_workflow-activity-vnext`

- [x] **Step 1: Analyze affected frontend scope**

Run the `frontend-incremental-pr` analyzer with base `feat/2026-08-04_workflow-activity-vnext` and use its output to select exact tests and changed-file static checks.

- [x] **Step 2: Run focused tests and static checks**

Run changed test files explicitly, related tests for changed production files, and Biome only for analyzer-reported static-check files. Do not run a full frontend test, typecheck, or build.

- [x] **Step 3: Review semantic drift and the full diff**

Search the changed navigation area for stale Teams-home assertions, confirm no `src/pages/teams/**` file changed, and inspect `git diff --check` plus the complete base diff.

- [x] **Step 4: Commit and deliver**

Stage only this task's tracked files, commit with an imperative single-purpose message, push the branch, and create a PR targeting `feat/2026-08-04_workflow-activity-vnext`. Record exact focused commands and state that the full frontend suite/build is delegated to GitHub CI.
