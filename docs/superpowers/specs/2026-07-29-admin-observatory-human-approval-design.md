---
title: "Admin Observatory Human Approval Design"
status: approved
owner: eanzhao
---

# Admin Observatory Human Approval

## Problem

`POST /api/chat` can create an `auto_review` run that suspends on a
`human_approval` step. The run report already materializes the suspension type,
prompt, timeout, and source content from committed `WorkflowSuspendedEvent`
facts, but `ObservatoryStepDetail` drops those fields. The `/admin` run detail
therefore shows a generic `active_step` diagnostic and has no reliable contract
for rendering approval controls.

The product mismatch is: the page presents a normal operator decision as a
failure while offering no way to perform the decision required to continue the
run.

## Semantic Decision

An incomplete step whose committed suspension type is exactly
`human_approval` is an action-required state, not a failure. The run detail owns
the operator action because it already owns the run's current state, trace, and
next action.

Only the owner of the run's scope may approve or reject it. An administrator
viewing another scope may inspect the same committed facts but remains read-only;
cross-scope observability does not grant command authority.

## Scope

This change will:

- preserve committed suspension type, prompt, content, and timeout through the
  existing projection and Observatory read path;
- show an action-required approval panel for an active `human_approval` step;
- allow the owning scope to approve or reject from the run detail;
- send the decision through the existing scope-first run resume command;
- keep informational active-step diagnostics visually neutral and reserve
  failure treatment for warning/error or terminal failure facts.

This change will not:

- add a second approval command or an Observatory-specific mutation endpoint;
- restore the legacy `/api/workflows/resume` surface;
- let administrators approve runs owned by another scope;
- add edit-and-approve support; approval keeps the committed draft unchanged;
- infer approval state from a step id, display text, diagnostic message, or actor
  id string.

## Authoritative Data Flow

The existing command and event path remains the only business path:

1. `HumanApprovalModule` publishes a committed `WorkflowSuspendedEvent` with
   `suspension_type`, `prompt`, `content`, and `timeout_seconds`.
2. `WorkflowExecutionArtifactMaterializationSupport` copies those typed facts
   into `WorkflowExecutionStepTrace`. A new protobuf
   `suspension_content` field preserves the full reviewable content. If the
   suspension is marked secure, the projector writes an empty value. Other text
   continues through `WorkflowAuditTextSanitizer`.
3. `WorkflowExecutionReadModelMapper` and
   `WorkflowRunObservatoryQueryService` expose the fields on
   `ObservatoryStepDetail`. No metadata key or timeline text is parsed.
4. The page selects active approvals using both facts:
   `completedAtUtc` is absent and `suspensionType === "human_approval"`.
5. The owning user submits to
   `POST /api/scopes/:scopeId/runs/:runId:resume` with `stepId`, `approved`, and
   optional `userInput`. The UI omits `actorId`; the scope-first endpoint resolves
   the opaque run actor from the authoritative binding.
6. The existing command pipeline dispatches `WorkflowResumedEvent` to the run
   actor. The `202 Accepted` response means inbox admission only. The page shows
   a pending state and refreshes through its existing observation loop until the
   committed read model changes.

## Approval Interaction

The action-required panel appears above diagnostics and contains:

- `需要审批` status;
- the suspension prompt;
- the complete sanitized review content in a scrollable preformatted region;
- the timeout when present;
- primary `批准并继续` and secondary `驳回` actions for the owner scope.

Reject opens a small feedback field. Non-whitespace feedback is required before
submission. It is sent as `userInput`, which the existing
`HumanApprovalModule.ResolveFeedback` contract already treats as the feedback
fallback. Approve sends no replacement content, so the module continues with
the committed pending content unchanged.

While a request is in flight, both actions are disabled. After `202 Accepted`,
the panel states that the decision was accepted for dispatch; it does not claim
that the workflow has already resumed. A failed request preserves the review
content and rejection feedback and displays the returned error. Duplicate clicks
must not dispatch duplicate commands.

For an administrator viewing a foreign scope, the panel still explains the
pending decision and shows its review content, but it replaces the action buttons
with a read-only ownership notice. The server-side scope route remains the final
authorization boundary even when the UI hides the controls.

## Diagnostic Presentation

`INFO active_step` remains useful current-position evidence. It is rendered as a
neutral `当前位置` strip when there is no problem diagnostic. A red
`失败诊断` strip is used only when a diagnostic has `warning`/`error` severity
or the run is `failed`, `timed_out`, or `stopped`. The action-required panel has
higher prominence than diagnostics because operator action is the next step.

## Contract Changes

- Add `suspension_content` to protobuf `WorkflowExecutionStepTrace`.
- Add `SuspensionContent` to the internal run-report step model and its clone,
  sanitizer, and read-model mapper paths.
- Add `SuspensionType`, `SuspensionPrompt`, `SuspensionContent`, and
  `SuspensionTimeoutSeconds` to `ObservatoryStepDetail`.
- Map those fields directly in `WorkflowRunObservatoryQueryService`.
- Keep the existing scope-first resume request unchanged. No new API DTO or
  command abstraction is introduced.

## Security and Failure Handling

- The existing Observatory detail endpoint remains authenticated, confidential,
  and scope-gated.
- Suspension content is sanitized before storage and is never copied into logs,
  metadata, or error text. Secure suspensions never materialize content.
- Ownership is true only when `detail.summary.scopeId` equals the authenticated
  `/api/workflow/observatory/me` scope. Filter state and admin elevation do not
  grant approval rights.
- The scope-first resume endpoint independently validates the caller scope and
  resolves the run binding. Stale or already-resolved approvals return the
  existing typed error and do not cause a second continuation.
- A `202` response is presented as accepted, not committed. Projection lag is
  handled by the existing refresh loop rather than query-time priming or actor
  state reads.

## Verification

Automated checks will prove:

1. A committed non-secure `WorkflowSuspendedEvent.Content` is sanitized and
   materialized as `SuspensionContent`; a secure suspension materializes no
   content.
2. Observatory detail returns all typed suspension facts without parsing
   diagnostics or step names.
3. A running owner-scope `human_approval` renders full review content and approve
   and reject actions.
4. Approve posts `{ stepId, approved: true }` to the scope-first run route.
5. Reject requires feedback and posts it as `userInput` with
   `approved: false`.
6. A cross-scope admin view is read-only and sends no mutation.
7. `INFO active_step` is neutral, while real warning/error diagnostics retain
   failure treatment.
8. Focused projection, Application, Host/API, static-console UI checks,
   architecture guards, documentation lint, and test-stability guards pass.

The final manual check will open an owner-scope `auto_review` run in the console,
approve or reject it, and verify that the same run advances after the accepted
command becomes visible in the committed read model.
