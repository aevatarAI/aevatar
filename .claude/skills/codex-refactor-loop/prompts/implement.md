# 任务：实施 ${CLUSTER_ID}

在 worktree `${WORKTREE_PATH}`、分支 `${BRANCH}` 中实施。

## 必读

1. `$REPO_ROOT/CLAUDE.md`
2. `$REPO_ROOT/.refactor-loop/runs/audit-iter-${ITERATION}.md` 的 `${CLUSTER_ID}` 段
3. 相关 `docs/canon/`

## Cluster framing

- Old pattern: `${OLD_PATTERN}`
- New principle: `${NEW_PRINCIPLE}`
- Scope paths:
${SCOPE_PATHS}
- Verification hints:
${VERIFICATION_HINTS}

## 流程

1. 读 audit 段和所有 scope 文件。
2. 打印 `PLAN:`，一行一个具体改动。
3. 实施，仅清理违规点；不新增功能、flag、模块。极小 helper 必须注释 `refactor helper, no behavior change`。
4. 重构的类/关键方法新增或更新：
   ```csharp
   // Refactor (iter${ITERATION}/${CLUSTER_ID}):
   //   Old pattern: ${OLD_PATTERN}
   //   New principle: ${NEW_PRINCIPLE}
   ```
   3-5 行内，说明代码自身。
5. 编译：`dotnet build aevatar.slnx --nologo`；最多修 5 次。
6. 跑 `verification_hints` 指定测试；不足就补；最多修 5 次。
7. 跑 `bash $REPO_ROOT/tools/ci/test_stability_guards.sh` 和 `bash $REPO_ROOT/tools/ci/architecture_guards.sh`；cluster 特定 guard 见 hints。
8. 改 proto 时本地重生成并确认编译。
9. `git add -A && git status` 只用于确认改动。
10. 写 `$REPO_ROOT/.refactor-loop/runs/implement-${CLUSTER_ID}.md`：文件列表、测试结果、deviation、`SCOPE_EXTEND`。

## Scope

- 只能改 `Scope paths`。
- `.refactor-loop/` 只允许写：`runs/implement-${CLUSTER_ID}.md` 与可选 `runs/scope-extend-${CLUSTER_ID}.log`。
- 范围外文件先打印共享规则规定的 `SCOPE_EXTEND` 行。

## Marker

末尾打印：`IMPLEMENT_DONE:${CLUSTER_ID}:<ok|partial|blocked>`
