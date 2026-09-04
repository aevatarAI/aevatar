# Workflow Canvas Scaling Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scale the shared React Flow Canvas and its vNext editor, Run Detail, and template-preview consumers to 1000-node workflows without implementing viewport persistence.

**Architecture:** Keep React Flow as the interaction engine, but give it stable semantic element references and stable configuration props. Move graph reconciliation into pure helpers, keep drag state local, separate Run Detail selection from graph derivation, and make layout/fit work conditional on actual topology needs. Add an opt-in production benchmark route and Chromium runner so CI records render, timing, and memory evidence without shipping the route in ordinary builds.

**Tech Stack:** React 19, TypeScript, `@xyflow/react@12.10.1`, Jest/Testing Library, Biome, Playwright Test with system Chrome, GitHub Actions.

---

### Task 1: Stable Graph Element References

**Files:**
- Create: `apps/aevatar-console-web/src/shared/graphs/reconcileGraphElements.ts`
- Create: `apps/aevatar-console-web/src/shared/graphs/reconcileGraphElements.test.ts`

- [ ] **Step 1: Write failing reference-reconciliation tests**

Cover unchanged elements, a single changed node, a selection transition, a
single changed execution status, and unchanged edges. Use distinct IDs and
assert identity with `toBe`, for example:

```ts
const previous = createNodes(500);
const incoming = previous.map((node) =>
  node.id === 'step:250'
    ? { ...node, position: { x: 900, y: 320 } }
    : { ...node, data: { ...node.data } },
);

const next = reconcileGraphNodes(previous, incoming, 'step:10');

expect(next[250]).not.toBe(previous[250]);
expect(next[249]).toBe(previous[249]);
expect(next[251]).toBe(previous[251]);
expect(next[10]?.selected).toBe(true);
```

Test that a subsequent selection from `step:10` to `step:11` replaces only
those two node objects. Test edges with a status-only node update and require
every edge reference to remain identical.

- [ ] **Step 2: Run the new test and verify RED**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/reconcileGraphElements.test.ts
```

Expected: FAIL because `reconcileGraphNodes` and `reconcileGraphEdges` do not
exist.

- [ ] **Step 3: Implement semantic reconciliation**

Implement exported generic helpers with these contracts:

```ts
export function reconcileGraphNodes<NodeType extends Node>(
  previous: readonly NodeType[],
  incoming: readonly NodeType[],
  selectedNodeId?: string,
): NodeType[];

export function reconcileGraphEdges<EdgeType extends Edge>(
  previous: readonly EdgeType[],
  incoming: readonly EdgeType[],
): EdgeType[];
```

Index previous elements by ID. Compare node position and the shallow semantic
fields used by the repository, including a shallow comparison of `data`,
`style`, and measured/dimension fields while ignoring selection until the last
step. Reuse the previous node when semantic content matches. Set selection only
when its boolean value changes. Preserve input ordering, additions, and
removals, and return the previous array itself when every element and order is
unchanged.

Edges use the same rule for ID, endpoints, handles, type, label, data, style,
labelStyle, marker ends, hidden/animated/selectable/deletable, and z-index.
They return the previous array when no semantic edge changed.

- [ ] **Step 4: Run the new test and verify GREEN**

Run the Step 2 command. Expected: PASS with all reference assertions green.

- [ ] **Step 5: Commit the task**

```bash
git add apps/aevatar-console-web/src/shared/graphs/reconcileGraphElements.ts apps/aevatar-console-web/src/shared/graphs/reconcileGraphElements.test.ts
git commit -m "Preserve graph element references"
```

### Task 2: Shared Canvas Render And Interaction Boundaries

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx`
- Modify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`
- Use: `apps/aevatar-console-web/src/shared/graphs/reconcileGraphElements.ts`

- [ ] **Step 1: Add failing Canvas behavior tests**

Extend the React Flow mock so tests can feed node changes and zoom snapshots.
Add assertions that:

```ts
expect(firstProps.nodeTypes).toBe(secondProps.nodeTypes);
expect(firstProps.proOptions).toBe(secondProps.proOptions);
expect(firstProps.onNodesChange).toBe(secondProps.onNodesChange);
expect(firstProps.onlyRenderVisibleElements).toBe(true);
```

Drive one position change through `onNodesChange` and assert the untouched
nodes passed back to React Flow retain identity. Rerender with a new selection
and assert only old/new selected nodes change identity. Render the Studio node
against zoom snapshots `0.80`, `0.75`, and `0.40`; prove the selector result is
the compact boolean and that compact mode omits parameter and role details.

Mock `requestAnimationFrame`, `fitView`, and `onMoveStart`. Assert the first
non-empty topology schedules one fit, replacing a topology before a user move
schedules one new fit, and a later topology change after a non-null move event
does not schedule a fit. A newly selected added node is the explicit focus
exception. Selection-only and position-only rerenders never fit.

- [ ] **Step 2: Run the Canvas test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/GraphCanvas.test.tsx
```

Expected: FAIL on unstable props, missing culling/LOD behavior, recreated
references, and the three-attempt fit loop.

- [ ] **Step 3: Stabilize GraphCanvas**

Make `nodes` and `edges` readonly props. Initialize local state from owned
copies, then reconcile incoming Studio elements with Task 1 helpers. Keep
`applyNodeChanges` as the only drag-time node transform; do not run a second
full node-decoration pass on every drag.

Move these invariants to module scope:

```ts
const STUDIO_NODE_TYPES = { studioWorkflowNode: MemoStudioWorkflowNode };
const STUDIO_PRO_OPTIONS = { hideAttribution: true } as const;
const STUDIO_MINIMAP_NODE_COLOR = (node: Node) =>
  getStudioGraphCategory(
    (node.data as StudioGraphNodeData | undefined)?.stepType || '',
  ).color;
```

Wrap the Studio node in `React.memo`. Replace the continuous zoom selector
with:

```ts
const compact = useStore(
  (state) => state.transform[2] < STUDIO_NODE_COMPACT_ZOOM,
);
```

Do not render `.studio-workflow-node__body` in compact mode. Pass
`onlyRenderVisibleElements` for the Studio variant.

Use `useCallback` for React Flow handlers. Replace the three-attempt fit loop
with one animation-frame request per eligible topology reason. Track the last
topology key and whether a non-null viewport move event established a manual
viewport. Allow an added-and-selected node to receive explicit focus; otherwise
manual navigation suppresses later automatic topology fits. Keep Controls fit
available and do not introduce viewport storage or layout callbacks.

Retain existing default-variant selection presentation and Studio deletion,
connection, context-menu, and drag-stop semantics.

- [ ] **Step 4: Run the Canvas test and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the task**

```bash
git add apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx
git commit -m "Optimize shared workflow canvas rendering"
```

### Task 3: Studio Canvas Wrapper Stability

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.tsx`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvasRegion.tsx`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioEditorSurface.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Create: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.test.tsx`

- [ ] **Step 1: Add a failing wrapper stability test**

Mock `GraphCanvas`, render `WorkflowStudioCanvas` with stable readonly arrays,
rerender with an unrelated prop change, and assert:

```ts
expect(secondProps.nodes).toBe(nodes);
expect(secondProps.edges).toBe(edges);
expect(secondProps.autoFitKey).toBe(firstProps.autoFitKey);
```

Test that changing only selection does not change `autoFitKey`, while adding a
node does. Add an editor-page assertion that the callback props passed through
the wrapper remain stable across an unrelated state update.

- [ ] **Step 2: Run related wrapper tests and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.test.tsx src/pages/workflow-activity-vnext/index.test.tsx
```

Expected: FAIL because arrays, topology keys, and inline editor callbacks are
unstable.

- [ ] **Step 3: Stabilize wrapper props and memo boundaries**

Pass readonly `nodes` and `edges` directly to `GraphCanvas`. Memoize the
topology key from the node and edge arrays:

```ts
const autoFitKey = React.useMemo(
  () => JSON.stringify({
    edges: edges.map(({ id }) => id),
    nodes: nodes.map(({ id }) => id),
  }),
  [edges, nodes],
);
```

Memoize the empty-state node and wrap `WorkflowStudioCanvas`,
`WorkflowStudioCanvasRegion`, and `WorkflowStudioEditorSurface` with
`React.memo` without custom comparators.

In `WorkflowEditorPage`, convert callbacks that cross the editor-to-Canvas
boundary to `useCallback` with exact dependencies. Do not move editing state or
introduce an in-memory resource registry.

- [ ] **Step 4: Run related tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the task**

```bash
git add apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.tsx apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvasRegion.tsx apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioEditorSurface.tsx apps/aevatar-console-web/src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.test.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx
git commit -m "Stabilize workflow canvas wrappers"
```

### Task 4: Conditional Automatic Layout

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/studio/graph.ts`
- Modify: `apps/aevatar-console-web/src/shared/studio/graph.test.ts`

- [ ] **Step 1: Add failing complete and partial layout tests**

Export no test-only production API. Instead, use a Jest module spy on an
exported `buildAutoLayoutPositions` helper or extract the decision into an
exported pure `needsStudioAutoLayout` function. Prove:

```ts
expect(needsStudioAutoLayout(steps, completePositions)).toBe(false);
expect(needsStudioAutoLayout(steps, partialPositions)).toBe(true);
```

Also assert complete saved positions are returned exactly and partial saved
positions remain fixed while missing nodes receive deterministic, finite,
non-overlapping fallback positions.

- [ ] **Step 2: Run the graph helper test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/studio/graph.test.ts
```

Expected: FAIL because the auto-layout decision helper is absent and the
builder always calculates automatic positions.

- [ ] **Step 3: Short-circuit automatic layout**

Sanitize positions first. Implement:

```ts
export function needsStudioAutoLayout(
  steps: readonly StudioGraphStep[],
  savedPositions: Readonly<Record<string, XYPosition>>,
): boolean {
  return steps.some((step) => savedPositions[step.id] === undefined);
}
```

Call `buildAutoLayoutPositions` only when the helper returns `true`; otherwise
use an empty fallback map. Keep saved positions authoritative for known steps,
ignore unknown/invalid positions as today, and preserve deterministic legacy
and partial-layout behavior.

- [ ] **Step 4: Run the graph helper test and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the task**

```bash
git add apps/aevatar-console-web/src/shared/studio/graph.ts apps/aevatar-console-web/src/shared/studio/graph.test.ts
git commit -m "Skip unnecessary workflow auto layout"
```

### Task 5: Incremental Run Detail Graph Updates

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/executionGraph.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/executionGraph.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx`

- [ ] **Step 1: Add failing execution graph reference tests**

Move the pure execution graph builder and its presentation helpers into the new
module. Add fixtures with three distinct step IDs. Build once, change one
step's execution status, reconcile again, and assert:

```ts
expect(next.nodes[changedIndex]).not.toBe(previous.nodes[changedIndex]);
expect(next.nodes[unchangedIndex]).toBe(previous.nodes[unchangedIndex]);
expect(next.nodes[unchangedIndex]?.data).toBe(
  previous.nodes[unchangedIndex]?.data,
);
expect(next.edges[0]).toBe(previous.edges[0]);
```

Build again with only a different selected step and require all graph element
references to stay unchanged because selection is not graph data.

- [ ] **Step 2: Run execution and Run Detail tests and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-activity-vnext/activity/executionGraph.test.ts src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
```

Expected: FAIL because the incremental module is absent and the page builder
embeds selection in every rebuild.

- [ ] **Step 3: Extract and reconcile the execution graph**

Export a pure builder that does not accept `selectedStepId` and never writes
`executionFocused` from UI selection. Add:

```ts
export function reconcileExecutionGraph(
  previous: ExecutionGraphView | undefined,
  next: ExecutionGraphView,
): ExecutionGraphView;
```

Use Task 1 helpers to preserve node and edge references, and preserve the
ordered-step array when its member references and order are unchanged. In the
page, derive the raw graph with `useMemo`, retain the previous reconciled value
with a ref, and keep `selectedNodeId` as the only graph selection input.

Memoize the GraphCanvas callbacks. Preserve all current graph topology,
status labels, selected-step fallback, timeline, duration, and error behavior.

- [ ] **Step 4: Run execution and Run Detail tests and verify GREEN**

Run the Step 2 command. Expected: PASS.

- [ ] **Step 5: Commit the task**

```bash
git add apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/executionGraph.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/executionGraph.test.ts apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.tsx apps/aevatar-console-web/src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx
git commit -m "Reuse unchanged run graph elements"
```

### Task 6: Repeatable Production Scale Benchmark

**Files:**
- Modify: `apps/aevatar-console-web/package.json`
- Modify: `apps/aevatar-console-web/pnpm-lock.yaml`
- Modify: `apps/aevatar-console-web/config/config.ts`
- Modify: `apps/aevatar-console-web/config/routes.ts`
- Modify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx`
- Modify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`
- Create: `apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/index.tsx`
- Create: `apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/benchmarkGraph.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/benchmarkGraph.test.ts`
- Create: `apps/aevatar-console-web/playwright.performance.config.ts`
- Create: `apps/aevatar-console-web/performance/workflowCanvas.performance.spec.ts`
- Create: `docs/performance/workflow-canvas-benchmark.md`
- Modify: `.github/workflows/ci.yml`

- [ ] **Step 1: Add failing deterministic benchmark fixture tests**

Create deterministic 100/500/1000-node graph fixtures with two outgoing edges
per non-terminal node and full Studio node data. Test exact node/edge counts,
unique IDs, finite positions, representative content, and deterministic output.
Define and validate a result schema containing:

```ts
type WorkflowCanvasBenchmarkResult = {
  buildMode: 'production';
  browser: string;
  graph: { nodes: 100 | 500 | 1000; edges: number };
  policy: { minimap: boolean; visibleElementsOnly: boolean };
  scenario: 'initial-load' | 'drag' | 'selection' | 'pan' | 'zoom-same-band' |
    'zoom-threshold' | 'status-update' | 'topology-add';
  durationMs: number;
  longTasks: number;
  renderedNodeCount: number;
  changedNodeReferences: number;
  usedHeapBytes: number | null;
};
```

- [ ] **Step 2: Run the fixture test and verify RED**

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-canvas-benchmark/benchmarkGraph.test.ts
```

Expected: FAIL because the fixture and schema do not exist.

- [ ] **Step 3: Implement the opt-in benchmark surface**

Implement the fixture and a functional benchmark page that renders the real
Studio `GraphCanvas`. Gate the route at config evaluation time:

```ts
const workflowCanvasBenchmarkEnabled =
  process.env.AEVATAR_WORKFLOW_CANVAS_BENCHMARK === '1';
```

Ordinary builds must not register the route. The page accepts only the
supported graph sizes, exposes explicit scenario controls to the Playwright
runner, and compares visible-element rendering and MiniMap enabled/disabled
policies. Add optional `showMiniMap`, `onlyRenderVisibleElements`, and
`onStudioNodeRender` inputs to `GraphCanvas`; production consumers retain the
measured defaults, while the benchmark supplies explicit variants. The Studio
node calls the narrow instrumentation callback without putting benchmark state
in node data. The page observes long tasks, samples `performance.memory` when
available, and writes each result to a typed in-page result store exposed to
the runner. It must not call backend APIs or require authentication.

- [ ] **Step 4: Add the production Chromium runner and CI artifact**

Add `@playwright/test` as a pinned dev dependency. Configure the performance
project to use the GitHub runner's installed Chrome channel and the already
built Umi `dist` directory. Add scripts that do not rebuild during preview.

Extend the existing `console-web` job after its production build:

```yaml
- name: Benchmark workflow Canvas
  env:
    AEVATAR_WORKFLOW_CANVAS_BENCHMARK: '1'
  run: pnpm --dir apps/aevatar-console-web benchmark:workflow-canvas

- name: Upload workflow Canvas benchmark
  if: always()
  uses: actions/upload-artifact@v4
  with:
    name: workflow-canvas-benchmark
    path: apps/aevatar-console-web/artifacts/workflow-canvas-benchmark
```

Ensure the benchmark-enabled environment is present during the existing build,
not only during preview. The runner executes every scenario at 100, 500, and
1000 nodes and writes machine-readable JSON plus a concise Markdown summary.
Semantic reference assertions may gate CI; timing and heap values are recorded
as baselines and must not use flaky absolute thresholds.

- [ ] **Step 5: Document execution and evidence semantics**

Document the exact CI commands, graph density, scenarios, metrics, artifact
paths, runner hardware variability, Chrome/build identification, and how to
compare later results. State that local production build/benchmark execution
is intentionally deferred to GitHub CI by the personal frontend validation
policy. Do not fill unavailable local production numbers with estimates.

- [ ] **Step 6: Run focused fixture and configuration tests**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/pages/workflow-canvas-benchmark/benchmarkGraph.test.ts
```

Expected: PASS. Do not run the local production build or production browser
benchmark.

- [ ] **Step 7: Commit the task**

```bash
git add .github/workflows/ci.yml apps/aevatar-console-web/package.json apps/aevatar-console-web/pnpm-lock.yaml apps/aevatar-console-web/config/config.ts apps/aevatar-console-web/config/routes.ts apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/index.tsx apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/benchmarkGraph.ts apps/aevatar-console-web/src/pages/workflow-canvas-benchmark/benchmarkGraph.test.ts apps/aevatar-console-web/playwright.performance.config.ts apps/aevatar-console-web/performance/workflowCanvas.performance.spec.ts docs/performance/workflow-canvas-benchmark.md
git commit -m "Add workflow canvas scale benchmark"
```

### Task 7: Focused Validation And Pull Request Delivery

**Files:**
- Review all files changed since `4b2878e213efcacf3406cc36096dfdd2b86b5cbe`.

- [ ] **Step 1: Run the frontend change-scope analyzer**

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

Record every related test and `staticCheckFiles` entry. Do not substitute a
full frontend command.

- [ ] **Step 2: Run every directly changed and dependency-related test**

At minimum, run the directly changed test files in one explicit Jest command:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/reconcileGraphElements.test.ts src/shared/graphs/GraphCanvas.test.tsx src/shared/studio/graph.test.ts src/pages/team-member-workflow-studio/components/WorkflowStudioCanvas.test.tsx src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/activity/executionGraph.test.ts src/pages/workflow-activity-vnext/activity/RunDetailPage.test.tsx src/pages/workflow-activity-vnext/workflows/WorkflowTemplateBrowser.test.ts src/pages/workflow-canvas-benchmark/benchmarkGraph.test.ts
```

Add only analyzer-reported related tests not already listed. Do not run the
full suite.

- [ ] **Step 3: Run required guards and changed-file static checks**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/docs/lint.sh
pnpm --dir apps/aevatar-console-web biome:lint --write=false <analyzer staticCheckFiles>
git diff --check
```

Run a typecheck only if the analyzer reports a repository-native affected
target. Otherwise record that GitHub CI owns full typecheck. Never run a local
production build.

- [ ] **Step 4: Review behavior and complete a two-stage code review**

Review the full diff for scope, reference semantics, React Flow correctness,
benchmark route gating, frontend identity rules, and the viewport-persistence
non-goal. Request spec-compliance review first, then code-quality review. Fix
all Critical and Important findings and rerun only affected focused checks.

- [ ] **Step 5: Push and create the pull request**

Push `feat/2026-09-03_workflow-canvas-scaling` and create a PR targeting
`feat/2026-08-04_workflow-activity-vnext`. The PR body must include problem and
solution, affected paths, exact local commands/results, production benchmark
artifact instructions, and:

```markdown
## Deferred

- Viewport persistence and cross-session restoration remain tracked by #3576
  and #3577. This PR does not use browser-local state as authority.

## Local verification

- Related tests: `<actual focused commands and results>`
- Changed-file static checks: `<actual commands and results>`
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Do not wait for CI after reporting the PR URL.
