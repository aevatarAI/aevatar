# Workflow Edge Selection Visibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a selected workflow connection immediately distinguishable at fitted canvas zoom without changing normal edge semantics or workflow behavior.

**Architecture:** Keep selection decoration in the shared `GraphCanvas` so Team member Workflow Studio and Workflow Activity vNext continue to use one implementation. Decorate only the selected edge by strengthening its path and marker while preserving every unrelated edge property and restoring the original presentation on deselection.

**Tech Stack:** React 19, TypeScript, `@xyflow/react`, Jest, Testing Library, Biome

---

## File Structure

- Modify `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`: add focused coverage for selected and unselected edge presentation.
- Modify `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx`: strengthen the selected path and clone an object marker definition with the selected color.
- Update `docs/superpowers/plans/2026-08-10-workflow-edge-selection-visibility.md`: mark completed steps as implementation proceeds.

### Task 1: Lock the selected-edge visual contract

**Files:**
- Test: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`

- [ ] **Step 1: Add the failing component test**

Add this test inside the existing `describe('GraphCanvas', ...)` block:

```tsx
it('makes the selected edge visually distinct without changing other edges', () => {
  const styledEdges = [
    {
      ...edges[0],
      markerEnd: {
        color: '#2F6FEC',
        height: 11,
        type: 'arrowclosed',
        width: 11,
      },
      style: {
        opacity: 0.9,
        stroke: '#2F6FEC',
        strokeWidth: 2.5,
      },
    },
    {
      ...edges[0],
      id: 'edge:publish:archive:linear',
      markerEnd: {
        color: '#8B5CF6',
        height: 11,
        type: 'arrowclosed',
        width: 11,
      },
      source: 'step:publish',
      style: {
        stroke: '#8B5CF6',
        strokeWidth: 2.5,
      },
      target: 'step:archive',
    },
  ];

  render(
    <GraphCanvas
      edges={styledEdges}
      nodes={nodes}
      selectedEdgeId="edge:assert:publish:linear"
      variant="studio"
    />,
  );

  const reactFlowProps = mockReactFlowRender.mock.calls.at(-1)?.[0] as any;
  const selectedEdge = reactFlowProps.edges[0];
  const unselectedEdge = reactFlowProps.edges[1];

  expect(selectedEdge.selected).toBe(true);
  expect(selectedEdge.style).toEqual(
    expect.objectContaining({
      filter: 'drop-shadow(0 0 3px rgba(22, 119, 255, 0.55))',
      opacity: 0.9,
      stroke: 'var(--ant-color-primary)',
      strokeWidth: 4,
    }),
  );
  expect(selectedEdge.markerEnd).toEqual({
    color: '#1677ff',
    height: 11,
    type: 'arrowclosed',
    width: 11,
  });
  expect(unselectedEdge).toEqual(
    expect.objectContaining({
      markerEnd: styledEdges[1].markerEnd,
      selected: false,
      style: styledEdges[1].style,
    }),
  );
});
```

- [ ] **Step 2: Run the test and verify the visual contract fails**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/GraphCanvas.test.tsx
```

Expected: FAIL because the selected edge still has `strokeWidth: 3`, has no drop-shadow filter, and retains the original marker color.

### Task 2: Implement the shared selected-edge decoration

**Files:**
- Modify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx:573`
- Test: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`

- [ ] **Step 1: Add selected-edge presentation constants**

Place these constants beside the other `GraphCanvas` presentation constants:

```tsx
const SELECTED_EDGE_COLOR = '#1677ff';
const SELECTED_EDGE_FILTER =
  'drop-shadow(0 0 3px rgba(22, 119, 255, 0.55))';
const SELECTED_EDGE_STROKE_WIDTH = 4;
```

- [ ] **Step 2: Apply the stronger path and marker presentation**

Update the decorated edge returned from `localEdges.map(...)` so the selected edge uses the new constants and an object marker definition is cloned rather than mutated:

```tsx
return {
  ...edge,
  selected: isSelected,
  markerEnd:
    isSelected && edge.markerEnd && typeof edge.markerEnd === 'object'
      ? {
          ...edge.markerEnd,
          color: SELECTED_EDGE_COLOR,
        }
      : edge.markerEnd,
  style: {
    ...edge.style,
    filter: isSelected ? SELECTED_EDGE_FILTER : edge.style?.filter,
    stroke: isSelected
      ? 'var(--ant-color-primary)'
      : edge.style?.stroke,
    strokeWidth: isSelected
      ? SELECTED_EDGE_STROKE_WIDTH
      : (edge.style?.strokeWidth ?? 1.5),
  },
  labelStyle: {
    ...edge.labelStyle,
    fill: isSelected
      ? 'var(--ant-color-primary)'
      : edge.labelStyle?.fill,
  },
};
```

String marker references remain unchanged because their shared marker definition cannot be safely recolored from `GraphCanvas`.

- [ ] **Step 3: Run the focused test and verify it passes**

Run:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/GraphCanvas.test.tsx
```

Expected: PASS for the complete `GraphCanvas.test.tsx` suite, including the new selected-edge contract.

- [ ] **Step 4: Commit the test-driven implementation**

```bash
git add apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx \
  apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx
git commit -m "Improve workflow edge selection visibility"
```

### Task 3: Run focused frontend validation

**Files:**
- Verify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx`
- Verify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx`

- [ ] **Step 1: Analyze the affected frontend scope**

Run from the repository root:

```bash
python3 ~/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py \
  --repo . \
  --base origin/feat/2026-08-04_workflow-activity-vnext
```

Expected: `aevatar-console-web` is the affected package, the two graph files are listed for static checking, and the analyzer identifies Jest as the relevant runner.

- [ ] **Step 2: Run every dependency-related test reported by the analyzer**

Use the analyzer's exact dependency-related Jest paths and explicitly include:

```bash
pnpm --dir apps/aevatar-console-web jest --runInBand src/shared/graphs/GraphCanvas.test.tsx
```

Expected: all scoped suites pass. Do not substitute a full frontend test run.

- [ ] **Step 3: Run changed-file Biome checks**

```bash
pnpm --dir apps/aevatar-console-web exec biome check \
  src/shared/graphs/GraphCanvas.tsx \
  src/shared/graphs/GraphCanvas.test.tsx
```

Expected: both files pass. Do not run a local production build or full TypeScript check; GitHub CI owns those checks.

- [ ] **Step 4: Review the final diff**

```bash
git diff origin/feat/2026-08-04_workflow-activity-vnext...HEAD -- \
  apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx \
  apps/aevatar-console-web/src/shared/graphs/GraphCanvas.test.tsx \
  docs/superpowers/specs/2026-08-10-workflow-edge-selection-visibility-design.md \
  docs/superpowers/plans/2026-08-10-workflow-edge-selection-visibility.md
git diff --check
```

Expected: the diff contains only the agreed edge-selection presentation, its test, and planning documents, with no publish changes or whitespace errors.

### Task 4: Verify the authenticated editor and update PR #3276

**Files:**
- Browser verify: `apps/aevatar-console-web/src/shared/graphs/GraphCanvas.tsx`
- PR update: `https://github.com/aevatarAI/aevatar/pull/3276`

- [ ] **Step 1: Reuse the authenticated local editor**

Open the existing Chrome tab at:

```text
http://127.0.0.1:5174/scopes/ccb108c4-dcb3-473a-a0f7-e9859bb2f2a0/workflow-activity-vnext/workflows/e4c08548f56b473eb965c94df542463d
```

Expected: `weekly_report_five_nodes` renders with five nodes and four edges, without an authentication wall, startup failure, blank page, or initial API error.

- [ ] **Step 2: Select one edge and inspect the result**

Click one connection only. Do not invoke Save, Publish, Run, or Delete.

Expected: the selected path has a computed 4 px stroke, a visible blue drop shadow, a synchronized selected arrow marker, and is immediately distinguishable from adjacent unselected edges at the fitted zoom.

- [ ] **Step 3: Push the implementation commit**

```bash
git push origin HEAD:fix/2026-08-06_one-click-workflow-publish
```

Expected: PR #3276 updates without force-pushing.

- [ ] **Step 4: Update the PR verification evidence**

Record the exact focused Jest and Biome commands and results in PR #3276. State explicitly:

```markdown
- Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Do not wait for CI after the PR update unless the user requests CI monitoring.
