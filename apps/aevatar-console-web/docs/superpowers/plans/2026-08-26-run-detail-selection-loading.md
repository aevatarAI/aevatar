# Run Detail Selection Loading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep committed Run history visible and interactive while a newly
selected Run's detail and graph load.

**Architecture:** Retain the independent TanStack Query boundaries for Run
history, detail, and graph. Split the existing structural skeleton into a
complete initial workspace and a reusable right-side detail stage. A selection
with committed history renders the normal rail beside the loading stage; a
manual grouped Refresh continues to own the whole-workspace busy state.

**Tech Stack:** React, TypeScript, TanStack Query, Ant Design, Jest, Testing
Library.

---

### Task 1: Lock The Loading Boundary

**Files:**
- Modify: `src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [x] Add a focused route-selection test with deferred detail and graph reads.
- [x] Assert the committed rail remains mounted, the selected row changes, the
  history query is not reissued, and only the right stage reports loading.
- [x] Run the named test and confirm RED against the complete-workspace
  skeleton.

### Task 2: Split Initial And Selection Loading

**Files:**
- Modify: `src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Modify: `src/shared/ui/AevatarLoading.tsx`
- Modify: `src/shared/ui/AevatarLoading.test.tsx`
- Modify: `src/global.less`

- [x] Extract the existing right-side skeleton into an accessible loading
  stage without changing its visual structure.
- [x] Keep the complete workspace skeleton for initial loading without
  committed history.
- [x] Render the committed, workflow-scoped history rail beside the loading
  stage when another history Run is selected.
- [x] Keep the workspace itself usable and expose one right-stage loading
  status.
- [x] Add a shared committed-content overlay using the canonical three-dot
  motion, then replace both page-local Run Detail refresh indicators.

### Task 3: Persist The Product Rule

**Files:**
- Modify: `AGENTS.md`
- Modify:
  `docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`

- [x] Record the reusable region-owned loading rule in the frontend guidance.
- [x] Record initial loading, Run selection, and grouped Refresh boundaries in
  UP-10.

### Task 4: Verify And Deliver

- [x] Run the focused Run Detail test and bounded related tests.
- [x] Run changed-file Biome, test stability, docs lint, and diff checks.
- [x] Reuse the user's existing Chrome tab to verify the remote-backed page.
- [x] Review and stage only current-task files, excluding `.planning/`.
- [x] Commit, push, and append exact evidence to Draft PR #3498 without
  changing its title, base, or merge gate.
