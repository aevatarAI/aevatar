# Workflow Activity vNext NyxID Visual Alignment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align every Workflow Activity vNext page with NyxID's measured layout, typography, density, table, form, and responsive patterns while preserving Aevatar colors and all existing product behavior.

**Architecture:** Keep the current route and component ownership. Centralize the measured visual contract in `styles.ts`, make `WorkflowActivityVNextShell.tsx` own the 52px top bar and responsive navigation, and limit page edits to semantic class hooks or removal of conflicting inline styles. No API, identity, query, routing, locale, or workflow behavior changes are allowed.

**Tech Stack:** React 19, TypeScript, Ant Design 6, CSS variables in the existing vNext style module, Jest/Testing Library for existing behavior coverage, Biome, and real-browser responsive verification.

---

## Design baseline declaration

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic;
  presentation may change, behavior may not.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```

### Task 1: Lock the measured design contract

**Files:**
- Modify: `docs/canon/frontend-design.md`
- Create: `apps/aevatar-console-web/docs/superpowers/plans/2026-08-05-workflow-activity-vnext-nyxid-visual-alignment.md`

- [x] Record the measured shell, typography, spacing, control, table, responsive, and accessibility rules.
- [x] Record the permitted Aevatar deviations: existing colors, AlibabaSans delivery, and the repository's 8px radius cap.
- [x] Update issue `#3194` with the measurement table, page inventory, and acceptance evidence.
- [x] Run `bash tools/docs/lint.sh` and `git diff --check`.
- [x] Commit the documentation as `Document NyxID visual alignment`.

### Task 2: Align the shared shell and navigation

**Files:**
- Modify: `src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [x] Preserve the existing semantic navigation and account/language components while moving their presentation into a 52px top bar.
- [x] Place the Aevatar brand and current breadcrumb in the top bar, start the 200px local rail below it, and render the page title inside the main content surface.
- [x] Replace the permanent mobile navigation strip with an accessible menu button and drawer using the same route builders.
- [x] Define shared spacing, type, radius, shadow, control, and table tokens in `.wa-vnext`.
- [x] Run the existing route integration test before and after the shell change to prove navigation, auth action reuse, and page behavior remain intact:

```bash
pnpm exec jest --runInBand --runTestsByPath \
  src/pages/workflow-activity-vnext/index.test.tsx
```

### Task 3: Align Workflows and New workflow

**Files:**
- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [x] Keep Workflow search and source filter in one responsive toolbar.
- [x] Render the Workflow table with a 32px header, 53-60px rows, one clear Open action, and the branch's existing accessible secondary actions.
- [x] Remove the mobile card transformation and use a named local horizontal scroll region.
- [x] Replace New workflow inline presentation styles with shared classes: four equal-level creation choices, then one form container for the selected method.
- [x] Verify Describe, blank, import, and template retain their current authoritative creation behavior through the existing focused test file.

### Task 4: Align the Workflow editor

**Files:**
- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [x] Keep Canvas and YAML as a compact segmented mode control and preserve the existing shared editor state.
- [x] Keep the canvas unframed/full-width inside the work surface; remove conflicting fixed layout values where shared responsive tokens can own sizing.
- [x] Preserve visible Save, Run, Publish availability and the current node library/detail/YAML behavior.
- [x] At mobile width, keep editor actions reachable, allow horizontal action scrolling only inside the toolbar, and preserve the existing accessible node panel behavior.

### Task 5: Align Activity and Run detail

**Files:**
- Modify: `src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Test: `src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [x] Apply the NyxID ledger density to Activity while preserving status, source, updated time, server filters, and the single detail target.
- [x] Keep mobile Activity as a semantic table in a bounded horizontal scroller rather than a card layout.
- [x] Move Run status and recovery actions close to the page title; use a 32px underline tab strip and single-layer detail sections.
- [x] Keep Steps and Diagnostics tables semantic and locally scrollable; preserve independent graph failure and immutable source behavior.
- [x] Run both focused behavior tests after the visual-only edits.

### Task 6: Align Settings

**Files:**
- Modify: `src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [x] Replace the desktop secondary side navigation with a 32px underline tab strip that remains horizontally scrollable on mobile.
- [x] Render each section in one bordered 8px container with a compact section header and 32-36px controls.
- [x] Preserve authoritative LLM clean/dirty/accepted/observed states, current account actions, and read-only advanced values.
- [x] Keep dirty actions at the bottom of the owning container and sticky only when needed; ensure they do not cover mobile inputs.

### Task 7: Focused verification and delivery

**Files:**
- Review every file changed by Tasks 1-6.

- [x] Run the frontend scope analyzer with the task start commit as base and use only its changed source/test/static-check files.
- [x] Run directly changed tests and dependency-related Jest tests only. Do not run the full frontend suite.
- [x] Run Biome with explicit changed frontend file paths. Skip package-wide typecheck because no repository-native affected typecheck target exists; GitHub CI owns full type verification.
- [x] Run `bash tools/ci/test_stability_guards.sh`, `bash tools/docs/lint.sh`, baseline verification, and `git diff --check`.
- [x] Verify real pages at `1440x900`, `834x1112`, and `390x844`, including local table scroll and page-level overflow checks.
- [x] Review `git diff`, stage only this task's files, commit `Align Workflow Activity with NyxID`, push the existing feature branch, update issue `#3194`, and update PR `#3189` with exact focused commands and the GitHub CI delegation statement.
