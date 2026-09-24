# Workflow Published Invoke Design

## Context

Workflow Activity vNext currently presents `Run` as a normal editor action, but the button opens an inline panel below the full-height canvas and the submission calls `/workflow/draft-run`. The user sees no visible response at the top of the page, and the execution bypasses the service revision created by Publish.

The editor must treat Run as an invocation of a published workflow service. Draft identity, publication revision identity, and callable service identity remain separate:

- `workflowId` identifies the workspace workflow draft and definition.
- `revisionId` identifies the published service revision accepted by Publish.
- `publishedServiceId` identifies the service targeted by Invoke.

## Selected Approach

Keep the existing publication observation and Activity run observation, but connect them through a publication-aware run contract. Publish produces a receipt containing the three identities. The editor enables Run only after that exact receipt is observed as the active serving workflow revision. Run opens a fixed Ant Design Drawer and invokes the published service through the existing service-scoped chat Invoke API.

The editor records the local document version captured by the publication review. A local edit or a later save makes the current publication stale for the visible editor document. Run remains disabled until the latest saved workflow is published again, so the canvas and the executed revision cannot silently diverge.

## User Flow

1. A valid workflow can be saved and published to an explicitly selected service.
2. While publication is being reviewed, submitted, or observed, Run is disabled.
3. Once the selected revision is active serving, Run becomes enabled.
4. Clicking Run opens a right-side drawer immediately, focuses the Input field, and shows the exact published service and revision.
5. The user enters the required text input and selects Start run.
6. The client calls `/api/scopes/{scopeId}/services/{publishedServiceId}/invoke/chat:stream`.
7. SSE frames supply the stable run ID; the existing Activity read model remains authoritative for queued, running, completed, failed, and cancelled state.
8. Closing and reopening the drawer preserves the accepted run and its result.

## Enablement Rules

Run is disabled when any of the following is true:

- the workflow has not been published in the current editor session;
- publication has not reached `observed` active-serving state;
- the editor contains unapplied, unsaved, or saved-after-publication changes;
- the workflow is structurally invalid;
- a save or structural mutation is active.

The disabled button remains a real disabled control. Its title explains the first recovery action: publish first, wait for publication, apply/save/publish the latest changes, or wait for the current editor update. After a run is accepted, the toolbar Run action can reopen the same drawer and result, while Start run remains disabled until the current submission or unresolved observation finishes.

## Component Boundaries

`useWorkflowPublication` continues to prove that the accepted workflow, service, and revision match authoritative read models. `useWorkflowEditor` owns input, invocation submission, SSE identity extraction, and Activity observation, but accepts an explicit published invocation target rather than serializing YAML. `WorkflowPublishedRunDrawer` owns the accessible Drawer presentation and delegates all state transitions to the hook.

The vNext editor removes its Draft Run submission path. Other Draft Run consumers remain unchanged.

## Error Handling

- Empty input is rejected locally and never dispatched.
- Backend prompt field errors stay attached to Input without clearing its value.
- Invoke transport and API errors are shown in the run drawer; structured prompt validation remains attached to Input without replacing the backend message.
- A stream ending without a run ID remains an unresolved observation and cannot be submitted again accidentally.
- A route change aborts the previous stream and clears its publication target so an old service can never receive the new workflow's input.

## Verification

Focused UI tests prove that an unpublished workflow has a disabled Run button, an observed publication enables it, clicking it opens a Drawer, and submission calls `streamChat` with the exact `publishedServiceId` while never calling `streamDraftRun`. Existing run observation tests are updated to use the published Invoke contract. Changed-file Biome checks and dependency-related Jest tests provide local evidence; the full frontend suite, typecheck, and build remain delegated to GitHub CI.
