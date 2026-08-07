# Hidden Workflow Member Publication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Workflow Activity vNext provision one hidden Team and Member per Workflow and publish through the existing member binding-run contract without exposing Team or Member concepts to users.

**Architecture:** A focused `workflowBackingAuthority` module owns hidden resource provisioning, typed relationship lookup, and cleanup. The Workflow creation and list surfaces call that module, while `useWorkflowPublication` observes member binding runs and `WorkflowEditorPage` reuses the existing explicit-request confirmation plus member binding APIs. The editor continues to expose only Workflow commands and toast notifications.

**Tech Stack:** React, TypeScript, React Query, Jest, Testing Library, Ant Design, existing `studioApi` wrappers.

---

### Task 1: Hidden Workflow Authority Module

**Files:**
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.ts`
- Create: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/team-member-workflow-studio/hooks/useTeamMemberWorkflowStudio.ts`

- [ ] **Step 1: Write failing identity lookup tests**

Add tests that pass members with distinct IDs and assert that lookup matches only the typed workflow reference:

```ts
expect(
  resolveWorkflowBackingAuthority({
    workflowId: 'wf-alpha',
    members: [
      member({ memberId: 'm-other', workflowId: 'wf-other' }),
      member({ memberId: 'm-alpha', teamId: 't-alpha', workflowId: 'wf-alpha' }),
    ],
  }),
).toEqual({ memberId: 'm-alpha', teamId: 't-alpha' });
```

Also assert that zero matches returns `null`, and two exact matches throw a duplicate-authority error rather than picking one.

- [ ] **Step 2: Run the new test and verify RED**

Run from `apps/aevatar-console-web`:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.test.ts --runInBand
```

Expected: FAIL because `workflowBackingAuthority` does not exist.

- [ ] **Step 3: Implement typed lookup**

Create these public types and function:

```ts
export type WorkflowBackingAuthority = {
  readonly memberId: string;
  readonly teamId: string;
};

export function resolveWorkflowBackingAuthority(input: {
  readonly members: readonly StudioMemberSummary[];
  readonly workflowId: string;
}): WorkflowBackingAuthority | null;
```

Filter only members where `implementationKind === 'workflow'`, `implementationRef?.implementationKind === 'workflow'`, and `implementationRef.workflowId === workflowId`. Require nonblank `memberId` and `teamId`. Never compare or construct identities by prefix.

- [ ] **Step 4: Verify typed lookup GREEN**

Run the Task 1 Jest command and expect all lookup tests to pass.

- [ ] **Step 5: Write failing provisioning tests**

Add injected dependency tests for:

```ts
await provisionWorkflowBackingAuthority({
  scopeId: 'scope-alpha',
  workflowId: 'wf-alpha',
  workflowName: 'Approval flow',
  api,
  wait: async () => undefined,
});
```

Assert the exact sequence:

```text
createTeam -> getTeam until readable -> createMember(teamId)
-> getMember until readable -> updateMemberImplementationRef(workflowId)
-> getMember until the typed link is readable
```

Use distinct fixtures `t-alpha`, `m-alpha`, `wf-alpha`, and `svc-alpha`. Add a test proving that an already-linked member is reused without create calls, and a test proving 404 materialization delays are retried without inventing another identity.

- [ ] **Step 6: Verify provisioning tests RED**

Run the Task 1 Jest command. Expected: FAIL because the provisioning API is missing.

- [ ] **Step 7: Implement provisioning and reuse the existing member-link wait behavior**

Define a narrow injected API contract using existing methods:

```ts
type WorkflowBackingAuthorityApi = Pick<
  typeof studioApi,
  | 'createMember'
  | 'createTeam'
  | 'getMember'
  | 'getTeam'
  | 'listMembers'
  | 'updateMemberImplementationRef'
>;
```

Export `provisionWorkflowBackingAuthority`, `waitForWorkflowMemberVisible`, and `linkWorkflowMemberDraft`. Move the equivalent `waitForCreatedMemberVisible` and `linkCreatedWorkflowMemberDraft` logic out of the Team Member hook and call the shared functions there, preserving its current retry behavior.

- [ ] **Step 8: Verify provisioning GREEN**

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.test.ts src/pages/team-member-workflow-studio/index.test.tsx --runInBand
```

Expected: all related authority and existing Team Member tests pass.

### Task 2: Provision One Hidden Pair During Workflow Creation

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx`

- [ ] **Step 1: Write a failing user-flow test**

Extend the Studio API mock with Team and Member methods. Create a materialized draft fixture and assert that one user creation command produces exactly one dedicated Team and one dedicated Member before navigation:

```ts
expect(mockStudioApi.createTeam).toHaveBeenCalledWith({
  scopeId: 'scope-alpha',
  displayName: 'Approval flow',
  description: expect.any(String),
});
expect(mockStudioApi.createMember).toHaveBeenCalledWith({
  scopeId: 'scope-alpha',
  displayName: 'Approval flow',
  implementationKind: 'workflow',
  teamId: 't-alpha',
});
expect(mockStudioApi.updateMemberImplementationRef).toHaveBeenCalledWith({
  scopeId: 'scope-alpha',
  memberId: 'm-alpha',
  implementationRef: {
    implementationKind: 'workflow',
    workflowId: 'wf-alpha',
  },
});
```

Assert no Team/Member label, selector, link, or success copy is rendered.

- [ ] **Step 2: Verify creation test RED**

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx --runInBand
```

Expected: FAIL because creation navigates after draft materialization without provisioning authorities.

- [ ] **Step 3: Implement hidden provisioning before navigation**

Change `finishSave` so both materialized and accepted-then-readable drafts call:

```ts
await provisionWorkflowBackingAuthority({
  api: studioApi,
  scopeId,
  workflowId: readable.workflowId,
  workflowName: readable.name,
});
navigateToWorkflow(readable.workflowId);
```

Keep the existing Workflow-only loading and failure presentation. Do not add Team or Member state to route/query parameters or visible text.

- [ ] **Step 4: Add a failing partial-provision failure test**

Make `updateMemberImplementationRef` fail and assert navigation does not occur, the existing Workflow creation error toast appears, and retrying uses the already-linked roster result rather than creating a second pair.

- [ ] **Step 5: Implement retry-safe resolution**

Before creating resources, call `listMembers(scopeId)` and reuse an exact typed match. Keep the creation form input intact when provisioning fails so the existing create action can be retried.

- [ ] **Step 6: Verify creation GREEN**

Run the Task 2 Jest command and expect all New Workflow tests to pass.

### Task 3: Publish Through Member Binding Runs

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Replace publication observer tests with binding-run expectations**

Define the receipt with distinct identities:

```ts
const receipt = {
  scopeId: 'scope-alpha',
  workflowId: 'wf-alpha',
  memberId: 'm-alpha',
  bindingRunId: 'bind-alpha',
  revisionId: 'rev-alpha',
};
```

Test active states, 404 projection delay, `succeeded` with matching `result.revisionId`, terminal failure/rejection, mismatched scope/member/run identity, and delayed observation without resubmission.

- [ ] **Step 2: Verify observer RED**

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts --runInBand
```

Expected: FAIL because the current observer reads workflow/service revision catalogs.

- [ ] **Step 3: Implement binding-run observation**

Replace workflow/service reads with `studioApi.getMemberBindingRun`. Preserve a receipt-bound React Query key. Return `publishedServiceId` only from a `succeeded` run whose scope, member, run, and revision all match the accepted receipt. Map exhausted polling to `delayed`, not `failed`.

- [ ] **Step 4: Verify observer GREEN**

Run the Task 3 observer command and expect all tests to pass.

- [ ] **Step 5: Write failing editor integration tests**

In `index.test.tsx`, mock `bindMemberWorkflow`, `getMemberBindingRun`, and `confirmInteractiveExplicitRequestPreview`. Assert Publish:

1. Resolves `m-alpha` from `implementationRef.workflowId === 'wf-alpha'`.
2. Saves dirty content before preview.
3. Calls the shared explicit-request confirmation helper.
4. Calls `bindMemberWorkflow` with separate member and workflow IDs.
5. Never calls `publishWorkflow`.
6. Treats cancellation as idle without an error toast.
7. Exposes `Check status` after delayed observation without a second bind call.

- [ ] **Step 6: Verify editor integration RED**

Run:

```bash
pnpm exec jest src/pages/workflow-activity-vnext/index.test.tsx --runInBand
```

Expected: FAIL on the current workflow publication endpoint and fabricated confirmations.

- [ ] **Step 7: Make publication preparation save dirty drafts**

Refactor the editor hook's save core to return the exact readable `StudioWorkflowFile`. Make `preparePublication` invoke that core when dirty, then serialize the saved document and return its exact YAML, `workflowId`, name, and document version. Keep the standalone Save button behavior unchanged.

- [ ] **Step 8: Bind the resolved Member**

Add a React Query member roster query and call `resolveWorkflowBackingAuthority`. In `publishWorkflow`:

```ts
const confirmations = await confirmInteractiveExplicitRequestPreview(preview);
if (confirmations === null) return;
const accepted = await studioApi.bindMemberWorkflow({
  scopeId: activeScopeId,
  memberId: authority.memberId,
  workflowId: preparation.workflowId,
  revisionId: preview.revisionId,
  workflowYamls: [preparation.workflowYaml],
  explicitRequestConfirmations: confirmations,
  displayName: preparation.workflowName,
});
```

Store the accepted binding receipt for `useWorkflowPublication`; remove `studioApi.publishWorkflow` from this path.

- [ ] **Step 9: Verify editor integration GREEN**

Run the Task 3 observer and editor commands and expect all tests to pass.

### Task 4: Toast-Only Workflow Editor Errors And Warnings

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.en-US.ts`
- Modify: `apps/aevatar-console-web/src/locales/workflowActivityVNextMessages.zh-CN.ts`

- [ ] **Step 1: Write failing presentation tests**

Assert publication failures, save/materialization failures, error findings, and warning findings do not render page-level Ant Design alerts. Assert `toast.error` or `toast.warning` receives a stable notification and delayed publication retains a `Check status` action in the command/status area.

- [ ] **Step 2: Verify presentation RED**

Run the focused `index.test.tsx` command. Expected: FAIL because publication and finding alerts still render in page flow.

- [ ] **Step 3: Implement toast-only presentation**

Remove only the editor's publication, save-materialization, and finding alert bands. Deduplicate findings by `level + code + path + message` in an effect and route errors/warnings to the shared toast. Preserve toolbar status, retry, and `Check status` controls. Do not remove draft run result/error panels because those are run output, not transient editor notifications.

- [ ] **Step 4: Verify presentation GREEN**

Run the focused `index.test.tsx` command and expect all presentation tests to pass.

### Task 5: Clean Hidden Resources When Deleting A Draft

**Files:**
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.test.ts`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx`
- Modify: `apps/aevatar-console-web/src/pages/workflow-activity-vnext/index.test.tsx`

- [ ] **Step 1: Write failing cleanup tests**

Test that cleanup resolves the exact typed member, then calls:

```text
deleteMember({ scopeId, memberId })
archiveTeam(scopeId, teamId)
deleteWorkflowDraft(workflowId, scopeId)
```

Treat member/team 404 as already cleaned. Assert another Workflow's Team/Member are untouched.

- [ ] **Step 2: Verify cleanup RED**

Run the backing authority and page test files. Expected: FAIL because draft deletion currently removes only the draft.

- [ ] **Step 3: Implement explicit cleanup**

Add `cleanupWorkflowBackingAuthority` using only IDs returned by `resolveWorkflowBackingAuthority`. Wire the existing draft delete confirmation through it, preserving the current retry state and toast wording at the Workflow level.

- [ ] **Step 4: Verify cleanup GREEN**

Run the Task 5 test command and expect all cleanup and page tests to pass.

### Task 6: Focused Validation And PR Delivery

**Files:**
- Review all files changed by Tasks 1-5.

- [ ] **Step 1: Run dependency-related Jest tests**

Run from `apps/aevatar-console-web`, passing only the changed source files to `--findRelatedTests`, then explicitly run every changed test file:

```bash
pnpm exec jest --findRelatedTests src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.ts src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.tsx src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.ts src/pages/workflow-activity-vnext/hooks/useWorkflowEditor.ts src/pages/workflow-activity-vnext/workflows/WorkflowEditorPage.tsx src/pages/workflow-activity-vnext/workflows/WorkflowsPage.tsx src/pages/team-member-workflow-studio/hooks/useTeamMemberWorkflowStudio.ts --runInBand
pnpm exec jest src/pages/workflow-activity-vnext/workflows/workflowBackingAuthority.test.ts src/pages/workflow-activity-vnext/workflows/NewWorkflowPage.test.tsx src/pages/workflow-activity-vnext/hooks/useWorkflowPublication.test.ts src/pages/workflow-activity-vnext/index.test.tsx src/pages/team-member-workflow-studio/index.test.tsx --runInBand
```

- [ ] **Step 2: Run changed-file static checks**

Run Biome only on changed frontend files, using explicit paths. Do not run package-wide lint, `tsc`, or production build. Run `bash tools/ci/test_stability_guards.sh` because tests changed.

- [ ] **Step 3: Review the complete diff**

Confirm the diff contains no backend changes, no new Team/Member UI text, no identity derivation, no `publishWorkflow` call in Workflow Activity vNext, and no page-level editor error/warning alert bands.

- [ ] **Step 4: Commit and update the PR**

Stage only this task's files, commit with an imperative message, force-push with lease because the branch was rebased, and update PR #3276 with exact focused commands and:

```text
Full frontend suite/build: deferred to GitHub CI by personal local workflow policy
```

Do not wait for CI unless requested.
