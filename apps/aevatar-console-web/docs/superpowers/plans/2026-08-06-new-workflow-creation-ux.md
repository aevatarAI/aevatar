# New Workflow Creation UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every New workflow method a focused, single-action path that hides non-decisions, preserves backend truth, and opens the common editor after authoritative draft creation.

**Architecture:** Keep route-owned form state in `NewWorkflowPage`, extract deterministic unique-file-name resolution into `workflowCreation.ts`, and preserve the existing typed Studio API plus `useDraftMaterialization` boundaries. The page derives method-specific names, selects the only directory automatically, renders a directory selector only for multiple choices, and runs generation/parse/create as distinct recovery-aware stages behind one user command.

**Tech Stack:** React 19, TypeScript, Ant Design 6, TanStack Query, Umi localization, Jest, Testing Library, Biome.

---

### Task 1: Protect Hidden Save Target And File Name Resolution

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowCreation.ts`

- [ ] **Step 1: Extend the page-test boundary mocks**

Add the two existing read boundaries used by `NewWorkflowPage`:

```tsx
jest.mock('@/shared/api/scopesApi', () => ({
  scopesApi: { listWorkflows: jest.fn() },
}));

studioApi: {
  authorWorkflow: jest.fn(),
  createWorkflowDraft: jest.fn(),
  getWorkspaceSettings: jest.fn(),
  listWorkflowDrafts: jest.fn(),
  parseYaml: jest.fn(),
}
```

Reset `listWorkflowDrafts` and `listWorkflows` to empty successful arrays in
`beforeEach` so every test has authoritative collection state.

Define the shared materialized response used by the creation cases:

```tsx
const materializedWorkflow = {
  kind: 'materialized',
  workflow: {
    directoryId: 'directory-alpha',
    directoryLabel: 'Workflows',
    document: { name: 'incident_review', roles: [], steps: [] },
    draftExists: true,
    fileName: 'incident-review.yaml',
    filePath: '/workflows/incident-review.yaml',
    findings: [],
    name: 'Incident review',
    updatedAtUtc: '2026-08-06T10:00:00Z',
    workflowId: 'wf-created-alpha',
    yaml: 'name: incident_review\nroles: []\nsteps: []\n',
  },
} as const;
```

- [ ] **Step 2: Write failing save-target and filename tests**

Add route-level tests that prove one directory is hidden, two directories are
selectable, and an occupied YAML filename receives the first free suffix:

```tsx
it('hides the only save target while still using its directory id', async () => {
  mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Start blank' }));
  expect(screen.queryByLabelText('Save to')).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Workflow name'), {
    target: { value: 'Incident review' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Create and open' }));

  await waitFor(() =>
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ directoryId: 'directory-alpha' }),
    ),
  );
});

it('shows Save to only when the workspace has multiple directories', async () => {
  mockStudioApi.getWorkspaceSettings.mockResolvedValue({
    runtimeBaseUrl: '',
    directories: [
      readyWorkspace.directories[0],
      {
        directoryId: 'directory-beta',
        isBuiltIn: false,
        label: 'Operations',
        path: '/operations',
      },
    ],
  });
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Start blank' }));
  fireEvent.mouseDown(screen.getByLabelText('Save to'));
  fireEvent.click(await screen.findByText('Operations'));
  fireEvent.change(screen.getByLabelText('Workflow name'), {
    target: { value: 'Incident review' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Create and open' }));

  await waitFor(() =>
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ directoryId: 'directory-beta' }),
    ),
  );
});

it('uses the first available YAML filename without changing the display name', async () => {
  mockStudioApi.listWorkflowDrafts.mockResolvedValue([
    { directoryId: 'directory-alpha', fileName: 'incident-review.yaml', name: 'Other' },
    { directoryId: 'directory-alpha', fileName: 'incident-review-2.yaml', name: 'Other 2' },
  ]);
  mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Start blank' }));
  fireEvent.change(screen.getByLabelText('Workflow name'), {
    target: { value: 'Incident review' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Create and open' }));

  await waitFor(() =>
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({
        fileName: 'incident-review-3.yaml',
        workflowName: 'Incident review',
      }),
    ),
  );
});
```

- [ ] **Step 3: Run the focused test file and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx
```

Expected: FAIL because the single selector is still visible, the multiple-only
label/action copy does not exist, and filename suffix resolution is absent.

- [ ] **Step 4: Implement deterministic filename resolution**

Add this public helper to `workflowCreation.ts`:

```ts
export function resolveAvailableWorkflowFileName(
  name: string,
  directoryId: string,
  drafts: readonly { readonly directoryId: string; readonly fileName: string }[],
): string {
  const preferred = slugifyWorkflowFileName(name);
  const extensionIndex = preferred.toLowerCase().lastIndexOf('.yaml');
  const stem = extensionIndex >= 0 ? preferred.slice(0, extensionIndex) : preferred;
  const occupied = new Set(
    drafts
      .filter((draft) => draft.directoryId === directoryId)
      .map((draft) => draft.fileName.trim().toLocaleLowerCase()),
  );
  if (!occupied.has(preferred.toLocaleLowerCase())) return preferred;

  let suffix = 2;
  while (occupied.has(`${stem}-${suffix}.yaml`.toLocaleLowerCase())) suffix += 1;
  return `${stem}-${suffix}.yaml`;
}
```

Use this helper when building every draft-create request. The backend remains
the final conflict authority when another client wins a concurrent create.

- [ ] **Step 5: Render a selector only for multiple directories**

Replace the unconditional `Save location` block with:

```tsx
{(workspace.data?.directories.length ?? 0) > 1 ? (
  <label className="wa-vnext__creation-field">
    <span>{t('workflowActivityVNext.new.directory', 'Save to')}</span>
    <Select
      aria-label={t('workflowActivityVNext.new.directory', 'Save to')}
      className="wa-vnext__field-control"
      onChange={setDirectoryId}
      options={directoryOptions}
      value={directoryId || undefined}
    />
  </label>
) : null}
```

Keep the existing effect that adopts the first real directory. Do not derive a
directory locally when the query is pending, failed, or empty.

- [ ] **Step 6: Run the focused test file and confirm GREEN**

Run the Step 3 command again.

Expected: PASS for the new save-target and filename behaviors; existing
recovery tests remain green.

- [ ] **Step 7: Commit the focused behavior**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowCreation.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx
git commit -m "Hide automatic workflow save targets"
```

### Task 2: Collapse Describe Into One Authoritative Action

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`

- [ ] **Step 1: Write the failing Describe integration test**

```tsx
it('generates, validates, creates, and opens a described workflow with one action', async () => {
  mockStudioApi.authorWorkflow.mockResolvedValue(
    'name: weekly_feedback\nroles: []\nsteps:\n  - id: summarize\n    type: llm_call\n',
  );
  mockStudioApi.parseYaml.mockResolvedValue({
    document: { name: 'weekly_feedback', roles: [], steps: [{ id: 'summarize', type: 'llm_call' }] },
    findings: [],
  });
  mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Describe' }));
  fireEvent.change(screen.getByLabelText('Workflow name'), {
    target: { value: 'Weekly feedback' },
  });
  expect(screen.queryByLabelText('Generated YAML')).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('What should this workflow do?'), {
    target: { value: 'Summarize weekly customer feedback' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Generate and open' }));

  await waitFor(() => expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledTimes(1));
  expect(mockStudioApi.authorWorkflow).toHaveBeenCalledWith(
    { prompt: 'Summarize weekly customer feedback' },
    expect.objectContaining({ onText: expect.any(Function) }),
  );
  expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
    expect.objectContaining({ workflowName: 'Weekly feedback' }),
  );
  expect(history.push).toHaveBeenCalledWith(
    '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-created-alpha',
  );
});
```

Add a failure case that rejects `createWorkflowDraft`, confirms the prompt
value remains visible, and confirms another click retries the single action.

- [ ] **Step 2: Run the focused test and confirm RED**

Run the Task 1 Step 3 command.

Expected: FAIL because Describe still renders Workflow name, Automation goal,
Generate workflow, generated YAML, and a second Create workflow action.

- [ ] **Step 3: Replace the preview flow with one submit pipeline**

Remove `generatedYaml` and `generatedReady`. Implement one handler with clear
stage boundaries:

```ts
const createFromDescription = async () => {
  const normalizedPrompt = prompt.trim();
  if (!normalizedPrompt || submitting || !directoryId) return;
  setSubmitting(true);
  setFailure('');
  setFindings([]);
  try {
    const generated = await studioApi.authorWorkflow(
      { prompt: normalizedPrompt },
      { onText: () => undefined },
    );
    const parsed = await studioApi.parseYaml({ yaml: generated });
    setFindings(parsed.findings);
    if (hasBlockingFindings(parsed.document, parsed.findings)) return;
    await persist(generated, name.trim(), { manageSubmitting: false });
  } catch (error) {
    setFailure(errorMessage(error));
  } finally {
    setSubmitting(false);
  }
};
```

Adapt `persist` so composed flows do not toggle `submitting` off between parse
and create. Keep the materialized/accepted handling in `finishSave` unchanged.

- [ ] **Step 4: Render the focused Describe surface**

Use `Describe your workflow`, the supporting sentence, a Workflow name input,
a labeled textarea, and one primary button:

```tsx
<label className="wa-vnext__creation-field">
  <span>{t('workflowActivityVNext.new.goal', 'What should this workflow do?')}</span>
  <Input.TextArea
    aria-label={t('workflowActivityVNext.new.goal', 'What should this workflow do?')}
    autoSize={{ minRows: 6, maxRows: 12 }}
    onChange={(event) => setPrompt(event.target.value)}
    value={prompt}
  />
</label>
<Button
  disabled={!prompt.trim() || saveTargetUnavailable}
  loading={submitting}
  onClick={() => void createFromDescription()}
  type="primary"
>
  {t('workflowActivityVNext.new.generateAndOpen', 'Generate and open')}
</Button>
```

- [ ] **Step 5: Run the focused test and confirm GREEN**

Run the Task 1 Step 3 command.

Expected: PASS with one Describe action and preserved existing save-target
recovery behavior.

- [ ] **Step 6: Commit Describe behavior**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx
git commit -m "Streamline described workflow creation"
```

### Task 3: Align Blank, Import, And Template Creation

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`

- [ ] **Step 1: Write failing method-specific tests**

Protect the final control contract:

```tsx
it('imports parsed YAML without asking for a second workflow name', async () => {
  mockStudioApi.parseYaml.mockResolvedValue({
    document: { name: 'imported_orders', roles: [], steps: [] },
    findings: [],
  });
  mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Import YAML' }));
  expect(screen.queryByLabelText('Workflow name')).not.toBeInTheDocument();
  fireEvent.change(screen.getByLabelText('Workflow YAML'), {
    target: { value: 'name: imported_orders\nroles: []\nsteps: []\n' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Import and open' }));

  await waitFor(() =>
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ workflowName: 'imported_orders' }),
    ),
  );
});

it('creates an independent template copy with one action', async () => {
  mockStudioApi.parseYaml.mockResolvedValue({
    document: { name: 'incident_triage', roles: [], steps: [] },
    findings: [],
  });
  mockStudioApi.createWorkflowDraft.mockResolvedValue(materializedWorkflow);
  renderWithQueryClient(<NewWorkflowPage scopeId="scope-alpha" />);

  fireEvent.click(await screen.findByRole('button', { name: 'Use template' }));
  expect(screen.queryByLabelText('Workflow name')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Use template and open' }));

  await waitFor(() =>
    expect(mockStudioApi.createWorkflowDraft).toHaveBeenCalledWith(
      expect.objectContaining({ workflowName: 'Incident triage copy' }),
    ),
  );
});
```

Update the existing `index.test.tsx` creation assertions to the new accessible
labels and action names without duplicating the new focused page coverage.

- [ ] **Step 2: Run both owning test files and confirm RED**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: FAIL on old name fields and old action labels.

- [ ] **Step 3: Implement method-specific name derivation**

- Blank continues to require `Workflow name` and uses `Create and open`.
- Describe requires `Workflow name`, uses it independently of the generated
  YAML document name, and completes through `Generate and open`.
- Import parses first, rejects a missing parsed name with localized actionable
  copy, then persists using that parsed name and `Import and open`.
- Template copies the selected YAML, parses it, and persists with the localized
  display name `${templateName} copy` through `Use template and open`.
- Import and Template do not render the shared Workflow name field.

Keep the server parser call for template YAML so backend field naming and
validation remain authoritative.

- [ ] **Step 4: Run both owning test files and confirm GREEN**

Run the Step 2 command.

Expected: PASS for all creation methods, invalid YAML preservation, method
switch recovery, and existing materialization behavior.

- [ ] **Step 5: Run the polling stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

Expected: PASS; the changed tests introduce no arbitrary delay or polling
helper.

- [ ] **Step 6: Commit all creation paths**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx
git commit -m "Align workflow creation methods"
```

### Task 4: Restore Visual Hierarchy And Localized Copy

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Test: `apps/aevatar-console-web/src/locales/catalog.test.ts`

- [ ] **Step 1: Add the new messages to both locale catalogues**

Use equivalent English and Chinese product copy for:

```ts
'workflowActivityVNext.new.describeTitle': 'Describe your workflow',
'workflowActivityVNext.new.describeDescription':
  'Start with the outcome. You can review every generated step in the editor.',
'workflowActivityVNext.new.goal': 'What should this workflow do?',
'workflowActivityVNext.new.generateAndOpen': 'Generate and open',
'workflowActivityVNext.new.createAndOpen': 'Create and open',
'workflowActivityVNext.new.importAndOpen': 'Import and open',
'workflowActivityVNext.new.templateAndOpen': 'Use template and open',
'workflowActivityVNext.new.directory': 'Save to',
'workflowActivityVNext.new.importedNameMissing':
  'The imported workflow needs a name before it can be created.',
```

Remove creation-page-only obsolete messages after `rg` confirms they have no
remaining consumers: `generatedYaml`, `generate`, `createGenerated`, and
`defaultWorkspace`.

- [ ] **Step 2: Replace the full-width panel with a bounded creation column**

Add scoped primitives in `styles.ts`:

```css
.wa-vnext__creation-surface {
  margin: 0 auto;
  max-width: 760px;
  min-width: 0;
  padding: 8px 0 40px;
}
.wa-vnext__creation-heading {
  border-bottom: 1px solid var(--wa-line);
  margin-bottom: 22px;
  padding-bottom: 18px;
}
.wa-vnext__creation-heading h2 {
  font-size: 22px;
  line-height: 28px;
  margin: 0;
}
.wa-vnext__creation-heading p {
  color: var(--wa-muted);
  font-size: 12px;
  line-height: 18px;
  margin: 6px 0 0;
  max-width: 620px;
}
.wa-vnext__creation-form {
  display: grid;
  gap: 18px;
}
.wa-vnext__creation-field {
  display: grid;
  font-size: 12px;
  font-weight: 700;
  gap: 7px;
}
.wa-vnext__creation-actions {
  align-items: center;
  display: flex;
  gap: 10px;
  justify-content: flex-end;
}
```

At mobile width, make actions a single-column grid with full-width buttons.
Keep method cards, alerts, and materialization notice stable and outside any
nested card.

- [ ] **Step 3: Run localized catalogue verification**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/locales/catalog.test.ts
```

Expected: PASS with matching message IDs and interpolation variables.

- [ ] **Step 4: Run changed-file formatting and lint checks**

Run the frontend scope analyzer first and pass only its `staticCheckFiles` to
Biome:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base feat/2026-08-04_workflow-activity-vnext
pnpm --dir apps/aevatar-console-web exec biome check --write src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx src/pages/workflow-activity-vnext/workflows/workflowCreation.ts src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/styles.ts src/locales/workflowActivityVNextMessages.en-US.ts src/locales/workflowActivityVNextMessages.zh-CN.ts
```

Expected: Biome exits 0 and only formats the explicit task files. Do not run a
package lint, full typecheck, or production build.

- [ ] **Step 5: Commit visual and copy changes**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts
git commit -m "Refine new workflow creation layout"
```

### Task 5: Focused Verification And Pull Request Delivery

**Files:**
- Review every file changed from `feat/2026-08-04_workflow-activity-vnext`
- Update the open PR body after push

- [ ] **Step 1: Run the scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base feat/2026-08-04_workflow-activity-vnext
```

Record affected packages, test runner, changed tests, and `staticCheckFiles`.

- [ ] **Step 2: Run only dependency-related and explicitly changed tests**

```bash
pnpm --dir apps/aevatar-console-web exec jest --findRelatedTests src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx src/pages/workflow-activity-vnext/workflows/workflowCreation.ts src/pages/workflow-activity-vnext/styles.ts --runInBand
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx src/pages/workflow-activity-vnext/index.test.tsx src/locales/catalog.test.ts
```

If `--findRelatedTests` selects an unexpectedly broad set, stop it and use the
three explicit test files instead. Do not run the complete frontend suite.

- [ ] **Step 3: Run required non-frontend guards**

```bash
bash tools/ci/test_stability_guards.sh
git diff --check feat/2026-08-04_workflow-activity-vnext...HEAD
```

Expected: PASS.

- [ ] **Step 4: Perform browser visual verification**

Start the existing development server on a free port with the worktree's
configured auth/backend origins kept consistent. Verify desktop and mobile
screens for the chooser, Describe, blank, import, template, loading, failed,
and multiple-directory states. Confirm no overlap, horizontal escape, hidden
primary action, or blank canvas. Capture screenshots as review evidence.

If the authenticated backend cannot be reached, use the existing deterministic
component tests for state evidence and report the browser gap. Do not add mock
production data or alter auth behavior to obtain a screenshot.

- [ ] **Step 5: Review the complete diff and commit any verification-only fixes**

```bash
git status --short
git diff --stat feat/2026-08-04_workflow-activity-vnext...HEAD
git diff feat/2026-08-04_workflow-activity-vnext...HEAD -- apps/aevatar-console-web/src/pages/workflow-activity-vnext apps/aevatar-console-web/src/locales apps/aevatar-console-web/docs/superpowers
```

Stage only task files and use an imperative commit message if verification
requires a source adjustment.

- [ ] **Step 6: Push and create the incremental PR**

Create `/tmp/aevatar-new-workflow-creation-ux-pr.md` with the final observed
command results substituted into this exact structure:

```markdown
## Problem and solution

The New workflow route exposed its only workspace directory as a user choice,
used an unclear Automation goal label, required a second create confirmation
after generation, and stretched short creation forms across the work area.

This PR hides the automatic directory, shows Save to only for multiple real
directories, gives every creation method one final action, derives generated
and imported names from validated documents, prevents YAML path collisions,
and restores a bounded creation layout.

## Affected paths

- Workflow Activity vNext New workflow route and creation helpers
- Focused route and component integration tests
- Workflow Activity vNext English and Chinese messages
- New workflow creation UX design and implementation documents

## Local verification

- Related tests: focused `NewWorkflowPage.test.tsx`, `index.test.tsx`, and `catalog.test.ts` Jest commands listed above - PASS
- Changed-file static checks: scope-analyzer `staticCheckFiles` passed to `biome check` - PASS
- Test stability guard: `bash tools/ci/test_stability_guards.sh` - PASS
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy

## Design baseline

Design baseline: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/`

Primary design: `aevatar-workflow-activity-vnext.excalidraw`

Design SHA-256: `30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de`

Contract specification: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`

User paths: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`

Authentication and localization: existing Aevatar login, callback, session,
returnTo, and Umi locale logic; presentation may change, behavior may not.

Production data source: real APIs and API-acknowledged user actions only; no
mock fallback.

Baseline integrity: `python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
```

If an executed command differs from the planned command or does not pass,
replace the corresponding verification line with the exact observed command
and result; do not add unverified claims.

```bash
git push -u origin fix/2026-08-06_new-workflow-creation-ux
gh pr create --base feat/2026-08-04_workflow-activity-vnext --head fix/2026-08-06_new-workflow-creation-ux --title "Fix new workflow creation UX" --body-file /tmp/aevatar-new-workflow-creation-ux-pr.md
```

The PR body must include problem and solution, affected paths, the required
Workflow Activity vNext baseline declaration, exact local commands/results,
and this statement:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy.
```

Do not wait for CI after creating the PR.
