# Team detail data and view-model extraction follow-up

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

Scope: staged follow-up for original request 2, excluding request 7 style tokenization and avoiding overlap with the active route-state extraction task.

Goal:
Reduce `apps/aevatar-console-web/src/pages/teams/detail.tsx` risk by extracting current-production Team Detail data loading and pure tab DTO/view-model derivation into small testable frontend modules, after or alongside the route-state hook work without duplicating it.

Context:
- Original request 2 identified `detail.tsx` as the largest risk point and proposed splitting route sync, React Query/data loading, workflow YAML parse, connector summary, topology graph, tab view-model, style tokens, and navigation action.
- Active task `0006-team-detail-route-extraction-front-end-policy` covers the route-state single-fact-source slice and re-checks stale topology evidence.
- This follow-up must not reimplement route parsing/building and must not include request 7 style tokenization.

Required work:
- Re-check current repository facts after the required pre-task sync and after any landed route-state extraction.
- If `useTeamDetailRouteState` or equivalent already exists, consume its `routeState`; if it does not exist yet, do not build a competing route parser and avoid editing route-sync code except for the narrow minimum needed to pass data into extracted helpers.
- Extract a page-local `useTeamDetailData(scopeId, routeState)` or equivalent hook responsible only for current Team Detail data loading/query composition and query-derived inputs. Keep API/client/backend contract behavior unchanged.
- Extract one or more pure `deriveTeamDetailViewModel(...)` helpers for current production tabs only, generating DTOs for overview/events/members/bindings/assets/advanced where that removes meaningful logic from `detail.tsx` and is testable.
- Keep workflow YAML parsing and connector summary derivation pure where possible; if those concerns are currently coupled to React rendering, extract only the pure derivation layer first.
- Do not invent Team topology graph DTOs if current Team Detail production topology code is absent. If topology production code exists after sync, extract only pure `TopologyNodeViewModel` / `TopologyEdgeViewModel` / entity maps and leave React label rendering inside the tab component.
- Preserve existing visible behavior, route hints, selected run/service/workflow/member behavior, tab behavior, and existing tests.
- Add focused tests for the extracted data/view-model helpers, including empty data, stale/missing route hints, connector summary, workflow parse failure handling, and at least one advanced/bindings/assets tab DTO case if those tabs have current production callers.

Out of scope:
- Do not perform style tokenization or broad inline style cleanup.
- Do not rework TeamDetailChrome tab accessibility; that is handled by the cleanup/a11y task.
- Do not delete legacy Team home files in this task.
- Do not change backend/proto/API/actor/projection/workflow engine/backend paths/backend runtime infrastructure/database/backend tests/backend config/architecture docs.
- Do not create a second route parser or duplicate `shared/navigation/teamRoutes.ts` behavior.

Dependency and conflict policy:
- If an active FKST task is already editing the same route-state lines in `detail.tsx`, narrow this task to non-overlapping data/view-model extraction or fail closed with a dependency note instead of producing conflicting edits.
- Prefer small extractions with real current callers over a wholesale rewrite of `detail.tsx`.
- If the file count is below the PR threshold, keep the work in automation state and do not manufacture churn.

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
