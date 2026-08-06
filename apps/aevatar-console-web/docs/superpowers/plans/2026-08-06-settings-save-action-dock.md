# Settings Save Action Dock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep dirty Workflow Activity vNext Settings save actions stationary at the bottom of the main work area without covering form content or changing save semantics.

**Architecture:** Add an optional footer slot to `WorkflowActivityVNextShell`. Only routes providing that slot split the main column into an independently scrolling body and a natural-height footer; `SettingsPage` provides the existing dirty actions through the slot and keeps all form/save state local.

**Tech Stack:** React 19, TypeScript, Umi Max, Ant Design, Jest, Testing Library, CSS template strings, Biome.

---

### Task 1: Protect The Shell-Owned Dirty Action Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [x] **Step 1: Add a focused route integration test**

Add a test in the existing `Workflow Activity vNext settings` describe block. Use distinct authoritative service data, edit Preferred service through the rendered select, and assert the accessible ownership contract:

```tsx
it('keeps dirty save actions outside the scrolling AI defaults panel', async () => {
  mockStudioApi.getUserLlmSettings.mockResolvedValue({
    savedSelection: null,
    savedRouteLabel: 'System default',
    selectionStatus: 'system_default',
    catalogDiagnostic: 'unspecified',
    remediation: 'none',
    catalogStatus: 'ready',
    capabilities: {
      canEditRoute: true,
      canEditModel: true,
      canSave: true,
      canRetryCatalog: true,
    },
    routeOptions: [
      {
        routeValue: '/api/v1/proxy/s/service-alpha',
        label: 'Service alpha',
        source: 'user_service',
        status: 'ready',
        allowed: true,
        ready: true,
        userServiceId: 'us-alpha',
        serviceSlug: 'service-alpha',
        modelCatalog: {
          certainty: 'enumerated',
          modelIds: ['model-alpha'],
          defaultModelId: 'model-alpha',
          diagnostic: 'unspecified',
        },
        description: null,
      },
    ],
    modelGroupsByRoute: [],
  });

  renderWithQueryClient(<WorkflowActivityVNextPage />);

  expect(
    screen.queryByRole('region', { name: 'Unsaved settings actions' }),
  ).not.toBeInTheDocument();
  fireEvent.mouseDown(
    await screen.findByRole('combobox', { name: 'Preferred service' }),
  );
  fireEvent.click(await screen.findByText('Service alpha'));

  const aiDefaultsPanel = screen.getByRole('region', {
    name: 'AI defaults',
  });
  const saveActions = screen.getByRole('region', {
    name: 'Unsaved settings actions',
  });
  expect(aiDefaultsPanel).not.toContainElement(saveActions);
  expect(
    within(saveActions).getByRole('button', { name: 'Save changes' }),
  ).toBeEnabled();

  fireEvent.click(
    within(saveActions).getByRole('button', {
      name: 'Restore saved settings',
    }),
  );
  expect(
    screen.queryByRole('region', { name: 'Unsaved settings actions' }),
  ).not.toBeInTheDocument();
  expect(mockStudioApi.saveUserLlmSettings).not.toHaveBeenCalled();
});
```

Reuse the file's existing `within` import from Testing Library.

- [x] **Step 2: Run the exact test and verify RED**

Run from the repository root:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx \
  --testNamePattern 'keeps dirty save actions outside the scrolling AI defaults panel'
```

Expected: FAIL because no region named `Unsaved settings actions` exists and the current action bar remains inside the AI defaults form.

### Task 2: Add The Optional Shell Footer And Move The Actions

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/WorkflowActivityVNextShell.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/settings/SettingsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [x] **Step 1: Add a route-neutral shell footer slot**

Extend `ShellProps` with:

```tsx
readonly footer?: React.ReactNode;
```

Build the existing header and content as `mainBody`. When `footer` is absent,
render the current main DOM and scrolling unchanged. When present, render:

```tsx
<main
  className={`wa-vnext__main${footer ? ' wa-vnext__main--with-footer' : ''}`}
>
  {footer ? (
    <div className="wa-vnext__main-scroll">{mainBody}</div>
  ) : (
    mainBody
  )}
  {footer ? <div className="wa-vnext__main-footer">{footer}</div> : null}
</main>
```

Do not add the wrapper on routes without a footer.

- [x] **Step 2: Move the action surface out of `aiPanel`**

Delete the dirty action JSX from the `wa-vnext__form`. Create one route-owned
action region using the same handlers and state:

```tsx
const settingsFooter = (
  <>
    {dirty ? (
      <div className="wa-vnext__settings-footer">
        <section
          aria-label={t(
            'workflowActivityVNext.settings.unsavedActionsAria',
            'Unsaved settings actions',
          )}
          className="wa-vnext__settings-savebar"
        >
          {/* Existing status copy and Restore / Save controls. */}
        </section>
      </div>
    ) : null}
  </>
);
```

Pass `footer={settingsFooter}` to `WorkflowActivityVNextShell`. The fragment is
always present so toggling dirty state does not replace the shell's scroll
subtree. Keep save, discard, accepted-to-observed, error, delayed, and
navigation logic unchanged.

- [x] **Step 3: Add synchronized locale messages**

Add to the English catalogue:

```ts
'workflowActivityVNext.settings.unsavedActionsAria':
  'Unsaved settings actions',
```

Add to the Chinese catalogue:

```ts
'workflowActivityVNext.settings.unsavedActionsAria': '未保存设置操作',
```

- [x] **Step 4: Implement stable scroll ownership and responsive sizing**

Add the shell contract:

```css
.wa-vnext__main--with-footer {
  display: grid;
  grid-template-rows: minmax(0, 1fr) auto;
  overflow: hidden;
}
.wa-vnext__main-scroll {
  min-height: 0;
  min-width: 0;
  overflow-x: hidden;
  overflow-y: auto;
  overscroll-behavior: contain;
}
.wa-vnext__main-footer { min-width: 0; }
.wa-vnext__settings-footer {
  background: var(--wa-surface);
  padding: 0 40px max(12px, env(safe-area-inset-bottom));
}
```

Change the save bar to normal layout inside the shell footer: remove
`position`, `bottom`, and `margin-top`; add `margin: 0 auto` and
`max-width: 1120px`. Use 32 px footer gutters below 1100 px and 16 px below
768 px. Preserve the current stacked mobile copy/actions at 600 px, and at
360 px change the action grid to one column. The dock must never overlay the
scroll region.

- [x] **Step 5: Run the exact test and verify GREEN**

Run the Task 1 Jest command again.

Expected: PASS with one test and no warning or console error.

- [x] **Step 6: Run dependency-related route coverage**

Run the complete colocated route file because shell composition is shared by
all vNext child routes:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: PASS for every test in that file.

### Task 3: Align Normative Documentation And Verify The Change

**Files:**
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-06-settings-save-action-dock-design.md`
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-design.md`
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-04-workflow-activity-vnext-user-paths.md`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`
- Modify: `apps/aevatar-console-web/docs/superpowers/plans/2026-08-06-settings-save-action-dock.md`

- [x] **Step 1: Record the approved and implemented contract**

Mark the new design approved for implementation, then implemented after the
focused checks pass. Replace the two sticky references at lines 520 and 666 of
the original design, the dirty-step reference at line 674 of the user paths,
and the two prototype-description references at lines 146 and 254 of the
baseline README with `shell-fixed Restore and Save changes dock` language.
Keep the imported Excalidraw, generator, PNGs, and prototype byte-identical;
document the required production deviation instead of editing those source
assets.

- [x] **Step 2: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

Read the JSON. Run every changed test explicitly and dependency-related Jest
tests for changed source files. Pass only reported `staticCheckFiles` to Biome.
Do not run a full local frontend test, lint, typecheck, or production build.

- [x] **Step 3: Run documentation and repository hygiene checks**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
```

Expected: baseline SHA matches, 17/17 frames pass, docs lint passes, test
stability guard passes, and no whitespace error is reported.

- [x] **Step 4: Verify real layout behavior in the existing browser**

Start the worktree frontend on a free non-5000/non-5050 port with matching
local OAuth origin configuration and no mock data. At 1440 x 900, 834 x 1112,
and 390 x 844, verify:

- the dirty dock stays stationary while Settings content scrolls;
- the last field and durable alerts can scroll fully above the dock;
- the dock never enters the navigation rail;
- actions remain reachable and do not overlap;
- mobile has no page-level horizontal overflow and includes safe-area spacing.

Capture screenshots as task evidence. If real authenticated backend state is
unavailable, report that exact residual gap and do not inject production mock
data.

Result: the real application ran on port 5187 with `MOCK=none`, but that origin
had no authenticated session and the registered NyxID callback port 5173 was
owned by another process. No session or Settings data was mocked. Authenticated
responsive screenshots remain a preview-environment verification item.

### Task 4: Review, Commit, Deliver, And Reach Mergeable State

**Files:**
- Review every file changed from `origin/feat/2026-08-04_workflow-activity-vnext`

- [x] **Step 1: Review the complete diff**

Confirm that the diff is frontend-only, contains no imported baseline artifact
changes, preserves all save contracts, and includes no unrelated worktree
files. Review long-label behavior and all conditional shell paths.

- [ ] **Step 2: Commit only task files as AbigailDeng**

Stage explicit paths only. Commit with:

```bash
git -c user.name=AbigailDeng \
  -c user.email=108705114+AbigailDeng@users.noreply.github.com \
  commit -m "Fix Settings save action dock"
```

- [ ] **Step 3: Push and open the pull request**

Push `fix/2026-08-06_settings-save-action-dock` and create a PR with base
`feat/2026-08-04_workflow-activity-vnext`. Verify the PR author is
`AbigailDeng`. Include the required design-baseline declaration, affected
paths, exact focused command results, screenshots or the exact browser-QA gap,
and:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

- [ ] **Step 4: Make the PR mergeable**

Poll required checks, review comments, merge conflicts, and GitHub mergeability.
Fix branch-owned failures, push updates, and recheck until the PR reports no
conflict and all required checks pass. Never create or update the PR as
`Abigail940404`.
