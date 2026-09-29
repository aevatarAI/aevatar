# Workflow Row Action Hierarchy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Workflow catalogue's mixed five-action presentation with one primary Open link, one neutral View activity link, and one stable overflow menu without changing any Workflow identity or mutation semantics.

**Architecture:** Keep the change local to the existing Workflow Activity vNext catalogue. Ant Design Button links provide real `href` values while an unmodified left click still enters the existing client-side history; the Dropdown owns all low-frequency row actions and continues to call the existing rename, copy, and delete-confirmation flows. Existing Aevatar interaction classes and vNext CSS define the stable action geometry and touch targets.

**Tech Stack:** React 19, TypeScript, Ant Design 6, Jest, Testing Library, Biome, Umi history and locale catalogues.

---

### Task 1: Prove The Persistent Action Hierarchy And Navigation Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`

- [ ] **Step 1: Write the failing route assertions**

Update the focused Workflow catalogue tests so a row exposes:

```tsx
const open = within(row).getByRole('link', {
  name: 'Open Support triage in Workflows',
});
const activity = within(row).getByRole('link', {
  name: 'View activity for Support triage in Workflows',
});

expect(open).toHaveAttribute(
  'href',
  '/scopes/scope-alpha/workflow-activity-vnext/workflows/wf-draft-alpha',
);
expect(activity).toHaveAttribute(
  'href',
  '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-draft-alpha',
);
expect(within(row).queryByRole('button', { name: /Delete/ })).not.toBeInTheDocument();
```

Also assert that duplicate-name rows have different Open, View activity, and More actions accessible names because each name includes `ownershipLabel`, and that a modified click is not intercepted by the component.

- [ ] **Step 2: Run the focused test to verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: FAIL because Open and Activity are buttons, their accessible names omit ownership, and the standalone Delete button is still rendered.

- [ ] **Step 3: Implement real primary and secondary links**

In `WorkflowsPage.tsx`:

```tsx
const editorHref = buildWorkflowActivityEditorHref(scopeId, row.workflowId);
const activityHref = `${buildWorkflowActivitySectionHref(
  scopeId,
  'activity',
)}?workflowId=${encodeURIComponent(row.workflowId)}`;

<Button
  aria-label={t('workflowActivityVNext.workflows.openAria', 'Open {name} in {owner}', {
    name: row.name,
    owner: row.ownershipLabel,
  })}
  className={AEVATAR_INTERACTIVE_BUTTON_CLASS}
  href={editorHref}
  icon={<EditOutlined />}
  onClick={(event) => handleClientLinkClick(event, editorHref)}
  type="primary"
>
  {t('workflowActivityVNext.common.open', 'Open')}
</Button>
```

Render View activity the same way with `HistoryOutlined`, the exact encoded `workflowId`, a neutral Button type, and a row-specific accessible name. `handleClientLinkClick` must prevent default only for an unmodified primary-button click before calling `history.push`.

- [ ] **Step 4: Run the focused test to verify GREEN**

Run the same focused Jest command. Expected: the persistent hierarchy, real href, exact Workflow identity, and modified-click assertions pass.

### Task 2: Move Destructive Management Into The Overflow Menu

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`

- [ ] **Step 1: Write the failing menu-order and confirmation assertions**

Open a draft row's More actions menu and assert this semantic order:

```tsx
expect(screen.getAllByRole('menuitem').map((item) => item.textContent)).toEqual([
  'Rename',
  'Copy workflow reference',
  'Delete draft',
]);
expect(screen.getByRole('separator')).toBeInTheDocument();
expect(screen.getByRole('menuitem', { name: 'Delete draft' })).toHaveClass(
  'ant-dropdown-menu-item-danger',
);
```

Click Delete draft and prove the existing confirmation opens while `deleteWorkflowDraft` remains uncalled. Update the existing deletion, retry, and refresh-failure tests to enter through More actions before confirming. Open a committed-only row menu and prove Copy workflow reference is its only menu item.

- [ ] **Step 2: Run the focused test to verify RED**

Run the focused Jest command from Task 1. Expected: FAIL because Delete draft is not in the Dropdown and the current draft menu has no divider or danger item.

- [ ] **Step 3: Implement the unified menu**

Remove the standalone Tooltip/Delete Button. Build the Dropdown items as Rename when draft-capable, Copy workflow reference for every row, then a divider and a danger Delete draft item when draft-capable:

```tsx
{ type: 'divider' },
{
  danger: true,
  icon: <DeleteOutlined />,
  key: 'delete',
  label: t('workflowActivityVNext.workflows.deleteDraft', 'Delete draft'),
}
```

Handle `delete` only by setting `deleteTarget` and resetting the existing retry state. Keep the modal and `confirmDelete` logic unchanged.

- [ ] **Step 4: Run the focused test to verify GREEN**

Run the focused Jest command. Expected: menu hierarchy, published-only stability, confirmation-only first click, delete success, retry, refresh failure, rename, and copy tests pass.

### Task 3: Align Styling, Locales, And The Product Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`

- [ ] **Step 1: Add the local action-layout contract**

Add narrowly scoped styles for an end-aligned, no-wrap action group with stable button geometry:

```css
.wa-vnext__workflow-actions-cell { text-align: right; }
.wa-vnext__workflow-actions { align-items: center; display: inline-flex; white-space: nowrap; }
.wa-vnext__workflow-actions .ant-btn { flex: 0 0 auto; }
@media (max-width: 767px) {
  .wa-vnext__workflow-actions .ant-btn { min-height: 44px; }
  .wa-vnext__workflow-actions .ant-btn-icon-only { min-width: 44px; width: 44px; }
}
```

Apply `AEVATAR_INTERACTIVE_BUTTON_CLASS` to all three persistent controls so global hover, active, focus, disabled, and loading states remain authoritative.

- [ ] **Step 2: Update localized visible and accessible copy**

Change `viewActivity` to `View activity` / `查看活动`. Update Open and More actions templates to include `{owner}`, and add `viewActivityAria` with `{name}` and `{owner}` in both locale catalogues. Preserve identical interpolation variables across locales.

- [ ] **Step 3: Update the vNext design specification**

Add one concise catalogue rule: exactly Open, View activity, and More actions remain persistent; Rename, Copy workflow reference, and Delete draft live in overflow; Delete is last, separated, dangerous, and confirmation-gated.

- [ ] **Step 4: Run scope analysis and focused validation**

Run:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
pnpm --dir apps/aevatar-console-web exec biome check <staticCheckFiles reported by the analyzer>
bash tools/docs/lint.sh
git diff --check origin/feat/2026-08-04_workflow-activity-vnext...HEAD
```

Expected: focused Jest passes; changed-file Biome reports no diagnostics; docs lint reports zero errors; diff check is clean. Do not run a full frontend test, lint, typecheck, or production build. Full verification belongs to GitHub CI.

- [ ] **Step 5: Commit and deliver the pull request**

Stage only the plan, specs, Workflows page, vNext styles, focused route test, and two locale files. Commit as `AbigailDeng`, push `fix/2026-08-06_workflow-row-action-hierarchy`, and create a ready PR targeting `feat/2026-08-04_workflow-activity-vnext`. The PR body must include problem and solution, affected paths, exact focused commands/results, the visual approval context, and the statement that full frontend suite/build is delegated to GitHub CI.
