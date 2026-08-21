---
title: "NyxID Connect Intent Typed Action"
date: 2026-07-31
status: approved
---

# NyxID Connect Intent Typed Action

## Goal

Make an ordinary request such as `我要连接 AWS Cost Explorer` reach the existing
`nyxid_require_service` path and return the existing schema-v4
`nyxid.action.request` rich card when the service is not connected.

## Root Cause

The persisted Agent Profile already classifies members with the strong typed
`AgentProfileSideEffectClass`, but `AgentTurnToolCatalogMaterializer` drops that
field when it builds classifier candidates. The streaming classifier therefore sees
only intent names and routing descriptions, and its instruction does not distinguish
the user's final requested outcome from an intermediate read-only catalog lookup. A
connect request can consequently select discovery or stop after `nyxid_catalog`,
return CLI instructions, and finish successfully without producing a typed action.

The downstream action contract is not broken: when `service_connect` is selected and
`nyxid_require_service` returns an authorization-required receipt, existing tests and
production canaries prove that Aevatar commits exactly one action, blocks the turn,
streams `CUSTOM nyxid.action.request`, and replays the blocked terminal on exact retry.

A second production failure occurs after the correct tool is selected. When live readiness
reports `Ready`, `NyxIdRequireServiceTool.CreateResultReceipt` returns `null` even
though the outcome is verified. The shared `ToolCallReceiptFinalizer` correctly treats a
missing provider receipt as unverified and replaces it with `tool_outcome_unknown`. The
tool therefore turns an already-connected service into the visible failure “The tool
outcome could not be verified.”

## Design

- Add the existing `AgentProfileSideEffectClass` to
  `AgentProfileTurnClassificationCandidate`; do not add a string bag or a second enum.
- Preserve each persisted member's side-effect class when materializing candidates.
- Serialize the enum as a readable snake-case classifier input field.
- Instruct the classifier to choose the intent that directly produces the user's final
  requested outcome, not a prerequisite discovery step. An `external_handoff` outcome
  takes precedence over `read_only` discovery when both could participate.
- Strengthen the NyxID Chat kernel invariant: catalog entries are connectable
  definitions, not connected inventory; after resolving the catalog slug for a
  connect/add/authorize outcome, the model must call `nyxid_require_service` and must
  not substitute CLI or credential instructions.
- Treat the user-facing service name as a candidate identity. Every connect/add/authorize
  turn must read the current NyxID catalog, and only the exact slug returned by that read
  may enter `nyxid_require_service.service_slug`. The readiness tool re-verifies that slug
  against the authoritative catalog before it can produce an authorization receipt.
- Requested scopes come from the same current catalog entry. A catalog entry with a scope
  menu cannot produce a connection card from an empty scope set; a bare source-code-hosting
  connection selects the entry's repository-access scope.
- Preserve the existing readiness branch semantics at the provider boundary:
  - `Ready` returns a typed `Success` receipt with no action;
  - `ServiceRegistrationRequired` returns `AuthorizationRequired`, producing the
    existing rich card and blocked terminal;
  - stale, malformed, or identity-mismatched results remain typed errors and fail closed.

## Boundaries

- Keep the persisted Agent Profile actor/read-model as the routing authority.
- Do not restore the deleted rollout profile files or configuration.
- Do not widen discovery intent tool policy or add language/provider/slug heuristics. Human
  names remain candidates until a current-turn catalog read resolves them.
- Do not create actions in an HTTP endpoint, infer actions from prose, execute a
  command twice, or change NyxID/NyxID Chat frontend code.
- Preserve current ready, blocked, failed, cancelled, pending-action, exact-retry,
  history, dispatch, and actor-state semantics.

## Acceptance

Focused tests must prove the strong typed side-effect survives Profile materialization,
the classifier request distinguishes final outcome from prerequisite discovery, and the
kernel forbids catalog/CLI substitution. A real `NyxIdRequireServiceTool` Ready result
must pass through `ToolCallReceiptFinalizer` as a non-synthetic `Success`, never
`tool_outcome_unknown`. The existing canonical profiled connect test must still produce
one schema-v4 `service.connect` action and a blocked terminal. After the new commit is
deployed, production acceptance must use only
`nyxid proxy request aevatar` and must observe
`nyxid_require_service -> nyxid.action.request -> RUN_FINISHED blocked` for natural
Chinese and English connect requests.
