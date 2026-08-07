# Single Workflow Editor Title Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the workflow editor's duplicated heading and name field with one inline-editable heading.

**Architecture:** Keep the shell's string `title` as the navigation and fallback label, and add an optional `heading` React node for page-specific interactive headings. The workflow editor supplies its existing controlled name input through that slot and removes the duplicate toolbar field; all title state and validation focus continue through the existing editor hook and ref.

**Tech Stack:** React 19, TypeScript, Ant Design 6, Jest, Testing Library

---

### Task 1: Lock the single-title behavior

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Write the failing regression test**

Strengthen the existing `keeps the editor header focused on the workflow name`
test:

```tsx
it('keeps the editor header focused on one inline workflow name', async () => {
  renderWithQueryClient(<WorkflowActivityVNextPage />);

  await screen.findByDisplayValue('Committed source');
  const workflowNameEditors = screen.getAllByRole('textbox', {
    name: 'Workflow name',
  });

  expect(workflowNameEditors).toHaveLength(1);
  expect(workflowNameEditors[0].closest('h1')).not.toBeNull();
  expect(
    screen.queryByText('Build, test, and refine this workflow.'),
  ).not.toBeInTheDocument();
});
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/pages/workflow-activity-vnext/index.test.tsx \
  -t "keeps the editor header focused on one inline workflow name"
```

Expected: FAIL because the current name input is in the secondary toolbar, outside the page `h1`.

### Task 2: Move editing into the heading

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`

- [ ] **Step 1: Add a custom heading slot to the shell**

Add `readonly heading?: React.ReactNode` to `ShellProps`, destructure it, and render:

```tsx
<h1>{heading ?? title}</h1>
```

- [ ] **Step 2: Supply the controlled title input through the heading slot**

Pass this prop from `WorkflowEditorPage`:

```tsx
heading={
  <Input
    aria-label={t('workflowActivityVNext.new.name', 'Workflow name')}
    className="wa-vnext__editor-name"
    disabled={editorWriteLocked}
    onChange={(event) => editor.updateTitle(event.target.value)}
    ref={workflowNameRef}
    value={editor.workflowTitle}
    variant="borderless"
  />
}
```

Delete the identical `Input` from `wa-vnext__editor-toolbar`. Keep save status and the Canvas/YAML segmented control in that toolbar.

- [ ] **Step 3: Style the heading as an inline editor**

Replace toolbar-oriented name sizing with heading-specific rules:

```css
.wa-vnext__heading-copy--custom { flex: 1 1 auto; max-width: min(560px, 100%); width: 100%; }
.wa-vnext__heading-copy--custom h1 { min-width: 0; width: 100%; }
.wa-vnext__editor-name.ant-input {
  color: var(--wa-ink);
  font-size: 28px;
  font-weight: 700;
  height: 36px;
  line-height: 28px;
  max-width: 100%;
  padding: 2px 4px;
}
.wa-vnext__editor-toolbar { justify-content: flex-end; }
```

At the mobile breakpoint, set the title input to `22px` and keep the custom heading container at full available width so long underscore-separated names remain visible. Remove obsolete mobile toolbar rules that treated the name input as a separate row.

- [ ] **Step 4: Run the focused test and verify GREEN**

Run the same command from Task 1. Expected: PASS with one textbox nested inside `h1`.

### Task 3: Focused verification and delivery

**Files:**
- Verify: all files changed in Tasks 1 and 2

- [ ] **Step 1: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo .
```

- [ ] **Step 2: Run dependency-related tests and changed-file static checks**

Run the workflow activity test file plus the analyzer-reported changed-file checks. Do not run the full frontend suite, package typecheck, or production build.

- [ ] **Step 3: Run the required test stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

- [ ] **Step 4: Browser-check the exact editor route**

Use the already running preview on port `5173`. Verify the intended workflow editor route renders, contains one inline title editor, and has no overlap at desktop and mobile viewports. If authentication or backend state blocks the route, stop the preview handoff and report the block rather than claiming visual verification.

- [ ] **Step 5: Review and deliver**

Review the complete diff, stage only the plan, test, shell, editor, and styles files, commit with `Use one workflow editor title`, push `fix/2026-08-06_one-click-workflow-publish`, and update PR `#3276` with exact focused commands and the statement that full frontend verification is delegated to GitHub CI.
