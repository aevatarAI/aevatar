# Live Run Node Tracking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the live Workflow Run console treat the currently executing node as the primary selection, keep it visible as execution advances, and render a stable compact running indicator.

**Architecture:** Keep transport decoding and node projection unchanged because `buildExecutionTrace` already materializes `aevatar.step.request` as a running node. Put live-follow policy in the shared `WorkflowExecutionLogsPanel`, which owns Nodes/Events presentation and can synchronize its controlled log selection with both the detail pane and existing canvas decoration. Raw events remain an explicit Events view and never become the implicit content of the Nodes view.

**Tech Stack:** React 19, TypeScript, Ant Design, Testing Library, Jest, shared Aevatar loading primitives.

---

### Task 1: Protect The Live Node Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioExecutionPanel.test.tsx`

- [x] **Step 1: Add a controlled execution-panel harness**

Create a test harness that owns `activeLogIndex`, renders a running `StudioExecutionDetail`, and accepts deterministic frame updates through Testing Library `rerender`.

- [x] **Step 2: Prove Nodes does not render lifecycle Events before a step starts**

Render a running execution containing only `RUN_STARTED`; assert the Nodes segment stays active, the Run-start event row and Event payload are absent, and the node-waiting state is rendered.

- [x] **Step 3: Prove the first running node becomes primary**

Append an `aevatar.step.request` frame for `step-alpha`; assert its row says `Running`, is selected, owns the detail pane, and contains the shared three-dot running indicator.

- [x] **Step 4: Prove live follow advances without stealing focus within one step**

Render a completed `step-alpha` and running `step-beta`, select `step-alpha` manually, then append running `step-gamma`. Assert the manual selection remains during `step-beta`, the transition to `step-gamma` selects it, and its row receives `scrollIntoView({ block: 'nearest' })` without DOM focus.

- [x] **Step 5: Run the new cases and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath \
  src/pages/team-member-workflow-studio/components/WorkflowStudioExecutionPanel.test.tsx \
  --testNamePattern 'keeps raw events out|follows each newly running node'
```

Expected: FAIL because Nodes currently falls back to Events, selection stays on the first node, no scroll-follow exists, and running status uses the Ant Design spinner.

### Task 2: Implement Shared Live Follow

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/workflows/WorkflowExecutionLogsPanel.tsx`

- [x] **Step 1: Keep Nodes and Events semantically separate**

When `overviewMode === 'nodes'`, render only projected node entries. If no node has started, retain the existing truthful waiting/empty copy rather than substituting event rows or event details.

- [x] **Step 2: Resolve the latest execution-owned node entry**

Match `trace.latestStepId` to the newest projected attempt for that step. While the execution is non-terminal, use that entry as the default selection when no valid controlled node selection exists.

- [x] **Step 3: Synchronize on node transitions**

Track the last auto-followed node entry ID in a ref. When a different live node attempt becomes current, call `onSelectLog(entry.logIndex)` exactly once. Do not repeat that call for additional frames on the same node, so a user's manual inspection remains stable until execution advances.

- [x] **Step 4: Keep the current node visible**

Register rendered row elements by entry ID and call `scrollIntoView({ block: 'nearest' })` when the live-follow target changes while Nodes is visible. Do not call `.focus()`.

- [x] **Step 5: Replace the clipped spinner**

Replace `LoadingOutlined` with `AevatarLoadingDots` inside a fixed `18px` inline-flex indicator box. Use the shared reduced-motion-aware loading implementation and mark the visual as decorative because the adjacent `Running` text carries status semantics.

- [x] **Step 6: Run the focused component tests and verify GREEN**

Run the Task 1 Jest command and expect both new cases plus the existing clipboard case to pass.

### Task 3: Verify And Deliver The Incremental PR

**Files:**
- Review: all files changed by Tasks 1 and 2

- [x] **Step 1: Run changed-file analysis**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [x] **Step 2: Preflight dependency-related tests**

Use `jest --listTests --findRelatedTests` for `WorkflowExecutionLogsPanel.tsx`; reject unrelated fan-out and otherwise run the same bounded related-test command. Run the changed component test explicitly.

- [x] **Step 3: Run focused static and repository guards**

Run analyzer-reported Biome checks, `bash tools/ci/test_stability_guards.sh`, `git diff --check`, and the vNext baseline verifier. Skip local package-wide `tsc` and production build because the personal incremental policy delegates those to GitHub CI.

- [x] **Step 4: Review and deliver**

Review the full base diff, stage only the task's plan, shared component, locale copy, and focused/integration tests, commit with an imperative single-purpose message, push `fix/2026-09-03_live-run-node-tracking`, and create a PR targeting `feat/2026-08-04_workflow-activity-vnext`. Include the required design-baseline declaration and exact focused verification evidence; do not wait for CI.

### Task 4: Preserve Each Node Start Across Batched SSE Frames

**Files:**
- Create: `docs/superpowers/specs/2026-09-03-live-workflow-node-presentation-design.md`
- Create: `apps/aevatar-console-web/src/shared/workflows/streamingExecutionPresentation.ts`
- Create: `apps/aevatar-console-web/src/shared/workflows/streamingExecutionPresentation.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/hooks/useTeamMemberWorkflowStudio.ts`

- [x] **Step 1: Add a single-chunk page regression test**

Make `parseBackendSSEStream` yield two complete node lifecycles synchronously.
Install a controlled `requestAnimationFrame` queue, start a published run, and
assert that `step-alpha` is visible as Running while `step-beta` is absent until
the two callbacks representing a completed paint boundary are released.

- [x] **Step 2: Run the page regression and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx \
  --testNamePattern 'presents each node start from one SSE chunk'
```

Expected: FAIL because the current stream loop consumes both node lifecycles
before React paints and the first observable state already contains both rows.

- [x] **Step 3: Add the shared paint-boundary helper and unit tests**

Implement `waitForWorkflowNodeStartPaint(event, signal, scheduler)` in
`streamingExecutionPresentation.ts`. Use `extractStepRequest(event)` as the
typed semantic detector. Schedule two animation frames for a node-start event,
resolve only from the second callback, return immediately for other events, and
cancel pending callbacks when the abort signal fires.

- [x] **Step 4: Integrate both workflow execution consumers**

After `setLiveRunExecution(...)` in `useWorkflowEditor.ts` and after
`setExecutionDetail(...)` in `useTeamMemberWorkflowStudio.ts`, call:

```ts
await waitForWorkflowNodeStartPaint(event, controller.signal);
```

Do not apply this wait to the parser or unrelated SSE consumers.

- [x] **Step 5: Verify GREEN and update the pull request**

Run the new helper test, the named page regression, the analyzer-selected
related tests, changed-file Biome, `bash tools/ci/test_stability_guards.sh`, the
vNext baseline verifier, and `git diff --check`. Review the base diff, commit,
push the existing branch, and update PR #3580 with the new commands and design
decision. Full frontend typecheck, suite, and production build remain delegated
to GitHub CI.

### Task 5: Present The Submitted Node Inventory Up Front

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/workflows/WorkflowExecutionLogsPanel.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioExecutionPanel.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/locales/en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/zh-CN.ts`

- [x] **Step 1: Write definition-first component regressions**

Pass three distinct workflow node snapshots to `ControlledExecutionPanel`.
With only `RUN_STARTED`, assert that all three node rows exist in definition
order, remain disabled, and say `Pending`. After adding the first step request,
assert that only the first row becomes Running and selected while the later rows
remain Pending. Add a terminal execution case asserting that definition nodes
without an attempt say `Not run`.

- [x] **Step 2: Update the single-chunk page regression**

Keep the existing controlled animation-frame boundary, but replace the old
assertion that `step-beta` is absent with:

```ts
const betaRow = within(logs).getByTestId(
  'workflow-execution-log-row-node-step-beta',
);
expect(betaRow).toHaveTextContent('Pending');
expect(betaRow).toBeDisabled();
```

After the first paint boundary, query the row again and assert that its real
runtime attempt is Running, selected, and enabled.

- [x] **Step 3: Run the focused cases and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand \
  --runTestsByPath \
  src/pages/team-member-workflow-studio/components/WorkflowStudioExecutionPanel.test.tsx \
  src/pages/workflow-activity-vnext/index.test.tsx \
  --testNamePattern 'shows submitted nodes before they start|marks unentered nodes as not run|presents each node start from one SSE chunk'
```

Expected: FAIL because `buildNodeOverviewEntries` currently drops every
definition node without a matching runtime log entry.

- [x] **Step 4: Merge definition placeholders with runtime attempts**

Extend the presentation-only status union with `pending` and `not-run`. Build a
placeholder entry for each unique definition node without a logged attempt:

```ts
function createDefinitionNodeEntry(
  node: WorkflowExecutionNodeSnapshot,
  status: 'pending' | 'not-run',
): ExecutionOverviewEntry {
  const stepId = node.stepId.trim();
  return {
    category: 'step',
    completedAt: '',
    entryId: `node-definition:${stepId}`,
    eventCount: 0,
    eventType: '',
    inputText: '',
    interactionText: '',
    logIndex: -1,
    logIndexes: [],
    meta: node.targetRole,
    outputText: '',
    payloadText: '',
    pendingText: '',
    previewText: '',
    rawText: '',
    rowType: 'node',
    startedAt: '',
    status,
    stepId,
    subtitle: node.subtitle,
    title: getUserFacingIdentifierLabel(stepId, node.title || stepId),
  };
}
```

Keep placeholders nonselectable and without timestamps or details. Replace a
placeholder with all real attempt entries when logs for that step exist, and
append runtime-only nodes after the definition order. In Workflow Activity,
capture `buildWorkflowExecutionNodeSnapshots(editor.document)` in component
state when the published run starts so edits made during that run cannot alter
its submitted inventory; reset that snapshot when changing workflow routes.

- [x] **Step 5: Add honest status copy and visuals**

Map `pending` to the existing localized Pending label and a neutral clock icon.
Add `teamMemberWorkflowStudio.executionPanel.status.notRun` with English
`Not run` and Chinese `未运行`, rendered with a neutral minus icon. Keep
interaction-level `waiting` separate because it describes a node that has
already started and is waiting for input or a signal.

- [x] **Step 6: Verify GREEN and deliver the PR update**

Run the focused RED command and the dependency-analyzer-selected related tests,
then changed-file Biome, `bash tools/ci/test_stability_guards.sh`, the vNext
baseline verifier, and `git diff --check`. Review the base diff, stage only Task
5 files plus this plan, commit, push the existing branch, and update PR #3580.
Package-wide typecheck, the full frontend suite, and production build remain
delegated to GitHub CI.
