# Workflow Catalogue Frontend Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Workflow Activity vNext browser-owned draft/committed catalogue join with the backend scope workflow catalogue query.

**Architecture:** Add the backend response and query types to the scope models, decode the contract at `scopesApi`, and consume it with one cancellable cursor query in `WorkflowsPage`. The page renders backend ordering and capabilities directly; existing write operations remain unchanged and invalidate the new catalogue after their observations complete.

**Tech Stack:** React 19, TypeScript 5.6, TanStack React Query 5, Ant Design 6, Jest 29, Testing Library.

---

## File Map

- Modify `apps/aevatar-console-web/src/shared/models/scopes.ts`: own the strong frontend representation of the backend scope workflow catalogue contract.
- Modify `apps/aevatar-console-web/src/shared/api/scopesApi.ts`: decode the catalogue response and build the typed query URL with cancellation.
- Create `apps/aevatar-console-web/src/shared/api/scopesApi.test.ts`: verify URL, signal, and boundary decoding behavior.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`: replace two list queries and browser semantics with the backend cursor query.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`: verify catalogue views, search, pagination, capabilities, identity isolation, and mutation refresh behavior.
- Modify `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`: add stable layout for the pagination command below the table.

### Task 1: Add The Typed Scope Catalogue API

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/models/scopes.ts`
- Modify: `apps/aevatar-console-web/src/shared/api/scopesApi.ts`
- Create: `apps/aevatar-console-web/src/shared/api/scopesApi.test.ts`

- [ ] **Step 1: Write the failing API contract test**

Create `scopesApi.test.ts` with an authenticated fetch fixture. Return one camel-case catalogue response containing an overlapping row and assert that this call:

```ts
const controller = new AbortController();
const response = await scopesApi.queryWorkflowCatalogue(
  {
    scopeId: 'scope alpha',
    view: 'drafts',
    query: '审批 flow',
    cursor: 'next token',
    take: 25,
  },
  controller.signal,
);
```

requests:

```text
/api/scopes/scope%20alpha/workflow-catalogue?view=drafts&query=%E5%AE%A1%E6%89%B9+flow&cursor=next+token&take=25
```

Assert `init.signal === controller.signal`, the decoded `workflowId` is `wf-alpha`, all four capability values are retained, committed facts remain distinct, `nextPageToken` is retained, and a `null` committed object decodes as `null` in a second row.

- [ ] **Step 2: Run the API test to verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest src/shared/api/scopesApi.test.ts --runInBand
```

Expected: FAIL because `queryWorkflowCatalogue` and the catalogue model types do not exist.

- [ ] **Step 3: Add exact catalogue model types**

Add these public shapes to `scopes.ts`:

```ts
export type ScopeWorkflowCatalogueView = 'all' | 'drafts';

export interface ScopeWorkflowCatalogueActionCapability {
  available: boolean;
  unavailableReason: string | null;
}

export interface ScopeWorkflowCatalogueRowCapabilities {
  open: ScopeWorkflowCatalogueActionCapability;
  activity: ScopeWorkflowCatalogueActionCapability;
  rename: ScopeWorkflowCatalogueActionCapability;
  delete: ScopeWorkflowCatalogueActionCapability;
}

export interface ScopeWorkflowCatalogueCommittedFacts {
  serviceKey: string;
  workflowName: string;
  actorId: string;
  activeRevisionId: string;
  deploymentId: string;
  deploymentStatus: string;
}

export interface ScopeWorkflowCatalogueRow {
  scopeId: string;
  workflowId: string;
  name: string;
  description: string;
  hasDraftSource: boolean;
  hasCommittedSource: boolean;
  updatedAtUtc: string;
  updatedAtSource: string;
  capabilities: ScopeWorkflowCatalogueRowCapabilities;
  sourceWatermarkUtc: string;
  committed: ScopeWorkflowCatalogueCommittedFacts | null;
}

export interface ScopeWorkflowCatalogueResponse {
  items: ScopeWorkflowCatalogueRow[];
  nextPageToken: string | null;
  freshness: {
    refreshWatermarkUtc: string | null;
    sourceVersionSemantics: string;
  };
  search: {
    searchableFields: string[];
    caseSemantics: string;
    unicodeNormalization: string;
    maximumQueryLength: number;
    emptyQuerySemantics: string;
    workflowIdSemantics: string;
  };
}

export interface ScopeWorkflowCatalogueQuery {
  scopeId: string;
  view: ScopeWorkflowCatalogueView;
  query?: string;
  cursor?: string;
  take?: number;
}
```

- [ ] **Step 4: Implement strict boundary decoders and the API method**

In `scopesApi.ts`, add focused decoders using the existing `expectRecord`, `expectArray`, `readBoolean`, `readNumber`, `readNullableString`, `readString`, and `readStringArray` helpers. Decode both camel-case and Pascal-case keys, matching the existing API boundary style.

Add:

```ts
queryWorkflowCatalogue(
  input: ScopeWorkflowCatalogueQuery,
  signal?: AbortSignal,
): Promise<ScopeWorkflowCatalogueResponse> {
  return requestJson(
    withQuery(
      `/api/scopes/${encodeURIComponent(input.scopeId)}/workflow-catalogue`,
      {
        view: input.view,
        query: input.query?.trim() || undefined,
        cursor: input.cursor,
        take: input.take,
      },
    ),
    decodeScopeWorkflowCatalogueResponse,
    { signal },
  );
}
```

Import `withQuery` from the HTTP client. Do not reuse `ScopeWorkflowSummary`; the catalogue has a different semantic contract.

- [ ] **Step 5: Run the API test to verify GREEN**

Run the same focused Jest command. Expected: PASS with no warnings.

- [ ] **Step 6: Commit the API increment**

```bash
git add apps/aevatar-console-web/src/shared/models/scopes.ts \
  apps/aevatar-console-web/src/shared/api/scopesApi.ts \
  apps/aevatar-console-web/src/shared/api/scopesApi.test.ts
git commit -m "Add scope workflow catalogue client"
```

### Task 2: Replace Client Catalogue Semantics With The Backend Query

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts`

- [ ] **Step 1: Replace catalogue mocks and write failing page tests**

Extend the `scopesApi` mock with `queryWorkflowCatalogue`. Replace the catalogue setup in the directly affected tests with responses shaped as:

```ts
{
  items: [catalogueRow],
  nextPageToken: null,
  freshness: {
    refreshWatermarkUtc: '2026-08-04T10:00:00Z',
    sourceVersionSemantics: 'max source timestamp',
  },
  search: {
    searchableFields: ['name', 'description', 'workflowId'],
    caseSemantics: 'ordinal ignore case',
    unicodeNormalization: 'FormKC',
    maximumQueryLength: 128,
    emptyQuerySemantics: 'no filter',
    workflowIdSemantics: 'exact or prefix',
  },
}
```

Add focused tests proving:

```ts
expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledWith(
  expect.objectContaining({ scopeId: 'scope-alpha', view: 'all', take: 50 }),
  expect.any(AbortSignal),
);
expect(mockStudioApi.listWorkflowDrafts).not.toHaveBeenCalled();
expect(mockScopesApi.listWorkflows).not.toHaveBeenCalled();
```

Also assert the selector offers `All workflows` and `Drafts`, does not offer Active or Archived, and a legacy `?view=archived` URL queries `view=all`.

Add a server-order assertion using two rows whose timestamps would have caused the old browser sort to reverse them.

- [ ] **Step 2: Run only the new catalogue page tests to verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  --runInBand \
  --testNamePattern='backend catalogue|All workflows|legacy catalogue|backend row order'
```

Expected: FAIL because the page still calls and merges the two old sources.

- [ ] **Step 3: Implement backend view state, debounce, and infinite query**

In `WorkflowsPage.tsx`:

- Replace `WorkflowView` with `ScopeWorkflowCatalogueView`.
- Make `readWorkflowView` return only `all` or `drafts`, defaulting all other values to `all`.
- Remove `toDraftRow`, `toCommittedRow`, both old list queries, `draftWorkflowIds`, and the merge/filter/sort memo.
- Keep raw input state and derive a debounced query with a local effect and 300 ms timer cleanup.
- Use `useInfiniteQuery` with a query key containing scope, view, and debounced query.

Use this query shape:

```ts
const catalogue = useInfiniteQuery({
  queryKey: [
    'workflow-activity-vnext',
    'workflow-catalogue',
    scopeId,
    view,
    debouncedQuery,
  ],
  initialPageParam: undefined as string | undefined,
  queryFn: ({ pageParam, signal }) =>
    scopesApi.queryWorkflowCatalogue(
      {
        scopeId,
        view,
        query: debouncedQuery || undefined,
        cursor: pageParam,
        take: 50,
      },
      signal,
    ),
  getNextPageParam: (lastPage) => lastPage.nextPageToken ?? undefined,
  retry: false,
});
```

Map loaded items to presentation rows without changing order. Use `committed?.activeRevisionId`, `deploymentId`, and `deploymentStatus`, defaulting presentation-only missing strings to empty strings. Keep every identity in its named field.

Write only `view=drafts` to the URL; omit `view=all` as the default. Keep trimmed `q` in the URL.

- [ ] **Step 4: Render cursor pagination without disturbing existing rows**

Below `TableScrollRegion`, render a stable pagination action only when `catalogue.hasNextPage` is true:

```tsx
<div className="wa-vnext__pagination-actions">
  <Button
    loading={catalogue.isFetchingNextPage}
    onClick={() => void catalogue.fetchNextPage()}
  >
    {t('workflowActivityVNext.workflows.loadMore', 'Load more')}
  </Button>
</div>
```

Add a compact flex rule in `styles.ts` that reserves stable spacing and right-aligns the command on desktop while stretching it on small screens.

- [ ] **Step 5: Run the focused page tests to verify GREEN**

Run the exact command from Step 2. Expected: PASS.

- [ ] **Step 6: Write and verify RED tests for search cancellation and pagination**

Use fake timers to type `审批`, assert no second query before 300 ms, advance timers, and assert the new request receives `query: '审批'` plus an AbortSignal.

Return a first page with `nextPageToken: '1'`, click Load more, and assert the next request carries `cursor: '1'`; then resolve the second page and assert both pages render in backend order. Also reject a next-page request, assert the first page remains visible with a local pagination error, then retry the same cursor successfully.

Run:

```bash
pnpm --dir apps/aevatar-console-web jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  --runInBand \
  --testNamePattern='debounces backend catalogue search|loads the next catalogue page'
```

Expected before final implementation adjustments: FAIL on the new assertions. After implementing timer cleanup and next-page rendering: PASS.

- [ ] **Step 7: Commit the query migration increment**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/styles.ts
git commit -m "Use backend workflow catalogue query"
```

### Task 3: Honor Backend Capabilities And Refresh The Catalogue

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`

- [ ] **Step 1: Write failing capability and refresh tests**

Add one row with `activity.available=false`, `rename.available=false`, and `delete.available=false`; assert Activity is disabled and Rename/Delete menu entries are absent even when source flags might otherwise imply availability.

Add mutation assertions that successful rename, delete, and observed archive each call:

```ts
expect(mockScopesApi.queryWorkflowCatalogue).toHaveBeenCalledTimes(2);
```

Do not assert a refresh through `listWorkflowDrafts` or cache writes to the removed committed query key.

- [ ] **Step 2: Run the capability tests to verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  --runInBand \
  --testNamePattern='backend catalogue capabilities|refreshes the catalogue after rename|refreshes the catalogue after delete|refreshes the catalogue after archive'
```

Expected: FAIL because actions and mutation refreshes still use old source facts and query instances.

- [ ] **Step 3: Bind actions to capabilities and catalogue refresh**

- Disable Open and Activity when their backend capability is unavailable. Do not construct client navigation for a disabled action.
- Include Rename and Delete menu entries only when their backend capability is available.
- Continue deriving Archive only through `canArchiveWorkflow` because the backend catalogue capability set does not define Archive.
- Resolve the archive command identity from `getWorkflowDetail`: require the exact `workflowId`, then pass the backend-provided `publishedServiceId`, `serviceAppId`, `serviceNamespace`, and `deploymentId`. Never parse `serviceKey` or substitute `workflowId` for `publishedServiceId`.
- Replace successful `drafts.refetch()` and `committed.refetch()` calls with `catalogue.refetch()`.
- Remove the old committed query cache write after archive observation.
- Make archive observation read `view=all` from `queryWorkflowCatalogue`, use the exact workflow ID as `query`, follow `nextPageToken` until exhausted, and select only the exact `workflowId` from the returned rows. Do not call `listWorkflows` as a fallback.
- Simplify error reporting to one catalogue error signature and one retry handler.

- [ ] **Step 4: Run the focused capability tests to verify GREEN**

Run the exact command from Step 2. Expected: PASS.

- [ ] **Step 5: Run the complete directly affected test files**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest \
  src/shared/api/scopesApi.test.ts \
  src/pages/workflow-activity-vnext/index.test.tsx \
  --runInBand
```

Expected: both suites pass. Fix only failures caused by the catalogue migration; preserve unrelated assertions and behavior.

- [ ] **Step 6: Commit the capability increment**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx \
  apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx
git commit -m "Honor workflow catalogue capabilities"
```

### Task 4: Focused Validation And Pull Request Delivery

**Files:**
- Review all task files and the two planning documents.

- [ ] **Step 1: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

Read the reported affected tests and `staticCheckFiles`. Do not expand to a full frontend command.

- [ ] **Step 2: Run the required test stability guard**

```bash
bash tools/ci/test_stability_guards.sh
```

Expected: PASS because the timer-based debounce test uses Jest fake timers rather than repository polling helpers.

- [ ] **Step 3: Run changed-file static checks only**

Use Biome against exactly the analyzer's TypeScript/TSX `staticCheckFiles`, for example:

```bash
pnpm --dir apps/aevatar-console-web exec biome lint \
  src/shared/models/scopes.ts \
  src/shared/api/scopesApi.ts \
  src/shared/api/scopesApi.test.ts \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx \
  src/pages/workflow-activity-vnext/styles.ts
```

Do not run local `tsc`, full lint, full test, or production build. No reliable affected-only typecheck target exists, so GitHub CI owns full type verification.

- [ ] **Step 4: Review and commit any validation-only fixes**

Run:

```bash
git diff --check
git status --short
git diff origin/feat/2026-08-04_workflow-activity-vnext...HEAD -- \
  apps/aevatar-console-web \
  docs/superpowers/specs/2026-08-07-workflow-catalogue-frontend-design.md \
  docs/superpowers/plans/2026-08-07-workflow-catalogue-frontend.md
```

Stage only task files and use `Fix workflow catalogue frontend validation` if a final validation commit is necessary.

- [ ] **Step 5: Push and create the pull request**

Push `feat/2026-08-07_workflow-catalogue-frontend` and create a PR targeting `feat/2026-08-04_workflow-activity-vnext`.

The PR body must contain Problem and solution, Impacted paths, and Local verification. Record every exact focused command and result, plus:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Stop after returning the PR URL. Do not babysit CI.
