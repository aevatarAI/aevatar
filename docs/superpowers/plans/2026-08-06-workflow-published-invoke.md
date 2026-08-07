# Workflow Published Invoke Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Workflow Activity vNext Run available only for an observed publication and execute it through the published service Invoke endpoint from a visible input drawer.

**Architecture:** Publication observation supplies an explicit `{ publishedServiceId, revisionId, workflowId }` target. The editor run hook consumes that target and reuses its existing SSE and Activity observation state machine while replacing Draft Run YAML submission with service-scoped `streamChat`. A focused Drawer component presents input and run result without moving the canvas.

**Tech Stack:** React 19, TypeScript, Ant Design 6, TanStack Query, Jest, Testing Library, Biome.

---

### Task 1: Lock the published-run contract with failing tests

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Add a test that renders a valid saved workflow with no publication and asserts the real `Run` button is disabled with a publish-first reason.
- [ ] Add a test that completes Publish observation, opens Run, and asserts an Ant Design dialog named `Run published workflow` is visible without scrolling.
- [ ] Add a test that submits Input and expects `runtimeRunsApi.streamChat(scopeId, { prompt }, signal, { serviceId: publishedServiceId })` while `streamDraftRun` remains unused.
- [ ] Run only those tests and verify RED because current Run is enabled before Publish, renders a region below the canvas, and calls Draft Run.

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand -t 'requires an observed publication|opens the published run drawer|invokes the exact published service'
```

### Task 2: Replace Draft Run with an explicit published Invoke target

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowPublishedRunDrawer.tsx`

- [ ] Introduce a `WorkflowPublishedInvocationTarget` containing distinct `workflowId`, `revisionId`, and `publishedServiceId` fields.
- [ ] Change `editor.run` and `editor.runAgain` to require that target and call service-scoped `runtimeRunsApi.streamChat` with only the prompt.
- [ ] Delete YAML serialization from the run submission path while retaining SSE parsing, stable run identification, retry locking, and Activity observation.
- [ ] Derive one Run availability state in `WorkflowEditorPage`: observed publication, valid unchanged saved document, no unapplied changes, and no active write/run lock.
- [ ] Render the input and existing result content inside `WorkflowPublishedRunDrawer`; show published service and revision and focus Input when opened.
- [ ] Close and route-change behavior must preserve the active run for the same workflow and abort/clear state for a different workflow.
- [ ] Run the three Task 1 tests and verify GREEN.

### Task 3: Update dependent run-flow tests

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Update existing editor run arrangements to first supply an observed publication receipt.
- [ ] Replace expectations for `streamDraftRun` and `workflowYamls` with `streamChat` and the exact service route target.
- [ ] Keep coverage for required Input, backend field errors, double-submit protection, run-again snapshots, unidentified streams, route-change isolation, and Activity observation.
- [ ] Run the related Workflow Activity test file directly and verify zero failures.

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand
```

### Task 4: Focused verification and delivery

**Files:**
- Verify all files changed from `origin/feat/2026-08-04_workflow-activity-vnext`.

- [ ] Run `frontend_change_scope.py --base origin/feat/2026-08-04_workflow-activity-vnext`.
- [ ] Run dependency-related Jest tests for changed source files and every changed test file explicitly.
- [ ] Run Biome only for analyzer `staticCheckFiles`; do not run full typecheck or production build.
- [ ] Review the complete diff for identity mixing, stale publication enablement, focus/accessibility, and unrelated changes.
- [ ] Commit with AbigailDeng identity, push the branch, and open a ready PR targeting `feat/2026-08-04_workflow-activity-vnext` with `Closes #3222` only if the updated acceptance criteria fully cover the issue.
- [ ] Check GitHub mergeability and resolve branch conflicts; full frontend verification remains delegated to GitHub CI.
