# Schedule History Action Column Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Separate Schedule History navigation from Completed time by rendering a dedicated Action column in the application and deterministic Schedule design baseline.

**Architecture:** Keep the existing Schedule detail data and destination-building logic unchanged. Migrate only presentation ownership: the fourth cell always renders the completed timestamp, while a fifth cell conditionally renders the existing native arrow link. Lock the contract in the normative Schedule History spec, component tests, localized headers, CSS proportions, and generated Schedule artifacts.

**Tech Stack:** React, TypeScript, Ant Design icons, Jest and Testing Library, CSS-in-TypeScript, Umi locale catalogues, deterministic Python Excalidraw/PNG generators.

---

### Task 1: Prove The Four-Column Coupling

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Replace the four-column width assertions with the approved five-column contract**

Assert that the generated CSS contains widths `27 / 14 / 19 / 29 / 11` for columns one through five.

```tsx
expect(workflowActivityVNextCss).toContain(
  '.wa-vnext__schedule-history-table th:nth-child(5), .wa-vnext__schedule-history-table td:nth-child(5) { width: 11%; }',
);
```

- [ ] **Step 2: Assert semantic separation in the rendered History table**

Extend the existing History integration case with an `Action` column header and verify that the completed timestamp and native Run link occupy different cells.

```tsx
expect(screen.getByRole('columnheader', { name: 'Action' })).toBeVisible();
const runLink = screen.getByRole('link', { name: /Open Run from/ });
expect(runLink.closest('td')?.cellIndex).toBe(4);
const completedTime = document.querySelector(
  'time[datetime="2026-08-19T03:01:00Z"]',
);
expect(completedTime?.closest('td')?.cellIndex).toBe(3);
expect(completedTime?.closest('td')).not.toContainElement(runLink);
```

Also assert that the failed pre-Run row still has five cells and its Action cell contains no link.

- [ ] **Step 3: Run the exact changed test and confirm RED**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx
```

Expected: FAIL because `Action` is absent, the link still occupies cell index 3, and CSS still defines four columns.

### Task 2: Render The Independent Action Column

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Add synchronized Action header copy**

```ts
'workflowActivityVNext.schedule.action': 'Action',
```

```ts
'workflowActivityVNext.schedule.action': '操作',
```

- [ ] **Step 2: Split the fourth body cell and add the fifth header/cell**

The completed cell always owns only its timestamp:

```tsx
<td>
  <time dateTime={fire.completedAt}>
    {formatScheduleDate(
      fire.completedAt,
      scheduleDetail.data.schedule.timezone,
    )}
  </time>
</td>
```

The fifth cell owns only the command:

```tsx
<td className="wa-vnext__schedule-history-action">
  {attemptHref && attemptLabel ? (
    <a
      aria-label={attemptLabel}
      className="wa-vnext__schedule-history-attempt-link"
      href={attemptHref}
      rel="noopener noreferrer"
      target="_blank"
    >
      <ArrowRightOutlined aria-hidden="true" />
    </a>
  ) : null}
</td>
```

Do not change `runHref`, the valid filtered-Activity fallback, route queries, new-tab behavior, or accessible destination labels.

- [ ] **Step 3: Rebalance CSS and give the icon a stable action target**

Set the five column widths to `27 / 14 / 19 / 29 / 11`. Keep Completed time on one line. Center the Action header/cell and render the link as a stable inline-flex icon target with vNext blue focus/hover treatment.

- [ ] **Step 4: Run the exact changed test and confirm GREEN**

```bash
pnpm exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx
```

Expected: PASS with five semantic columns, valid links in cell index 4, and failed rows without an Action link.

### Task 3: Align The Deterministic Schedule Baseline

**Files:**
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py`
- Regenerate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.excalidraw`
- Regenerate: all seven `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-*.png` scene files
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`

- [ ] **Step 1: Change the History frame to five columns**

Use 492px-wide columns matching the approved proportions:

```python
widths = [133, 69, 93, 143, 54]
labels = ("SCHEDULED", "SOURCE", "RESULT", "COMPLETED", "ACTION")
```

Render `Run started` for error-free attempts and a blue trailing arrow in the Action column only for those rows. Keep the failed row Action empty.

- [ ] **Step 2: Regenerate the Schedule Excalidraw and all source-linked PNGs**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py
/Users/abigaildeng/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/bin/python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/render-schedule-png.py
```

- [ ] **Step 3: Update verifier and README hashes from the regenerated files**

Update the Schedule design SHA-256 and all seven PNG SHA-256 values. Add a verifier requirement for `ACTION` so a four-column design cannot return silently.

- [ ] **Step 4: Verify the baseline and inspect the History PNG**

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
bash tools/docs/lint.sh
```

Expected: 17/17 main frames, 7/7 Schedule frames, 7 standalone nonblank Schedule PNGs, byte-identical generator output, and docs lint with zero errors.

### Task 4: Focused Verification And Delivery

**Files:**
- Review all files changed by Tasks 1-3.

- [ ] **Step 1: Analyze the frontend dependency scope**

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base HEAD
```

- [ ] **Step 2: Run related Jest and every changed test explicitly**

Run Jest only for source files reported by the analyzer and rerun `WorkflowScheduleSurface.test.tsx` directly. Do not run a bare package test command.

- [ ] **Step 3: Run changed-file static checks and applicable guards**

Run Biome only on the analyzer's `staticCheckFiles`, then run:

```bash
bash tools/ci/test_stability_guards.sh
git diff --check
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:5175/
```

Do not run a local full frontend typecheck, test suite, or production build. GitHub CI owns full validation.

- [ ] **Step 4: Reuse the user's existing Chrome for visual verification**

Claim the existing `127.0.0.1:5175` tab if the Chrome connection exposes it. Verify the Action header, independent completed-time cell, action-cell alignment, and multiple rows. Do not open a new window, duplicate tab, separate browser, or the in-app browser.

- [ ] **Step 5: Commit, push, and update PR #3498**

Stage only the current task's implementation, test, locale, CSS, plan, and design-baseline files. Commit with an imperative message, push `feat/2026-08-20_workflow-schedule-frontend`, and append the exact verification evidence to PR #3498 while preserving Draft, `[DO NOT MERGE]`, and base `feat/2026-08-04_workflow-activity-vnext`.
