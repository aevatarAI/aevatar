# Team cleanup and tabs accessibility

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

Scope: implement requests 4 and 8 only.

Goal:
Remove unused old Team home implementations if truly unreferenced, and make Team detail tabs accessible.

Current evidence:
- /teams route points through apps/aevatar-console-web/src/pages/teams/index.tsx to home.tsx.
- LegacyTeamsHome.tsx and TeamsHomeRosterV0.tsx appear unreferenced by rg, but must be checked for dynamic imports, feature flags, or tests before removal.
- TeamDetailChrome.tsx has role="tablist" around line 99 but tab buttons currently use aria-current rather than role="tab" / aria-selected and do not implement roving keyboard focus.

Required work:
- Confirm LegacyTeamsHome.tsx and TeamsHomeRosterV0.tsx have no hidden references, dynamic imports, route configs, or test dependencies. Delete them if unreferenced; otherwise archive or document why retained.
- Update TeamDetailChrome tabs to use proper tablist semantics: role="tab", aria-selected, stable ids/aria-controls if corresponding panels can be wired safely, and keyboard navigation for ArrowLeft/ArrowRight/Home/End with roving focus.
- Add focused tests for tab a11y attributes and keyboard behavior.

Out of scope:
- Do not perform style tokenization or broad inline-style cleanup.
- Do not modify Team operational model in this task beyond what is necessary for tests.
- Do not touch backend/proto/API/actor/projection/workflow/runtime/database paths.

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
