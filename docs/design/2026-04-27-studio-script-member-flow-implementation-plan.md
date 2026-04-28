# Studio Script Member Flow Implementation Plan

Date: 2026-04-27
Branch: `feat/2026-04-27_complete-studio-script-flow`
Related issues:
- https://github.com/aevatarAI/aevatar/issues/441
- https://github.com/aevatarAI/aevatar/issues/442

## Problem

Studio already exposes parts of the Script implementation workflow, but the user-facing member lifecycle is still uneven.

Today a user can edit and draft-run a Script in Build. They cannot yet trust Studio to move that Script through the same member loop as Workflow: save, observe applied revision, bind as a service, invoke through the bound endpoint contract, and observe runtime facts.

There is also a smaller UX problem in the Create member modal. Script and GAgent are shown as member types, but the modal currently makes the creation step ambiguous. For Script, the user needs to name the script draft before entering Build; otherwise Build can show starter source while still blocking every meaningful action behind `Select Script`.

## Goals

- Keep Script and GAgent visible as member type choices.
- Make Create member honest: Workflow creates a member immediately; Script creates a named local Script draft identity, not a bound member.
- Treat Script as a first-class member lifecycle participant once a saved revision is observed as applied.
- Preserve Draft-run as a Build-only action.
- Bind only catalog-observed Script revisions.
- Invoke only through the bound service endpoint contract.
- Avoid a broad frontend adapter rewrite in this branch.

## Non-Goals

- No backend Script runtime protocol redesign.
- No generic frontend typed command form builder unless the existing endpoint metadata already supports it.
- No new `StudioMemberTypeAdapter` abstraction in this iteration.
- No GAgent direct member creation API work.
- No query-time replay, projection priming, or frontend-owned fact registry.

## UX Structure

Script should read as a member implementation workbench, not as a loose code editor tab. The core structure is:

```text
Create member
  -> choose Script
  -> enter Script name
  -> Create Script draft
  -> Build > Script
  -> validate source
  -> optional draft-run
  -> save revision
  -> observe catalog applied
  -> continue to Bind
  -> invoke through bound service contract
  -> observe member facts
```

The Create member modal stays small and honest:

- Workflow is the only type that asks for `Member name` and creates a draft member immediately.
- Script asks for `Script name` and creates a local draft identity. This identity can be validated, draft-run, and saved as a Script revision, but it is not a callable member until Bind succeeds.
- GAgent is visible as a member type, but it acts as a builder entry until its member API is available.
- Script action label is `Create Script draft`.
- GAgent action label is `Open GAgent builder`.
- The modal should not imply that a Script or GAgent member has been created before the builder and binding lifecycle actually completes.
- Script draft naming should use a slug preview. The persisted script id should be stable, readable, and editable before creation.

Build > Script should have a stable three-part layout:

```text
+--------------------------------------------------------------------------------+
| Script member implementation                                                    |
| Status: Dirty / Validation failed / Save accepted / Catalog applied / Bound      |
+----------------------------------------------+---------------------------------+
| Source editor                                 | Readiness checklist             |
|                                              | [ ] Source selected             |
|                                              | [ ] Validation clean            |
|                                              | [ ] Draft-run tested, optional  |
|                                              | [ ] Save accepted               |
|                                              | [ ] Catalog applied             |
|                                              |                                 |
|                                              | Primary action                  |
|                                              | Validate / Save revision /      |
|                                              | Waiting for catalog / Bind      |
+----------------------------------------------+---------------------------------+
| Draft-run input/output, clearly marked as unsaved-source testing                 |
+--------------------------------------------------------------------------------+
```

Primary actions should be state-driven:

| State | Primary action | Bind availability |
| --- | --- | --- |
| No script identity | Create or select Script | Hidden |
| Draft identity exists, source unknown | Validate | Hidden |
| Dirty or unknown | Validate | Hidden |
| Validation clean | Save revision | Hidden |
| Save accepted | Waiting for catalog | Hidden |
| Catalog applied | Continue to Bind | Visible |
| Bound | Invoke member | Uses the bound service contract |

Key UX rules:

- `Continue to Bind` is not a generic footer action. It appears only after Studio observes the applied Script revision through the catalog or equivalent read model state.
- Draft-run is explicitly labeled as testing the current editor source. It must not imply that the Script is saved, catalog-applied, or callable as a member service.
- The Script selector is backed by the scope Script catalog. It should not be the only way to start when the scope has no saved scripts.
- Bind shows Script as a pending candidate only when the applied revision is clean and not dirty.
- Invoke is contract-first. If the bound Script exposes a chat-compatible endpoint, Studio can show chat-style invocation. Otherwise it should show the endpoint contract or a clear unsupported state.
- Observe should prioritize member facts: selected Script revision, binding status, service id, endpoint, actor ids, and latest observed version. Runtime debug details can remain secondary or collapsible.

## CLI Frontend Parity Audit

The CLI frontend already behaves more like a complete Script workbench. Studio Console does not need to copy every feature in this branch, but it should copy the lifecycle shape.

Reference surface:

- `tools/Aevatar.Tools.Cli/Frontend/src/ScriptsStudio.tsx`
- `tools/Aevatar.Tools.Cli/Frontend/src/scripts-studio/models.ts`
- `tools/Aevatar.Tools.Cli/Frontend/src/scripts-studio/package.ts`

Console gaps found against the CLI implementation:

| Priority | Gap | Console decision |
| --- | --- | --- |
| P0 | Script identity can be edited before save. CLI has a Script ID title input and local draft model. | Add `Script name` to Create member modal and create a draft identity before Build. |
| P0 | Empty scope is not a dead end. CLI starts from a local draft. | Build > Script must allow starter source with a draft script id, not only existing catalog selection. |
| P0 | Save uses the chosen identity, then observes catalog application. | `Save revision` must save with the draft `scriptId`, refetch catalog, and only then allow Bind. |
| P1 | Diagnostics are navigable. CLI surfaces validation messages and editor markers. | Implemented in Console: validation diagnostics render below the editor and click through to the matching file/line. |
| P1 | Runtime read model is visible after draft-run. | Implemented in Console: dry-run now shows structured run facts next to the raw output. |
| P2 | Multi-file package tree, entry point management, and proto file handling. | Implemented in Console: Build > Script can add, rename, remove, select files, set entry source, and edit entry behavior type. |
| P2 | Ask AI and promotion/evolution history. | Promotion/evolution proposal and session decision history are implemented. Ask AI remains intentionally out of this slice. |
| P2 | Typed invoke form for non-chat endpoints. | Implemented in Invoke: endpoint request/response type URLs are visible, protobuf base64 input is explicit, and JSON is only a scratchpad. |

## Delivery Slices

### Slice 1: Create Member Modal UX

Issue: https://github.com/aevatarAI/aevatar/issues/441
Milestone: `Create Team / Studio Entry Follow-up`

This is a narrow UI behavior change.

Workflow keeps the current create path:

```text
Create member
  -> select Workflow
  -> enter Member name
  -> select Workflow directory
  -> Create member
  -> blank workflow draft opens in Build
```

Script creates a named draft identity and opens the builder:

```text
Create member
  -> select Script
  -> enter Script name
  -> review generated script id slug
  -> Create Script draft
  -> Build > Script
  -> starter source is available under that script id
```

GAgent remains a builder entry point until its direct member creation API exists:

```text
Create member
  -> select GAgent
  -> Member name is hidden
  -> Open GAgent builder
  -> Build > GAgent
```

Implementation scope:

- Update `apps/aevatar-console-web/src/pages/studio/index.tsx`.
- Keep the existing `Create member` modal.
- Change modal OK text by selected kind:
  - Workflow: `Create member`
  - Script: `Create Script draft`
  - GAgent: `Open GAgent builder`
- Render `Member name` and `Workflow directory` only for Workflow.
- Render `Script name` only for Script.
- Generate a slug-style `scriptId` preview from `Script name`.
- On Script, create a page-local Script draft identity, close the modal, and navigate to Build > Script with that draft selected.
- On GAgent, close the modal and navigate to Build > GAgent.
- Respect the existing Script leave guard before changing build surfaces.
- If Script is disabled by workspace features, show a clear warning instead of navigating.
- Do not call the member create API for Script. The Script becomes a member only after save, catalog application, and bind.

Script draft creation contract:

```ts
type PendingScriptDraft = {
  scriptId: string;
  displayName: string;
  source: string;
  createdFrom: 'create-member-modal';
};
```

The draft is frontend-local until `Save revision` calls the Script save API. If the generated `scriptId` collides with an existing catalog script, the modal should either block with a clear error or ask the user to choose the existing script explicitly.

Current catalog selector data source remains:

```text
scriptsApi.listScripts(scopeId, true)
  -> GET /api/scopes/{scopeId}/scripts?includeSource=true
```

The selector should list saved scope scripts. A newly named draft can appear in the editor before it appears in that catalog response, but the UI must label it as a draft and avoid presenting it as catalog-applied.

P1 follow-through:

- Script drafts created from the modal are stored in local browser storage by scope and script id, so refresh can restore the draft identity and source.
- Build > Script now shows a lifecycle status strip covering identity, validation, save observation, and revision.
- Pending save observation stays honest: the UI says catalog is still pending and offers a catalog refresh action instead of enabling Bind.
- Dry-run results expose structured facts (`runId`, `runtimeActorId`, `definitionActorId`, `sourceHash`, `readModelUrl`) before the raw output.
- Validation diagnostics are listed with severity, location, code, and message; selecting one focuses the editor on that diagnostic.

P2 follow-through:

- Build > Script now exposes a package tree for C# and proto files.
- Entry source and entry behavior type are editable from the Script build surface.
- Promotion/evolution uses the existing `proposeEvolution` API and shows the current session's decisions without inventing a separate read model.
- Invoke for non-chat endpoints is contract-aware: it shows endpoint kind plus request/response type URLs and requires a protobuf `payloadBase64`.
- JSON in Invoke is a scratchpad only. Studio does not convert arbitrary JSON into protobuf bytes unless a typed encoder is introduced later.

Tests:

- Workflow create still calls the existing workflow save path.
- Script selection shows `Script name`.
- Script name can be edited before creation.
- Script action creates a draft identity and opens the Script build panel.
- Script starter source is available under the draft `scriptId`.
- GAgent selection hides `Member name`.
- GAgent action opens the GAgent build panel.

## Slice 2: Script Member Lifecycle

Issue: https://github.com/aevatarAI/aevatar/issues/442
Milestone: `Studio Script Lifecycle Follow-up`

This is the actual Script member loop.

```text
Build > Script
  -> edit source
  -> validate
  -> draft-run, optional
  -> save accepted
  -> observe catalog applied
  -> parent Studio receives applied script state
  -> Bind shows Script pending candidate
  -> bindScopeScript
  -> service catalog refetch
  -> Invoke uses bound service endpoint contract
  -> Observe shows member/service/runtime facts
```

### State Model

The parent Studio page should only know lifecycle-ready Script facts. It should not own the editor source as durable state.

Suggested parent state shape:

```ts
type StudioScriptBuildState = {
  scriptId: string;
  displayName: string;
  scriptRevision: string;
  revisionId?: string;
  sourceHash?: string;
  definitionActorId?: string;
  dirty: boolean;
  validationStatus: 'unknown' | 'valid' | 'invalid';
  saveStatus: 'idle' | 'accepted' | 'applied' | 'failed';
};
```

Important rule:

```text
dirty source          -> no bind candidate
save accepted only   -> no bind candidate
save applied/catalog -> bind candidate allowed
```

### Build Handoff

Add a callback from `StudioScriptBuildPanel` to the Studio parent:

```ts
onScriptBuildStateChange?: (state: StudioScriptBuildState | null) => void;
```

`StudioScriptBuildPanel` should emit:

- selected script id
- display name
- active or accepted revision
- dirty state
- validation status
- save observation status
- definition actor id and source hash for display only

It must not ask the parent to infer readiness from UI text like `saveNotice`.

### Bind Candidate

Extend `buildPendingBindCandidate` in `apps/aevatar-console-web/src/pages/studio/index.tsx`.

Current behavior:

```text
activeBuildMode == workflow
  -> workflow pending candidate
otherwise
  -> null
```

Target behavior:

```text
activeBuildMode == workflow
  -> workflow pending candidate

activeBuildMode == script
  -> if script build state is applied and not dirty
       Script pending candidate
     else
       null

activeBuildMode == gagent
  -> no direct candidate in this issue
```

Script candidate:

```ts
{
  kind: 'script',
  displayName,
  description: 'Bind the applied Script revision as a callable member service.',
  actionLabel: 'Bind Script member',
  scriptId,
  scriptRevision,
  revisionId,
}
```

### Bind Handler

Extend `handleBindPendingCandidate` to support Script:

```text
workflow candidate -> studioApi.bindScopeWorkflow
script candidate   -> studioApi.bindScopeScript
```

After binding:

- refetch scope services
- refetch scope binding
- resolve the bound service id from the response or refreshed catalog
- select the bound service and default endpoint
- keep the user on Bind or move to Invoke only through the existing lifecycle action

Do not bind local editor source. Do not bind a merely accepted save.

### Invoke Contract

Script invocation must use the bound service endpoint contract.

Do not assume every Script exposes chat stream. The backend may create Script endpoints from declared command messages. A Script that declares `AppScriptCommand` is not automatically a chat endpoint.

UI behavior:

```text
bound Script service has chat-compatible endpoint
  -> allow chat smoke test

bound Script service has non-chat command endpoint
  -> show endpoint contract
  -> block chat smoke test with clear copy
  -> optionally allow typed invoke if existing metadata supports it

no bound Script service
  -> show "Bind this Script member before invoking it."
```

### Observe

Observe should lead with member facts, not runtime internals.

Recommended layers:

1. Binding facts:
   - service id
   - implementation kind
   - active revision
   - script id
   - script revision
2. Latest invocation:
   - run id
   - endpoint
   - status
   - timestamp
   - error
3. Runtime debug:
   - runtime actor id
   - definition actor id
   - source hash
   - state version

Raw actor ids should be secondary or collapsible.

## Failure Modes

| Failure | Expected handling |
| --- | --- |
| Script feature disabled | Create modal warns and stays in place. |
| User has unsaved Script edits | Leave guard blocks navigation unless confirmed. |
| Save accepted but projection/catalog not applied | Bind candidate remains hidden. |
| Save observation fails or times out | Show warning, do not allow bind. |
| Bind API fails | Bind panel shows error notice. |
| Bound Script has no chat endpoint | Invoke shows endpoint contract and honest blocked state. |
| Refetched service catalog does not include returned service id yet | Keep current selection stable and show eventual consistency copy. |

## Test Plan

### Unit / Component Tests

Target file:

- `apps/aevatar-console-web/src/pages/studio/index.test.tsx`

Required cases:

- Workflow create path remains unchanged.
- Script create entry shows `Script name`, not `Member name`.
- Script create action creates a local draft identity and opens Build > Script.
- Script draft can be validated and saved with the draft `scriptId`.
- GAgent create entry hides `Member name`.
- GAgent create action opens Build > GAgent.
- Dirty Script source does not create a bind candidate.
- Save accepted but not applied does not create a bind candidate.
- Applied Script revision creates a Script pending bind candidate.
- Script pending bind calls `studioApi.bindScopeScript`.
- Script pending bind does not call `studioApi.bindScopeWorkflow`.
- Non-chat Script endpoint does not render chat smoke test as available.

### API Tests

Existing shared API tests should remain valid:

- `apps/aevatar-console-web/src/shared/studio/api.test.ts`
- `apps/aevatar-console-web/src/shared/studio/scriptsApi.test.ts`

Add coverage only if request payload shape changes.

### Commands

Run focused UI tests:

```bash
npm --prefix apps/aevatar-console-web run test:ui -- --runTestsByPath src/pages/studio/index.test.tsx --runInBand
```

Run type check before handoff:

```bash
npm --prefix apps/aevatar-console-web run tsc
```

## Parallelization

Sequential implementation is preferred for this branch.

Both slices touch `apps/aevatar-console-web/src/pages/studio/index.tsx`, and Slice 2 depends on the Create member modal semantics being settled. Splitting into parallel worktrees would create avoidable merge conflicts.

Recommended order:

```text
1. Finish Create member modal UX
2. Verify focused Studio page tests
3. Add Script build state handoff
4. Add Script bind candidate and bind handler
5. Add Invoke contract honesty
6. Verify focused tests and typecheck
```

## Acceptance Criteria

- Create member modal asks Workflow for `Member name`, Script for `Script name`, and GAgent for no name until the GAgent API exists.
- Workflow create behavior is unchanged.
- Script creation from the modal produces a named local draft identity and opens Build > Script.
- Empty Script catalogs do not dead-end the user at a disabled selector.
- Script Build can produce a parent-visible applied revision state.
- Script Bind is gated on catalog-applied revision.
- Script Bind uses `studioApi.bindScopeScript`.
- Script Invoke is based on bound endpoint contract.
- Non-chat Script endpoints are not misrepresented as chat.
- Tests cover both UI entry behavior and Script lifecycle gates.
