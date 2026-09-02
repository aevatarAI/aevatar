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

## Test Contract

The API regression fixture mirrors the reported v2 JSON: numeric enum values,
empty service grants, protobuf timestamp, null catalog authority, and null
owner LLM selection. Tests prove it decodes to a ready review and that invalid
requirement/grant combinations are rejected.

The page regression test proves a no-service review is rendered, remains a
two-step confirmation, and sends the create request only after confirmation.
It also proves the primary action is in the Modal footer rather than the
scrollable review body.

## Scope

This change does not alter backend evidence, infer whether a workflow should
require an LLM, change the dedicated Agent Key lifecycle, or add a compatibility
API. If a workflow that actually uses an owner LLM still produces a null
selection, its revision evidence remains a separate backend defect.
