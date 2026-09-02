# Resizable Workflow Run Panels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep Published Run bounded to the canvas workspace while restoring accessible side-panel and Logs resizing, a collapsed Logs dock, and automatic expansion for each explicit run.

**Architecture:** Extract the existing Studio resize state and event contract into shared workflow primitives, then consume those primitives from both Studio and Workflow Activity vNext. Keep execution data visibility separate from dock expansion so collapse never clears facts and incoming SSE frames never override a manual collapse.

**Tech Stack:** React, TypeScript, Ant Design, Jest, Testing Library, Biome

---

### Task 1: Lock Product Behavior With Regression Tests

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [x] Add a test that opens Published Run, verifies its shared workspace height, finds the vertical `Resize published run panel` separator, and changes the panel width with ArrowLeft/ArrowRight while respecting min/max values.
- [x] Add a test that verifies the Logs dock is collapsed before execution, automatically expands on Start, exposes the horizontal `Resize workflow run console` separator, changes height with ArrowUp/ArrowDown, and can be manually collapsed without clearing execution data.
- [x] Run the two focused tests before production edits and confirm they fail because the separators and collapsed dock do not exist and the panel is content-sized.

### Task 2: Share Resize Behavior

**Files:**
- Create: `apps/aevatar-console-web/src/shared/workflows/useWorkflowPanelResize.ts`
- Create: `apps/aevatar-console-web/src/shared/workflows/WorkflowPanelResizeHandle.tsx`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/index.tsx`

- [x] Move the established 420px side-panel default, width and height clamping, mouse listener cleanup, and 24px keyboard steps into `useWorkflowPanelResize`.
- [x] Render separators through `WorkflowPanelResizeHandle` with `role=separator`, orientation/value ARIA attributes, visible resize cursors, and the existing neutral divider treatment.
- [x] Replace Studio's local resize implementation with the shared hook and component without changing its current tests or behavior.

### Task 3: Bound Published Run And Add The Logs Dock

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/shared/workflows/WorkflowExecutionLogsPanel.tsx`

- [x] Give the desktop run workspace one bounded height and make Canvas, YAML, and Run Input consume `height: 100%`; keep the Run Input body scrollable and its action footer anchored.
- [x] Render the shared vertical resize handle only while Run Input is open and pass the resulting width to `WorkflowRunInputPanel`.
- [x] Keep a compact Logs bar in the shell footer, default it to collapsed, and separate expand/collapse from clear.
- [x] On Start, preserve execution data visibility, reset selection, and expand Logs exactly once. Render the horizontal resize handle and the shared Logs console only while expanded.
- [x] Hide fine-grained resize handles and restore natural stacked heights at the mobile breakpoint.
- [x] Run the focused tests and confirm both pass.

### Task 4: Focused Verification And Delivery

**Files:**
- Modify: `docs/superpowers/specs/2026-08-10-shared-workflow-run-console-design.md`

- [x] Run the frontend scope analyzer against `origin/feat/2026-08-04_workflow-activity-vnext`.
- [x] Run explicit changed Jest tests and Jest `--findRelatedTests` only for changed source files.
- [x] Run Biome only on analyzer `staticCheckFiles`, then run `bash tools/ci/test_stability_guards.sh`, `bash tools/docs/lint.sh`, and `git diff --check`.
- [x] Use the existing authenticated Chrome tab to verify desktop drag sizing, bounded Published Run height, collapsed/expanded Logs, and the relevant mobile viewport without starting a remote run.
- [ ] Review the complete diff, stage only task files, commit, push, and update PR #3389 with exact verification evidence. Full frontend suite, typecheck, and production build remain delegated to GitHub CI.
