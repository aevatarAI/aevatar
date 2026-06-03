# Team detail route extraction retry

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

## Host Forbidden Paths

Do not modify backend, proto, API, actor, projection, workflow engine/backend paths, backend/runtime infrastructure, database, backend tests, backend config, or architecture docs. Frontend page runtime helpers under `apps/aevatar-console-web/src/pages/**/runtime/**` are allowed for frontend/product tasks. If a backend error appears, only locate, reproduce, record, attribute, and risk-label it, then write exactly:

`后端错误已记录，按用户要求未修改`

## Trigger Source

User-created FKST inbox task from the Aevatar control repo.

## Root Cause

Use real product/UI investigation from the Aevatar host repo and the runbook. Do not infer from memory.

## Fix Path

Implement the narrowest frontend/product slice that satisfies the task details below, using isolated FKST worktrees and the configured integration branch. Do not create PRs below the runbook threshold unless the user explicitly authorized that exception.

## Task Details

Scope: retry original requests 2, 3, and 6 after correcting the Aevatar frontend-loop host policy. This task replaces the stale 0003-team-detail-route-topology attempt that failed under the old Host protected path policy.

Goal:
Reduce Team detail page risk with a staged, low-risk extraction. Use current repository facts: if Team Detail topology production code is still absent after the required sync, do not invent topology abstractions; implement the route-state hook and only extract current-production DTO/view-model helpers when there is a real caller.

Current evidence from previous FKST analysis:
- The old topology evidence may be stale in the current repository; re-check after required sync before implementing.
- detail.tsx still has URL route state and local state mirroring for tab/serviceId/runId/workflowId.
- Existing shared navigation helpers for Team detail route state should be reused rather than duplicated where practical.

Required work:
- Add useTeamDetailRouteState hook (or equivalent current-path module) to make tab/serviceId/runId/workflowId/scopeId URL state the single fact source where practical.
- Preserve existing selected run/service/workflow behavior and stale hint handling.
- Re-check whether Team Detail topology graph derivation exists in current production code. If absent, record it as stale evidence and do not add dead topology DTOs.
- Extract one small pure detail DTO/view-model helper only if it has a current production caller and reduces risk without broad detail.tsx rewrite.
- Add focused tests for route state transitions, selected run/service/workflow preservation, unknown tab normalization, stale route hints, and any extracted current-production DTO helper.

Out of scope:
- Do not change style tokenization or broad inline styles.
- Do not delete legacy home files in this task.
- Do not touch backend/proto/API/actor/projection/workflow engine/backend paths/backend runtime infrastructure/database/backend tests/backend config/architecture docs.

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
