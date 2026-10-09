# Workflow Activity vNext Scoped Publication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an operator publish a saved, valid vNext Workflow to one explicitly selected real scope service, then observe the accepted update through exact workflow and service-revision reads.

**Architecture:** A vNext-local publication hook owns the accepted receipt and bounded observation state. A focused dialog loads only real scope services, requires a deliberate service selection, and collects any real external-request confirmations before calling the existing typed `studioApi.saveAndBindWorkflow` adapter. The editor remains the owner of document validation and serialization; no Team/member API, inferred service identity, mock data, or browser-storage state is introduced.

**Tech Stack:** React 19, TypeScript, Ant Design, TanStack Query, existing Studio/scope-runtime adapters, Jest, Testing Library, Biome.

**Design baseline:**

```text
Design baseline:
  apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/
Primary design:
  aevatar-workflow-activity-vnext.excalidraw
Design SHA-256:
  30e74d7b410ae72c4c91432355436679033679c54c10b1702908435b001577de
Contract specification:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-design.md
User paths:
  apps/aevatar-console-web/docs/superpowers/specs/
  2026-08-04-workflow-activity-vnext-user-paths.md
Production data source:
  Real APIs and API-acknowledged user actions only; no mock fallback.
```

---

### Task 1: Receipt-Bound Publication Observation

**Files:**
- Create: `src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.ts`
- Create: `src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts`

- [ ] **Step 1: Write failing observation tests**

Cover these public behaviors with distinct fixtures:

```ts
it('observes only the accepted workflow, service, and revision once the active serving revision is published', async () => {
  const result = await observeWorkflowPublication({
    receipt: { scopeId: 'scope-alpha', workflowId: 'wf-alpha', revisionId: 'rev-alpha', serviceId: 'svc-alpha' },
    readWorkflow: async () => readableWorkflowDetail('scope-alpha', 'wf-alpha'),
    readRevisions: async () => revisionCatalog('scope-alpha', 'svc-alpha', publishedActiveRevision('rev-alpha')),
    delaysMs: [0],
  });

  expect(result.kind).toBe('observed');
});

it('treats receipt-bound workflow 404 and 409 plus a missing revision as observation delay without creating another publish request', async () => {
  const result = await observeWorkflowPublication({
    receipt: { scopeId: 'scope-alpha', workflowId: 'wf-alpha', revisionId: 'rev-alpha', serviceId: 'svc-alpha' },
    readWorkflow: async () => { throw httpStatusError(409); },
    readRevisions: async () => revisionCatalog('scope-alpha', 'svc-alpha', null),
    delaysMs: [0],
  });

  expect(result).toEqual({ kind: 'delayed' });
});
```

- [ ] **Step 2: Run the new observation test to verify it fails**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts
```

Expected: FAIL because `observeWorkflowPublication` and its receipt-bound observation behavior do not exist.

- [ ] **Step 3: Implement the minimal observation hook**

Export a `WorkflowPublicationReceipt` with only API-returned `scopeId`, `workflowId`, `revisionId`, and `serviceId`. `observeWorkflowPublication` must query the exact `scopesApi.getWorkflowDetail` and `scopeRuntimeApi.getServiceRevisions` identities in parallel on each bounded attempt. Return `observed` only when:

```ts
workflow.available === true &&
workflow.scopeId === receipt.scopeId &&
workflow.workflow?.workflowId === receipt.workflowId &&
catalog.scopeId === receipt.scopeId &&
catalog.serviceId === receipt.serviceId &&
catalog.activeServingRevisionId === receipt.revisionId &&
revision.revisionId === receipt.revisionId &&
revision.implementationKind === 'workflow' &&
normalize(revision.status) === 'published' &&
revision.isActiveServing &&
revision.isServingTarget &&
revision.allocationWeight > 0 &&
normalize(revision.servingState) === 'active'
```

Treat workflow `404`/`409`, service catalog `404`, and a catalog lacking the exact revision as eventual-consistency observation states. Map `401` and `403` distinctly. Treat `PreparationFailed`, a nonempty service failure reason, and `Retired` as terminal failure. The hook retry calls only the exact read functions and never calls `saveAndBindWorkflow`.

- [ ] **Step 4: Run the observation test to verify it passes**

Run the same Jest command from Step 2.

Expected: PASS with no timer-driven artificial success state.

### Task 2: Explicit Target Selection And External-Request Review

**Files:**
- Create: `src/pages/workflow-activity-vnext/workflows/WorkflowPublishDialog.tsx`
- Modify: `src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Write failing route-integration tests**

Add tests that render the real vNext editor through its route owner and prove:

```ts
it('requires an explicitly selected real scope service before publishing a saved workflow', async () => {
  // Open Publish, wait for real service rows, and assert the primary submit action is disabled.
  // Select 'Service alpha', submit, and assert saveAndBindWorkflow receives serviceId: 'svc-alpha'.
});

it('keeps publish accepted while the exact workflow and service revision are still being observed', async () => {
  // Return 202 data, hold or delay the exact GETs, and assert no success toast or ready claim.
});

it('retries only observation reads after a delayed publication', async () => {
  // Make the receipt-bound reads return 404/409, click Check again, and assert one POST total.
});

it('renders unauthorized and forbidden publication observation states distinctly', async () => {
  // Exercise 401 and 403 from an exact observation read through the rendered alert.
});
```

- [ ] **Step 2: Run only the named new route tests to verify they fail**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx --testNamePattern 'explicitly selected real scope service|publish accepted|retries only observation reads|unauthorized and forbidden publication'
```

Expected: FAIL because the editor's current Publish action is disabled and no publication dialog or observation UI exists.

- [ ] **Step 3: Implement the focused dialog**

The dialog must load `scopeRuntimeApi.listServices(scopeId, { take: 200 })` only while open. It begins with no selection, uses real service display names, and has distinct loading, empty, failure, retry, unauthorized, and forbidden content. It must never choose or submit a default service on the user's behalf.

On submission, use an exact editor-generated publication snapshot and call `studioApi.previewExplicitRequests`. When preview items exist, render an in-dialog review using only relevant user decisions: method/path, risk, and whether approval is required. Keep call-site IDs and request digests out of the default UI; use them only to construct the confirmation payload. A user cancellation returns to the target-selection state without making a publish POST.

- [ ] **Step 4: Run the named route tests to verify they pass**

Run the command from Step 2.

Expected: PASS; each test uses a different `workflowId`, `serviceId`, and `revisionId` fixture identity.

### Task 3: Editor Integration And Honest User Feedback

**Files:**
- Modify: `src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `src/pages/workflow-activity-vnext/workflows/WorkflowPublishDialog.tsx`
- Modify: `src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `src/locales/workflowActivityVNextMessages.zh-CN.ts`
- Test: `src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Add the smallest editor publication preparation boundary**

Expose a function from `useWorkflowEditor` that parses and serializes the current document only when it is saved, valid, and a real draft. It returns the exact draft `workflowId`, display name, and serialized YAML, or an actionable validation failure. It must not fabricate a revision, modify the current draft, or save silently.

- [ ] **Step 2: Wire the dialog to the existing typed adapters**

Use `createWorkflowRevisionIdentityCandidate()` only as the required request identity candidate. Pass the selected service ID exactly to `studioApi.saveAndBindWorkflow`; capture only API-returned `workflowId`, `revisionId`, and `binding.serviceId` as the observation receipt. Reject a missing binding service ID, scope mismatch, or returned target mismatch as a visible publish failure rather than substituting any identity.

- [ ] **Step 3: Render persistent state and one completed-action toast**

Keep the receipt-bound state in the editor while it is mounted:

```text
Ready -> Reviewing -> Submitting -> Accepted -> Observing -> Published
                                        |             |
                                        |             +-> Delayed -> Check again (GET only)
                                        +-> Failed / Unauthorized / Forbidden
```

Use inline alerts for every nonterminal phase. Emit one localized `ConsoleToast` success only after exact workflow and active service-revision evidence reaches `Published`; do not toast for a click, preview request, or `202 Accepted` response. Never claim full invocation readiness because no public HTTP contract proves it.

- [ ] **Step 4: Run the focused route and hook tests to verify the integrated flow passes**

Run:

```bash
pnpm --dir apps/aevatar-console-web exec jest --runInBand --runTestsByPath src/pages/workflow-activity-vnext/index.test.tsx src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts
```

Expected: PASS.

### Task 4: Focused Verification And Delivery

**Files:**
- Review only the current task's vNext hook, dialog, editor, locale, test, and plan files.

- [ ] **Step 1: Re-run the frontend scope analyzer**

Run:

```bash
python3 /Users/abigaildeng/.codex/skills/frontend-incremental-pr/scripts/frontend_change_scope.py --repo . --base origin/feat/2026-08-04_workflow-activity-vnext
```

- [ ] **Step 2: Run dependency-related tests and changed-file static checks**

Run explicit changed/new Jest tests plus `pnpm exec jest --findRelatedTests` only for changed shared source files when the analyzer identifies a direct consumer graph. Run `pnpm exec biome check` only with the analyzer's `staticCheckFiles` that belong to this task.

- [ ] **Step 3: Verify baseline and diff hygiene**

Run:

```bash
python3 apps/aevatar-console-web/docs/design-baselines/workflow-activity-vnext/verify-baseline.py
git diff --check -- apps/aevatar-console-web
```

Expected: declared SHA, byte-identical generator output, and 17/17 frame inventory pass; no whitespace errors.

- [ ] **Step 4: Review and deliver only task-owned changes**

Review the complete diff. Stage only the publication task's frontend files, commit with an imperative message, push the implementation branch, and create or update the implementation Draft PR. Do not modify, ready, merge, or auto-merge PR #3187. Full frontend suite, package-wide lint, package-wide TypeScript, and production build are delegated to GitHub CI under the personal incremental frontend policy.
