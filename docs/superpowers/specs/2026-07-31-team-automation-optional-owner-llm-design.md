---
title: "Team Automation Optional Owner LLM Authorization"
status: approved
owner: AbigailDeng
---

# Team Automation Optional Owner LLM Authorization

## Goal

Allow the Team automation authorization flow to consume a valid scheduled
invocation v2 plan when the workflow needs no owner LLM route and no external
NyxID service grants. Preserve fail-closed decoding for malformed or broader
plans, and keep the explicit preflight, review, confirmation, and create flow.

## Verified Root Cause

The backend planner omits the protobuf `owner_llm_selection` message when the
workflow revision does not require an owner LLM route. For a workflow with no
external capabilities it also returns an empty `nyxIdServiceGrants` array and
`NOT_REQUIRED` service and node grant requirements. That is a successful,
deliberately supported plan.

The frontend decoder currently passes `ownerLlmSelection` unconditionally to
`expectRecord`. A JSON `null` therefore throws before the page can enter its
`reviewing` state. The create request is never sent because the user never
receives the second confirmation action.

After the create request is accepted, the scheduled-dispatch read model uses
`ownerLlmRouteKind = "unspecified"` and empty owner LLM route, service, slug,
and model strings for the same valid no-owner plan. The frontend list decoder
still requires a non-empty route and model, so it rejects the newly materialized
row and fails the entire automation list even though the backend returns an
active lifecycle status and authoritative state version.

The page then cannot match the accepted `scheduleId` to a decoded read-model
row. Its mutation observation remains open and drives a two-second polling
interval for up to 60 seconds. The long polling is therefore a frontend
consequence of the decoder mismatch, not evidence that the create command or
backend materialization failed.

## Contract Mapping

`TeamAutomationCredentialPlan` will expose both protobuf grant requirements as
the typed union `"required" | "not_required"`. Unknown, missing, or
`UNSPECIFIED` values continue to fail closed.

`TeamAutomationPermissionReview.ownerLLMSelection` will be the decoded
selection object or `null`. `null` means that this invocation plan does not
require an owner LLM route; it must not be replaced with a gateway, model, or
service inferred by the browser.

The decoder will keep these plan invariants:

- `serviceGrantRequirement = not_required` requires no service grants.
- `serviceGrantRequirement = required` requires at least one exact service
  grant.
- `nodeGrantRequirement` must agree with the exact per-service node grant
  requirements.
- a NyxID owner LLM selection must still match an exact service grant by
  `userServiceId`, route value, and optional slug snapshot.
- a gateway owner LLM selection remains valid without a service grant.
- an absent owner LLM selection is valid independently of whether non-LLM
  workflow capabilities require exact service grants.

The scheduled-dispatch read-model decoder will treat
`ownerLlmRouteKind = "unspecified"` with empty route, service, slug, and model
as the canonical no-owner runtime evidence. Gateway and NyxID user-service
routes must continue to carry the fields required by their route kind, and
unknown route kinds continue to fail closed.

## Authorization Review

When an owner LLM selection exists, the review keeps showing its service route
and model. When it is absent, the review says that no owner LLM model grant is
required. If the service grant list is also empty, it additionally states that
no external NyxID service grant is required. Exact service grant display names,
node IDs, and the `read`/`proxy` credential scopes remain visible without
inventing identities.

The confirmation actions move into the Ant Design Modal footer. During review,
the Modal body gets a viewport-bounded internal scroll area. This keeps Back
and Authorize and continue available when DevTools or a short viewport reduces
the visible height.

## Post-Create Visibility

An accepted create receipt provides a stable `scheduleId`, `operationId`, and
`commandId`, while the confirmed draft provides the display name, member, and
cadence. The page will use those values to render a clearly labeled transient
row immediately after acceptance. This row means only "command accepted;
waiting for authoritative read model" and must not claim that the automation is
active or committed at a particular state version.

When a decoded read-model row with the same `scheduleId` appears, it replaces
the transient row. The authoritative lifecycle status and state version remain
the only source for active, failed, or authorization-required status. If the
list request fails, the transient accepted row remains visible alongside the
error and manual recovery action instead of disappearing with the list.

## Bounded Refresh

Automatic refresh is scoped to a mutation accepted by the current page. The
page invalidates the list immediately, then may refetch at the existing
two-second interval for no more than approximately six seconds. A historical
or remotely created `provisioning_pending` row does not start background
polling by itself.

If the authoritative mutation result is still not visible at the deadline,
automatic requests stop. The page keeps the transient accepted row, explains
that automatic refresh stopped, and exposes a Refresh command. Refresh performs
one authoritative list request and does not restart another automatic polling
window. Navigating away naturally discards the page-local transient receipt.

## Test Contract

The API regression fixture mirrors the reported v2 JSON: numeric enum values,
empty service grants, protobuf timestamp, null catalog authority, and null
owner LLM selection. Tests prove it decodes to a ready review and that invalid
requirement/grant combinations are rejected.

The page regression test proves a no-service review is rendered, remains a
two-step confirmation, and sends the create request only after confirmation.
It also proves the primary action is in the Modal footer rather than the
scrollable review body.

A read-model regression fixture proves that the canonical `unspecified` owner
LLM evidence with empty route and model decodes successfully, while malformed
non-unspecified evidence remains rejected. Page tests prove that an accepted
create appears immediately, an authoritative row replaces the transient row,
automatic polling stops by the six-second deadline, historical pending rows do
not initiate polling, and manual Refresh performs one request without restarting
the polling window.

## Scope

This change does not alter backend evidence, infer whether a workflow should
require an LLM, change the dedicated Agent Key lifecycle, or add a compatibility
API. The transient accepted row is page-local presentation state, not a second
read model or browser-owned business fact. If a workflow that actually uses an
owner LLM still produces a null selection, its revision evidence remains a
separate backend defect.
