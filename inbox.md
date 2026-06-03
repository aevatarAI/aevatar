# Team detail route and topology extraction

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

Do not modify backend, proto, API, actor, projection, workflow, runtime, database, backend tests, backend config, or architecture docs. If a backend error appears, only locate, reproduce, record, attribute, and risk-label it, then write exactly:

`后端错误已记录，按用户要求未修改`

## Trigger Source

User-created FKST inbox task from the Aevatar control repo.

## Root Cause

Use real product/UI investigation from the Aevatar host repo and the runbook. Do not infer from memory.

## Fix Path

Implement the narrowest frontend/product slice that satisfies the task details below, using isolated FKST worktrees and the configured integration branch. Do not create PRs below the runbook threshold unless the user explicitly authorized that exception.

## Task Details

Scope: implement requests 2, 3, and 6 as a staged, low-risk extraction.

Goal:
Reduce detail.tsx risk by extracting route state and topology derivation into testable units without broad visual redesign.

Current evidence:
- detail.tsx is 3359 lines and mixes route sync, React Query, workflow parsing, connector summaries, topology graph derivation, tab view-model, styles, and navigation actions.
- routeState is read around line 970 and mirrored into local state around lines 982-1006, with push handlers around line 2925 and later.
- topology graph derivation begins around line 1963 and creates React.createElement labels around lines 2038, 2091, and 2125.

Required work:
- Add useTeamDetailRouteState hook to make tab/serviceId/runId/workflowId/scopeId URL state the single fact source where practical.
- Add pure deriveTeamTopologyViewModel or equivalent that returns TopologyNodeViewModel, TopologyEdgeViewModel, entity maps, selected detail rows, inbound/outbound rows, and depth metadata without React elements.
- Move React label rendering into TeamTopologyTab or a small rendering adapter so topology derivation is pure and unit-testable.
- Start extracting pure detail DTO derivation only where it is low risk; do not attempt a full 3359-line rewrite in one pass.
- Add tests for route state transitions, selected run/service/workflow preservation, topology node/edge/entity derivation, selected node fallback, and no-graph empty state.

Out of scope:
- Do not change style tokenization or broad inline styles.
- Do not delete legacy home files in this task.
- Do not change backend/proto/API/actor/projection/workflow/runtime/database paths.

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
