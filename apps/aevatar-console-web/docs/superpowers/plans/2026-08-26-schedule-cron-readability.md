# Schedule Cron Readability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make expanded Schedule recurrence details understandable in product language while preserving the exact cron expression as secondary technical information.

**Architecture:** Keep `scheduleRecurrenceSummary` as the single frontend interpretation path and reuse it inside the existing Overview disclosure. Add only locale-backed presentation labels and small scoped styles, then update the deterministic Schedule design sources and regression verifier so raw cron cannot become the only local explanation again.

**Tech Stack:** React 19, TypeScript, Umi locale catalogs, Testing Library/Jest, CSS, deterministic HTML/Excalidraw/Pillow design generators, Python baseline verifier.

---

### Task 1: Protect the readable disclosure contract with TDD

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Extend the existing Overview behavior test before production code**

Add the interaction and scoped assertions to `presents Schedule Overview with primary actions and guarded lifecycle actions` immediately after the existing collapsed-state assertion:

```tsx
const advancedDetails = screen
  .getByText('Advanced details')
  .closest('details');
expect(advancedDetails).not.toBeNull();
if (!advancedDetails) {
  throw new Error('Expected Schedule advanced details disclosure');
}

fireEvent.click(within(advancedDetails).getByText('Advanced details'));

expect(within(advancedDetails).getByText('Runs')).toBeVisible();
expect(
  within(advancedDetails).getByText('Every weekday at 09:00'),
).toBeVisible();
expect(within(advancedDetails).getByText('Technical format')).toBeVisible();
expect(within(advancedDetails).getByText('0 9 * * 1-5')).toBeVisible();
```

- [ ] **Step 2: Run the named test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx \
  --testNamePattern 'presents Schedule Overview with primary actions and guarded lifecycle actions'
```

Expected: FAIL because the expanded disclosure does not render `Runs` or `Technical format`. A syntax, fixture, or selector error is not the required RED state and must be corrected before continuing.

### Task 2: Render product meaning before technical syntax

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`
- Test: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Replace the single raw-cron row with the readable-to-technical hierarchy**

Keep the existing `<details>` and `<dl>`. Render these two rows:

```tsx
<div>
  <dt>{t('workflowActivityVNext.schedule.runs', 'Runs')}</dt>
  <dd>
    {scheduleRecurrenceSummary(
      scheduleDetail.data.schedule.cronExpression,
    )}
  </dd>
</div>
<div>
  <dt>
    {t(
      'workflowActivityVNext.schedule.technicalFormat',
      'Technical format',
    )}
  </dt>
  <dd>
    <code>{scheduleDetail.data.schedule.cronExpression}</code>
  </dd>
</div>
```

Do not add another cron parser. Unsupported valid expressions continue to use the existing `Custom schedule` fallback and preserve the exact cron string in the second row.

- [ ] **Step 2: Add synchronized locale keys**

Add to the English catalog:

```ts
'workflowActivityVNext.schedule.runs': 'Runs',
'workflowActivityVNext.schedule.technicalFormat': 'Technical format',
```

Add to the Chinese catalog:

```ts
'workflowActivityVNext.schedule.runs': '运行周期',
'workflowActivityVNext.schedule.technicalFormat': '技术格式',
```

- [ ] **Step 3: Clarify row rhythm and secondary code treatment**

Keep the existing compact definition-list layout. Increase the list gap to `8px`, keep the labels subdued, and give only the `<code>` value a subtle background, small radius, and horizontal padding using existing `--wa-*` tokens. Do not introduce another card, divider panel, or tooltip-only explanation.

- [ ] **Step 4: Rerun the named test and verify GREEN**

Run the same named Jest command from Task 1.

Expected: PASS with `Runs`, `Every weekday at 09:00`, `Technical format`, and `0 9 * * 1-5` visible inside the expanded disclosure.

### Task 3: Keep design artifacts semantically aligned

**Files:**
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/prototype.html`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.excalidraw`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-workflows-list-modal.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-workflow-editor-panel.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-review.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-creation-pending.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-detail.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-history.png`
- Generate: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/schedule-edit.png`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py`
- Modify: `apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/README.md`

- [ ] **Step 1: Update the interactive prototype Overview disclosure**

In `scheduleOverviewMarkup`, replace the single `Cron expression` row with the same hierarchy, using the already-computed human summary:

```html
<details class="schedule-advanced">
  <summary>Advanced details</summary>
  <div><span>Runs</span><strong>${escapeHtml(editModel.humanSummary)}</strong></div>
  <div><span>Technical format</span><code>${escapeHtml(schedule.cronExpression)}</code></div>
</details>
```

Adjust only the existing scoped `.schedule-advanced` styles as needed for two readable rows.

- [ ] **Step 2: Update the deterministic Schedule Overview frame**

Change frame `05 · Workflow — schedule overview` so Advanced details is visibly expanded and contains these exact facts:

```text
Runs              Every weekday at 09:00
Technical format  0 9 * * 1-5
```

Keep the recurrence meaning visually stronger than the monospace technical format. Do not add cron-field instructions or duplicate timezone.

- [ ] **Step 3: Strengthen deterministic semantic verification**

Require the Overview frame and prototype source to contain all of:

```python
(
    "advanced details",
    "runs",
    "every weekday at 09:00",
    "technical format",
    "0 9 * * 1-5",
)
```

This must fail if raw cron again becomes the only explanation.

- [ ] **Step 4: Regenerate the Schedule board and standalone PNGs**

Run from the repository root:

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/aevatar-workflow-schedule-design.gen.py
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/render-schedule-png.py
```

Expected: the Excalidraw board and seven declared Schedule PNGs are generated deterministically.

- [ ] **Step 5: Update declared deterministic hashes**

Compute the regenerated board and PNG SHA-256 values, then update `EXPECTED_SCHEDULE_SHA256`, `SCHEDULE_PNG_SHA256`, and the README `Schedule design SHA-256` declaration to the exact generated values. The README and verifier must declare the same board hash.

- [ ] **Step 6: Verify the baseline twice**

Run twice from `apps/aevatar-console-web/`:

```bash
python3 docs/design-baselines/workflow-activity-vnext/verify-baseline.py
```

Expected: both runs pass, proving the generator output, declared hashes, frame inventory, PNG inventory, and readable Overview semantics are stable.

- [ ] **Step 7: Inspect the generated Schedule Overview PNG**

Open `schedule-detail.png` at original detail and confirm the two rows fit without overlap, truncation, or competing with the primary Overview hierarchy.

### Task 4: Run focused verification and deliver through the existing Draft PR

**Files:**
- Review every file changed in Tasks 1-3
- Never stage: `.planning/`

- [ ] **Step 1: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

Use its `staticCheckFiles` exactly; do not run a full frontend typecheck, test suite, lint, or build.

- [ ] **Step 2: Preflight related tests for only the behavior-owning component**

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --listTests \
  --findRelatedTests \
  src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx
```

Run the matching `--findRelatedTests --runInBand` command only if the complete list contains at most 10 files and stays within the Schedule/workflow-activity-vNext domain. Never include locale catalogs or shared styles in dependency discovery.

- [ ] **Step 3: Run the changed test file explicitly**

```bash
pnpm --dir apps/aevatar-console-web exec jest \
  --runInBand \
  --runTestsByPath src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx
```

Expected: PASS with zero test failures.

- [ ] **Step 4: Run changed-file static and repository checks**

Run Biome only against the analyzer's `staticCheckFiles`, followed by:

```bash
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
git diff --check
```

Expected: every command exits `0`. Full frontend suite, full `tsc`, and production build remain delegated to GitHub CI by the personal incremental frontend policy.

- [ ] **Step 5: Reuse the existing Chrome tab for targeted visual verification**

Confirm the server at `http://127.0.0.1:5175/` belongs to this worktree and still points at the configured remote backend. In the user's existing Chrome tab, open a Schedule Overview, expand Advanced details, and verify English and Chinese labels, readable recurrence, exact cron value, responsive fit, focusability, and no sibling-content loading regression. Do not open a second browser session or duplicate tab.

- [ ] **Step 6: Review and stage only this task's files**

Review `git diff`, confirm there are no unrelated changes, and explicitly stage the plan, specification, component, test, locale, styles, prototype, generator, generated board, seven Schedule PNGs, verifier, and README. Never stage `.planning/`.

- [ ] **Step 7: Commit and push**

```bash
git commit -m "Make Schedule cron details readable"
git push origin feat/2026-08-20_workflow-schedule-frontend
```

- [ ] **Step 8: Update Draft PR #3498 and verify delivery state**

Keep title `[DO NOT MERGE] Wire workflow-scoped Schedule management`, base `feat/2026-08-04_workflow-activity-vnext`, and Draft status. Add the semantic decision and exact focused command results to the PR, including:

```markdown
## Local verification

- Related tests: `<actual commands and results>`
- Changed-file static checks: `<actual commands and results>`
- Design baseline: `<actual commands and results>`
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Finally confirm local `HEAD`, `origin/feat/2026-08-20_workflow-schedule-frontend`, and PR #3498 head resolve to the same commit SHA.
