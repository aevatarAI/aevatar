# Add Node Sequential Link Design

## Status

Approved for implementation on 2026-08-06.

Implementation branch: `fix/2026-08-06_add-node-sequential-link`.

Base branch: `feat/2026-08-04_workflow-activity-vnext` at
`121f0132f1191206aaffdaf71d189840c41d7c5e`.

## Problem

Workflow Activity vNext appends a selected node type by calling
`insertStepByType` without an insertion source. The helper therefore adds the
new step to the document array without updating a predecessor's `next` field.
The canvas renders only explicit `next` and `branches` edges, while the runtime
still permits adjacent steps to execute through implicit sequential ordering.

After insertion, the serialize endpoint validates the complete document and
returns `implicit_next` warnings for every non-terminal step that still relies
on array order. This makes the Add node action appear to have introduced an
error and leaves the newly added node visually disconnected.

## Product Contract

`Add node` extends the current executable flow; it does not create an
unconnected step by default.

- When a step is selected, insert the new step immediately after it.
- When no step is selected, append the new step after the final document step.
- If the selected step already has an explicit linear successor, preserve that
  successor as the inserted step's `next` target.
- Before insertion, materialize existing implicit sequential transitions that
  are already defined by adjacent document order: a non-final step with no
  `next` and no branches points to the following step.
- Do not synthesize a linear transition for a step that owns branches.
- Preserve every existing explicit `next` and branch target.
- Keep a truly empty workflow behavior unchanged: its first node has no
  predecessor and remains the terminal step.
- The inserted node becomes selected so its configuration surface opens as it
  does today.

## State Transition

Given this implicit document:

```text
create_weekly_report, transform_step
```

adding `assign_step` without a selected node produces:

```text
create_weekly_report.next = transform_step
transform_step.next = assign_step
assign_step.next = null
```

Given an explicit chain and a selected middle step:

```text
draft.next = review
review.next = publish
```

adding `assign_step` after `review` produces:

```text
draft.next = review
review.next = assign_step
assign_step.next = publish
```

## Implementation Boundary

Keep the change frontend-only.

1. Add a pure document helper that materializes only runtime-defined implicit
   sequential transitions without altering explicit or branched transitions.
2. Update the vNext editor insertion path to apply that helper and pass the
   selected step ID, or the final step ID when no step is selected, to
   `insertStepByType`.
3. Keep serialization as the server-authoritative validation boundary. Do not
   filter or suppress returned findings.
4. Do not change backend validation, runtime semantics, graph rendering, or
   the Team Member Workflow Studio behavior in this fix.

## Failure And Concurrency

The existing structural-mutation generation guard remains authoritative.
Materialization and insertion happen in memory before the single serialize
request. A failed or stale serialize response does not update the current
document. Retry repeats the same deterministic insertion intent against the
still-current document.

## Verification

Add focused coverage for:

- materializing adjacent implicit transitions while preserving explicit and
  branched transitions;
- adding a node with no selected step after the final step and sending a fully
  explicit linear chain to serialization;
- inserting after a selected middle step while preserving its former
  successor;
- keeping first-node insertion valid for an empty document.

Run only the changed test files and changed-file static checks. The complete
frontend suite, typecheck, and production build remain delegated to GitHub CI
under the personal incremental frontend policy.

## Design Baseline

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Authentication and localization:
  Existing Aevatar login, callback, session, returnTo, and Umi locale logic;
  presentation may change, behavior may not.
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
Baseline integrity:
  python3 apps/aevatar-console-web/docs/design-baselines/
  workflow-activity-vnext/verify-baseline.py
```
