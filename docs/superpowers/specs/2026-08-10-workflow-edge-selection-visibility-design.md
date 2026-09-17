# Workflow Edge Selection Visibility

## Context

Workflow Studio and Workflow Activity vNext both render editable workflow connections through the shared `GraphCanvas`. A normal linear connection is already rendered as a 2.5 px blue stroke. Selecting it currently changes the stroke to a similar blue and increases its width to only 3 px. At the fitted zoom used for workflows with several nodes, that 0.5 px difference is difficult to perceive. The arrow marker also keeps its normal appearance, so it does not reinforce the selection state.

The edge is selected correctly in application state and React Flow. This change therefore addresses only the shared visual presentation. It does not alter workflow editing, deletion, saving, publishing, or backend behavior.

## Design

Selected edges will retain the existing Ant Design primary color while receiving three coordinated cues:

- Increase the selected path stroke width to 4 px.
- Add a restrained blue drop shadow around the selected path so the state remains visible after canvas zooming.
- Update the arrow marker color to the same selected primary color.

Normal linear and branch edges will retain their existing semantic colors and widths. Selection will remain static rather than animated to avoid unnecessary motion and visual noise in an operational editor.

The styling will remain in the shared `GraphCanvas` edge decoration path so Team member Workflow Studio and Workflow Activity vNext use exactly the same behavior. No page-specific override or second edge component will be introduced.

## State Flow

The owning editor continues to provide `selectedEdgeId`. `GraphCanvas` compares that identifier with each rendered edge and decorates only the matching edge. Deselecting the edge restores the original edge style and marker configuration without mutating the source graph data.

## Error Handling

This is a deterministic presentation change and introduces no new asynchronous work or failure state. Existing editor error handling and toast behavior remain unchanged.

## Verification

Focused component coverage will verify that:

- the selected edge receives the stronger stroke and drop shadow;
- the selected arrow marker uses the selected color;
- unselected edges preserve their original style and marker color;
- selecting an edge still preserves its other edge configuration.

The existing authenticated local Workflow Activity vNext editor will then be used for a browser smoke check at its current fitted zoom. The selected connection must be immediately distinguishable from adjacent unselected connections without invoking Save, Publish, Run, or Delete.

## Scope Boundaries

- No publish code or publication contract changes.
- No workflow identity, routing, API, or backend changes.
- No animation or custom edge renderer.
- No changes to the meaning of linear and branch edge colors.
