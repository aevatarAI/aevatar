# Team operational model unification policy-v2

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

Scope: retry original request 1 after the FKST protected policy gate fix. This replaces failed task 0002, which was useful analysis but failed before implementation under the old gate/state.

Goal:
Unify Team selection and service/run/workflow matching across the Team home, detail, and create flows so the Team a user sees on `/teams` has the same operational meaning when opened in `/teams/:scopeId`.

Current evidence:
- `home.tsx` builds workflow operational units but renders primarily through scope/team preview selection.
- `useTeamRuntimeLens.ts` selects one service from preferred service, binding service, or first service before loading runs.
- This can let the home Team card and detail Team lens choose different runtime surfaces.

Required work:
- Introduce a shared pure model module such as `teamOperationalModel.ts` or `teamWorkspaceModel.ts` in the Team frontend page area.
- The model should define the canonical service selection order, workflow/service binding relationship, run lookup inputs, and selected Team identity for home/detail/create usage.
- Reuse the shared runtime status semantics from the runtime semantics task if present. If it is not landed yet, keep this task narrow or fail closed with a dependency note rather than duplicating status semantics.
- Update `home.tsx` and `useTeamRuntimeLens.ts` to consume the same operational model.
- Touch `new.tsx` only if a minimal shared selection helper is directly useful there; avoid forcing runtime query concerns into create flow.
- Add focused tests proving home and detail choose the same service/run/workflow under preferred service, binding service, missing preferred service, first-service fallback, and no-service cases.

Out of scope:
- Do not split `detail.tsx` broadly.
- Do not change style tokenization or broad inline styles.
- Do not remove legacy home files in this task.
- Do not touch backend/proto/API/actor/projection/workflow engine/backend paths/backend runtime infrastructure/database/backend tests/backend config/architecture docs.

Dependency policy:
- If the runtime semantics foundation has not landed, avoid duplicating its helper surface; either depend on existing current code or stop with a clear dependency note.

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
