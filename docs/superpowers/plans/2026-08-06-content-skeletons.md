# Console Content Skeletons Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace first-load blank, loading-copy, spinner, and premature-empty states on approved console primary surfaces with reusable structure-matching skeletons.

**Architecture:** Add one shared `AevatarContentSkeleton` with table, list, and canvas presets. Pages keep query ownership and existing error/empty branches; they render a preset only for initial loading without usable data, while background refetch keeps content visible.

**Tech Stack:** React 19, TypeScript, Ant Design 6 Skeleton/theme tokens, TanStack Query 5, Jest 29, Testing Library, Biome 2.

---

## File Map

- Create `apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.tsx`: semantic shared table/list/canvas skeleton presets.
- Create `apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.test.tsx`: shared contract and preset geometry tests.
- Modify `apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.tsx`: delegate loading to the shared table preset.
- Modify `apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.test.tsx`: assert structure skeleton loading semantics.
- Modify catalog pages and their tests: Workflows, Primitives, Topology actors, Services, Deployments, Runtime GAgents, and Governance.
- Modify `apps/aevatar-console-web/src/pages/studio/components/StudioFilesPage.tsx` and its test: tree skeleton for primary Files navigation.
- Modify Mission Wall stage, styles, and page test: dark canvas skeleton for initial runtime loading.

### Task 1: Shared Structure Skeletons

**Files:**
- Create: `apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.tsx`
- Create: `apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.test.tsx`
- Modify: `apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.tsx`
- Modify: `apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.test.tsx`

- [ ] **Step 1: Write a failing readiness table-skeleton test**

Update the existing readiness loading test to require a table preset, a hidden accessible label, no visible description, and no empty state:

```tsx
expect(screen.getByRole("status")).toHaveAttribute("aria-busy", "true");
expect(screen.getByRole("status")).toHaveAttribute("data-variant", "table");
expect(screen.getAllByTestId("aevatar-content-skeleton-row")).toHaveLength(4);
expect(screen.queryByText("Keep the current inventory visible until the request resolves.")).toBeNull();
expect(screen.queryByText("No inventory")).toBeNull();
```

- [ ] **Step 2: Run readiness test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/shared/ui/InventoryReadinessState.test.tsx
```

Expected: FAIL because readiness still renders visible loading prose and has no `data-variant="table"` contract.

- [ ] **Step 3: Implement the table preset and integrate readiness**

Create the shared component with the exported API from the design, implement its table renderer, and change only the readiness loading branch. Normalize row counts to at least one and table columns to a small default set. Use stable test IDs and theme tokens.

```tsx
if (kind === "loading") {
  return (
    <AevatarContentSkeleton
      ariaLabel={String(title)}
      columnWidths={[96, "1.6fr", "1fr", "1fr", 112]}
      rows={4}
      variant="table"
    />
  );
}
```

- [ ] **Step 4: Run readiness test and verify GREEN**

Run the Step 2 command again. Expected: PASS.

- [ ] **Step 5: Write failing direct list and canvas preset tests**

Create `AevatarContentSkeleton.test.tsx`. Confirm the already implemented table contract, then require list and canvas geometry that is not implemented yet:

```tsx
render(
  <AevatarContentSkeleton
    ariaLabel="Loading workflow catalog"
    columnWidths={[120, "2fr", "1fr"]}
    rows={3}
    variant="table"
  />,
);
expect(screen.getAllByTestId("aevatar-content-skeleton-row")).toHaveLength(3);
expect(screen.getAllByTestId("aevatar-content-skeleton-cell")).toHaveLength(9);

rerender(
  <AevatarContentSkeleton
    ariaLabel="Loading connectors"
    listLayout="grid"
    rows={4}
    variant="list"
  />,
);
expect(screen.getByRole("status")).toHaveAttribute("data-list-layout", "grid");

rerender(
  <AevatarContentSkeleton
    ariaLabel="Loading workflow runs"
    className="mission-wall-stage-skeleton"
    variant="canvas"
  />,
);
expect(screen.getByRole("status")).toHaveClass("mission-wall-stage-skeleton");
expect(screen.getAllByTestId("aevatar-content-skeleton-node").length).toBeGreaterThan(1);
```

- [ ] **Step 6: Run direct preset test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/shared/ui/AevatarContentSkeleton.test.tsx
```

Expected: FAIL because the component does not yet render list-layout metadata or canvas nodes.

- [ ] **Step 7: Implement list and canvas presets**

Implement the exported API exactly as designed. Normalize row counts to at least one and table columns to a small default set. Use stable test IDs and theme tokens:

```tsx
export type AevatarContentSkeletonVariant = "canvas" | "list" | "table";

export type AevatarContentSkeletonProps = {
  readonly ariaLabel: string;
  readonly className?: string;
  readonly columnWidths?: readonly (number | string)[];
  readonly listLayout?: "grid" | "stack" | "tree";
  readonly rows?: number;
  readonly style?: React.CSSProperties;
  readonly variant: AevatarContentSkeletonVariant;
};

return (
  <div
    aria-busy="true"
    className={className}
    data-list-layout={variant === "list" ? listLayout : undefined}
    data-variant={variant}
    role="status"
    style={rootStyle}
  >
    <span className="aevatar-loading-visually-hidden">{ariaLabel}</span>
    <div aria-hidden="true">{renderPreset()}</div>
  </div>
);
```

- [ ] **Step 8: Run both shared tests and verify GREEN**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/shared/ui/AevatarContentSkeleton.test.tsx \
  src/shared/ui/InventoryReadinessState.test.tsx
```

Expected: both test files PASS with no warnings.

- [ ] **Step 9: Commit the shared component batch**

```bash
git add apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.tsx \
  apps/aevatar-console-web/src/shared/ui/AevatarContentSkeleton.test.tsx \
  apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.tsx \
  apps/aevatar-console-web/src/shared/ui/InventoryReadinessState.test.tsx
git commit -m "Add shared content skeleton presets"
```

### Task 2: Primary Catalog Tables

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflows/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflows/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/actors/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/actors/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/services/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/Deployments/index.test.tsx`

- [ ] **Step 1: Write failing initial-loading tests**

Use an unresolved Promise in Workflows and Topology. Assert page chrome remains visible, the table skeleton is present, and empty copy is absent. Strengthen the existing Services and Deployments deferred-query tests to assert `data-variant="table"` and that visible loading descriptions are absent.

```tsx
expect(await screen.findByRole("status")).toHaveAttribute("data-variant", "table");
expect(screen.getByText("Find workflows")).toBeInTheDocument();
expect(screen.queryByText("No workflows matched the current filters.")).toBeNull();
```

For Topology, resolve the pending actor query to `[]` and assert the skeleton is replaced by the existing no-target state.

- [ ] **Step 2: Run tests and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/pages/workflows/index.test.tsx \
  src/pages/actors/index.test.tsx \
  src/pages/services/index.test.tsx \
  src/pages/Deployments/index.test.tsx
```

Expected: FAIL because Workflows renders loading text, Topology renders an empty state, and readiness does not yet expose the expected page behavior until Task 1 is integrated.

- [ ] **Step 3: Integrate table presets**

Insert this table preset before Workflows' current `filteredRows.length === 0` branch; preserve the current empty and populated branches without modification:

```tsx
{catalogQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.workflows.index.loading.workflow.catalog", "Loading workflow catalog")}
    columnWidths={["1.4fr", "1fr", "1fr", "1.2fr", 160]}
    rows={4}
    variant="table"
  />
) : filteredRows.length === 0 ? (
```

The line above must flow directly into the page's current `<Empty>` branch, followed by its current `<Table<WorkflowLibraryRow>>` branch.

Insert this branch before Topology's current `displayActors.length > 0` branch:

```tsx
{actorsQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.actors.index.reading", "Reading traceable objects")}
    columnWidths={["1.4fr", "1fr", "1fr", "1.2fr", 160]}
    rows={4}
    variant="table"
  />
) : displayActors.length > 0 ? (
```

Preserve the current actor table and no-target branches after that insertion. Do not alter Services or Deployments production pages beyond the shared readiness integration from Task 1.

- [ ] **Step 4: Run tests and verify GREEN**

Run the Task 2 command again. Expected: all four files PASS.

- [ ] **Step 5: Commit the catalog table batch**

```bash
git add apps/aevatar-console-web/src/pages/workflows/index.tsx \
  apps/aevatar-console-web/src/pages/workflows/index.test.tsx \
  apps/aevatar-console-web/src/pages/actors/index.tsx \
  apps/aevatar-console-web/src/pages/actors/index.test.tsx \
  apps/aevatar-console-web/src/pages/services/index.test.tsx \
  apps/aevatar-console-web/src/pages/Deployments/index.test.tsx
git commit -m "Add skeletons to primary catalog tables"
```

### Task 3: Primary Lists, Cards, and File Tree

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/primitives/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/primitives/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/gagents/index.tsx`
- Modify: `apps/aevatar-console-web/src/pages/gagents/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/pages/studio/components/StudioFilesPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/studio/components/StudioFilesPage.test.tsx`

- [ ] **Step 1: Write failing list-loading tests**

Use pending API promises and assert the correct list layout:

```tsx
expect(await screen.findByRole("status")).toHaveAttribute("data-variant", "list");
expect(screen.getByRole("status")).toHaveAttribute("data-list-layout", "grid");
expect(screen.queryByText("没有匹配的连接器")).toBeNull();
```

GAgents must assert both main inventories use stack skeletons while unresolved. Files must assert the workflow and script groups render tree skeletons instead of `Loading workflows...` or `Loading scripts...` text.

- [ ] **Step 2: Run tests and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand \
  src/pages/primitives/index.test.tsx \
  src/pages/gagents/index.test.tsx \
  src/pages/studio/components/StudioFilesPage.test.tsx
```

Expected: FAIL because these surfaces still render Empty components or visible loading copy.

- [ ] **Step 3: Integrate list presets**

Insert this grid list preset before Primitives' current `filteredRows.length === 0` branch; preserve the current empty and populated branches:

```tsx
{primitivesQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.primitives.index.loading.connectors", "Loading connectors")}
    listLayout="grid"
    rows={4}
    variant="list"
  />
) : filteredRows.length === 0 ? (
```

The line above must flow directly into the page's current `<Empty>` branch, followed by the current card catalog branch.

Insert the GAgent Kinds loading branch before the current empty result:

```tsx
{gAgentKindsQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel="Loading runtime GAgent kinds"
    listLayout="stack"
    rows={5}
    variant="list"
  />
) : filteredKinds.length === 0 ? (
```

Inside Actor Registry, keep query errors first, then insert:

```tsx
) : gAgentActorsQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel="Loading actor registry"
    listLayout="stack"
    rows={4}
    variant="list"
  />
) : actorGroups.length === 0 ? (
```

Inside the expanded `workflows/` folder, insert:

```tsx
{workflows.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.studio.studiofilespage.loading.workflows", "Loading workflows")}
    listLayout="tree"
    rows={3}
    variant="list"
  />
) : filteredWorkflows.length > 0 ? (
```

Inside the expanded, scoped `scripts/` folder, insert:

```tsx
{scripts.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.studio.studiofilespage.loading.scripts", "Loading scripts")}
    listLayout="tree"
    rows={3}
    variant="list"
  />
) : filteredScripts.length > 0 ? (
```

Preserve each existing populated and empty branch. Do not change detail/editor content loading.

- [ ] **Step 4: Run tests and verify GREEN**

Run the Task 3 command again. Expected: all three files PASS.

- [ ] **Step 5: Commit the list batch**

```bash
git add apps/aevatar-console-web/src/pages/primitives/index.tsx \
  apps/aevatar-console-web/src/pages/primitives/index.test.tsx \
  apps/aevatar-console-web/src/pages/gagents/index.tsx \
  apps/aevatar-console-web/src/pages/gagents/index.test.tsx \
  apps/aevatar-console-web/src/pages/studio/components/StudioFilesPage.tsx \
  apps/aevatar-console-web/src/pages/studio/components/StudioFilesPage.test.tsx
git commit -m "Add skeletons to primary resource lists"
```

### Task 4: Governance Catalog Tables

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/governance/components/GovernanceWorkbench.tsx`
- Modify: `apps/aevatar-console-web/src/pages/governance/index.test.tsx`

- [ ] **Step 1: Write failing policy, binding, and endpoint loading tests**

For each route view, leave the corresponding query pending and assert a table skeleton replaces loading copy while the view heading/action remains present:

```tsx
expect(await screen.findByRole("status")).toHaveAttribute("data-variant", "table");
expect(screen.queryByText("Loading policies...")).toBeNull();
expect(screen.getByRole("button", { name: "新建策略" })).toBeInTheDocument();
```

- [ ] **Step 2: Run the governance test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/governance/index.test.tsx
```

Expected: FAIL because Ant Design's empty slot still renders loading copy.

- [ ] **Step 3: Render explicit skeleton branches**

For each catalog view, branch before the real `Table`:

```tsx
{policiesQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.governance.governanceworkbench.copy.77", "Loading policies")}
    columnWidths={["1.4fr", "1fr", "1fr", 120]}
    rows={4}
    variant="table"
  />
) : (
  <Table<ServicePolicySnapshot>
    columns={policyTableColumns}
    dataSource={policiesQuery.data?.policies ?? []}
    locale={{
      emptyText: t(
        "pages.governance.governanceworkbench.copy.78",
        "This service has no governance policies yet.",
      ),
    }}
    pagination={{ pageSize: 8, showSizeChanger: false }}
    rowKey="policyId"
    size="middle"
  />
)}
```

Render bindings with its own loading contract:

```tsx
{bindingsQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.governance.governanceworkbench.copy.80", "Loading bindings")}
    columnWidths={["1.2fr", "1fr", "1.4fr", 120]}
    rows={4}
    variant="table"
  />
) : (
  <Table<ServiceBindingSnapshot>
    columns={bindingTableColumns}
    dataSource={bindingsQuery.data?.bindings ?? []}
    locale={{
      emptyText: t(
        "pages.governance.governanceworkbench.copy.81",
        "This service has no binding dependencies yet.",
      ),
    }}
    pagination={{ pageSize: 8, showSizeChanger: false }}
    rowKey="bindingId"
    size="middle"
  />
)}
```

Render endpoints with its own loading contract:

```tsx
{endpointsQuery.isLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.governance.governanceworkbench.copy.86", "Loading endpoint catalog")}
    columnWidths={["1.2fr", "1fr", "1.4fr", "1fr", 120]}
    rows={4}
    variant="table"
  />
) : (
  <Table<ServiceEndpointExposureSnapshot>
    columns={endpointTableColumns}
    dataSource={endpointsQuery.data?.endpoints ?? []}
    locale={{
      emptyText: t(
        "pages.governance.governanceworkbench.copy.87",
        "This service has no endpoint catalog yet.",
      ),
    }}
    pagination={{ pageSize: 8, showSizeChanger: false }}
    rowKey="endpointId"
    size="middle"
  />
)}
```

- [ ] **Step 4: Run governance test and verify GREEN**

Run the Task 4 command again. Expected: PASS.

- [ ] **Step 5: Commit the governance batch**

```bash
git add apps/aevatar-console-web/src/pages/governance/components/GovernanceWorkbench.tsx \
  apps/aevatar-console-web/src/pages/governance/index.test.tsx
git commit -m "Add skeletons to governance catalogs"
```

### Task 5: Mission Wall Canvas

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/MissionWall/components/MissionStage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/MissionWall/missionWallStyles.ts`
- Modify: `apps/aevatar-console-web/src/pages/MissionWall/index.test.tsx`

- [ ] **Step 1: Write a failing runtime-loading canvas test**

Keep the initial auth or snapshot request unresolved, render Mission Wall, and assert the primary stage exposes a canvas skeleton without the old visible loading title:

```tsx
expect(await screen.findByRole("status")).toHaveAttribute("data-variant", "canvas");
expect(screen.getByRole("status")).toHaveClass("mission-wall-stage-skeleton");
expect(screen.queryByText("Loading workflow runs")).toBeNull();
```

- [ ] **Step 2: Run Mission Wall test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/MissionWall/index.test.tsx
```

Expected: FAIL because the stage still renders its text state panel.

- [ ] **Step 3: Integrate and theme the canvas preset**

Separate loading from real empty states:

```tsx
{isRuntimeLoading ? (
  <AevatarContentSkeleton
    ariaLabel={t("pages.missionwall.state.loadingTitle", "Loading workflow runs")}
    className="mission-wall-stage-skeleton"
    variant="canvas"
  />
) : !focusRun || !graphHasNodes ? (
  <div className="mission-wall-state-panel">
    <div className="mission-wall-state-panel__kicker">
      {t("pages.missionwall.state.emptyKicker", "Waiting for runs")}
    </div>
    <div className="mission-wall-state-panel__title">
      {focusRun
        ? selectedPublishedWorkflowWithoutRun
          ? t("pages.missionwall.state.publishedWorkflowTitle", "No visible run")
          : t("pages.missionwall.state.auditPendingTitle", "No step flow for this run yet")
        : t("pages.missionwall.state.emptyTitle", "No published workflows are visible")}
    </div>
  </div>
) : (
  <WorkflowReplayCanvas graph={graph} />
)}
```

Add Mission Wall CSS that maps skeleton fills and borders to `--wall-panel-soft`, `--wall-line`, and `--wall-muted`, preserves stage height, and keeps the existing responsive breakpoints.

- [ ] **Step 4: Run Mission Wall test and verify GREEN**

Run the Task 5 command again. Expected: PASS.

- [ ] **Step 5: Commit the Mission Wall batch**

```bash
git add apps/aevatar-console-web/src/pages/MissionWall/components/MissionStage.tsx \
  apps/aevatar-console-web/src/pages/MissionWall/missionWallStyles.ts \
  apps/aevatar-console-web/src/pages/MissionWall/index.test.tsx
git commit -m "Add a Mission Wall loading skeleton"
```

### Task 6: Focused Verification and Pull Request

**Files:**
- Modify only if verification exposes a task-related defect.

- [ ] **Step 1: Run the frontend scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/dev
```

Record `affectedPackages`, `relatedTests`, and `staticCheckFiles`. Do not replace analyzer output with a full-suite fallback.

- [ ] **Step 2: Run all changed and related tests**

Run the union of the focused test commands from Tasks 1–5 plus any extra analyzer-reported related tests. Expected: PASS.

- [ ] **Step 3: Run changed-file static checks and stability guard**

```bash
pnpm --dir apps/aevatar-console-web exec biome lint \
  src/shared/ui/AevatarContentSkeleton.tsx \
  src/shared/ui/AevatarContentSkeleton.test.tsx \
  src/shared/ui/InventoryReadinessState.tsx \
  src/shared/ui/InventoryReadinessState.test.tsx \
  src/pages/workflows/index.tsx \
  src/pages/workflows/index.test.tsx \
  src/pages/actors/index.tsx \
  src/pages/actors/index.test.tsx \
  src/pages/services/index.test.tsx \
  src/pages/Deployments/index.test.tsx \
  src/pages/primitives/index.tsx \
  src/pages/primitives/index.test.tsx \
  src/pages/gagents/index.tsx \
  src/pages/gagents/index.test.tsx \
  src/pages/studio/components/StudioFilesPage.tsx \
  src/pages/studio/components/StudioFilesPage.test.tsx \
  src/pages/governance/components/GovernanceWorkbench.tsx \
  src/pages/governance/index.test.tsx \
  src/pages/MissionWall/components/MissionStage.tsx \
  src/pages/MissionWall/missionWallStyles.ts \
  src/pages/MissionWall/index.test.tsx
bash tools/ci/test_stability_guards.sh
```

If no affected typecheck target exists, skip local typechecking and record that GitHub CI owns it. Do not run a local production build.

- [ ] **Step 4: Browser-smoke intended loading states**

Start the console dev server only after confirming required configuration. Verify representative table, list, and Mission Wall canvas skeletons at desktop and mobile breakpoints. Do not hand off a URL if authentication or API startup prevents the intended screens from rendering; stop any server started only for a failed preview.

- [ ] **Step 5: Review and commit any verification-only fixes**

```bash
git diff --check
git status --short
git diff --stat origin/dev...HEAD
```

Stage only files listed in this plan and commit an imperative, single-purpose message if verification required changes.

- [ ] **Step 6: Push and create the pull request**

Push `feat/2026-08-06_content-skeletons` and create a PR targeting `dev`. Include problem/solution, affected paths, exact focused commands and results, and:

The PR `Local verification` section must list each focused Jest command from Tasks 1-5 with its observed PASS count, followed by the exact changed-file Biome command from Step 3 and the test-stability guard result. End the section with: `Full frontend suite/build: deferred to GitHub CI by personal local workflow policy`.

Do not babysit CI unless the user asks.
