# Team operational model source of truth

⟦AI:FKST⟧

## Control Runbook

Before executing, read and obey:

- `/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop/visible-codex-refactor-loop-operating-plan.md`
- `/Users/pottersun/Desktop/sbt_projects/aevatar/AGENTS.md`

The control runbook is the visible supervisor policy for this Aevatar FKST task. If it conflicts with package defaults, fail closed and record the conflict rather than expanding scope silently.

## Host Configuration

- Host repo: `/Users/pottersun/Desktop/sbt_projects/aevatar`
- Integration branch: `auto-frontend-dev`
- Current control repo: `/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop`
- FKST wrapper: `/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop/scripts/fkst-aevatar`
- Integration sync script: `/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop/scripts/sync-aevatar-integration`

## Required Pre-task Sync

Before implementation work starts, synchronize remote `origin/dev` into `auto-frontend-dev` by running this from the control repo:

```bash
/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop/scripts/sync-aevatar-integration
```

This sync is the only allowed direct push to the integration branch. If the sync has conflicts, requires destructive git operations, or cannot push cleanly, stop and report the blocker instead of continuing the task.

## Host Protected Path Policy

Do not modify backend, proto, API, actor, projection, workflow engine/backend paths, backend/runtime infrastructure, database, backend tests, backend config, or architecture docs. Frontend page runtime helpers under `apps/aevatar-console-web/src/pages/**/runtime/**` are allowed for frontend/product tasks. If a backend error appears, only locate, reproduce, record, attribute, and risk-label it, then write exactly:

`后端错误已记录，按用户要求未修改`

## Trigger Source

User-created FKST inbox task from the Aevatar control repo.

## Root Cause

Use real product/UI investigation from the Aevatar host repo and the runbook. Do not infer from memory.

## Fix Path

Implement the narrowest frontend/product slice that satisfies the task details below, using isolated FKST worktrees and the configured integration branch. Do not create PRs below the runbook threshold unless the user explicitly authorized that exception.

## Task Details

Rationalized source requirements covered: original item 1, with dependency awareness from item 5.

Current repo facts:
- Team home uses `buildWorkflowOperationalUnits` and local preview/team logic.
- Team detail uses `useTeamRuntimeLens` under `apps/aevatar-console-web/src/pages/teams/runtime/`.
- Team create page exists at `apps/aevatar-console-web/src/pages/teams/new.tsx`.

Goal:
Introduce one shared Team operational/workspace model that owns service, workflow, binding, and run matching semantics across Team home and detail pages, and include create page only if it currently participates in the same selection semantics.

Expected implementation:
- Add a focused module such as `teamOperationalModel.ts` or `teamWorkspaceModel.ts` in the Team runtime area.
- The model should make the selection rules explicit: preferred service, binding-backed service, workflow operational unit, fallback service, selected run, and explanatory reason/metadata where useful.
- Refactor `home.tsx` and `useTeamRuntimeLens.ts` to consume the shared model rather than each maintaining a separate service/run/workflow selection trunk.
- Inspect `new.tsx`; if it has relevant Team selection semantics, wire it to the model. If not, record a no-op with evidence in the FKST verdict.

Acceptance criteria:
- A Team shown on home and then opened in detail resolves through the same operational model for service/run/workflow identity.
- Tests cover at least one home/detail consistency scenario, including fallback behavior when no preferred service is available.
- The implementation keeps data fetching boundaries intact; the shared model should be deterministic and testable.

Explicit exclusions:
- Do not implement original item 7 style tokenization.
- Do not modify backend, API, workflow engine, actor, projection, database, backend config, or architecture docs.

## Files allowlist

- `src/**`
- `apps/**`
- `packages/**`
- `components/**`
- `pages/**`
- `public/**`
- `styles/**`
- `tests/**`

## Verification

- Run focused frontend checks appropriate to touched files.
- Run `git diff --check`.
- Use visible browser verification when a local frontend page is available.
- If tests are modified, run `bash tools/ci/test_stability_guards.sh`.

## Commit body metadata

trigger-source: user-created fkst inbox task via aevatar-frontend-loop
action-type: frontend/product automation task
equivalence-semantics: preserve backend behavior while improving the requested frontend/product behavior
reuse: future Aevatar FKST tasks can reuse the runbook-injected task shape
failure-trace-owner: fkst evolve/review verdicts and visible supervisor runbook

⟦AI:FKST⟧
