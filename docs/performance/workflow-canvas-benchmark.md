# Workflow Canvas Benchmark

The workflow canvas benchmark measures the real Studio `GraphCanvas` in a
production Umi bundle without authentication or backend requests. The route is
registered only when `AEVATAR_WORKFLOW_CANVAS_BENCHMARK=1` is present during
config evaluation. Ordinary builds do not contain the route.

The benchmark keeps all state in the current page. It does not persist or
restore a viewport or any result through local storage, session storage, or a
server.

## Graph fixtures

The deterministic fixture sizes are exactly 100, 500, and 1000 nodes. Node IDs,
positions, Studio node data, and edges are stable across runs. Every node links
to its next node and the node after that when those targets exist. Therefore,
the fixture has `2N - 3` edges: 197, 997, and 1997 respectively.

Each node contains the complete `StudioGraphNodeData` contract, including its
step identity and type, role, parameter summary, branch count, execution state,
and focus state. Positions use a fixed 40-column grid and are finite. Initial
node dimensions are fixed at 268 by 120 pixels to provide deterministic geometry
before browser measurement completes. The topology scenario adds one node and
two edges while replacing only the branch source and new node references.

## Policies and scenarios

Every graph size runs under the full 2x2 policy matrix so MiniMap and visible
element rendering can each be compared while holding the other toggle fixed:

| Policy | MiniMap | Visible elements only |
| --- | --- | --- |
| `full-graph-with-minimap` | on | off |
| `full-graph-without-minimap` | off | off |
| `visible-elements-with-minimap` | on | on |
| `visible-elements-without-minimap` | off | on |

Each policy runs these scenarios:

- `initial-load`: navigation, initial graph commit, node measurement, and the
  first stable Studio fit.
- `drag`: a real pointer drag of a hit-tested Studio node, with its changed
  on-screen position verified.
- `selection`: real pointer clicks that select one hit-tested Studio node, then
  move selection to a second node and clear the first.
- `pan`: a real pointer pan from an unobstructed canvas point, with viewport
  translation verified and zoom held constant.
- `zoom-same-band`: a non-expanding control zoom that changes scale without
  crossing the compact-node threshold.
- `zoom-threshold`: control zooms that cross the compact-node threshold.
- `status-update`: one immutable execution-status update.
- `topology-add`: one node and two edges added with unaffected references
  retained.

## Result contract

The page runtime-validates each result before appending it to its typed in-page
store. The runner validates it again before writing artifacts. Each result has
exactly this shape:

```ts
{
  buildMode: 'production';
  browser: string;
  graph: { nodes: 100 | 500 | 1000; edges: number };
  policy: { minimap: boolean; visibleElementsOnly: boolean };
  scenario:
    | 'initial-load'
    | 'drag'
    | 'selection'
    | 'pan'
    | 'zoom-same-band'
    | 'zoom-threshold'
    | 'status-update'
    | 'topology-add';
  durationMs: number;
  longTasks: number;
  renderedNodeCount: number;
  changedNodeReferences: number;
  usedHeapBytes: number | null;
}
```

`renderedNodeCount` is the total number of Studio node commit callbacks invoked
during the scenario, including repeat commits for the same node.
`changedNodeReferences` records immutable graph reference changes: all fixture
nodes on initial load, two when selection moves between nodes, one for drag and
status update, two for topology add, and zero for pan and zoom. The drag
scenario commits the new position back through the same immutable owner-update
path used by product wrappers. A buffered Long Tasks observer provides
`longTasks`. Chromium's `performance.memory` is sampled when exposed and is
otherwise reported as `null`; unavailable values are never invented.

The JSON artifact envelope also records the runner OS, architecture, CPU model,
logical CPU count, total memory, production build commit when available, system
Chrome version, and user agent. React Profiler commit samples are included only
when the production runtime exposes them. The Markdown artifact states when
profiling is unavailable and reports `Complete: yes/no` plus the captured result
count as `Results: X/96`, including for partial failure artifacts.

CI gates schema validity, graph density, policy/scenario coverage, render-count
invariants, compact-band behavior, and exact reference-change counts. Timing,
long-task frequency, and heap values have no absolute pass/fail thresholds.

## CI and local commands

CI installs dependencies, runs the existing checks, and builds with the route
enabled:

```bash
pnpm --dir apps/aevatar-console-web install --frozen-lockfile
AEVATAR_WORKFLOW_CANVAS_BENCHMARK=1 pnpm --dir apps/aevatar-console-web build
pnpm --dir apps/aevatar-console-web benchmark:workflow-canvas
```

The benchmark command starts `max preview` on port 4174 and serves the already
built `apps/aevatar-console-web/dist` directory. It does not trigger another
build. Playwright uses the GitHub runner's installed system Chrome through
`channel: 'chrome'`; it does not download a browser.

Outputs are written to:

- `apps/aevatar-console-web/artifacts/workflow-canvas-benchmark/results.json`
- `apps/aevatar-console-web/artifacts/workflow-canvas-benchmark/summary.md`

CI uploads that directory as the `workflow-canvas-benchmark` artifact even when
the benchmark fails, so partial evidence remains available.

## Comparing runs

Durations, long tasks, and heap use vary with CPU contention, memory pressure,
Chrome version, runner image, and production bundle. Compare results only when
the build mode, browser/build identity, viewport, runner class, fixture size,
policy, and scenario match. Use repeated CI runs and distributions rather than
a single duration as a regression threshold. Reference-change and render-count
invariants are stable semantic signals and remain suitable for CI gates.

The local production build and browser benchmark were not run for this change.
The frontend incremental-validation policy delegates the full production build
and browser benchmark to GitHub CI.
