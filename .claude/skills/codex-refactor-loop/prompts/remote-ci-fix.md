# 任务：修复 PR 远端 CI 失败 ${CHECK_NAME}

worktree: `${WORKTREE_PATH}`，分支 `${BRANCH}` （通常是 trunk）。
PR: `${PR_NUMBER}`，失败 check: `${CHECK_NAME}`，run url: `${RUN_URL}`。

## 必读

1. `/Users/auric/aevatar/CLAUDE.md`
2. 失败日志：`${FAILURE_LOG_PATH}` —— 完整 stderr/stdout 的最后 200-1000 行
3. 最近 commits（可能引入失败的）：通过 `git log --oneline -10 origin/${BASE_BRANCH}..HEAD` 查看
4. 失败 check 对应的本地 job 脚本（如有，位于 `.github/workflows/`）

## 工作流

1. **诊断**：
   - 读失败日志，识别根因 token（首个错误行、第一条 assertion fail、第一条 build error）
   - `git blame` / `git log -S <token>` 找哪个 commit 引入了变化
   - 用 `git diff <last-known-good>..HEAD -- <suspect-paths>` 看具体改动
   - 打印 `DIAGNOSIS: <root cause one-liner> | <suspect commit shas>`

2. **本地复现**：
   - 找对应的本地命令（如 `dotnet test test/<project>/... --filter <name>`）
   - 在 worktree 内跑确认能复现远端失败
   - **如本地不能复现** → 这是 infra/env 问题（如缺 docker service），打印 `LOCAL_REPRO: failed | reason: <env gap>` 并停止；不要盲改代码
   - **如本地复现** → 继续

3. **修复**：
   - 按 CLAUDE.md 原则修复，**不破坏已合并 cluster 的成果**
   - 改动**最小化**：只动失败 test 直接相关的代码 + test
   - 如失败是 test 写错（不是产线 bug），改 test；如失败是产线 bug，改产线
   - 加 `// Fix (remote-ci/${CHECK_NAME}):` 注释说明根因

4. **本地验证**：
   - 重跑失败 test：必须 pass
   - 跑 `bash tools/ci/architecture_guards.sh` + `bash tools/ci/test_stability_guards.sh`：必须 pass
   - 如果失败是某个特定 guard 出来的，重跑那个 guard

5. **暂存**：
   - `git add -A && git status`
   - **不 commit**（controller 处理）

6. 摘要写入 `/Users/auric/aevatar/.refactor-loop/runs/remote-ci-fix-${CHECK_NAME}-${SHA_SHORT}.md`：
   - 根因分析
   - 改动文件列表
   - 本地复现/验证命令
   - 任何 deviation

7. 末尾打印 `REMOTE_CI_FIX_DONE:${CHECK_NAME}:<status>` 其中 status ∈ {ok, infra, blocked}。

## 红线

- worktree 外**唯一可写**：`/Users/auric/aevatar/.refactor-loop/runs/remote-ci-fix-${CHECK_NAME}-${SHA_SHORT}.md`
- 禁止 commit/push/checkout/install
- 禁止 disable 测试或加 `[Skip]` 让 CI 绿
- 禁止改 worktree 外的其它 cluster 工作
- 禁止 hypothetical 修复——必须本地复现后再改
- 禁止扩 scope 到失败 test 直接相关之外的代码

开始执行。
