# Admin Observatory Graph Design

## Goal

Replace the `/admin` run observatory Graph tab's synthetic vertical list with an honest, interactive rendering of the graph returned by the workflow observatory API. Preserve the existing admin shell, run filters, scope controls, diagnostics, and polling behavior.

## Root Cause

`mapObsDetail` currently reduces the graph response to a node-only array. `obsDetail` then renders that array in response order and inserts a vertical connector between every adjacent pair. This discards the API's `edges`, invents relationships that may not exist, and cannot show branches. It also assigns `waiting` to every graph node without a matching step trace, which incorrectly presents run and actor topology nodes as pending after a completed run.

## Design

### Data Mapping

- Keep the API's `rootNodeId`, `nodes`, and `edges` in the mapped run detail.
- Derive each node's display status from committed step traces and the authoritative run status:
  - step nodes use their matching `stepId`;
  - run and actor topology nodes reflect the overall run status;
  - nodes without committed evidence remain pending.
- Keep incoming and outgoing edge counts as presentation-only values derived from the preserved edge list.
- When graph data is absent, fall back to step nodes and their explicit `nextStepId` relationships. Do not infer edges from array order.

### Layout and Rendering

- Render a dependency-layered DAG from the preserved edges. Desktop uses left-to-right layers; narrow viewports use top-to-bottom layers.
- Render `NEXT` edges as prominent solid arrows. Render containment and ownership edges as quieter dashed arrows. Suppress redundant containment edges when a `NEXT` edge already establishes the visible step flow.
- Defend against missing endpoints and cycles: ignore invalid edges, choose the API root or a stable first node when no root is available, and cap layout traversal.
- Use existing admin design tokens for colors, borders, status tones, typography, and reduced-motion behavior. Add no dependency.

### Interaction

- Support wheel zoom and explicit zoom-in/zoom-out controls.
- Support pointer drag to pan the graph without moving nodes.
- Provide fit-to-view and 1:1 controls. Auto-fit only on the graph's first visible render; preserve the user's transform during polling redraws.
- Make nodes keyboard-focusable buttons. Clicking or pressing a node opens a right-side detail panel; `Escape` and its close button dismiss it.
- The node panel shows status, type, full node ID, step ID when present, incoming/outgoing counts, and related committed timeline events. It makes no additional API request.
- Include a compact status legend and accessible labels for the graph, controls, nodes, and detail panel.

### State and Polling

- Store graph viewport state and selected node in `OBS_STATE`.
- Reset the graph viewport and node selection when the selected run changes.
- Preserve viewport state when the existing 3-second polling path refreshes the same run.
- Close the node panel when refreshed graph data no longer contains the selected node.

## Verification

- Add a static-asset regression test that verifies the admin graph retains and renders real edges, derives topology-node status from the run, exposes zoom/pan/fit controls, and renders node details.
- Run that test red before implementation and green after implementation.
- Run the affected test project, repository test-stability guard, frontend/static asset build checks applicable to this file, and the architecture guard.
- Launch the local host and visually verify a representative branched graph at desktop and narrow widths, including zoom, pan, fit, node selection, keyboard dismissal, and polling preservation.

## Scope Limits

- No backend contract change.
- No iframe replacement of the admin observatory.
- No new graph library or shared abstraction.
- No changes to other observatory tabs or admin navigation.
