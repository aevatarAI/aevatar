# Workflow Canvas Scaling Design

## Context

Workflow Activity vNext renders its editor through the shared Studio Canvas:

```text
WorkflowEditorPage
  -> WorkflowStudioEditorSurface
  -> WorkflowStudioCanvasRegion
  -> WorkflowStudioCanvas
  -> GraphCanvas
  -> @xyflow/react
```

Run Detail and workflow template preview also render through `GraphCanvas`.
The current Canvas keeps drag positions local until drag stop, but it still
performs avoidable graph-wide work during node changes. Several props lose
referential stability, every Studio node subscribes to the continuous zoom
value, offscreen nodes remain mounted, and Run Detail rebuilds all graph
elements during ordinary page renders.

The work is based on
`origin/feat/2026-08-04_workflow-activity-vnext` at `4b2878e2`, with
`@xyflow/react@12.10.1`.

## Scope

This change will:

- preserve references for semantically unchanged nodes, node data, and edges;
- stabilize React Flow configuration, handlers, arrays, and component
  boundaries;
- reduce node updates caused by zooming;
- add a measured visible-element and level-of-detail policy for large graphs;
- avoid unnecessary automatic layout and repeated fit calculations;
- make initial fit, topology fit, new-node focus, manual navigation, and manual
  fit deterministic within the current Canvas session;
- stop Run Detail status and selection updates from rebuilding semantically
  unchanged graph elements;
- keep the shared Canvas correct for the vNext editor, Run Detail, and template
  preview;
- add repeatable scale regression coverage for 100, 500, and 1000 nodes and
  record the measurement environment and results.

## Explicit Non-Goals

Viewport persistence is deferred. This branch will not:

- save or restore viewport coordinates through the workflow layout contract;
- claim cross-reload, cross-session, or cross-device viewport restoration;
- add browser storage as a substitute for server authority;
- modify the backend persistence contract tracked by issues #3576 and #3577;
- change workflow YAML or execution semantics;
- replace React Flow with a different graph renderer.

Node-position persistence already present in the editor remains unchanged.
The Canvas may track whether the user has manually navigated during the current
mount solely to prevent disruptive automatic fits. That transient flag is not
workflow state and is not persisted.

## Design Principles

### Reference Stability

React Flow receives one stable node or edge object for each semantically
unchanged element. A node object changes only when its position, dimensions,
selection, or node data changes. An edge object changes only when its semantic
content or selection presentation changes.

`GraphCanvas` will no longer decorate every node and edge into a new object on
each relevant render. Pure reconciliation helpers will compare the incoming
element with the previously rendered element and reuse the previous object
when the semantic content is unchanged. Selection reconciliation will update
only the previously selected and newly selected objects. React Flow's
`applyNodeChanges` remains responsible for gesture-local changes and retains
the library's interaction semantics.

Callers will pass their memoized arrays directly. They will not use
`nodes={[...nodes]}` or `edges={[...edges]}`. `GraphCanvas` will accept readonly
arrays at its boundary and copy only when the React Flow state API requires an
owned array.

### Stable React Boundaries

The Studio node type table, fit options, React Flow options, MiniMap callback,
and invariant styles will be module-level constants. Event handlers whose
identity reaches React Flow will use `useCallback` and read the current values
through dependencies or narrowly scoped refs where required.

`StudioWorkflowNode`, `WorkflowStudioCanvas`, `WorkflowStudioCanvasRegion`, and
`WorkflowStudioEditorSurface` will have explicit memo boundaries. Memoization
is not used to hide unstable inputs: the caller arrays and callbacks must first
be stable.

### Zoom And Level Of Detail

Studio nodes need only know whether the current zoom is below the compact
threshold. Their React Flow store selector will return that boolean directly,
so intermediate wheel or pinch values that remain on the same side of the
threshold do not notify every mounted node.

The compact representation will omit the parameter summary and secondary role
detail. It retains the step identity, type/category, connection handles, and
execution status needed for navigation and diagnosis. Crossing the threshold
is the only zoom-driven custom-node render boundary.

### Visible Elements And MiniMap

The large-graph policy will be selected from benchmark evidence rather than
assumption. The initial candidate is React Flow's
`onlyRenderVisibleElements` for Studio graphs, because Studio nodes contain
substantially more content than the default graph nodes. Correctness coverage
will verify that selection, navigation, dragging, and nodes re-entering the
viewport continue to work.

The MiniMap stays available unless measurements show it dominates interaction
cost at a repeatable graph-size threshold. If a threshold is justified, the
Canvas will apply it consistently and keep Controls available for navigation.
The threshold and evidence will be recorded in the benchmark report rather
than embedded as an unexplained constant.

### Automatic Layout And Viewport Movement

`buildStudioGraphElements` will inspect sanitized saved positions before
running automatic layout. If every current step has a valid saved position,
automatic layout is skipped. Partial or absent layouts retain the existing
deterministic fallback behavior and never move valid saved positions.

Canvas movement follows this precedence within one mount:

1. The first non-empty topology receives one measurement-aware initial fit.
2. A newly added node may receive one focused reveal if it is outside the
   current viewport.
3. A topology replacement before manual navigation may receive one topology
   fit.
4. Once the user pans or zooms, later topology changes do not automatically
   move the established viewport.
5. The React Flow Controls fit action remains an explicit user command and is
   always allowed.

The current three-attempt animation-frame fit loop will be removed. Fitting
will wait for the relevant nodes to be initialized and then run once for the
specific reason. Selection, position-only changes, and execution-status
changes do not trigger fit.

### Run Detail Graph Updates

Run Detail will separate graph topology from selection. Selection remains the
`selectedNodeId` input to `GraphCanvas` and will not be copied into every
execution node's data.

Execution graph derivation will be memoized and reconciled by stable node and
edge identity. A status update creates a new data and node object only for the
affected step. Unchanged steps and all unchanged edges retain their previous
references. The ordered step list remains derived from the authoritative run
detail and is not turned into a second source of truth.

## Benchmark Design

A repository-owned benchmark fixture will generate representative Studio node
content and deterministic edges for 100, 500, and 1000 nodes. It will exercise:

- initial mount and first fit;
- a single ungrouped-node drag;
- selection from one node to another;
- pan and zoom without and with a detail-threshold crossing;
- one execution-status update;
- one topology addition.

The browser harness will record custom-node render counts, affected object
reference counts, React commits where the production profiling runtime exposes
them, long tasks or interaction frame timing, and browser heap measurements
where Chromium exposes them. It will record browser version, build mode,
hardware/runner description, graph size, and edge count with every result.

The personal frontend validation policy forbids a local production build for
this request. Local work will therefore verify the fixture and instrumentation
with focused tests. The production build benchmark will run in GitHub CI and
write its result as an artifact; the PR will distinguish locally observed
development evidence from CI-owned production evidence.

## Error Handling

Performance helpers are pure and fail closed: invalid graph elements are left
to existing React Flow validation, while missing optional benchmark metrics are
reported as unavailable instead of fabricated. Benchmark failures include the
scenario and graph size that failed.

Canvas interaction callbacks keep their current behavior. A failed parent
delete leaves local graph state unchanged. A failed layout callback does not
introduce a second local persistence mechanism.

## Verification

Focused regression tests will prove:

- dragging one node preserves unaffected node and data references;
- selection changes preserve every unaffected node and edge reference;
- stable parent rerenders keep React Flow arrays and configuration stable;
- zoom updates within one detail band do not rerender every Studio node;
- crossing the compact threshold changes the rendered detail intentionally;
- the selected visible-element policy is passed to React Flow and preserves
  viewport entry/exit behavior;
- complete saved node positions skip automatic layout, while partial and
  missing layouts retain deterministic placement;
- automatic fit runs once for an eligible reason and does not run after manual
  navigation for selection, status, or ordinary topology changes;
- Run Detail status updates preserve unaffected node/data/edge references;
- template preview and Run Detail retain their existing functional behavior;
- 100, 500, and 1000 node fixtures execute the benchmark scenarios and produce
  schema-valid measurement output.

Local validation will consist only of directly related Jest files,
changed-file Biome checks, the repository test-stability guard when tests are
modified, and any repository-native affected typecheck reported by the scope
analyzer. The full frontend suite, full typecheck, production build, and
production benchmark remain GitHub CI responsibilities.

## Delivery Boundary

The pull request will reference #3578 and explicitly list the viewport
persistence acceptance items as deferred on #3576 and #3577. It will not mark
those items complete or describe the issue as fully closed until the
authoritative server contract exists.
