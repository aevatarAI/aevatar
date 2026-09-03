# Frontend-Only Workflow Configuration Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make workflow step Configuration understandable without changing or
adding a backend contract.

**Architecture:** Keep `WorkflowNodeInspector` on the existing workflow
parameter draft/update path. Extend only the static frontend field schema and
presentation copy; do not discover or infer server-owned capabilities.

**Tech Stack:** React, TypeScript, Ant Design, Jest, Testing Library, Umi.

---

### Task 1: Remove The Backend-Dependent Design

**Files:**

- Remove from the branch diff:
  `src/platform/Aevatar.GAgentService.Hosting/Endpoints/WorkflowCapabilityHttpContracts.cs`
- Restore to the base version:
  `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeWorkflowEndpoints.cs`
- Restore to the base version:
  `test/Aevatar.GAgentService.Integration.Tests/ScopeWorkflowEndpointsTests.cs`
- Restore to the base version:
  `test/Aevatar.GAgentService.Integration.Tests/GAgentServiceHostingServiceCollectionExtensionsTests.cs`
- Restore capability client additions in:
  `apps/aevatar-console-web/src/shared/studio/api.ts`
- Restore capability decoder tests in:
  `apps/aevatar-console-web/src/shared/studio/api.test.ts`
- Remove:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.tsx`
- Remove:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowToolCallConfiguration.test.tsx`
- Remove:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.ts`
- Remove:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/toolCallConfiguration.test.ts`

- [ ] **Step 1: Mechanically reverse the branch's capability endpoint and
  client commits without rewriting branch history.**

- [ ] **Step 2: Confirm the diff contains no files under `src/` or `test/`.**

Run:

```bash
git diff --name-only origin/feat/2026-08-04_workflow-activity-vnext...HEAD -- src test
```

Expected after the correction commit: no output.

### Task 2: Specify The Frontend-Only Inspector Behavior

**Files:**

- Modify:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx`
- Modify:
  `apps/aevatar-console-web/src/shared/studio/nodeConfigFields.structured.test.ts`

- [ ] **Step 1: Add a failing inspector test.**

The test renders a `tool_call` draft with these existing parameters:

```ts
{
  tool: 'nyxid_proxy',
  arguments: '{"query":{"request":"$input"}}',
}
```

It asserts that `Tool name`, `Arguments JSON`, `Required`, `Optional`, and the
argument-contract guidance are visible without mocking any capability API.

- [ ] **Step 2: Run the test and verify RED.**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.test.tsx \
  --runInBand
```

Expected: FAIL because the current inspector still renders the removed dynamic
capability picker or lacks the arguments field.

- [ ] **Step 3: Add a failing schema test.**

Assert that `getStudioNodeConfigurationSchema('tool_call')` exposes `tool` as a
required single-line value and `arguments` as an optional multi-line string.

- [ ] **Step 4: Run the schema test and verify RED.**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  src/shared/studio/nodeConfigFields.structured.test.ts --runInBand
```

Expected: FAIL because `arguments` is not yet part of the static schema.

### Task 3: Implement Honest Field Guidance

**Files:**

- Modify:
  `apps/aevatar-console-web/src/shared/studio/nodeConfigFieldSchemas.ts`
- Modify:
  `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowNodeInspector.tsx`
- Modify:
  `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify:
  `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify translation entries in:
  `apps/aevatar-console-web/src/locales/en-US.ts`
- Modify translation entries in:
  `apps/aevatar-console-web/src/locales/zh-CN.ts`

- [ ] **Step 1: Extend the static tool-call schema.**

Add `arguments` as a `multi-line` string field. Its input value is raw text and
therefore continues to serialize as a string.

- [ ] **Step 2: Render requirement state and persistent helper copy.**

Each field header shows `Required` or `Optional`. Beneath the control, render
its explicit description; when no description exists, render its placeholder
as a persistent example so help remains visible after a value is entered.

- [ ] **Step 3: Keep the existing inspector hierarchy improvements.**

Retain step purpose, Settings, collapsed Technical details, collapsed Advanced
JSON, localized errors, `Cancel`, and `Apply step`. Remove all `scopeId`,
capability state, discovery queries, readiness queries, and operation-specific
presentation.

- [ ] **Step 4: Run the two focused tests and verify GREEN.**

Run the exact commands from Task 2. Expected: PASS.

### Task 4: Verify And Deliver

**Files:** all frontend and documentation files changed by Tasks 1-3.

- [ ] **Step 1: Run the frontend change-scope analyzer.**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run every analyzer-selected related test and changed-file
  static check.**

Do not run a full frontend test suite, full TypeScript check, or production
build. GitHub CI owns full verification.

- [ ] **Step 3: Inspect the running page at desktop and mobile widths.**

Reuse the existing browser tab and the development server connected to the
configured remote backend. Confirm no overlap, clipped text, or capability API
request occurs.

- [ ] **Step 4: Review and stage only this task's files.**

```bash
git diff --check
git diff --name-status origin/feat/2026-08-04_workflow-activity-vnext...HEAD
```

- [ ] **Step 5: Commit and push.**

```bash
git commit -m "Keep workflow configuration guidance frontend-only"
git push origin feat/2026-09-02_workflow-configuration-guidance
```

- [ ] **Step 6: Update PR #3574.**

Record the exact focused commands and results, plus:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```
