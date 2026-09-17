# Workflow List Metadata Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Workflow catalogue scan-friendly by separating lifecycle status and last update, moving descriptions into an accessible fixed-width popover, and removing technical scope and revision details from the primary list.

**Architecture:** Keep the existing draft/committed merge and query behavior intact. Change only the `WorkflowRow` presentation mapping and table rendering in `WorkflowsPage.tsx`, add narrow table/popover styles in the existing vNext stylesheet, and add the new column and status locale strings to the existing locale dictionaries.

**Tech Stack:** React 19, TypeScript, Ant Design 6 `Popover`, Jest, Testing Library, Biome.

---

## File Map

- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`: user-observable regression coverage for list hierarchy, hidden technical values, and popover behavior.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`: merged row model and four-column table rendering.
- `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`: stable table column sizing, name trigger focus treatment, and fixed-width description content.
- `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`: English column and Published labels.
- `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`: Chinese column and Published labels.

### Task 1: Lock The Correct Product Semantics With A Failing Test

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Replace the old mixed-metadata assertion with the desired list behavior**

Use distinct workflow, revision, service, scope, and ownership values. Assert the four column headers and row cell labels, then assert that ownership, scope, revision ID, and Created are absent. The key expectations are:

```tsx
expect(within(workflowTable).getAllByRole('columnheader')).toHaveLength(4);
expect(within(workflowTable).getByRole('columnheader', { name: 'Status' })).toBeVisible();
expect(
  within(workflowTable).getByRole('columnheader', { name: 'Last updated' }),
).toBeVisible();
expect(within(apacRow).getByText('Published')).toBeVisible();
expect(within(apacRow).queryByText('rev-support-apac-7')).not.toBeInTheDocument();
expect(within(workflowTable).queryByText('scope-alpha')).not.toBeInTheDocument();
expect(within(workflowTable).queryByText('APAC operations')).not.toBeInTheDocument();
expect(within(workflowTable).queryByRole('columnheader', { name: 'Created' })).not.toBeInTheDocument();
```

- [ ] **Step 2: Assert description disclosure instead of default description rendering**

Give the workflow-name trigger an accessible label such as `Description for Support triage`. Assert the description is absent initially, then use both pointer and keyboard interactions to disclose it. Verify the rendered content owns the fixed-width class:

```tsx
const descriptionTrigger = within(emeaRow).getByRole('button', {
  name: 'Description for Support triage',
});
expect(screen.queryByText(emeaDescription)).not.toBeInTheDocument();
fireEvent.mouseEnter(descriptionTrigger);
const description = await screen.findByText(emeaDescription);
expect(description).toHaveClass('wa-vnext__workflow-description');
fireEvent.mouseLeave(descriptionTrigger);
await waitFor(() => expect(screen.queryByText(emeaDescription)).not.toBeInTheDocument());
fireEvent.focus(descriptionTrigger);
expect(await screen.findByText(emeaDescription)).toBeVisible();
```

Add a committed-only workflow with no description and assert its name is not a description button.

- [ ] **Step 3: Run the focused regression test and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx --selectProjects jsdom --runInBand -t "separates workflow status"
```

Expected: FAIL because the current table has two headers, permanently renders descriptions, includes ownership and revision values, and has no Status or Last updated columns.

### Task 2: Implement The Four-Column Catalogue

**Files:**

- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Remove ownership from the row presentation model**

Delete `ownershipLabel` from `WorkflowRow` and from both row mappers. Keep `description`, `activeRevisionId`, and `updatedAtUtc` because they still drive search, popover content, status, and sorting.

- [ ] **Step 2: Add the accessible description trigger**

Import `Popover` and render it only for a trimmed non-empty description:

```tsx
const workflowName = <span className="wa-vnext__title">{row.name}</span>;

{row.description.trim() ? (
  <Popover
    content={
      <p className="wa-vnext__workflow-description">
        {row.description}
      </p>
    }
    placement="bottomLeft"
    trigger={['hover', 'focus']}
  >
    <button
      aria-label={t(
        'workflowActivityVNext.workflows.descriptionAria',
        'Description for {name}',
        { name: row.name },
      )}
      className="wa-vnext__workflow-name-trigger"
      type="button"
    >
      {workflowName}
    </button>
  </Popover>
) : (
  workflowName
)}
```

The trigger is contextual and must not navigate or replace the explicit Open action.

- [ ] **Step 3: Split status and update time into columns**

Add `Status` and `Last updated` headers between Workflow and Actions. Render corresponding cells with matching `data-label` values. Status uses only `Published` or `Draft`; update time uses `formatDate(row.updatedAtUtc)` without an `Updated` prefix.

- [ ] **Step 4: Add stable, viewport-safe styling**

Define table width classes so the Workflow column owns remaining space and Status, Last updated, and Actions have stable widths. Add a native-looking name button reset with a visible focus outline. The popover description content must use:

```css
.wa-vnext__workflow-description {
  margin: 0;
  max-width: calc(100vw - 48px);
  overflow-wrap: anywhere;
  white-space: normal;
  width: 320px;
}
```

- [ ] **Step 5: Add locale keys and remove stale primary-list copy**

Add English and Chinese values for `columnStatus`, `columnUpdated`, `publishedStatus`, and `descriptionAria`. Remove `publishedRevision`, `updatedContext`, and `workspaceOwner` only if no remaining references exist in the vNext area.

- [ ] **Step 6: Run the focused test and confirm GREEN**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx --selectProjects jsdom --runInBand -t "separates workflow status"
```

Expected: PASS for the new regression test.

### Task 3: Focused Regression And Static Verification

**Files:**

- Verify all files listed in the File Map.

- [ ] **Step 1: Run the frontend change scope analyzer**

Run from the worktree root:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Record `relatedTests` and `staticCheckFiles`. Do not replace missing affected checks with a full suite.

- [ ] **Step 2: Run the complete related page test file**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest src/pages/workflow-activity-vnext/index.test.tsx --selectProjects jsdom --runInBand
```

Expected: the Workflow Activity vNext test file passes with zero failures.

- [ ] **Step 3: Run changed-file static checks only**

Pass exactly the analyzer-reported frontend source, test, style, and locale files to Biome. Do not run package-wide lint, full typecheck, or production build. If no repository-native affected typecheck exists, record that GitHub CI owns type verification.

- [ ] **Step 4: Review semantics and diff integrity**

Run:

```bash
rg -n "publishedRevision|Published \{revision\}|updatedContext|ownershipLabel" apps/aevatar-console-web/src/pages/workflow-activity-vnext apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.*.ts
git diff --check
git diff --stat origin/feat/2026-08-04_workflow-activity-vnext...HEAD
git status --short
```

Expected: no stale primary-list references, no whitespace errors, and only task-owned files changed.

- [ ] **Step 5: Commit the implementation**

Stage only the implementation, test, locale, style, and plan files, then commit:

```bash
git commit -m "Refine workflow list metadata hierarchy"
```

### Task 4: Deliver The Pull Request

- [ ] **Step 1: Push the branch**

```bash
git push -u origin fix/2026-08-06_workflow-list-metadata
```

- [ ] **Step 2: Create a ready pull request**

Create a PR targeting `feat/2026-08-04_workflow-activity-vnext`. Include the problem and solution, affected Workflow catalogue path, exact focused commands and results, and this statement:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy.
```

Stop after reporting the ready PR URL. Do not wait for CI unless the user asks.
