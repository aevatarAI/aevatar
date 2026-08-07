# Workflow List Filter Semantics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Align Workflow and Activity list filtering without inventing unsupported Workflow states.

**Architecture:** Keep resource-specific filter semantics while sharing one toolbar interaction model. Workflows derives Draft membership only from the real scoped draft API; Activity keeps its real Run API filters, and both pages synchronize list state with the URL.

**Tech Stack:** React 19, TypeScript, TanStack Query, Ant Design, Umi locale, Jest, Testing Library, Biome.

---

## Task 1: Specify Workflow filter behavior in tests

**Files:**

- Modify: `src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] Replace the pathname-only test location with a pathname-plus-search snapshot so `useConsoleLocation()` sees copied Workflow URLs:

```tsx
let mockLocation = '/scopes/scope-alpha/workflow-activity-vnext/workflows';

const readMockUrl = () => new URL(mockLocation, 'http://console.local');

useLocation: () => ({
  hash: '',
  pathname: readMockUrl().pathname,
  search: readMockUrl().search,
}),

getLocationSnapshot: () =>
  `${readMockUrl().pathname}${readMockUrl().search}`,
```

- [ ] Add focused tests with distinct identities (`wf-draft-alpha`, `wf-committed-beta`) proving:

```tsx
mockLocation =
  '/scopes/scope-alpha/workflow-activity-vnext/workflows?q=support&view=drafts';

expect(await screen.findByText('Support triage')).toBeInTheDocument();
expect(screen.queryByText('Invoice review')).not.toBeInTheDocument();
expect(screen.getByRole('searchbox', { name: 'Search workflows' })).toHaveValue(
  'support',
);
expect(screen.getByRole('combobox', { name: 'Workflow view' })).toHaveTextContent(
  'Drafts',
);
```

```tsx
fireEvent.mouseDown(screen.getByRole('combobox', { name: 'Workflow view' }));
expect(await screen.findByRole('option', { name: 'All workflows' })).toBeVisible();
expect(screen.getByRole('option', { name: 'Drafts' })).toBeVisible();
expect(screen.queryByRole('option', { name: 'Committed' })).not.toBeInTheDocument();
expect(screen.queryByRole('option', { name: 'Published' })).not.toBeInTheDocument();
expect(screen.queryByRole('option', { name: 'Failing' })).not.toBeInTheDocument();
```

```tsx
fireEvent.change(screen.getByRole('searchbox', { name: 'Search workflows' }), {
  target: { value: 'invoice' },
});
await waitFor(() =>
  expect(history.replace).toHaveBeenLastCalledWith(
    '/scopes/scope-alpha/workflow-activity-vnext/workflows?q=invoice',
  ),
);
```

```tsx
mockStudioApi.listWorkflowDrafts.mockRejectedValue(new Error('draft source down'));
mockLocation =
  '/scopes/scope-alpha/workflow-activity-vnext/workflows?view=drafts';
expect(await screen.findByText('Draft workflows unavailable')).toBeInTheDocument();
expect(screen.getByRole('button', { name: 'Retry workflows' })).toBeEnabled();
expect(screen.queryByText('No workflows yet')).not.toBeInTheDocument();
```

- [ ] Run the Workflow test and confirm RED because the view select, URL-backed search/view, clear action, and draft-unavailable state do not exist yet:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand
```

Expected: the newly added assertions fail for missing `Workflow view`, missing URL restoration, or missing draft-unavailable state; unrelated existing tests remain runnable.

## Task 2: Implement the Workflow view filter and honest draft failure

**Files:**

- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `src/locales/catalog.test.ts`

- [ ] Import `Select`, `useConsoleLocation`, and move Refresh into the shell header next to New workflow:

```tsx
headerActions={
  <Space wrap>
    <Button icon={<ReloadOutlined />} onClick={retry}>
      {t('workflowActivityVNext.common.refresh', 'Refresh')}
    </Button>
    <Button
      icon={<PlusOutlined />}
      onClick={() => history.push(buildWorkflowActivityNewHref(scopeId))}
      type="primary"
    >
      {t('workflowActivityVNext.workflows.new', 'New workflow')}
    </Button>
  </Space>
}
```

- [ ] Restore `query` and `view` from `location.search`, synchronize browser navigation into state, and serialize only non-default values:

```tsx
type WorkflowView = 'all' | 'drafts';

function readWorkflowView(params: URLSearchParams): WorkflowView {
  return params.get('view') === 'drafts' ? 'drafts' : 'all';
}

const location = useConsoleLocation();
const initialParams = React.useMemo(
  () => new URLSearchParams(location.search),
  [location.search],
);
const [query, setQuery] = React.useState(initialParams.get('q') ?? '');
const [view, setView] = React.useState<WorkflowView>(
  readWorkflowView(initialParams),
);

React.useEffect(() => {
  const params = new URLSearchParams(location.search);
  setQuery(params.get('q') ?? '');
  setView(readWorkflowView(params));
}, [location.search]);

React.useEffect(() => {
  const params = new URLSearchParams();
  if (query.trim()) params.set('q', query.trim());
  if (view === 'drafts') params.set('view', 'drafts');
  const suffix = params.toString();
  history.replace(`${location.pathname}${suffix ? `?${suffix}` : ''}`);
}, [location.pathname, query, view]);
```

- [ ] Build draft membership exclusively from exact API-returned Workflow IDs and apply it before text search:

```tsx
const draftWorkflowIds = React.useMemo(
  () => new Set((drafts.data ?? []).map((item) => item.workflowId)),
  [drafts.data],
);

return [...merged.values()]
  .filter((item) => view !== 'drafts' || draftWorkflowIds.has(item.workflowId))
  .filter((item) => {
    const normalized = query.trim().toLowerCase();
    return (
      !normalized ||
      [item.name, item.description, item.workflowId].some((value) =>
        value.toLowerCase().includes(normalized),
      )
    );
  })
  .sort(/* retain current updated-at ordering */);
```

- [ ] Add the two-option view Select. Disable only Drafts when the draft source failed; do not add Committed, Published, Failing, Ready, or inferred lifecycle values:

```tsx
<Select
  aria-label={t('workflowActivityVNext.workflows.viewFilter', 'Workflow view')}
  onChange={setView}
  options={[
    {
      label: t('workflowActivityVNext.workflows.allView', 'All workflows'),
      value: 'all',
    },
    {
      disabled: drafts.isError,
      label: t('workflowActivityVNext.workflows.draftsView', 'Drafts'),
      value: 'drafts',
    },
  ]}
  value={view}
/>
```

- [ ] Before ordinary empty rendering, show an honest unavailable state when `view === 'drafts' && drafts.isError`. Its Retry calls `drafts.refetch()` and must not re-request committed data or synthesize rows.

- [ ] For a successful zero-row filtered result, render `No matching workflows`, the revised `Try a different search or filter.`, and a `Clear filters` button that calls `setQuery('')` and `setView('all')`. Preserve the original `No workflows yet` create action only for an unfiltered, successfully empty catalogue.

- [ ] Add both locale catalogues:

```ts
'workflowActivityVNext.workflows.allView': 'All workflows',
'workflowActivityVNext.workflows.clearFilters': 'Clear filters',
'workflowActivityVNext.workflows.draftsUnavailable':
  'Draft workflows unavailable',
'workflowActivityVNext.workflows.draftsUnavailableDescription':
  'Try again to load draft workflows.',
'workflowActivityVNext.workflows.draftsView': 'Drafts',
'workflowActivityVNext.workflows.viewFilter': 'Workflow view',
```

```ts
'workflowActivityVNext.workflows.allView': '全部工作流',
'workflowActivityVNext.workflows.clearFilters': '清除筛选',
'workflowActivityVNext.workflows.draftsUnavailable': '草稿工作流不可用',
'workflowActivityVNext.workflows.draftsUnavailableDescription':
  '请重试加载草稿工作流。',
'workflowActivityVNext.workflows.draftsView': '草稿',
'workflowActivityVNext.workflows.viewFilter': '工作流视图',
```

- [ ] Run focused GREEN verification:

```bash
pnpm exec jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/locales/catalog.test.ts \
  --runInBand
```

Expected: all selected tests pass; console output has no React act errors or unexpected network failures.

## Task 3: Make Activity search URL-backed without changing API semantics

**Files:**

- Modify: `src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`
- Modify: `src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`

- [ ] Import `fireEvent` and `history` in the test, then add a failing test that starts from all supported URL fields:

```tsx
mockSearch =
  '?q=customer&status=failed&origin=draft&definition=definition-alpha&workflowFilter=unavailable';

renderWithQueryClient(<ActivityPage scopeId="scope-alpha" />);

expect(screen.getByRole('searchbox', { name: 'Search runs' })).toHaveValue(
  'customer',
);
fireEvent.change(screen.getByRole('searchbox', { name: 'Search runs' }), {
  target: { value: 'invoice' },
});
await waitFor(() =>
  expect(history.replace).toHaveBeenLastCalledWith(
    '/scopes/scope-alpha/workflow-activity-vnext/activity?q=invoice&status=failed&origin=draft&definition=definition-alpha&workflowFilter=unavailable',
  ),
);
expect(mockListRuns).toHaveBeenLastCalledWith('scope-alpha', {
  status: 'failed',
  origins: ['draft'],
  definitionActorIds: ['definition-alpha'],
  take: 100,
});
```

- [ ] Run RED and confirm failure is caused by the missing `q` restoration/serialization:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx --runInBand
```

- [ ] Initialize search from `initialParams`, restore it in the location effect, and serialize trimmed `q` alongside every existing parameter:

```tsx
const [search, setSearch] = React.useState(initialParams.get('q') ?? '');

// location.search effect
setSearch(params.get('q') ?? '');

// URL serialization effect
if (search.trim()) params.set('q', search.trim());
```

- [ ] Add `search` to the URL effect dependency list only. Keep the existing TanStack Query key and `workflowActivityApi.listRuns` payload unchanged so free text remains local filtering.

- [ ] Run GREEN:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx --runInBand
```

Expected: all selected Activity tests pass and the API assertion contains only status, origins, definitionActorIds, and take.

## Task 4: Align toolbar responsiveness

**Files:**

- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/activity/ActivityPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/styles.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`
- Test: `src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx`

- [ ] Wrap each page's resource-specific Select controls in `wa-vnext__toolbar-filters`, use the same `wa-vnext__toolbar-search` class on both search inputs, and remove divergent inline widths.

```tsx
<Input className="wa-vnext__toolbar-search" /* existing props */ />
<Space className="wa-vnext__toolbar-filters" wrap>
  {/* page-specific Select controls */}
</Space>
```

- [ ] Add stable dimensions and mobile full-width behavior without changing other vNext tables or the shell:

```css
.wa-vnext__toolbar-search { flex: 0 1 360px; max-width: 100%; width: 360px; }
.wa-vnext__toolbar-filters { justify-content: flex-end; }
.wa-vnext__toolbar-filters .ant-select { min-width: 160px; }

@media (max-width: 600px) {
  .wa-vnext__toolbar-search { flex-basis: auto; width: 100%; }
  .wa-vnext__toolbar-filters { display: grid; grid-template-columns: 1fr; width: 100%; }
  .wa-vnext__toolbar-filters .ant-select,
  .wa-vnext__toolbar-filters .ant-space-item,
  .wa-vnext__toolbar-filters .ant-btn { width: 100%; }
}
```

- [ ] Run the two page tests after the markup/style refactor:

```bash
pnpm exec jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx \
  --runInBand
```

Expected: both page test files pass.

## Task 5: Focused verification, browser evidence, and delivery

**Files:**

- Verify only the files changed by Tasks 1-4 and this plan.

- [ ] Run the exact selected test set:

```bash
pnpm exec jest \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx \
  src/locales/catalog.test.ts \
  --runInBand
```

- [ ] Run dependency-selected tests:

```bash
pnpm exec jest --findRelatedTests \
  src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx \
  src/pages/workflow-activity-vnext/activity/ActivityPage.tsx \
  src/pages/workflow-activity-vnext/styles.ts \
  src/locales/workflowActivityVNextMessages.en-US.ts \
  src/locales/workflowActivityVNextMessages.zh-CN.ts \
  --runInBand
```

- [ ] Compute the frontend-only change scope:

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . --base HEAD
```

- [ ] Run Biome only on the analyzer's exact changed frontend files, for example:

```bash
pnpm exec biome check \
  src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx \
  src/pages/workflow-activity-vnext/activity/ActivityPage.tsx \
  src/pages/workflow-activity-vnext/index.test.tsx \
  src/pages/workflow-activity-vnext/activity/ActivityPage.test.tsx \
  src/pages/workflow-activity-vnext/styles.ts \
  src/locales/workflowActivityVNextMessages.en-US.ts \
  src/locales/workflowActivityVNextMessages.zh-CN.ts
```

- [ ] Run whitespace validation:

```bash
git diff --check
```

- [ ] Do not run the full frontend test suite, package-wide lint, package-wide typecheck, or production build locally. Record that GitHub CI owns full verification under the machine-wide `frontend-incremental-pr` policy.

- [ ] Verify the already-running real frontend at `http://localhost:5173` without mock data at desktop `1440x900`, tablet `834x1112`, and mobile `390x844`. Capture screenshots proving the resource-specific filters, header Refresh placement, URL restoration, responsive full-width controls, and lack of horizontal overflow. Record any real remote API or authentication gap instead of synthesizing success.

- [ ] Review `git diff --stat`, `git diff`, and `git status --short`. Confirm every changed path is inside `apps/aevatar-console-web/`, no legacy route/auth/menu/API adapter changed, and no unsupported Workflow state or identity conversion was introduced.

- [ ] Stage only this task's files, commit with:

```bash
git commit -m "Align Workflow and Activity filters"
```

- [ ] Push `feat/2026-08-04_workflow-activity-vnext-implementation` and update Draft PR #3189 with exact focused commands/results plus the CI-delegated full checks. Keep PR #3189 Draft. Do not change PR #3187, enable auto-merge, or merge either PR.
