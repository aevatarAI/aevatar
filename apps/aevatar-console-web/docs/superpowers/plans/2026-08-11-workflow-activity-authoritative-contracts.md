---
title: Workflow Activity authoritative contracts implementation plan
status: approved
owner: Aevatar frontend
---

# Workflow Activity Authoritative Contracts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace Workflow Activity's client-side run inference with the completed #3250-#3252 backend feed, recovery, receipt, and lineage contracts.

**Architecture:** Shared models and strict transport decoders own the backend contract. Activity consumes an infinite cursor query keyed by URL-backed server filters, while Run Detail consumes typed recovery and lineage directly from its existing detail query. Public run identities remain separate from technical actor addresses throughout routing and presentation.

**Tech Stack:** React 19, TypeScript, Umi Max, Ant Design 6, TanStack Query 5, Jest, Testing Library, Biome.

---

### Task 1: Typed Feed, Recovery, Receipt, and Lineage Contracts

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/models/workflowActivity.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/workflowActivityApi.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/workflowActivityApi.test.ts`

- [ ] **Step 1: Add failing adapter tests**

Add tests whose mocked response includes a paginated envelope and numeric protobuf enum values:

```ts
await expect(
  workflowActivityApi.listActivityRuns('scope-alpha', {
    workflowId: 'wf-alpha',
    cursor: 'opaque-cursor',
    includeTotalCount: true,
  }),
).resolves.toMatchObject({
  hasMore: true,
  nextCursor: 'next-cursor',
  totalCount: 42,
});

expect(authFetchMock).toHaveBeenCalledWith(
  expect.stringContaining('workflowId=wf-alpha'),
  undefined,
);
```

Also assert strict decoding for recovery capability, separate retry/fork and sub-workflow lineage, and required `newRunId` on a successful fork receipt.

- [ ] **Step 2: Run the focused test and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/api/workflowActivityApi.test.ts
```

Expected: FAIL because `listActivityRuns`, the new model fields, and `newRunId` decoding do not exist.

- [ ] **Step 3: Add explicit model types**

Add numeric unions matching the protobuf JSON values and separate relationship structures:

```ts
export type WorkflowRecoveryEligibility = 0 | 1 | 2 | 3;
export type WorkflowRecoveryRecommendedAction = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7;
export type WorkflowRunLineageAvailability = 0 | 1 | 2 | 3;

export interface WorkflowActivityRunFeedPage {
  readonly items: readonly WorkflowActivityRunFeedRow[];
  readonly nextCursor: string | null;
  readonly hasMore: boolean;
  readonly totalCount: number | null;
}
```

Model the complete backend row, action capability, recovery capability, retry/fork lineage, sub-workflow lineage, and lineage run reference without collapsing identities.

- [ ] **Step 4: Implement strict decoders and the additive API method**

Add focused decoder functions and build the endpoint query without modifying `listRuns`:

```ts
listActivityRuns(scopeId, filter = {}) {
  return requestActivityJson(
    withQuery('/api/workflow/observatory/activity-runs', {
      scope: scopeId.trim(),
      workflowId: filter.workflowId?.trim(),
      cursor: filter.cursor?.trim(),
      includeTotalCount: filter.includeTotalCount,
      take: filter.take,
    }),
    decodeActivityRunFeedPage,
  );
}
```

Extend `decodeDetail` with top-level `recoveryCapability` and `lineage`. Extend `decodeForkReceipt` with a required non-blank `newRunId` while retaining `newRunActorId` as technical data.

- [ ] **Step 5: Re-run the adapter test and confirm GREEN**

Run the Step 2 command. Expected: PASS.

### Task 2: Activity Feed and Cursor Pagination

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify only if needed for responsive table density: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`

- [ ] **Step 1: Replace list mocks with failing feed behavior tests**

Use distinct identities in fixtures (`workflowId: 'wf-alpha'`, `runId: 'run-alpha'`, `actorId: 'actor-technical-alpha'`). Assert that:

```ts
expect(listActivityRunsMock).toHaveBeenCalledWith(
  'scope-alpha',
  expect.objectContaining({
    workflowId: 'wf-alpha',
    includeTotalCount: true,
  }),
);
expect(getWorkflowDetailMock).not.toHaveBeenCalled();
```

Add tests for authoritative initiator/input/failure/waiting/duration presentation, total and loaded counts, cursor-based Load more, preserved rows on next-page failure, refresh-from-start on malformed cursor, and cursor reset after a URL filter changes.

- [ ] **Step 2: Run the Activity test and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx
```

Expected: FAIL because Activity still calls `listRuns`, resolves workflow ids through `scopesApi`, and has no pagination envelope.

- [ ] **Step 3: Implement an infinite Activity query**

Replace the workflow-resolution and list query with `useInfiniteQuery`:

```ts
const runs = useInfiniteQuery({
  queryKey: ['workflow-activity-vnext', 'activity-runs', scopeId, filters],
  initialPageParam: undefined as string | undefined,
  queryFn: ({ pageParam }) =>
    workflowActivityApi.listActivityRuns(scopeId, {
      ...filters,
      cursor: pageParam,
      includeTotalCount: pageParam === undefined,
      take: 50,
    }),
  getNextPageParam: (lastPage) =>
    lastPage.hasMore ? lastPage.nextCursor ?? undefined : undefined,
  retry: false,
});
```

Flatten pages without deducing facts, apply `q` only to loaded rows, and keep first-page and next-page error states separate.

- [ ] **Step 4: Render the authoritative operational row**

Keep the existing responsive table and render workflow/run reference, status context, started/duration, initiator/source, redacted input summary, and exact-run action. Add loaded/total state and Load more. Do not render actor id or raw input.

- [ ] **Step 5: Re-run the Activity test and confirm GREEN**

Run the Step 2 command. Expected: PASS.

### Task 3: Capability-Driven Recovery and Durable Lineage

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runRecovery.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/runRecovery.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Modify only if needed for compact related-run presentation: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`

- [ ] **Step 1: Rewrite recovery unit tests around backend capability**

Delete graph/failed-step inference assertions. Add capability assertions:

```ts
expect(resolveRunRecovery(capability)).toEqual({
  retry: expect.objectContaining({ enabled: true, startingStepId: 'step-failed' }),
  runAgain: expect.objectContaining({ enabled: false, reason: 'Fix access first.' }),
});
```

Cover eligible, ineligible, unavailable, missing starting step, reuse, cost, and recommended-action values.

- [ ] **Step 2: Run the recovery test and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-activity-vnext/activity/runRecovery.test.ts
```

Expected: FAIL because `resolveRunRecovery` still accepts steps and graph candidates.

- [ ] **Step 3: Implement the pure capability presentation helper**

Accept only `WorkflowRunRecoveryCapability`. Return two presentations containing enabled state, reason, recommended actions, starting step, reuse, cost, revision id, and revision version. Treat eligible-without-starting-step as unavailable rather than inferring a step.

- [ ] **Step 4: Add failing Run Detail behavior tests**

Assert focusable `aria-disabled` unavailable actions and their reason, modal revision/start/reuse/cost disclosure, fork request input and starting step, accepted-vs-completed wording, Open new run navigation to `run-new`, and no route containing `actor-new`.

Add separate lineage assertions for retry history and sub-workflows, including source/original/parent/root/child run links and absence of actor ids from default content.

- [ ] **Step 5: Run the Run Detail test and confirm RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: FAIL because the page still infers recovery and opens general Activity after a fork.

- [ ] **Step 6: Implement recovery confirmation, receipt navigation, and lineage**

Drive actions exclusively from `run.recoveryCapability`. Preserve graph loading only for the Graph tab. Confirm immutable-new-run semantics before fork. Route accepted receipts and lineage through:

```ts
history.push(buildWorkflowActivityRunHref(scopeId, receipt.newRunId));
```

Render retry/fork and sub-workflow relationships as separate unframed sections. Keep actor ids in Technical details only.

- [ ] **Step 7: Re-run recovery and Run Detail tests and confirm GREEN**

Run both commands from Steps 2 and 5. Expected: PASS.

### Task 4: Focused Verification and Delivery

**Files:**
- Review every file changed from `origin/feat/2026-08-04_workflow-activity-vnext`

- [ ] **Step 1: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Read `~/.codex/skills/frontend-incremental-pr/references/framework-commands.md` after the analyzer output.

- [ ] **Step 2: Run every changed test and dependency-related focused test**

At minimum rerun the three commands from Tasks 1-3 together with any additional tests reported by the analyzer. Do not run the complete Jest suite.

- [ ] **Step 3: Run changed-file static checks only**

Use the analyzer's `staticCheckFiles` with Biome. Run TypeScript only if the repository exposes a reliable affected target; otherwise state that GitHub CI owns full type verification. Do not run a local production build.

- [ ] **Step 4: Run the required test stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

- [ ] **Step 5: Review, commit, push, and create the PR**

Inspect the complete diff, stage only this task's files, use imperative focused commits, push `feat/2026-08-11_workflow-activity-contracts`, and create a PR targeting `feat/2026-08-04_workflow-activity-vnext`. Include exact focused commands and state that full frontend suite/typecheck/build are delegated to GitHub CI.

