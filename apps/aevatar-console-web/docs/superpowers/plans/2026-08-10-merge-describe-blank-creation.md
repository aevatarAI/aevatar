# Merge Describe And Blank Creation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Present three workflow creation methods and let the fixed `Generate and open` action create either a generated or blank workflow according to whether the user entered a description.

**Architecture:** Keep the existing Describe form as the single owner of name and optional description input. At its submit event, branch on `prompt.trim()`: reuse `generateAndOpen()` for non-empty descriptions and `createBlank()` for empty descriptions, while leaving shared persistence, filename resolution, directory selection, materialization, and recovery unchanged.

**Tech Stack:** React 19, TypeScript, Ant Design, TanStack Query, Jest, Testing Library, Biome, pnpm.

---

### Task 1: Protect The Merged User Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Update the focused chooser and blank-creation expectations**

Make the component test assert that the chooser exposes `Describe`, `Import YAML`, and `Use template`, does not expose `Start blank`, and that selecting Describe reveals the fixed `Generate and open` action. Change blank-creation cases to enter a Workflow name in Describe, leave the description empty, click `Generate and open`, and assert that `authorWorkflow` and `parseYaml` were not called while `createWorkflowDraft` received the existing minimal blank YAML.

- [ ] **Step 2: Run the component test to verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx
```

Expected: FAIL because `Start blank` still renders and `Generate and open` is disabled without a description.

- [ ] **Step 3: Update route integration expectations**

Replace route-level `Start blank` interactions with `Describe`, enter the same names, leave the description empty, and click `Generate and open`. Where a test switches back to a method after a failed generated submission, select `Describe` again and verify that the failure clears while the shared input remains.

### Task 2: Merge The Creation Modes

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowCreation.ts`

- [ ] **Step 1: Remove the separate blank method**

Delete the `FileAddOutlined` import and `blank` chooser item. Narrow `WorkflowCreationMode` to `'describe' | 'import' | 'template'`, and render the Workflow name field only for Describe.

- [ ] **Step 2: Route the fixed action by description presence**

Keep the action label as the existing `workflowActivityVNext.new.generate` / `Generate and open`. Enable it when a trimmed name and save target exist. In the click handler call `generateAndOpen()` when `prompt.trim()` is non-empty; otherwise call `createBlank()`. Keep both existing functions and their stage-specific failure handling unchanged.

- [ ] **Step 3: Run both affected test files to verify GREEN**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: both files pass with no test failures.

### Task 3: Focused Validation And Delivery

**Files:**
- Verify every modified source, test, and documentation file reported by the analyzer.

- [ ] **Step 1: Run the frontend change scope analyzer**

Run from the repository root:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo .
```

Read `frontend-incremental-pr/references/framework-commands.md`, then use only analyzer-reported affected tests and static-check files.

- [ ] **Step 2: Run changed-file static and documentation checks**

Run the analyzer-prescribed Biome command for `staticCheckFiles`, the focused baseline verifier required for vNext work, documentation lint when the changed spec/plan is in its scope, and:

```bash
git diff --check
```

Do not run package-wide TypeScript, the full frontend suite, or a production build locally. If the repository has no affected TypeScript target, record that GitHub CI owns full typechecking and build verification.

- [ ] **Step 3: Review and deliver the exact task diff**

Compare the diff against the amended design specification: three chooser methods, no Start blank card, one fixed Generate and open action, name-only blank branch, described generation branch, and unchanged import/template/materialization behavior. Stage only the task files, commit with an imperative message, push the current branch, and create or update the pull request with exact local commands, results, the required vNext baseline declaration, and the statement that the full frontend suite/build is delegated to GitHub CI.
