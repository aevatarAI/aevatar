# Schedule Attempt Run Detail Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure a Schedule History row links only to that attempt's authoritative Run Detail and never to the Schedule-wide Activity list.

**Architecture:** Normalize the Workflow facade's `runActorId` and the scheduled-dispatch transport's `targetActorId` into one authoritative UI `runActorId`. Preserve the header-level `View related runs in Activity` link for Schedule-wide navigation, while rendering each row as an exact Run Detail link only when that normalized identity exists.

**Tech Stack:** React, TypeScript, React Testing Library, Jest, Biome

---

### Task 1: Lock The Correct Row Navigation Contract

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx`

- [ ] **Step 1: Replace the legacy fallback expectation with a non-interactive row expectation**

```tsx
it('keeps a History attempt without Run identity non-interactive', async () => {
  const schedule = createScheduleSummary();
  mockedWorkflowScheduleApi.list.mockResolvedValue({
    items: [schedule],
    nextCursor: null,
    totalCount: 1,
  });
  mockedWorkflowScheduleApi.get.mockResolvedValue({
    schedule,
    recentFires: [
      {
        scheduledFireAt: '2026-08-20T01:00:00Z',
        completedAt: '2026-08-20T01:02:00Z',
        idempotencyKey: 'schedule-alpha:fire:1',
        runActorId: '',
        error: '',
        manual: false,
      },
    ],
  });

  renderSurface(true, 'modal', jest.fn(), 'list');
  fireEvent.click(
    await screen.findByRole('button', { name: 'View Daily workflow run' }),
  );
  fireEvent.click(await screen.findByRole('tab', { name: 'History' }));
  await screen.findByRole('heading', { name: 'Recent attempts' });

  expect(
    screen.queryByRole('link', { name: /View related runs from/ }),
  ).not.toBeInTheDocument();
  const attemptRow = screen.getByText('Run started').closest('tr');
  expect(attemptRow?.querySelectorAll('td')[4]?.querySelector('a')).toBeNull();
  expect(
    screen.getByRole('link', { name: 'View related runs in Activity' }),
  ).toHaveAttribute(
    'href',
    '/scopes/scope-alpha/workflow-activity-vnext/activity?workflowId=wf-alpha&schedule=schedule-alpha',
  );
});
```

- [ ] **Step 2: Run the regression test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand \
  src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.test.tsx \
  -t "keeps a History attempt without Run identity non-interactive"
```

Expected: FAIL because the current row still renders `View related runs from ...`.

### Task 2: Preserve The Authoritative Remote Run Identity

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/api/workflowScheduleApi.test.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/workflowScheduleApi.ts`

- [ ] **Step 1: Add a failing decoder test using the observed remote transport field**

```tsx
it('maps the scheduled dispatch target actor to the authoritative Run destination', async () => {
  mockJson({
    schedule: createSummary(),
    recentFires: [
      {
        scheduledFireAt: '2026-08-20T08:00:00Z',
        completedAt: '2026-08-20T08:01:00Z',
        idempotencyKey: 'schedule-alpha:fire:1',
        targetActorId: 'run-alpha',
        error: '',
        manual: false,
      },
    ],
  });

  await expect(
    workflowScheduleApi.get('scope-alpha', 'wf-alpha', 'schedule-alpha'),
  ).resolves.toEqual({
    schedule: expect.objectContaining({ scheduleId: 'schedule-alpha' }),
    recentFires: [expect.objectContaining({ runActorId: 'run-alpha' })],
  });
});
```

- [ ] **Step 2: Run the decoder regression test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand \
  src/shared/api/workflowScheduleApi.test.ts \
  -t "maps the scheduled dispatch target actor to the authoritative Run destination"
```

Expected: FAIL because the current decoder returns an empty `runActorId`.

- [ ] **Step 3: Map the explicit transport identity at the API boundary**

```tsx
runActorId:
  readNullableString(
    fire,
    ['runActorId', 'RunActorId'],
    `${entryLabel}.runActorId`,
  ) ??
  readNullableString(
    fire,
    ['targetActorId', 'TargetActorId'],
    `${entryLabel}.targetActorId`,
  ) ??
  '',
```

- [ ] **Step 4: Run the decoder regression test and verify GREEN**

Run the Step 2 command again.

Expected: PASS.

### Task 3: Remove The Misleading Fallback

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowScheduleSurface.tsx`

- [ ] **Step 1: Make the row Action depend only on `runActorId`**

```tsx
const attemptHref = runActorId
  ? buildWorkflowActivityRunHref(scopeId, runActorId, {
      workflowId,
      schedule: scheduleDetail.data.schedule.scheduleId,
    })
  : null;
const attemptLabel = attemptHref
  ? t('workflowActivityVNext.schedule.openRunAria', 'Open Run from {date}', {
      date: formattedScheduledAt,
    })
  : null;
```

- [ ] **Step 2: Run the focused regression test and verify GREEN**

Run the Task 1 command again.

Expected: PASS.

### Task 4: Verify And Deliver The Increment

**Files:**
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-11-workflow-schedule-design.md`
- Modify: `apps/aevatar-console-web/docs/superpowers/specs/2026-08-21-workflow-schedule-history-design.md`

- [ ] **Step 1: Run the frontend change scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run only analyzer-selected related tests and changed-file checks**

Run the explicit changed test files plus every dependency-related test reported by the analyzer. Run Biome only on `staticCheckFiles`. Do not run a full frontend suite, full typecheck, or production build.

- [ ] **Step 3: Run required repository checks**

```bash
bash tools/docs/lint.sh
bash tools/ci/test_stability_guards.sh
git diff --check
```

- [ ] **Step 4: Verify in the user's existing Chrome tab**

Reload the existing local Workflows tab. Confirm remote `targetActorId` records render one unique exact Run Detail link per attempt, records without a normalized identity remain non-interactive, and the header-level related-Runs link remains separate.

- [ ] **Step 5: Review, commit, push, and update PR #3498**

Stage only the component, decoder, regression tests, locale cleanup, and Schedule docs for this task. Push `feat/2026-08-20_workflow-schedule-frontend`, update the PR verification evidence, and confirm the PR remains Draft, contains `[DO NOT MERGE]`, and targets `feat/2026-08-04_workflow-activity-vnext`.
