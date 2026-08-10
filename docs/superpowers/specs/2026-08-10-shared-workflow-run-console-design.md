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

Draft Studio execution frames are adapted with `buildExecutionTrace`. Published
workflow runs are adapted from `WorkflowActivityRunDetail` by
`adaptActivityRunToExecutionLogs`.

The Activity read model remains the only durable source for published run
status, steps, output, failure, and usage. SSE is used only to discover the
stable run ID. While Activity materialization is pending, the console shows the
current workflow definition as pending nodes. Once the read model is observed,
the normalized Activity trace replaces that provisional presentation.

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
- pending definition nodes before Activity evidence exists;
- Activity step, output, and failure adaptation into the shared Logs console;
- refresh of non-terminal Activity runs and persistence of Logs when the input
  panel closes.
