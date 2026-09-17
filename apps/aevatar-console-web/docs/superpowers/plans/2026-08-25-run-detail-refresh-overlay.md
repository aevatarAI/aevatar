# Run Detail Refresh Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give manual Run Detail refresh a clear workspace-level loading state without hiding committed content or shifting the page shell.

**Architecture:** Keep the existing page-local grouped refresh command as the only owner of manual refresh state. When `refreshing` is true, mark the Run workspace busy, make its history/stage content inert, and render one absolute overlay with a compact status indicator; the header and global navigation remain outside that ownership boundary.

**Tech Stack:** React, TypeScript, Ant Design icons, TanStack React Query, Jest, Testing Library, vNext CSS tokens.

---

### Task 1: Lock The Workspace Refresh Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [ ] **Step 1: Extend the existing grouped-refresh test**

After clicking `Refresh`, assert one accessible status named `Refreshing run details…`, assert the `.wa-vnext-run-detail` workspace has `aria-busy="true"`, assert `.wa-vnext-run-detail__refresh-content` is inert, and keep the existing assertion that `Incident review` remains mounted.

```tsx
const refreshStatus = screen.getByRole('status', {
  name: 'Refreshing run details…',
});
expect(refreshStatus).toHaveClass('wa-vnext-run-detail__refresh-overlay');
expect(document.querySelector('.wa-vnext-run-detail')).toHaveAttribute(
  'aria-busy',
  'true',
);
expect(
  document.querySelector('.wa-vnext-run-detail__refresh-content'),
).toHaveAttribute('inert');
```

After resolving all requests, assert the status is gone, `aria-busy` is `false`, and `inert` is removed.

```tsx
expect(
  screen.queryByRole('status', { name: 'Refreshing run details…' }),
).not.toBeInTheDocument();
expect(document.querySelector('.wa-vnext-run-detail')).toHaveAttribute(
  'aria-busy',
  'false',
);
expect(
  document.querySelector('.wa-vnext-run-detail__refresh-content'),
).not.toHaveAttribute('inert');
```

- [ ] **Step 2: Run the exact test and verify RED**

```bash
cd apps/aevatar-console-web
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: the grouped-refresh test fails because the workspace status, busy attribute, and inert content boundary do not exist yet.

### Task 2: Implement The Non-Destructive Refresh Overlay

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Add the refresh overlay component**

Place the component next to `RunDetailLoadingWorkspace` and reuse the imported `LoadingOutlined` icon.

```tsx
function RunDetailRefreshOverlay() {
  const label = t(
    'workflowActivityVNext.run.refreshingDetail',
    'Refreshing run details…',
  );

  return (
    <div
      aria-label={label}
      aria-live="polite"
      className="wa-vnext-run-detail__refresh-overlay"
      role="status"
    >
      <span className="wa-vnext-run-detail__refresh-indicator">
        <LoadingOutlined aria-hidden="true" spin />
        <span>{label}</span>
      </span>
    </div>
  );
}
```

- [ ] **Step 2: Connect the loaded and fallback workspaces**

For both rendered `.wa-vnext-run-detail` roots, add `aria-busy={refreshing}`. Immediately inside each root, open the following boundary before the history rail and close it after the complete stage section:

```tsx
<div className="wa-vnext-run-detail__refresh-content" inert={refreshing}>
  {/* Keep the current history rail and complete stage section here. */}
</div>
{refreshing ? <RunDetailRefreshOverlay /> : null}
```

The parent opening tag becomes:

```tsx
<div
  aria-busy={refreshing}
  className="wa-vnext-run-detail wa-vnext-run-detail--bounded"
>
```

- [ ] **Step 3: Style the busy state with existing tokens**

Make the workspace a positioning context, preserve grid participation with `display: contents`, and cover the workspace with a restrained overlay. Do not blur or replace committed content.

```css
.wa-vnext-run-detail { position: relative; }
.wa-vnext-run-detail__refresh-content { display: contents; }
.wa-vnext-run-detail__refresh-overlay {
  align-items: center;
  background: color-mix(in srgb, var(--wa-surface) 64%, transparent);
  cursor: progress;
  display: flex;
  inset: 0;
  justify-content: center;
  position: absolute;
  z-index: 20;
}
.wa-vnext-run-detail__refresh-indicator {
  align-items: center;
  background: color-mix(in srgb, var(--wa-surface) 94%, transparent);
  border: 1px solid var(--wa-line);
  border-radius: 6px;
  color: var(--wa-ink);
  display: inline-flex;
  font-size: 12px;
  font-weight: 600;
  gap: 8px;
  line-height: 17px;
  padding: 9px 12px;
}
.wa-vnext-run-detail__refresh-indicator > .anticon {
  color: var(--wa-blue);
  font-size: 15px;
}
```

- [ ] **Step 4: Add localized status copy**

Add these exact catalog entries in the existing `workflowActivityVNext.run.*` groups.

```ts
'workflowActivityVNext.run.refreshingDetail': 'Refreshing run details…',
```

```ts
'workflowActivityVNext.run.refreshingDetail': '正在刷新运行详情…',
```

- [ ] **Step 5: Run the exact test and verify GREEN**

```bash
cd apps/aevatar-console-web
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: all Run Detail tests pass, including the workspace busy-state assertions.

### Task 3: Incremental Verification And PR Delivery

**Files:**
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Verify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Verify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Verify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Analyze changed frontend scope**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base HEAD
```

- [ ] **Step 2: Run dependency-related tests and exact static checks**

```bash
cd apps/aevatar-console-web
pnpm exec jest --findRelatedTests src/locales/workflowActivityVNextMessages.en-US.ts src/locales/workflowActivityVNextMessages.zh-CN.ts src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx src/pages/workflow-activity-vnext/styles.ts --runInBand
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
pnpm exec biome check src/locales/workflowActivityVNextMessages.en-US.ts src/locales/workflowActivityVNextMessages.zh-CN.ts src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx src/pages/workflow-activity-vnext/styles.ts
```

Expected: dependency-related and exact tests pass; Biome reports no diagnostics. Do not run a full frontend test suite, typecheck, or production build.

- [ ] **Step 3: Run applicable repository guards**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5175/
```

- [ ] **Step 4: Verify in the user's existing Chrome only**

Claim the existing `127.0.0.1:5175` Chrome tab. Confirm the overlay covers the Run workspace while the header remains stable, committed content stays visible, the compact indicator does not overlap other UI, and completion removes the overlay. If the Chrome bridge remains unavailable after its required retry, do not open a new browser/window/tab; record the limitation.

- [ ] **Step 5: Review, commit, push, and update PR #3498**

Stage only this plan, the Run Detail source/test, vNext styles, and English/Chinese vNext messages. Commit with `Show Run detail refresh overlay`, push `feat/2026-08-20_workflow-schedule-frontend`, and append exact verification evidence to PR #3498. Confirm the PR remains Draft with `[DO NOT MERGE]` and base `feat/2026-08-04_workflow-activity-vnext`.
