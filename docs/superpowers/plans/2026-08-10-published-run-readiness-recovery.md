# Published Run Readiness Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable published Workflow Activity Run after a fresh editor mount by restoring the exact active revision and published service from the authoritative workflow detail.

**Architecture:** Keep draft authoring state and published invocation state separate. `WorkflowEditorPage` reads the exact scope workflow detail and restores a typed invocation target only from matching authoritative identities; the existing observer remains responsible for a receipt from a new Publish command, which takes precedence while it is active.

**Tech Stack:** React 19, TypeScript, TanStack Query, Jest, Testing Library, Biome.

---

### Task 1: Lock Fresh-Session Published Run Behavior

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Add a test whose draft and scope workflow detail share `workflowId = "wf-committed-source"` while the detail exposes `activeRevisionId = "rev-existing"` and `publishedServiceId = "svc-existing"`.
- [ ] Render a fresh editor, wait for Run to become enabled, open it, and assert that the drawer shows `rev-existing` and `svc-existing`.
- [ ] Run only that test and verify it fails because the current editor requires a same-session publication receipt.

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand -t 'restores an authoritative publication when opening a published workflow'
```

### Task 2: Restore the Authoritative Publication

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`

- [ ] Query `scopesApi.getWorkflowDetail(activeScopeId, activeWorkflowId)` with a route-scoped query key and no retries.
- [ ] Derive a restored invocation target only from an available exact scope/workflow match with non-blank `activeRevisionId` and `publishedServiceId`.
- [ ] Prefer a same-session Publish receipt; suppress the restored invocation target while a replacement Publish submission is active.
- [ ] Keep the receipt observer scoped to newly accepted Publish commands and derive the restored target's document version from the initial editor revision.
- [ ] Run the focused regression test and verify it passes.

### Task 3: Protect Existing Behavior

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Give editor tests an explicit unpublished workflow detail by default so every test declares its publication state honestly.
- [ ] Run dependency-related tests for the changed editor page and the changed test file.
- [ ] Run the repository test stability guard because a frontend test changed.
- [ ] Run Biome only on analyzer-reported static-check files.

### Task 4: Deliver the Focused Fix

**Files:**
- Review all files changed from `origin/feat/2026-08-04_workflow-activity-vnext`.

- [ ] Run `frontend_change_scope.py` with the feature branch as base and record its exact commands.
- [ ] Review the diff for identity mixing, stale route state, and unrelated changes.
- [ ] Stage only this task's files, commit, push, and open a ready pull request targeting `feat/2026-08-04_workflow-activity-vnext`.
- [ ] Record focused test/static-check results and delegate the full frontend suite, typecheck, and build to GitHub CI.
