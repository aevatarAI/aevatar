# Workflow Editor Toast And Publish Contract Design

## Context

Workflow Activity vNext currently renders publication, save materialization, and
workflow validation failures as full-width page alerts. The UI therefore makes
transient failures and non-blocking validation findings occupy the primary
editing surface. It also keeps retry actions inside those alerts, so hiding the
alerts without moving the recovery actions would make the workflow impossible
to recover.

The current vNext publication orchestration also differs from Team Member
Workflow Studio. Team Member Studio saves dirty drafts during Publish,
serializes and validates the exact document being published, previews explicit
requests, asks for confirmation when required, dispatches a member binding
command, and observes the authoritative binding run. Workflow Activity vNext
requires a separately saved draft and currently constructs explicit request
confirmations without using the shared confirmation helper.

This design separates the immediate UI correction from the later publication
contract decision.

## Semantic Mismatch

The UI implies that errors and warnings are persistent editor content, while
the product expects them to be transient notifications that do not resize or
displace the workflow canvas.

Classification:

- `placement`: errors and warnings occupy the primary editing surface.
- `runtime`: an accepted publish command is followed by an observation process,
  but the UI can present an observation delay as a publication failure.
- `contract`: `memberId`, `workflowId`, and `publishedServiceId` are separate
  resource identities, while a literal copy of the Team Member API call would
  cross those boundaries.
- `mental-model`: Publish should behave as one command with honest asynchronous
  status, not as a modal service-selection workflow or a permanent error panel.

## Scope For This Change

This change will:

1. Rebase the existing PR branch onto
   `origin/feat/2026-08-04_workflow-activity-vnext` at `cac625168` or its newer
   fetched descendant.
2. Adopt the latest shared `ConsoleToast` implementation from the base branch.
3. Remove full-width publication error alerts and save-materialization error
   alerts from Workflow Editor.
4. Remove inline workflow finding alerts, including non-blocking yellow
   warnings.
5. Present workflow errors as error toasts and workflow warnings as warning
   toasts.
6. Preserve recovery actions such as `Retry` and `Check again` inside the
   corresponding toast or an existing command/status control.
7. Keep the current Publish submission and observation implementation unchanged
   until the frontend and backend agree on the contract described below.

This change will not:

- switch Workflow Activity vNext to a member API;
- change the Publish endpoint or accepted response;
- add `publishedServiceId` to `ScopeWorkflowUpsertResult`;
- change warning severity or make non-blocking warnings block Publish;
- add a new service-selection dialog;
- claim that an accepted command is already published.

## Error And Warning Presentation

### Ownership

The editor canvas owns workflow content. Toasts own transient errors and
warnings. The toolbar status owns stable save and publication progress.

### Rules

- A failed command or query is shown with `ConsoleToast.error`.
- A validation finding with error severity is shown with
  `ConsoleToast.error`.
- A validation finding with warning severity is shown with
  `ConsoleToast.warning`.
- Findings are deduplicated by a stable key derived from code, path, severity,
  and message so rerenders do not create repeated notifications.
- A delayed asynchronous observation is a warning, not a failed publish.
- A successful save or publish may use an existing success toast or toolbar
  status; it does not need a full-width success alert.
- Retry actions move with the notification. Removing an alert must not remove
  the user's recovery path.
- Technical details may be included in the toast content when useful, but they
  must not create a permanent page region.

### Expected Editor Layout

The area between the editor toolbar and the canvas contains no publication,
materialization, validation-error, or validation-warning alert bands. The
canvas keeps a stable vertical position while toasts appear in the shared
top-right console notification surface.

## Correct Publish Product Flow

The following flow is the target contract for frontend/backend discussion. It
is not implemented by this change.

### Resource Identities

- `memberId` identifies Team Member authority. Only member endpoints accept it.
- `workflowId` identifies an editable workflow draft or definition. Workflow
  draft and workflow publication endpoints accept it.
- `publishedServiceId` identifies the callable published service. Service
  revision and invocation queries accept it.
- `revisionId` identifies one immutable publication attempt/artifact revision.
- `commandId` and `correlationId` trace a command; they are not resource IDs.

No frontend code may derive one identity from another by equality, prefix,
route position, service key parsing, or naming convention.

### 1. Prepare The Exact Draft

When the user selects Publish:

1. Apply or explicitly reject unapplied node-inspector changes.
2. Serialize the current document and validate the serialized result.
3. Block only on error-level findings. Emit warning findings as warning toasts.
4. If the document is dirty, save it through the workflow draft API.
5. Observe the exact saved draft/version before publishing, or let a single
   backend command atomically accept the saved content and publication intent.

The published bytes must be the same bytes that were validated and reviewed.

### 2. Review Explicit Requests

The frontend generates a fresh opaque `revisionId` candidate and calls the
typed explicit-request preview endpoint with `scopeId`, `workflowId`,
`revisionId`, and the exact workflow YAML.

- If there are no explicit requests, continue without a dialog.
- If explicit requests exist, reuse
  `confirmInteractiveExplicitRequestPreview`.
- If the user cancels, return to idle without an error toast.
- The backend must verify confirmations against the same workflow and revision;
  the frontend confirmation is not authority by itself.

### 3. Dispatch A Workflow Publish Command

Workflow Activity vNext dispatches a workflow publication command, not a member
binding command. A suggested request contract is:

```text
scopeId
workflowId
revisionId
workflowName
displayName
workflowYaml or savedDraftVersion
explicitRequestConfirmations[]
```

The response is an honest accepted receipt:

```text
acceptanceStage = accepted
scopeId
workflowId
revisionId
acceptedAtUtc
commandHandles[] {
  stage
  targetActorId
  commandId
  correlationId
}
readModelUrl
```

The accepted response means only that the command entered the target actor
inbox. It must not imply that the revision is committed, projected, serving, or
readable.

If the backend can authoritatively allocate `publishedServiceId` before
dispatch, it may return that opaque ID. The frontend must not require it in the
accepted receipt. The current `feature/integrate` contract does not return it.

### 4. Commit And Project Authoritative State

The workflow authority commits the publication state and emits the committed
fact into the standard projection pipeline. The projection materializes a typed
workflow publication read model containing at least:

```text
scopeId
workflowId
revisionId
publicationState
publishedServiceId
definitionActorId
failureCode/failureReason when terminally failed
authoritativeStateVersion
updatedAtUtc
```

Recommended states are `accepted`, `preparing`, `published`, and `failed`.
`accepted` may remain command-side only if the read model starts at
`preparing`.

The most direct API is either:

```text
GET /api/scopes/:scopeId/workflows/:workflowId
```

with a typed current publication sub-message, or:

```text
GET /api/scopes/:scopeId/workflows/:workflowId/publications/:revisionId
```

backed by the same workflow-owned current-state projection. The query must not
prime projections or replay the event store.

### 5. Observe Without Re-Publishing

After receiving an accepted receipt, the frontend polls or subscribes to the
exact `scopeId + workflowId + revisionId` publication state.

- `preparing`: show `Publishing` in the command/status control.
- `published`: store the typed `publishedServiceId`, show a success toast, and
  enable run/invoke only when the serving revision is available.
- `failed`: show one error toast with the authoritative failure reason and allow
  a new Publish attempt.
- observation timeout: show a warning toast and expose `Check status`; do not
  submit another publish command.
- `401/403`: show an authorization error toast and require the corresponding
  authentication or permission recovery.

If the workflow read model exposes only `publishedServiceId`, the frontend may
then query the exact service revision catalog to confirm that `revisionId` is
active and serving. A typed publication state on the workflow read model is
preferred because it avoids making the frontend infer one business state from
two independently delayed query replicas.

### 6. Idempotency And Retry

- Before an accepted receipt exists, the same `revisionId` and idempotency key
  may be retried according to the command contract.
- After an accepted receipt exists, `Check status` only re-observes; it never
  dispatches a duplicate Publish command.
- A new Publish attempt uses a new `revisionId` only after the prior attempt is
  terminally failed or the user has changed the saved document.
- Repeated projection writes are idempotent and older authoritative versions
  cannot overwrite newer ones.

## Why Team Member Publish Cannot Be Copied Literally

Team Member Studio publishes member authority by calling
`bindMemberWorkflow(scopeId, memberId, workflowId, ...)` and observing a member
binding run. Workflow Activity vNext edits a workflow resource and does not
have a canonical `memberId` in its route or draft contract. Calling the member
endpoint with `workflowId` would violate the repository identity rules.

The reusable part is the orchestration pattern:

```text
save exact draft
-> serialize and validate
-> preview explicit requests
-> obtain required confirmations
-> dispatch the correct resource command
-> observe authoritative status
-> toast success/failure
```

Only the resource-specific dispatch and observation adapters differ.

## Test Contract For The Immediate UI Change

Focused tests must prove:

- publication failure does not render a page-level alert;
- publication failure emits an error toast;
- delayed publication emits a warning toast and retains `Check again`;
- save materialization failure emits an error toast and retains `Try again`;
- error-level findings emit error toasts and are absent from the page flow;
- warning-level findings emit warning toasts and are absent from the page flow;
- repeated renders do not duplicate the same finding toast;
- the canvas and editor controls remain available after notifications;
- the existing Publish API request and observation calls are unchanged.

Only related Jest files, changed-file Biome checks, the frontend stability
guard, and baseline verification run locally. Full frontend test, typecheck,
and production build remain delegated to GitHub CI.
