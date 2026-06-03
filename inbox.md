# 为 Team runtime semantics 候选分支重新提 PR

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

## Required PR Output

Do not auto-merge implementation or reconciliation work into `auto-frontend-dev`.
After implementation and verification, push the implementation branch and open or update a normal PR targeting `auto-frontend-dev`.
If related slices are below the runbook PR threshold individually, aggregate them into one reviewable PR batch rather than merging directly.
Stop at PR ready / waiting for human review and merge.
After FKST creates or updates the PR, complete the GitHub visibility transaction through the local codex-refactor-loop bridge:

```bash
/Users/pottersun/Desktop/sbt_projects/aevatar-frontend-loop/scripts/decorate-fkst-pr-with-codex-loop --once
```

Do not add runbook labels or status-card templates to FKST package departments. FKST `github_publisher` is only the low-level GitHub executor; labels, status cards, phase cards, and readback belong to the codex-refactor-loop controller layer.
If an approach solver writes a `Rule exception:` field, it must be exactly `Rule exception: none` unless this task truly changes host project rule sources or trusted-boundary policy. User-authorized smaller PRs are ordinary runbook paths, not rule exceptions.

## Host Protected Path Policy

Do not modify backend, proto, API, actor, projection, workflow engine/backend paths, backend/runtime infrastructure, database, backend tests, backend config, or architecture docs. Frontend page runtime helpers under `apps/aevatar-console-web/src/pages/**/runtime/**` are allowed for frontend/product tasks. If a backend error appears, only locate, reproduce, record, attribute, and risk-label it, then write exactly:

`后端错误已记录，按用户要求未修改`

## Trigger Source

User-created FKST inbox task from the Aevatar control repo.

## Root Cause

Use real product/UI investigation from the Aevatar host repo and the runbook. Do not infer from memory.

## Fix Path

Implement the narrowest frontend/product slice that satisfies the task details below, using isolated FKST worktrees and the configured integration branch as the PR base. Do not merge directly into the integration branch. Do not create PRs below the runbook threshold unless the user explicitly authorized the smaller PR path.

## Task Details

Rerun note: PR #1742 was manually closed by the user as a trial PR. Do not reopen #1742, do not treat it as completed, and do not reuse its closed PR as the output. Produce a fresh review branch and a fresh normal PR targeting auto-frontend-dev, then stop for human review/merge.

Previous rerun note: inbox 0022 produced the correct six-file business commit but was blocked by FKST control-layer artifact validation after protected witness insertion moved the `⟦AI:FKST⟧` sentinel away from the final line. The local FKST package has been hotfixed and `scripts/check-fkst-local-patches` passed. Do not reuse the blocked 0022 output as the final publication fact; rerun this task to create a fresh branch and PR through the normal FKST publisher flow.

This is a single inbox retry task. Only process one FKST candidate branch to validate FKST branch + PR output. Avoid detail.tsx conflict risk.

Mandatory constraints:
- Do not auto-merge to `auto-frontend-dev`.
- Do not directly push implementation/reconciliation commits to `auto-frontend-dev`.
- PR base must be `auto-frontend-dev`.
- PR head must use a fresh reviewable branch with an Aevatar-style name, for example `refactor/2026-06-03_team-runtime-semantics-rerun` or an equivalent clear name. Do not reuse the closed #1742 head if it would update/reopen that PR.
- PR creation must stop at waiting for human review/merge.
- This trial handles only item 5: shared status/format/sort runtime semantics for Team frontend pages/helpers.
- Do not handle item 7 style tokenization.
- Do not handle 0015 operational model.
- Do not handle 0016/0017/0018/0019 detail/topology/a11y slices.
- If an approach solver writes a `Rule exception:` field, that line must be exactly `- Rule exception: none`; user-authorized smaller PRs are ordinary runbook paths, not rule changes.

Input candidate to port:
- `dev-rc-20260603-0014-team-runtime-semantics-v3_from_auto-frontend-dev`
- commit `dfb725fcd refactor(teams): 统一运行语义助手`
- Main change: add `apps/aevatar-console-web/src/pages/teams/runtime/runtimeRunSemantics.ts` and tests; converge duplicated run status/sort/label semantics in `home.tsx`, `workflowOperationalUnits.ts`, and `teamRuntimeLens.ts`.
- This branch should not touch `detail.tsx`.

Expected implementation scope:
- exactly the narrow six meaningful frontend files unless the current base makes an equivalent minimal adjustment necessary:
  - `apps/aevatar-console-web/src/pages/teams/runtime/runtimeRunSemantics.ts`
  - `apps/aevatar-console-web/src/pages/teams/runtime/runtimeRunSemantics.test.ts`
  - `apps/aevatar-console-web/src/pages/teams/home.tsx`
  - `apps/aevatar-console-web/src/pages/teams/workflowOperationalUnits.ts`
  - `apps/aevatar-console-web/src/pages/teams/workflowOperationalUnits.test.ts`
  - `apps/aevatar-console-web/src/pages/teams/runtime/teamRuntimeLens.ts`

PR requirements:
- Create a normal GitHub PR, not an integration merge.
- PR body should use the visible shape: Problem / Solution / Impact Paths / Verification / Backend Boundary / Merge Policy.
- Status card must state base/head, file count, verification, backend boundary, merge policy, and next action.
- Status card must state this is a user-authorized single-candidate trial PR with 6 meaningful frontend files, below the default 10-file threshold, and intentionally not aggregated with 0015/0016/0017/0018/0019 or style tokenization to avoid detail.tsx conflicts.
- Labels/status cards/readback are applied by the codex-refactor-loop bridge after FKST publisher ack, not by FKST native github_publisher templates.

Verification requirements:
- `git diff --check`
- focused tests for `runtimeRunSemantics` and `workflowOperationalUnits`
- Aevatar console frontend Team/home/runtime focused tests if available
- `npm run tsc` or project-equivalent type check
- if tests are modified, `bash tools/ci/test_stability_guards.sh`
- If local toolchain cannot run one item, record it honestly in the PR/status card; do not mark unrun checks as passing.

If PR creation cannot complete:
- record the blocker and exact reason.
- do not fallback to auto-merge.

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
