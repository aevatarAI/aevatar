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

This design covers both the notification correction and the approved
publication model: users work only with Workflow resources, while each
Workflow owns one system-managed Team and one system-managed Member that remain
hidden from the product UI. Publication reuses the existing member binding-run
contract without changing backend endpoints.

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
7. Replace Workflow Activity vNext's workflow publication submission with the
   existing member binding-run publication orchestration.
8. Provision one hidden Team and one hidden workflow Member for every newly
   created Workflow, using only existing Team and Member endpoints.
9. Keep the hidden resource identities explicit and recoverable through typed
   member read-model fields.

This change will not:

- change any backend endpoint or response contract;
- expose Team or Member creation, selection, navigation, or terminology to the
  Workflow user;
- derive `memberId`, `workflowId`, or `publishedServiceId` from one another;
- change warning severity or make non-blocking warnings block Publish;
- add a new service-selection dialog;
- claim that an accepted binding command is already published.

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

### Resource Model

The user creates and operates only a Workflow. Internally, every Workflow owns
one distinct system-managed Team and one distinct system-managed workflow
Member:

```text
Workflow A -> hidden Team A -> hidden Member A -> Published Service A
Workflow B -> hidden Team B -> hidden Member B -> Published Service B
```

The hidden resources are not shared between Workflows. They do not appear as
creation steps, selectors, navigation destinations, labels, or settings in the
Workflow product surface.

- `workflowId` identifies the editable draft or definition.
- `teamId` identifies the hidden Team owned by exactly one Workflow.
- `memberId` identifies the hidden Member and is the binding authority.
- `publishedServiceId` identifies the callable published service.
- `revisionId` identifies one immutable publication attempt.

No frontend code may derive one identity from another by equality, prefix,
route position, service key parsing, or naming convention. Each ID comes from
its own existing API response.

### 1. Provision Hidden Authorities

Creating a Workflow remains one user command. The frontend orchestrates these
existing APIs behind that command:

1. Create the Workflow draft and receive `workflowId`.
2. Create a dedicated Team and receive `teamId`.
3. Create a workflow Member assigned to that Team and receive `memberId`.
4. Patch the Member's typed implementation reference with
   `implementationKind = workflow` and the exact `workflowId`.
5. Open the Workflow editor without exposing Team or Member concepts.

The typed `implementationRef.workflowId` is the durable lookup relationship.
Reloading or entering from the Workflow list resolves the backing Member by
that field, never by an ID convention. Provisioning retries must reuse an
already-linked Member instead of creating a second hidden resource pair.

### 2. Prepare The Exact Draft

When the user selects Publish:

1. Apply or explicitly reject unapplied node-inspector changes.
2. If the document is dirty, serialize, validate, and save it through the
   workflow draft API.
3. Block only on error-level findings and emit warning findings as warning
   toasts.
4. Publish the exact serialized bytes that were validated and saved.

### 3. Review Explicit Requests

Generate a fresh opaque `revisionId` and call the existing typed explicit
request preview endpoint with `scopeId`, `workflowId`, `revisionId`, and the
exact Workflow YAML.

- Continue immediately when there are no explicit requests.
- Reuse `confirmInteractiveExplicitRequestPreview` when confirmation is needed.
- Treat cancellation as idle, without an error toast.

### 4. Dispatch The Existing Member Binding Command

Resolve the Workflow's backing `memberId` and call:

```text
PUT /api/scopes/:scopeId/members/:memberId/binding
```

The body carries the draft identity separately:

```text
revisionId
workflow.workflowId
workflow.workflowYamls[]
explicitRequestConfirmations[]
```

Workflow Activity vNext must not call
`PUT /api/scopes/:scopeId/workflows/:workflowId` for Publish. The accepted
member binding response means only that the binding run was accepted for
dispatch; it does not mean publication succeeded.

### 5. Observe The Binding Run

Poll the exact run returned by the accepted receipt:

```text
GET /api/scopes/:scopeId/members/:memberId/binding-runs/:bindingRunId
```

- Active states remain `accepted`, `admission_pending`, `admitted`,
  `platform_binding_pending`, and `member_notification_pending`.
- `succeeded` triggers a member refetch, a success toast, and enables published
  actions from the returned `publishedServiceId`.
- `failed` or `rejected` triggers one error toast with the authoritative reason.
- Observation delay triggers a warning toast and `Check status`; it never
  dispatches another Publish command.

### 6. Retry And Recovery

- A refresh resolves the backing Member through typed
  `implementationRef.workflowId`.
- An accepted run is only observed; it is not resubmitted.
- A terminally failed run may be followed by a new attempt with a new
  `revisionId`.
- Partial hidden-resource provisioning is recoverable: retries reuse the Team
  and Member identities already returned or materialized for the Workflow.

### 7. Delete And Archive

Deleting a Workflow must also clean up its one-to-one hidden Member and Team
through existing endpoints. Cleanup uses the explicit resolved IDs and never
constructs one ID from another. A failed cleanup is reported as a toast and is
retryable so the product does not silently leave unreachable hidden resources.

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
