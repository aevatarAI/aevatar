# Team runtime semantics foundation policy-v2

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

Scope: retry original request 5 and prerequisite pieces for request 1 after correcting the FKST Host Protected Path Policy and protected-layer tunable format. This replaces failed task 0005, which reached 3/3 approach consensus but failed before implementation because the old host policy section/tunable format was not parseable by FKST gates.

Goal:
Create a shared frontend runtime semantics module for Team pages so run sorting, success/failure/waiting classification, status labels, and status normalization do not drift between home and detail.

Current evidence:
- `apps/aevatar-console-web/src/pages/teams/home.tsx` duplicates compareRuns, isSuccessfulRun, isWaitingRun, isFailedRun behavior.
- `apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.ts` has Team detail run selection/sorting behavior that must align with Team home.
- `workflowOperationalUnits.ts` also has run-state semantics.

Required work:
- Add a small shared Team frontend runtime semantics module under the allowed frontend page runtime helper area, for example `apps/aevatar-console-web/src/pages/teams/runtime/teamRunSemantics.ts`.
- Move shared pure helpers there: trim/normalize status, compare/sort runs, successful/waiting/blocked/failed classification, and user-facing status labels where repeated.
- Update `home.tsx`, `workflowOperationalUnits.ts`, and `runtime/teamRuntimeLens.ts` to use the shared helpers without intentionally changing visible behavior.
- Add focused tests covering waiting/human-gated runs, failed runs, `lastSuccess` true/false, timestamp/stateVersion/runId ordering, and status alias coverage.

Out of scope:
- Do not change style tokenization or broad inline styles.
- Do not split `detail.tsx` in this task.
- Do not touch backend/proto/API/actor/projection/workflow engine/backend paths/backend runtime infrastructure/database/backend tests/backend config/architecture docs.

Implementation note:
- Use current repository facts after the required pre-task sync.
- Preserve the failed 0005 approach consensus framing unless current files have materially changed.

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
