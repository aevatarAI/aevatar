# Team legacy cleanup and detail tabs accessibility

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

Rationalized source requirements covered: original items 4 and 8.

Current repo facts:
- Current Team directory no longer showed `LegacyTeamsHome.tsx` or `TeamsHomeRosterV0.tsx` in a quick file scan; verify again from current HEAD before acting.
- `apps/aevatar-console-web/src/pages/teams/components/TeamDetailChrome.tsx` has a `role="tablist"` detail navigation area.

Goal:
Remove/record obsolete Team home implementations if they still exist, and make Team detail tabs follow accessible tab semantics and keyboard navigation.

Expected implementation:
- Search for `LegacyTeamsHome`, `TeamsHomeRosterV0`, feature flags, dynamic imports, and route references.
- If old files exist and are truly unused, delete them with tests/import cleanup. If they no longer exist, record a no-op cleanup result in the FKST verdict rather than inventing work.
- Update `TeamDetailChrome.tsx` tabs to include complete `role="tab"`, `aria-selected`, stable `id`, `aria-controls`/panel linkage where local structure allows, and correct `tabIndex`.
- Add roving keyboard behavior for ArrowLeft/ArrowRight and Home/End if not already present.
- Keep visual styling and layout unchanged except for necessary focus affordance improvements.

Acceptance criteria:
- Team detail tabs are operable via keyboard and expose selected state to assistive tech.
- Tests cover tab roles/selected state and keyboard movement.
- Legacy cleanup is either completed or explicitly recorded as already absent with search evidence.

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
