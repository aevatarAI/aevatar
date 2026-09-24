# Workflow Archive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore an honest Archive action for active published Workflows, observe authoritative deactivation before success, and separate active and archived catalogue views.

**Architecture:** A focused `workflowArchival.ts` module owns deployment-status normalization, archive eligibility, and bounded read-model observation. `WorkflowsPage.tsx` maps catalogue rows to those facts, submits the existing deployment deactivation command through `servicesApi`, and keeps command acceptance distinct from observed archival. No backend contract or client-side archived store is added.

**Tech Stack:** React, TypeScript, Ant Design, React Query, Jest, Testing Library, Biome.

---

### Task 1: Authoritative Archive Classification And Observation

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowArchival.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowArchival.test.ts`

- [ ] **Step 1: Write failing classification tests**

Create tests that use distinct identities and prove status normalization and
eligibility:

```ts
expect(isWorkflowArchived({ deploymentStatus: 'Deactivated' })).toBe(true);
expect(isWorkflowArchived({ deploymentStatus: 'deactivated' })).toBe(true);
expect(canArchiveWorkflow({
  activeRevisionId: 'rev-alpha',
  deploymentId: 'dep-alpha',
  deploymentStatus: 'Active',
  hasCommittedSource: true,
})).toBe(true);
expect(canArchiveWorkflow({
  activeRevisionId: '',
  deploymentId: '',
  deploymentStatus: '',
  hasCommittedSource: false,
})).toBe(false);
```

- [ ] **Step 2: Run classification tests and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/workflowArchival.test.ts
```

Expected: FAIL because `workflowArchival.ts` does not exist.

- [ ] **Step 3: Implement minimal classification helpers**

Define a narrow shared shape and normalize enum spelling without guessing any
other lifecycle:

```ts
export type WorkflowArchivalFacts = {
  readonly activeRevisionId: string;
  readonly deploymentId: string;
  readonly deploymentStatus: string;
  readonly hasCommittedSource: boolean;
};

function normalizeDeploymentStatus(value: string): string {
  return value.trim().toLowerCase().replace(/[\s_-]+/g, '');
}

export function isWorkflowArchived(
  workflow: Pick<WorkflowArchivalFacts, 'deploymentStatus'>,
): boolean {
  return normalizeDeploymentStatus(workflow.deploymentStatus) === 'deactivated';
}

export function canArchiveWorkflow(workflow: WorkflowArchivalFacts): boolean {
  return (
    workflow.hasCommittedSource &&
    Boolean(workflow.activeRevisionId.trim()) &&
    Boolean(workflow.deploymentId.trim()) &&
    normalizeDeploymentStatus(workflow.deploymentStatus) === 'active'
  );
}
```

- [ ] **Step 4: Run classification tests and verify GREEN**

Run the Task 1 Step 2 command. Expected: all classification tests PASS.

- [ ] **Step 5: Write failing bounded-observation tests**

Add tests for delayed-to-observed state, exact Workflow identity, read failure,
and exhausted observation:

```ts
const readWorkflows = jest
  .fn()
  .mockResolvedValueOnce([{ workflowId: 'wf-alpha', deploymentStatus: 'Active' }])
  .mockResolvedValueOnce([{ workflowId: 'wf-alpha', deploymentStatus: 'Deactivated' }]);

await expect(observeWorkflowArchival({
  delaysMs: [0, 1],
  readWorkflows,
  wait: jest.fn(async () => undefined),
  workflowId: 'wf-alpha',
})).resolves.toEqual({ kind: 'observed', workflows: expect.any(Array) });
```

Also assert a list containing only `wf-beta` never satisfies `wf-alpha` and
returns `{ kind: 'delayed' }`.

- [ ] **Step 6: Run observation tests and verify RED**

Run the Task 1 Step 2 command. Expected: FAIL because
`observeWorkflowArchival` is not exported.

- [ ] **Step 7: Implement the bounded observer**

Use the existing publication/materialization convention with injectable timing:

```ts
export const WORKFLOW_ARCHIVAL_OBSERVATION_DELAYS_MS = [
  0, 300, 700, 1200, 2000,
] as const;

export async function observeWorkflowArchival(input: {
  readonly delaysMs?: readonly number[];
  readonly readWorkflows: () => Promise<readonly ScopeWorkflowSummary[]>;
  readonly wait?: (delayMs: number) => Promise<void>;
  readonly workflowId: string;
}): Promise<
  | { readonly kind: 'observed'; readonly workflows: readonly ScopeWorkflowSummary[] }
  | { readonly kind: 'delayed' }
> {
  const delays = input.delaysMs ?? WORKFLOW_ARCHIVAL_OBSERVATION_DELAYS_MS;
  const wait = input.wait ?? defaultWait;
  for (const delayMs of delays) {
    if (delayMs > 0) await wait(delayMs);
    const workflows = await input.readWorkflows();
    const target = workflows.find((item) => item.workflowId === input.workflowId);
    if (target && isWorkflowArchived(target)) return { kind: 'observed', workflows };
  }
  return { kind: 'delayed' };
}
```

- [ ] **Step 8: Run helper tests and commit Task 1**

Run the Task 1 Step 2 command. Expected: all helper tests PASS.

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowArchival.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowArchival.test.ts
git commit -m "Model workflow archival observation"
```

### Task 2: Catalogue Archive Interaction And Views

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Add failing catalogue tests for action eligibility**

Mock `servicesApi.deactivateDeployment`, render an active published row, a
draft-only row, and a deactivated row, then assert:

```ts
fireEvent.click(within(activeRow).getByRole('button', {
  name: 'More actions for Active workflow',
}));
expect(await screen.findByRole('menuitem', { name: 'Archive' })).toBeVisible();

fireEvent.click(within(draftRow).getByRole('button', {
  name: 'More actions for Draft workflow',
}));
expect(screen.queryByRole('menuitem', { name: 'Archive' })).not.toBeInTheDocument();
```

Select `Archived`, assert the deactivated row has status `Archived`, and assert
it has no Archive menu action.

- [ ] **Step 2: Run the catalogue test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: FAIL because the selector and menu do not expose Archived/Archive.

- [ ] **Step 3: Add deployment facts and view filtering**

Extend `WorkflowRow` with `deploymentId` and `deploymentStatus`; preserve the
committed values when a draft overlays a committed row. Replace the view type
with:

```ts
type WorkflowView = 'active' | 'drafts' | 'archived';
```

Filter rows by `isWorkflowArchived(row)`:

```ts
if (view === 'drafts') return draftWorkflowIds.has(item.workflowId);
if (view === 'archived') return isWorkflowArchived(item);
return !isWorkflowArchived(item);
```

Omit `view=active` from the URL, preserve `drafts` and `archived`, and label the
default option `Active workflows`.

- [ ] **Step 4: Render Archive status and eligible menu action**

Calculate archived status before Published/Draft. Add an `InboxOutlined`
Archive menu item only when `canArchiveWorkflow(row)` is true. Selecting it must
set `archiveTarget` and reset archive failure/submission state; it must not call
the API until modal confirmation.

- [ ] **Step 5: Run the catalogue test and verify the eligibility slice GREEN**

Run the Task 2 Step 2 command. Expected: action and filter assertions PASS;
interaction assertions added next remain absent.

- [ ] **Step 6: Add failing accepted-versus-observed interaction tests**

Make list responses transition from Active to Deactivated and assert the exact
identity call:

```ts
expect(mockServicesApi.deactivateDeployment).toHaveBeenCalledWith(
  'wf-alpha',
  'dep-alpha',
  { tenantId: 'scope-alpha', appId: 'default', namespace: 'default' },
);
```

Hold the observation read unresolved and assert no `Workflow archived` toast
appears from the accepted receipt. Resolve Deactivated, then assert the dialog
closes, the toast appears, and the row moves to the Archived view.

Add a delayed case in which `Check again` invokes only
`observeWorkflowArchival`/list reads and
`deactivateDeployment` remains called exactly once.

- [ ] **Step 7: Run interaction tests and verify RED**

Run the Task 2 Step 2 command. Expected: FAIL because the Archive dialog and
submission state do not exist.

- [ ] **Step 8: Implement confirmation, command submission, and observation**

Add `archiveTarget`, `archiving`, `archiveSubmitted`, and `archivePhase` state.
The confirm handler must:

```ts
if (!archiveSubmitted) {
  await servicesApi.deactivateDeployment(
    archiveTarget.workflowId,
    archiveTarget.deploymentId,
    {
      tenantId: scopeId,
      appId: scopeServiceAppId,
      namespace: scopeServiceNamespace,
    },
  );
  setArchiveSubmitted(true);
}

const result = await observeWorkflowArchival({
  workflowId: archiveTarget.workflowId,
  readWorkflows: () => scopesApi.listWorkflows(scopeId),
});
```

On `observed`, refresh the committed query, close/reset the modal, and show
`Workflow archived`. On `delayed`, retain submitted state and show the delayed
message with `Check again`. On request/read error, distinguish whether command
acceptance already occurred so a retry never duplicates the command.

- [ ] **Step 9: Add complete English and Chinese messages**

Add typed locale entries for Active/Archived views, Archived status, Archive
menu label, confirmation title/body, failure, delayed observation, retry/check,
and success. Chinese copy must preserve the same operational meaning rather
than shortening Archive to Delete.

- [ ] **Step 10: Run component and helper tests and commit Task 2**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/workflowArchival.test.ts src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: both suites PASS.

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts
git commit -m "Restore workflow archive action"
```

### Task 3: Focused Validation And Delivery

**Files:**
- Modify only if verification finds a task-specific defect: files listed in Tasks 1-2
- Verify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-06-workflow-archive-design.md`
- Verify: `apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-workflow-archive.md`

- [ ] **Step 1: Run the frontend change-scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Use only the reported dependency-related Jest targets and explicit changed test
files. Do not run the full frontend suite, package-wide typecheck, or build.

- [ ] **Step 2: Run focused tests**

At minimum run the two explicit changed suites from Task 2 Step 10. If the
analyzer reports additional direct dependencies, run those exact files only.

- [ ] **Step 3: Run changed-file static checks and guards**

```bash
pnpm --dir apps/aevatar-console-web exec biome check \
  src/pages/workflow-activity-vnext/workflows/workflowArchival.ts \
  src/pages/workflow-activity-vnext/workflows/workflowArchival.test.ts \
  src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/locales/workflowActivityVNextMessages.en-US.ts \
  src/locales/workflowActivityVNextMessages.zh-CN.ts
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
```

Expected: all commands exit 0. Full frontend suite/typecheck/build remains
delegated to GitHub CI by the personal workflow policy.

- [ ] **Step 4: Verify the browser surface without mutating real data**

Start or reuse the feature-branch frontend with the configured remote backend.
Use an authenticated session to verify menu/dialog copy and desktop/mobile
layout. Do not confirm a real Archive command. Use automated component tests as
the authoritative proof for post-confirmation Active-to-Archived transitions.

- [ ] **Step 5: Review and commit remaining plan/verification changes**

```bash
git status --short
git diff --stat origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git add apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-workflow-archive.md
git commit -m "Document workflow archive implementation"
```

Skip the final commit if the plan was already included in an earlier focused
commit and the worktree is clean.

- [ ] **Step 6: Push and create the pull request**

Push `fix/2026-08-06_workflow-archive` and create a ready pull request targeting
`feat/2026-08-04_workflow-activity-vnext`. The PR body must include problem and
solution, affected paths, the exact focused verification commands/results, and:

```markdown
- Full frontend suite/typecheck/build: deferred to GitHub CI by personal local workflow policy
```

Stop after reporting the PR URL; do not babysit CI unless requested.
