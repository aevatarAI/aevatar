# Workflow Archive Design

**Date:** 2026-08-06
**Status:** Approved for implementation

## Problem

The Workflow catalogue overflow menu exposes only draft maintenance actions and
`Copy workflow reference`. A published Workflow without an editable draft
therefore has no lifecycle action at all. Users expect to be able to archive a
published Workflow without deleting its authoring source, published revisions,
or Activity history.

`Delete draft` cannot satisfy that expectation. It removes only the editable
workspace document and deliberately leaves the published Workflow runnable.

## Product Semantics

Archive is the product-level presentation of deactivating the Workflow's active
published deployment:

- archiving stops new runs through the published Workflow;
- the editable draft, published revisions, and Activity history remain;
- publishing the Workflow again restores an active published deployment;
- draft-only Workflows continue to use `Delete draft` and do not expose
  `Archive`;
- an already archived Workflow does not expose a second Archive action.

This is a boundary mapping, not a new global Workflow lifecycle. The backend's
deployment state remains authoritative. The frontend must never persist a local
archived flag or report success from command acceptance alone.

## Catalogue Views And Status

The existing default `All workflows` label becomes `Active workflows`, because
archived Workflows are intentionally excluded from that view. The selector has
three values:

- **Active workflows:** draft-only Workflows and published Workflows whose
  deployment is not deactivated;
- **Drafts:** every Workflow with an editable draft, including a draft whose
  previously published deployment is archived;
- **Archived:** committed Workflows whose authoritative deployment status is
  `Deactivated`.

The URL omits the default active view, keeps `view=drafts`, and adds
`view=archived`. Unknown view values fall back to active.

Status presentation follows runtime truth. `Deactivated` maps to `Archived`
before the existing Published/Draft rules, so an archived published Workflow
with a remaining draft is still visibly archived.

## Archive Action

`Archive` appears in the overflow menu only when all of these facts are true:

- the row has a committed source;
- it has an active revision;
- it has a non-empty deployment ID;
- its normalized deployment status is `Active`.

The menu action uses an archive icon, is visually separated from ordinary
actions, and opens a confirmation dialog. The dialog states that new runs stop
while drafts, published revisions, and Activity history remain. The confirming
button is explicit and visually cautionary.

Draft rows retain `Rename` and `Delete draft`. All rows retain
`Copy workflow reference`.

## Data Flow

1. The user confirms Archive.
2. The frontend calls the existing service deployment deactivation endpoint
   with `serviceId = workflowId`, the row's exact `deploymentId`,
   `tenantId = scopeId`, and the canonical scope service app/namespace.
3. A `202 Accepted` receipt means only that the command entered dispatch. The
   UI enters an observing state; it does not close the dialog or show success.
4. A bounded observer rereads the scope Workflow list and matches the exact
   `workflowId`. Success requires its normalized `deploymentStatus` to become
   `Deactivated`.
5. Once observed, the authoritative committed query is refreshed, the dialog
   closes, and the UI reports `Workflow archived`.

The observer follows the existing Workflow publication/materialization pattern:
deterministic delay injection for tests, a small bounded delay schedule in the
browser, and no query-time mutation or locally invented state.

## Failure Handling

- If the deactivation request fails before acceptance, keep the dialog open,
  report that the archive request failed, and let `Try again` resubmit it.
- If the request is accepted but the read model is not observed within the
  bounded window, keep the dialog open and report that archival is still being
  confirmed. `Check again` repeats only observation and must not submit a second
  deactivation command.
- Unauthorized and forbidden responses use the same non-technical Workflow
  error surface as the existing catalogue actions.
- Refresh or partial-list failures do not optimistically remove the row.

## Implementation Boundaries

- Add a focused archival helper beside the vNext Workflow surface for status
  normalization, action eligibility, and accepted-command observation.
- Reuse `servicesApi.deactivateDeployment`; do not add a duplicate HTTP client
  or a frontend-only archive store.
- Extend `WorkflowRow` only with the committed deployment facts required by the
  action and status.
- Keep all changes within Workflow Activity vNext, its locale catalogues, and
  focused tests. No backend contract or protobuf change is needed.

## Verification

Focused tests must prove:

- an active published row exposes Archive while draft-only and archived rows do
  not;
- confirmation calls deactivation with distinct Workflow, deployment, and
  scope identities;
- command acceptance alone does not show success;
- observed `Deactivated` state closes the dialog and moves the row from Active
  to Archived;
- delayed observation can be retried without resubmitting the command;
- draft deletion semantics remain unchanged;
- `view=archived` is preserved in navigation and stale/unknown views fall back
  to Active.

Browser verification must cover the overflow menu, confirmation dialog, active
and archived filters, and the no-overlap behavior of the table at desktop and
mobile widths. No destructive archive request will be submitted against the
user's real remote data during browser verification without separate explicit
authorization.
