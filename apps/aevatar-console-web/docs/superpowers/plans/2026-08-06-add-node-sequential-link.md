# Add Node Sequential Link Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Workflow Activity vNext Add node extend the current flow with explicit sequential transitions instead of appending a disconnected step that triggers `implicit_next` warnings.

**Architecture:** Add one pure document-normalization helper beside the existing insertion helpers, then compose it into only the Workflow Activity vNext `addNode` callback. The helper materializes runtime-defined adjacent ordering without changing explicit or branched transitions; `insertStepByType` remains responsible for rewiring the chosen predecessor and preserving its former successor.

**Tech Stack:** React, TypeScript, Jest, Testing Library, pnpm.

---

## File Map

- Create `apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-add-node-sequential-link.md`: executable TDD and delivery plan.
- Modify `apps/aevatar-console-web/src/shared/studio/document.ts`: export a pure helper that turns eligible adjacent implicit transitions into explicit `next` values.
- Modify `apps/aevatar-console-web/src/shared/studio/document.test.ts`: cover chain materialization, explicit and branched preservation, terminal behavior, and immutability.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`: normalize the document, resolve the insertion predecessor, and pass it to `insertStepByType`.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`: exercise Add node through the page UI for append and selected-middle insertion.
- Existing `apps/aevatar-console-web/docs/superpowers/specs/2026-08-06-add-node-sequential-link-design.md`: approved behavior contract; no implementation edits expected.

### Task 1: Specify The Pure Transition Materializer

**Files:**

- Modify: `apps/aevatar-console-web/src/shared/studio/document.test.ts`
- Test: `apps/aevatar-console-web/src/shared/studio/document.test.ts`

- [ ] **Step 1: Import the new helper and write the failing chain test**

Add `materializeImplicitSequentialTransitions` to the import from `./document`, then add a test with three ordered steps: an implicit first step, a second step with an explicit successor, and a terminal third step. Assert that the first gains `next: 'review_step'`, the explicit second successor remains unchanged, and the input object still equals a deep snapshot.

```ts
it('materializes only eligible implicit sequential transitions without mutating the document', () => {
  const document: StudioWorkflowDocument = {
    name: 'workspace-demo',
    roles: [],
    steps: [
      { id: 'draft_step', type: 'llm_call', next: null, branches: {} },
      {
        id: 'review_step',
        type: 'human_approval',
        next: 'publish_step',
        branches: {},
      },
      { id: 'publish_step', type: 'emit', next: null, branches: {} },
    ],
  };
  const snapshot = structuredClone(document);

  const result = materializeImplicitSequentialTransitions(document);

  expect(result.steps?.map((step) => step.next)).toEqual([
    'review_step',
    'publish_step',
    null,
  ]);
  expect(document).toEqual(snapshot);
  expect(result).not.toBe(document);
});
```

- [ ] **Step 2: Write the failing branched and terminal test**

Add a case where the first step has `branches: { approved: 'publish_step' }` and no `next`, followed by a second step. Assert the first step still has no linear successor and its branch remains byte-for-byte equivalent. In the same test group, assert empty and single-step documents keep no synthesized transition.

```ts
it('preserves branched transitions and terminal documents', () => {
  const branched = materializeImplicitSequentialTransitions({
    name: 'branched',
    roles: [],
    steps: [
      {
        id: 'approval_step',
        type: 'human_approval',
        next: null,
        branches: { approved: 'publish_step' },
      },
      { id: 'publish_step', type: 'emit', next: null, branches: {} },
    ],
  });

  expect(branched.steps?.[0]).toEqual(
    expect.objectContaining({
      next: null,
      branches: { approved: 'publish_step' },
    }),
  );
  expect(materializeImplicitSequentialTransitions({ steps: [] }).steps).toEqual([]);
  expect(
    materializeImplicitSequentialTransitions({
      steps: [{ id: 'only_step', type: 'llm_call', next: null, branches: {} }],
    }).steps?.[0]?.next,
  ).toBeNull();

  const firstInsertion = insertStepByType(
    materializeImplicitSequentialTransitions({ steps: [] }),
    'assign',
  );
  expect(firstInsertion.document.steps).toEqual([
    expect.objectContaining({ id: 'assign_step', next: null }),
  ]);
});
```

- [ ] **Step 3: Run the exact helper tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/document.test.ts --testNamePattern 'materializes only eligible implicit sequential transitions|preserves branched transitions and terminal documents'
```

Expected: FAIL because `materializeImplicitSequentialTransitions` is not exported or defined.

- [ ] **Step 4: Commit the failing tests only**

```bash
git add apps/aevatar-console-web/src/shared/studio/document.test.ts
git commit -m "Test implicit workflow transition materialization"
```

### Task 2: Implement The Pure Transition Materializer

**Files:**

- Modify: `apps/aevatar-console-web/src/shared/studio/document.ts`
- Test: `apps/aevatar-console-web/src/shared/studio/document.test.ts`

- [ ] **Step 1: Implement the minimal pure helper**

Place the exported helper before `insertStepByType`. Clone every step, inspect every non-final step, and only set `next` when the current `next` normalizes empty, the branch map has no entries, and the following sibling has a normalized ID.

```ts
export function materializeImplicitSequentialTransitions(
  document: StudioWorkflowDocument,
): StudioWorkflowDocument {
  const steps = Array.isArray(document.steps) ? document.steps : [];
  const nextSteps = steps.map((entry, index) => {
    const step = { ...entry } as StudioWorkflowStepDocument;
    const followingStepId = normalizeString(steps[index + 1]?.id);
    const hasBranches = Object.keys(step.branches ?? {}).length > 0;

    if (
      index < steps.length - 1 &&
      !normalizeString(step.next) &&
      !hasBranches &&
      followingStepId
    ) {
      return { ...step, next: followingStepId } satisfies StudioWorkflowStepDocument;
    }

    return step;
  });

  return { ...document, steps: nextSteps };
}
```

- [ ] **Step 2: Run the exact helper tests and verify GREEN**

Run the command from Task 1 Step 3.

Expected: PASS for both focused tests.

- [ ] **Step 3: Run all shared document helper tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/document.test.ts
```

Expected: all tests in `document.test.ts` pass.

- [ ] **Step 4: Commit the helper implementation**

```bash
git add apps/aevatar-console-web/src/shared/studio/document.ts
git commit -m "Materialize implicit workflow transitions"
```

### Task 3: Specify Add Node Placement Through The Page

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Write the failing no-selection append test**

Arrange `getWorkflow` with two implicit steps, make `serializeYaml` return its submitted document, click `Add node`, then `Insert Assign node`. Assert the serialized steps are `draft_step -> review_step -> assign_step`, with the inserted step terminal.

```ts
it('adds a node after the final step and materializes the existing implicit chain', async () => {
  const document = {
    name: 'committed_source',
    roles: [],
    steps: [
      { id: 'draft_step', type: 'llm_call', next: null, branches: {} },
      { id: 'review_step', type: 'human_approval', next: null, branches: {} },
    ],
  };
  mockStudioApi.getWorkflow.mockResolvedValue({
    workflowId: 'wf-committed-source',
    name: 'Committed source',
    fileName: 'committed-source.yaml',
    filePath: '',
    directoryId: '',
    directoryLabel: '',
    yaml: 'name: committed_source\nroles: []\nsteps: []\n',
    updatedAtUtc: '2026-08-04T10:00:00Z',
    document,
    draftExists: false,
    findings: [],
  });
  mockStudioApi.serializeYaml.mockImplementation(async ({ document: submitted }) => ({
    yaml: 'serialized',
    document: submitted,
    findings: [],
  }));

  renderWithQueryClient(<WorkflowActivityVNextPage />);
  fireEvent.click(await screen.findByRole('button', { name: 'Add node' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Insert Assign node' }));

  await waitFor(() => expect(mockStudioApi.serializeYaml).toHaveBeenCalledTimes(1));
  expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual([
    expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
    expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
    expect.objectContaining({ id: 'assign_step', next: null }),
  ]);
});
```

- [ ] **Step 2: Write the failing selected-middle insertion test**

Arrange an explicit three-step chain, click `Select step:review_step`, then use Add node to insert Assign. Assert serialization receives `draft_step -> review_step -> assign_step -> publish_step` and all unaffected transitions remain intact.

```ts
expect(mockStudioApi.serializeYaml.mock.calls[0][0].document.steps).toEqual([
  expect.objectContaining({ id: 'draft_step', next: 'review_step' }),
  expect.objectContaining({ id: 'review_step', next: 'assign_step' }),
  expect.objectContaining({ id: 'assign_step', next: 'publish_step' }),
  expect.objectContaining({ id: 'publish_step', next: null }),
]);
```

- [ ] **Step 3: Run the exact page tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern 'adds a node after the final step and materializes the existing implicit chain|inserts a node after the selected middle step and preserves its successor'
```

Expected: FAIL because the current callback appends without `afterStepId` and does not materialize existing adjacency.

- [ ] **Step 4: Commit the failing page tests only**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx
git commit -m "Test Add node sequential placement"
```

### Task 4: Wire Explicit Placement Into Workflow Activity vNext

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Import the materializer**

```ts
import {
  applyStepInspectorDraft,
  createStepInspectorDraft,
  insertStepByType,
  materializeImplicitSequentialTransitions,
} from '@/shared/studio/document';
```

- [ ] **Step 2: Normalize the document and resolve the predecessor**

Inside `addNode`, after obtaining `current`, materialize its implicit adjacency. Resolve a selected step only when `selectedNodeId` has the `step:` prefix; otherwise use the last step with a non-empty ID.

```ts
const explicitDocument = materializeImplicitSequentialTransitions(current);
const selectedStepId = selectedNodeId.startsWith('step:')
  ? selectedNodeId.slice('step:'.length).trim()
  : '';
const finalStepId = [...(explicitDocument.steps ?? [])]
  .reverse()
  .map((step) => String(step.id ?? '').trim())
  .find(Boolean);
const inserted = insertStepByType(explicitDocument, stepType, {
  afterStepId: selectedStepId || finalStepId || null,
});
```

Do not add warning filtering. Keep the existing serialization request, generation guard, selection update, retry state, and local-edit marking unchanged.

- [ ] **Step 3: Add the selection dependency**

```ts
[document, markLocalEdit, parseCurrentYaml, selectedNodeId]
```

- [ ] **Step 4: Run the exact page tests and verify GREEN**

Run the command from Task 3 Step 3.

Expected: both new tests pass.

- [ ] **Step 5: Run the existing insertion regression tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern 'waits for a node insertion to finish before allowing a competing save|locks structural editing while a save is still in flight|keeps a failed node insertion visible and retryable'
```

Expected: all three existing insertion lifecycle tests pass.

- [ ] **Step 6: Commit the vNext wiring**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts
git commit -m "Fix Add node sequential placement"
```

### Task 5: Focused Validation And Pull Request

**Files:**

- Verify all task files listed above.
- Do not run package-wide test, lint, typecheck, or build commands locally.

- [ ] **Step 1: Analyze the frontend change scope**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Expected: frontend-only scope limited to the Workflow Activity vNext editor, shared Studio document helper, focused tests, and docs.

- [ ] **Step 2: Read the framework command guidance selected by the analyzer**

Read `~/.codex/skills/frontend-incremental-pr/references/framework-commands.md` and select only changed-file static checks supported by this project. If no reliable affected typecheck exists, skip local typecheck and state that GitHub CI owns full verification.

- [ ] **Step 3: Run focused tests together**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/shared/studio/document.test.ts src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: both changed test files pass.

- [ ] **Step 4: Run mandatory test and baseline guards**

```bash
bash tools/ci/test_stability_guards.sh
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
```

Expected: all commands exit 0 and the baseline digest remains unchanged.

- [ ] **Step 5: Review the complete diff and repository state**

```bash
git status --short --branch
git diff --stat origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git diff origin/feat/2026-08-04_workflow-activity-vnext...HEAD -- apps/aevatar-console-web/src/shared/studio/document.ts apps/aevatar-console-web/src/shared/studio/document.test.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx apps/aevatar-console-web/docs/superpowers/specs/2026-08-06-add-node-sequential-link-design.md apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-add-node-sequential-link.md
```

Expected: no unrelated file changes, no generated artifacts, and the implementation matches the approved design.

- [ ] **Step 6: Commit the implementation plan and any final focused corrections**

```bash
git add apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-add-node-sequential-link.md
git commit -m "Document Add node implementation plan"
```

- [ ] **Step 7: Push and open the pull request against the required base**

```bash
git push -u origin fix/2026-08-06_add-node-sequential-link
gh pr create --base feat/2026-08-04_workflow-activity-vnext --head fix/2026-08-06_add-node-sequential-link --title "Fix Add node sequential placement" --body-file <prepared-pr-body>
```

The PR body must include the root cause, explicit transition solution, affected paths, exact focused commands and results, unchanged vNext design baseline declaration, and this statement:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy.
```

Do not wait for CI after the PR is created unless the user asks.
