# 为 Team runtime semantics 候选分支提 PR

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

这是一个单 inbox 试跑任务，只处理一个 FKST 候选分支，目的是验证 FKST 产出 branch + PR 的流程，不做多分支聚合，避免 detail.tsx 冲突。

必须遵守：
- 不要自动 merge 到 `auto-frontend-dev`。
- 不要直接 push implementation/reconciliation commit 到 `auto-frontend-dev`。
- PR base 必须是 `auto-frontend-dev`。
- PR head 使用新的 reviewable branch，按 Aevatar 规则命名，例如 `refactor/2026-06-03_team-runtime-semantics` 或同等清晰名称。
- PR 创建后停止在 waiting for human review/merge。
- 这个试跑只处理第 5 条公共状态/格式化/排序工具收敛，以及它对首页/runtime helpers 的窄影响。
- 不处理第 7 条样式 token 化。
- 不处理 0015 operational model。
- 不处理 0016/0017/0018/0019，避免详情页冲突。

需要纳入的 FKST 候选分支仅此一个：
- `dev-rc-20260603-0014-team-runtime-semantics-v3_from_auto-frontend-dev`

候选分支信息：
- commit `dfb725fcd refactor(teams): 统一运行语义助手`
- 主要改动：新增 `apps/aevatar-console-web/src/pages/teams/runtime/runtimeRunSemantics.ts` 和测试；收敛 `home.tsx`、`workflowOperationalUnits.ts`、`teamRuntimeLens.ts` 中重复的 run 状态判断、排序、label 语义。
- 这条分支不应该触碰 `detail.tsx` 大型拆分冲突区。

PR 目标：
- 创建一个普通 GitHub PR，而不是合并到 integration branch。
- PR 描述采用类似 #1693 的形态：Problem / Solution / Impact Paths / Verification / Backend Boundary / Merge Policy。
- PR 状态卡片必须写明 base/head、文件数、验证、backend boundary、merge policy、next action。
- labels/status card 遵守 runbook。

验证要求：
- `git diff --check`
- focused tests for `runtimeRunSemantics` and `workflowOperationalUnits`
- Aevatar console frontend Team/home/runtime 相关 focused tests，如现有测试入口可用
- `npm run tsc` 或项目等价 type check
- 如果 tests 被修改，运行 `bash tools/ci/test_stability_guards.sh`
- 若本地环境无法运行某项验证，必须在 PR/status card 诚实记录，不要把未运行写成通过。

如果无法完成 PR：
- 记录 blocker 和准确原因。
- 不要 fallback 到 auto merge。

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
