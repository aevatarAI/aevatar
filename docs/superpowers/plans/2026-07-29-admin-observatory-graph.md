# Admin Observatory Graph Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Render the `/admin` observatory Graph tab as the real interactive workflow DAG, with honest node state, zoom, pan, fit controls, and node inspection.

**Architecture:** Keep the implementation inside the existing embedded `admin.html`: preserve the API graph contract during detail mapping, derive presentation state from committed run evidence, render an edge-driven SVG/HTML DAG, and reuse the admin drawer for node details. Execute the production scripts in the existing Jest/JSDOM stack so regression tests assert observable mapping and interaction behavior.

**Tech Stack:** Embedded HTML/CSS/vanilla JavaScript, Jest, JSDOM, ASP.NET Core static asset endpoints, xUnit, FluentAssertions.

## Global Constraints

- Preserve the existing admin shell, filters, scope controls, diagnostics, and polling behavior.
- Use API `rootNodeId`, `nodes`, and `edges`; never infer graph edges from array order.
- Node state must come from committed step traces and the authoritative run status.
- Add no dependency, backend contract, iframe, or shared abstraction.
- Preserve keyboard access and reduced-motion behavior.
- Do not include unrelated working-tree changes.

---

### Task 1: Preserve Graph Semantics and Derive Honest State

**Files:**
- Create: `apps/aevatar-console-web/src/adminObservatoryGraph.test.ts`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: observatory graph payload `{ rootNodeId, nodes, edges }` and mapped run detail `{ status, rawStatus, steps, timeline }`.
- Produces: `obsMapGraph(graph, steps, runStatus)` returning `{ rootNodeId, nodes, edges }`, where each node has `st`, `incoming`, and `outgoing`.

- [x] **Step 1: Write the failing production-script behavior test**

Load `admin.html`, execute its real scripts in JSDOM without booting network activity, and map a hand-checked completed graph fixture:

```ts
const mapped = api.mapObsDetail(detail, graph).graph;

expect(mapped.rootNodeId).toBe('actor-root');
expect(mapped.edges).toEqual(graph.edges);
expect(mapped.nodes).toEqual(
  expect.arrayContaining([
    expect.objectContaining({
      nodeId: 'actor-root',
      st: 'success',
      incoming: 0,
      outgoing: 1,
    }),
  ]),
);
```

- [x] **Step 2: Run the focused test and confirm RED**

Run:

```bash
./apps/aevatar-console-web/node_modules/.bin/jest \
  --config apps/aevatar-console-web/jest.config.ts \
  --selectProjects jsdom \
  --runTestsByPath apps/aevatar-console-web/src/adminObservatoryGraph.test.ts \
  --runInBand
```

Observed: FAIL because the old mapped graph had no `rootNodeId` and discarded all edges.

- [x] **Step 3: Implement the minimum semantic mapping**

Implement `obsMapGraph` to validate endpoints, preserve explicit edges, derive topology state from the run, map step state by bare `stepId`, and derive incoming/outgoing counts. For missing graph data, create only explicit `nextStepId` relationships.

- [x] **Step 4: Run the focused test and confirm GREEN**

Run the command from Step 2. Observed: PASS.

### Task 2: Render and Operate the Interactive DAG

**Files:**
- Modify: `apps/aevatar-console-web/src/adminObservatoryGraph.test.ts`
- Modify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`

**Interfaces:**
- Consumes: `detail.graph` from Task 1 and `detail.events` for node inspection.
- Produces: `obsGraphView(detail)`, `obsBindGraph(root)`, and `obsOpenGraphNode(nodeId)`; viewport state lives in `OBS_STATE.graphView`.

- [x] **Step 1: Write the failing interaction behavior test**

Render a branched graph, bind the production interactions, and assert the two branches share a layer while zoom, pan, redraw preservation, narrow layout, and node inspection alter visible DOM state:

```ts
expect(successNode?.style.left).toBe(errorNode?.style.left);
expect(successNode?.style.top).not.toBe(errorNode?.style.top);
expect(root.querySelectorAll('path[data-edge-type="NEXT"]')).toHaveLength(2);

zoomIn.click();
expect(viewport.style.transform).not.toBe(fittedTransform);

planNode.click();
expect(document.querySelector('#drawer-root .drawer')).toHaveTextContent(
  'Evaluating branch',
);
```

- [x] **Step 2: Run the focused test and confirm RED**

Run the Task 1 test command. Observed: FAIL because `obsGraphView`, `obsBindGraph`, and `obsOpenGraphNode` did not exist.

- [x] **Step 3: Add the edge-driven renderer and styles**

Implement a bounded dependency-layer layout, desktop horizontal and narrow vertical orientation, SVG arrows, prominent `NEXT` edges, muted structural edges, focusable status cards, an empty state, and a compact legend using existing design tokens.

- [x] **Step 4: Add viewport interactions and node inspection**

Implement wheel/button zoom, pointer pan, fit-to-view, 1:1, polling redraw preservation, and an existing-drawer node inspector containing status, type, IDs, edge counts, and committed events.

- [x] **Step 5: Run both graph tests and confirm GREEN**

Run the Task 1 test command. Observed: both behavior tests PASS.

### Task 3: Verify and Deliver

**Files:**
- Verify: `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html`
- Verify: `apps/aevatar-console-web/src/adminObservatoryGraph.test.ts`

**Interfaces:**
- Consumes: completed Tasks 1-2.
- Produces: fresh test/build/guard evidence and a fast-forward push to `origin/feature/integrate`.

- [ ] **Step 1: Run frontend behavior and type checks**

```bash
./apps/aevatar-console-web/node_modules/.bin/jest \
  --config apps/aevatar-console-web/jest.config.ts \
  --selectProjects jsdom \
  --runTestsByPath apps/aevatar-console-web/src/adminObservatoryGraph.test.ts \
  --runInBand
pnpm --dir apps/aevatar-console-web tsc
```

- [ ] **Step 2: Run the existing embedded asset test class**

```bash
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj \
  --nologo --filter FullyQualifiedName~BackendConsoleStaticAssetEndpointTests
```

- [ ] **Step 3: Run mandatory guards and the affected build**

```bash
bash tools/ci/test_stability_guards.sh
bash tools/ci/architecture_guards.sh
dotnet build src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
```

- [ ] **Step 4: Perform browser verification**

Open the production asset with an authenticated or controlled graph fixture and verify desktop/narrow layouts, fit, zoom, wheel, pan, node drawer, Escape close, and polling redraw preservation. Capture a screenshot.

- [ ] **Step 5: Rebase, review the exact diff, and push**

```bash
git fetch origin feature/integrate
git rebase origin/feature/integrate
git diff --check origin/feature/integrate...HEAD
git status --short
git log --oneline origin/feature/integrate..HEAD
git push origin HEAD:feature/integrate
```
