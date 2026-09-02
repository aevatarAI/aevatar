# Workflow Node Reselection Design

## Problem

The vNext workflow editor currently treats every node click as a request to
leave the active node configuration context. When the inspector contains
unapplied changes, clicking the already-selected node therefore opens the
discard confirmation even though no editing context would change.

This is a runtime and mental-model mismatch: users expect selecting the active
node to be idempotent, while the current event path treats it like a node
switch.

## Decision

Node reselection is a no-op at the vNext editor page boundary. When the clicked
node ID equals the editor's selected node ID, the page keeps the current
inspector and its unapplied configuration draft without opening a dialog.

The existing discard confirmation remains authoritative for real context
changes:

- selecting a different node;
- selecting the canvas;
- closing the inspector;
- switching to YAML mode;
- inserting another node;
- navigating away from the editor.

The selection boundary owns this decision because it has both the clicked node
ID and the current selected node ID. The shared graph canvas and the inspector
remain independent of route-specific selection policy.

## Scope

- Add an identity check to the vNext editor's node-selection handler.
- Add rendered interaction coverage for same-node reselection and different-node
  switching while node configuration is dirty.
- Preserve all existing save, run, publish, close, canvas, YAML, insertion, and
  navigation guards.
- Do not change shared canvas behavior, localization, API contracts, or backend
  code.

## Verification

The focused route integration test must prove both sides of the boundary:

1. Re-clicking the dirty active node does not open the discard dialog and does
   not change the inspector draft.
2. Clicking another node while dirty still opens the discard dialog, preserves
   the active draft on cancel, and switches only after explicit discard.

The exact test file is run directly. Shared locale files are not passed to
`--findRelatedTests`.
