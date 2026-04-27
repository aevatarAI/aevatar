# Studio Script Member Flow Implementation Plan

Date: 2026-04-27
Branch: `feat/2026-04-27_complete-studio-script-flow`
Related issues:
- https://github.com/aevatarAI/aevatar/issues/441
- https://github.com/aevatarAI/aevatar/issues/442

## Problem

Studio already exposes parts of the Script implementation workflow, but the user-facing member lifecycle is still uneven.

Today a user can edit and draft-run a Script in Build. They cannot yet trust Studio to move that Script through the same member loop as Workflow: save, observe applied revision, bind as a service, invoke through the bound endpoint contract, and observe runtime facts.

There is also a smaller UX problem in the Create member modal. Script and GAgent are shown as member types, but the modal currently makes their `Member name` input look meaningful even though their direct member create APIs are not available yet.

## Goals

- Keep Script and GAgent visible as member type choices.
- Make Create member honest: only Workflow creates a member immediately.
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

Script and GAgent become builder entry points:

```text
Create member
  -> select Script
  -> Member name is hidden
  -> Open Script builder
  -> Build > Script

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
  - Script: `Open Script builder`
  - GAgent: `Open GAgent builder`
- Render `Member name` and `Workflow directory` only for Workflow.
- On Script, close the modal and navigate to Build > Script.
- On GAgent, close the modal and navigate to Build > GAgent.
- Respect the existing Script leave guard before changing build surfaces.
- If Script is disabled by workspace features, show a clear warning instead of navigating.

Tests:

- Workflow create still calls the existing workflow save path.
- Script selection hides `Member name`.
- Script action opens the Script build panel.
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
- Script create entry hides `Member name`.
- Script create action opens Build > Script.
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

- Create member modal does not ask for Script/GAgent member names until those APIs exist.
- Workflow create behavior is unchanged.
- Script Build can produce a parent-visible applied revision state.
- Script Bind is gated on catalog-applied revision.
- Script Bind uses `studioApi.bindScopeScript`.
- Script Invoke is based on bound endpoint contract.
- Non-chat Script endpoints are not misrepresented as chat.
- Tests cover both UI entry behavior and Script lifecycle gates.

