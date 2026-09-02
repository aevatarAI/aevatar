# Workflow Node Reselection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make clicking the already-selected workflow node idempotent while preserving discard confirmation for real node switches.

**Architecture:** Keep the policy in `WorkflowEditorPage`, where the clicked node ID and active node ID are both available. Add rendered route-level tests because the regression depends on the canvas, editor selection state, inspector draft state, and modal collaborating correctly.

**Tech Stack:** React 19, TypeScript, Umi Max, Ant Design, Jest, Testing Library

---

### Task 1: Lock The Selection Boundary With Failing Tests

**Files:**
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Add the same-node regression test**

Add a rendered editor test that selects `step-root`, changes its Instruction to
`Updated prompt`, clicks `Select step:step-root` again, and asserts that no
`Discard node changes?` dialog exists and the Instruction still contains the
unapplied value.

```tsx
it('keeps unapplied node configuration when the selected node is clicked again', async () => {
  renderWithQueryClient(<WorkflowActivityVNextPage />);

  const selectedNode = await screen.findByRole('button', {
    name: 'Select step:step-root',
  });
  fireEvent.click(selectedNode);
  const inspector = await screen.findByRole('complementary', {
    name: 'Configure step-root',
  });
  fireEvent.change(within(inspector).getByLabelText('Instruction'), {
    target: { value: 'Updated prompt' },
  });

  fireEvent.click(selectedNode);

  expect(
    screen.queryByRole('dialog', { name: 'Discard node changes?' }),
  ).not.toBeInTheDocument();
  expect(within(inspector).getByLabelText('Instruction')).toHaveValue(
    'Updated prompt',
  );
});
```

- [ ] **Step 2: Add the different-node guard test**

Override the workflow fixture with distinct `step-root` and `step-second`
nodes. Dirty `step-root`, click `step-second`, verify the confirmation appears,
cancel and verify the root draft remains, then discard and verify the second
inspector opens.

```tsx
it('asks before switching from a node with unapplied configuration', async () => {
  mockStudioApi.getWorkflow.mockResolvedValue({
    workflowId: 'wf-committed-source',
    name: 'Committed source',
    fileName: 'committed-source.yaml',
    filePath: '',
    directoryId: '',
    directoryLabel: '',
    yaml: [
      'name: committed_source',
      'roles: []',
      'steps:',
      '  - id: step-root',
      '    type: llm_call',
      '    parameters:',
      '      prompt_prefix: Original prompt',
      '  - id: step-second',
      '    type: transform',
      '    parameters:',
      '      operation: trim',
      '',
    ].join('\n'),
    updatedAtUtc: '2026-08-04T10:00:00Z',
    document: {
      name: 'committed_source',
      roles: [],
      steps: [
        {
          id: 'step-root',
          type: 'llm_call',
          parameters: { prompt_prefix: 'Original prompt' },
        },
        {
          id: 'step-second',
          type: 'transform',
          parameters: { operation: 'trim' },
        },
      ],
    },
    draftExists: false,
    findings: [],
  });
  renderWithQueryClient(<WorkflowActivityVNextPage />);

  fireEvent.click(
    await screen.findByRole('button', { name: 'Select step:step-root' }),
  );
  const rootInspector = await screen.findByRole('complementary', {
    name: 'Configure step-root',
  });
  fireEvent.change(within(rootInspector).getByLabelText('Instruction'), {
    target: { value: 'Updated prompt' },
  });
  const secondNode = screen.getByRole('button', {
    name: 'Select step:step-second',
  });

  fireEvent.click(secondNode);

  const discardDialog = await screen.findByRole('dialog', {
    name: 'Discard node changes?',
  });
  expect(within(rootInspector).getByLabelText('Instruction')).toHaveValue(
    'Updated prompt',
  );
  fireEvent.click(
    within(discardDialog).getByRole('button', { name: 'Cancel' }),
  );
  expect(within(rootInspector).getByLabelText('Instruction')).toHaveValue(
    'Updated prompt',
  );

  fireEvent.click(secondNode);
  fireEvent.click(screen.getByRole('button', { name: 'Discard changes' }));

  expect(
    await screen.findByRole('complementary', {
      name: 'Configure step-second',
    }),
  ).toBeVisible();
});
```

- [ ] **Step 3: Run only the two new tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx \
  --testNamePattern 'selected node is clicked again|switching from a node with unapplied configuration'
```

Expected: the same-node test fails because the discard dialog opens. The
different-node test passes or exposes only a test-fixture issue that must be
corrected before implementation.

### Task 2: Make Same-Node Selection Idempotent

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx:297`

- [ ] **Step 1: Add the minimal identity guard**

```tsx
const requestNodeSelect = React.useCallback(
  (nodeId: string) => {
    if (nodeId === editor.selectedNodeId) return;
    requestInspectorDiscard(() => editor.selectNode(nodeId));
  },
  [editor.selectNode, editor.selectedNodeId, requestInspectorDiscard],
);
```

- [ ] **Step 2: Re-run the two focused tests and verify GREEN**

Run the same Jest command from Task 1. Expected: both selected tests pass with
zero failures.

### Task 3: Validate And Deliver The Incremental Change

**Files:**
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`

- [ ] **Step 1: Run the complete directly changed test file**

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: the file passes. Do not pass locale catalogs or other high-fan-out
files to `--findRelatedTests`.

- [ ] **Step 2: Run required focused guards and changed-file checks**

Run `bash tools/ci/test_stability_guards.sh`, the frontend change-scope
analyzer, the changed-file Biome command selected from the analyzer output,
and `git diff --check`. Do not run a full frontend test, lint, typecheck, or
production build. If no repository-native affected typecheck exists, delegate
typecheck to GitHub CI.

- [ ] **Step 3: Verify the browser interaction**

On the authenticated local vNext editor:

- dirty a node and re-click it: no modal and draft retained;
- dirty a node and click another node: modal shown;
- cancel: first-node draft retained;
- discard: second-node inspector opens.

- [ ] **Step 4: Review, commit, push, and update the pull request**

Stage only this task's source, test, design, and plan files. Commit with the
imperative message `Fix workflow node reselection`. Push the current branch and
update PR #3269 with exact focused verification commands, results, the vNext
design-baseline declaration, and the statement that the full frontend
suite/build is delegated to GitHub CI.
