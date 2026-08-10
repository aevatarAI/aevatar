# Shared Workflow Run Console Design

## Problem

Workflow Activity vNext introduced a separate published-run drawer even though
Team Member Workflow Studio already owned the product's run input panel and
execution Logs console. The duplicate drawer exposed publication identifiers,
used a different layout, and rendered a bespoke result summary instead of the
node/event console users already understand.

## Component Ownership

The reusable presentation components live under `shared/workflows`:

- `WorkflowRunInputPanel` owns the right-side run input layout, attachments,
  and primary run action. Its explicit `draft` and `published` variants share
  the same file selection, drag-and-drop, removal, and file-only execution
  behavior while keeping their labels and invocation ownership distinct.
- `WorkflowExecutionLogsPanel` owns the Logs header, Nodes/Events overview,
  node input/output details, copy actions, and clear action. It accepts a
  normalized execution model and does not know how execution facts were
  transported or stored.
- `WorkflowSidePanel` is the shared panel shell used by run and YAML surfaces.

The existing Studio components remain thin adapters so their public behavior
does not change. The vNext editor consumes the shared components directly.

## Data Contracts

Draft Studio and Published Run SSE frames are normalized into
`StudioExecutionDetail` and adapted with the same `buildExecutionTrace` path.
Published workflow history is adapted from `WorkflowActivityRunDetail` by
`adaptActivityRunToExecutionLogs`.

The current browser SSE session owns real-time status, steps, output, and
failure facts for the command the user just issued. Activity remains the
durable authoritative historical read model, but an eventually consistent
non-terminal Activity snapshot cannot overwrite fresher live completion facts.
Once Activity materializes a terminal run, its normalized trace replaces the
live session presentation.

The run action is busy only while the current SSE request is active, including
the interval after a terminal event arrives but before the stream closes. A
stale accepted, pending, running, or waiting Activity snapshot does not keep the
action disabled after that request ends.

Live terminal status requires an explicit `RUN_FINISHED`, `RUN_ERROR`, or
`RUN_STOPPED` fact. A clean stream end without one does not invent success.
Workflow definition membership is ordering context, not execution evidence:
nodes without matching runtime or Activity step facts are omitted rather than
labeled Pending.

The published invocation target remains the exact backend-provided
`publishedServiceId`. The editor does not display, derive, or interchange
`workflowId`, `revisionId`, and `publishedServiceId` in the run input surface.
Published runs without files use the JSON chat stream request. Runs with files
use the same service chat stream endpoint with multipart `payload` and `file`
parts; the transport change does not change the published service identity.

## Refresh And Clear Semantics

An observed non-terminal Activity run is refreshed while it remains accepted,
pending, running, or waiting. Terminal snapshots stop refreshing.

Clearing Logs only hides the current local console presentation. It does not
delete Activity history or mutate the authoritative run. Closing the run input
panel leaves Logs visible, and reopening the panel preserves the current input.

## Verification

Focused tests cover:

- preservation of Studio draft attachments and its existing Logs behavior;
- exact published-service invocation and input validation;
- absence of publication IDs from the shared run panel;
- release of the published run action when the current SSE stream ends;
- live completion facts taking precedence over stale non-terminal Activity;
- omission of definition nodes that have no execution facts;
- Activity step, output, and failure adaptation into the shared Logs console;
- refresh of non-terminal Activity runs and persistence of Logs when the input
  panel closes.
