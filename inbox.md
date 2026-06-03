# Team detail view model extraction

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

Rationalized source requirements covered: original item 2, excluding topology which has its own task.

Current repo facts:
- `apps/aevatar-console-web/src/pages/teams/detail.tsx` mixes data shaping for overview/events/members/bindings/assets/advanced with page orchestration.
- Existing tab components include `tabs/TeamOverviewTab.tsx` and `tabs/TeamMembersTab.tsx`; other detail tab rendering may still live inline.

Goal:
Move non-topology detail page DTO derivation out of React component flow into pure, testable view-model functions.

Expected implementation:
- Add `deriveTeamDetailViewModel(...)` or a small set of pure derivation helpers under the Team page/runtime area.
- Cover overview, events, members, bindings, assets, and advanced DTOs only where those DTOs currently exist in `detail.tsx`; do not invent new product surfaces.
- Keep React rendering in components/tabs; keep pure derivation free of React elements, side effects, navigation calls, and query hooks.
- Update `detail.tsx` to assemble the derived DTOs and pass them to existing tab/components.

Acceptance criteria:
- `detail.tsx` loses a meaningful amount of inline data-shaping responsibility without changing visible behavior.
- Pure view-model tests cover representative loaded, empty, and partial-data states.
- Existing detail/home tests still pass or unrelated failures are documented.

Explicit exclusions:
- Do not implement original item 7 style tokenization.
- Do not perform topology graph extraction in this task except to avoid conflicts; topology has a separate task.
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
