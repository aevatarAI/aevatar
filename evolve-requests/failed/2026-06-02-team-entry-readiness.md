# Aevatar console Team entry readiness loop

## Source
User requested restarting the work through globally installed fkst / global codex-refactor-loop, not Aevatar repo-local `.claude` and not a direct Codex App worktree thread.

## Host
- repo: `/Users/potter/Desktop/sbt_project/aevatar`
- integration branch: `auto-frontend-dev`
- rules: `/Users/potter/Desktop/sbt_project/aevatar/AGENTS.md`
- global skill: `/Users/potter/.codex/skills/codex-refactor-loop/SKILL.md`
- package root: `/Users/potter/.local/lib/fkst/current/share/fkst`

## Task
Run the fkst-backed global codex-refactor-loop for `apps/aevatar-console-web/src/pages/teams`.
Start from product/UI discovery around Team entry readiness and Team Test affordances.
Use three solver views: minimal / structural / delete, then meta-judge consensus.
Implement only agreed frontend slices in fkst candidate worktrees, not in the host main working tree.

## Scope
Allowed paths are frontend Team module paths under:
- `apps/aevatar-console-web/src/pages/teams/**`
- directly related frontend tests under the same module

Forbidden paths:
- backend
- proto
- API
- actor
- projection
- workflow
- runtime
- database
- backend tests
- backend config
- architecture docs

Do not touch GitHub PR/issue #1642.
Do not use the previously stopped Codex App worktree `/Users/potter/.codex/worktrees/7df9/aevatar` as source of truth.
Do not use Aevatar repo-local `.claude/skills/codex-refactor-loop`.

## PR policy
Do not create a PR until there are at least 10 meaningful tracked files in the same Team theme.
Do not churn files only to hit the threshold.
If the theme naturally cannot reach 10 meaningful files, record the blocker and wait for human override.
When threshold is met, PR base must be `auto-frontend-dev` and include validation results.

## Verification
At minimum:
- `git diff --check`
- `bash tools/ci/test_stability_guards.sh` when tests change
- focused frontend tests for changed Team surfaces
- `pnpm --dir apps/aevatar-console-web tsc`

## Expected status output
Write a clear Chinese transcript in runtime artifacts / mailbox comments with:
- skill/global entry confirmation
- branch/worktree status
- decision chain
- cumulative file count
- validation commands/results
- whether PR was created or why not
