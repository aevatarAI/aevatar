# 整理 Team refactor 候选分支并提 PR

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

## Host Protected Path Policy

Do not modify backend, proto, API, actor, projection, workflow engine/backend paths, backend/runtime infrastructure, database, backend tests, backend config, or architecture docs. Frontend page runtime helpers under `apps/aevatar-console-web/src/pages/**/runtime/**` are allowed for frontend/product tasks. If a backend error appears, only locate, reproduce, record, attribute, and risk-label it, then write exactly:

`后端错误已记录，按用户要求未修改`

## Trigger Source

User-created FKST inbox task from the Aevatar control repo.

## Root Cause

Use real product/UI investigation from the Aevatar host repo and the runbook. Do not infer from memory.

## Fix Path

Implement the narrowest frontend/product slice that satisfies the task details below, using isolated FKST worktrees and the configured integration branch as the PR base. Do not merge directly into the integration branch. Do not create PRs below the runbook threshold unless the user explicitly authorized that exception.

## Task Details

请整理 FKST 已经为 Aevatar Team refactor 产出的候选实现分支，形成一个正常 GitHub PR，而不是直接合入 integration branch。

必须遵守：
- 不要自动 merge 到 `auto-frontend-dev`。
- 不要直接 push implementation/reconciliation commit 到 `auto-frontend-dev`。
- PR base 必须是 `auto-frontend-dev`。
- PR head 使用新的 reviewable branch，按 Aevatar 规则命名，例如 `refactor/2026-06-03_team-refactor-runtime-detail-topology-a11y` 或同等清晰名称。
- PR 创建后停止在 waiting for human review/merge。
- PR 需要说明第 7 条样式 token 化已排除。

需要纳入的 FKST 候选分支：
- `dev-rc-20260603-0014-team-runtime-semantics-v3_from_auto-frontend-dev`
- `dev-rc-20260603-0016-team-detail-data-route-v3_from_auto-frontend-dev`
- `dev-rc-20260603-0017-team-detail-viewmodel-v3_from_auto-frontend-dev`
- `dev-rc-20260603-0018-team-topology-model-v3-r3_from_auto-frontend-dev`
- `dev-rc-20260603-0019-team-cleanup-a11y-v3_from_auto-frontend-dev`

已知情况：
- 这些候选分支由 FKST 后台产出并已 push 到 origin。
- FKST 状态把 0014/0016/0017/0018/0019 放进 failed，但原因看起来是 implement artifact / trace 归档环节失败，不是候选实现不存在。
- 0017 与 0016 在 `apps/aevatar-console-web/src/pages/teams/detail.tsx` 有冲突。
- 0018 与前面详情抽取分支在 `detail.tsx` 和 `tabs/TeamOverviewTab.tsx` 有冲突，需要由 FKST 自己在新的 PR branch 里 reconcile。
- 0015 `team-operational-model-v3` 仍在方案裁决/未实现状态，不要把它伪装成已完成；可以在 PR 描述中标为后续独立任务。

PR 目标内容：
- 汇总第 1/2/3/4/5/6/8 条中已经有候选实现的部分，不包含第 7 条。
- 保留 backend/proto/API/actor/projection/workflow engine/database/config/architecture docs 禁改边界。
- 如果候选实现之间冲突，优先生成一个可测试、可 review 的整合分支；不要在 integration branch 上解冲突。
- 如果无法完成 PR，记录 blocker 和准确原因，不要 fallback 到 auto merge。

验证要求：
- `git diff --check`
- Aevatar console frontend 的 focused Team tests
- `npm run tsc` 或项目等价 type check
- 如果 tests 被修改，运行 `bash tools/ci/test_stability_guards.sh`
- 若能启动本地前端页面，再做可见浏览器 smoke；不能启动则在 PR/status card 写明原因。

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
