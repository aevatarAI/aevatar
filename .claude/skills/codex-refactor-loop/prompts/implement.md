# 任务：实施 {{cluster_id}}

你以无人值守模式在 worktree `{{worktree_path}}` 中工作，对应分支 `{{branch}}`。

## 必读上下文

1. 主仓库 `$REPO_ROOT/CLAUDE.md` 全部强制条款。
2. 完整审计：`$REPO_ROOT/.refactor-loop/runs/audit-iter-{{iteration}}.md` 中 "{{cluster_id}}" 一节。
3. `docs/canon/` 下相关权威文档。

## 错误模式 / 设计原则

- **错误模式**：{{old_pattern}}
- **设计原则**：{{new_principle}}

## 硬约束

1. **作用域**：仅修改下列文件；扩展前必须打印 `SCOPE_EXTEND: <file> <reason>`：
{{scope_paths}}
2. **代码注释**：被重构的每个类/关键方法必须新增/更新一段：
   ```
   // Refactor (iter{{iteration}}/{{cluster_id}}):
   //   Old pattern: {{old_pattern}}
   //   New principle: {{new_principle}}
   ```
   3-5 行内；不是 changelog，是代码自我说明。
3. **不新增功能**：不引入新接口、新 flag、新模块；只清理违反点。新增极小辅助类型须注释 "refactor helper, no behavior change"。
4. **测试**：按 `verification_hints` 跑测试，必须通过；测试不足必须补；任何 `Task.Delay` 轮询测试必须改为确定性断言。
5. **架构守卫**：跑 `bash $REPO_ROOT/tools/ci/test_stability_guards.sh` 和 `bash $REPO_ROOT/tools/ci/architecture_guards.sh`，必须通过。其它 cluster 特定守卫见 verification hints。
6. **不依赖外部仓库**：禁止建议在 NyxID/chrono-* 改动。
7. **Protobuf**：如改 proto，必须本地重生成并确认编译通过。
8. **dotnet 命令带 `--nologo`**。

## 流程

1. 读 audit 段、读所有 `scope_paths` 文件。
2. 打印 `PLAN:` 前缀的具体改动计划（一行一项）。
3. 实施。
4. 编译：`dotnet build aevatar.slnx --nologo`，失败时修复，最多 5 次迭代。
5. 跑指定测试。失败则修复（禁止 disable/skip），最多 5 次。
6. 跑架构守卫，失败则修复。
7. `git add -A && git status` 确认改动。
8. **不要 commit**，把改动留在工作树。
9. 摘要写入 `$REPO_ROOT/.refactor-loop/runs/implement-{{cluster_id}}.md`：
   - 修改文件列表（带行数）
   - 测试结果
   - deviation 记录
   - `SCOPE_EXTEND` 记录
10. 末尾打印 `IMPLEMENT_DONE:{{cluster_id}}:<status>` 其中 status ∈ {ok, partial, blocked}。

## 红线

- 禁止改 worktree 外文件，**唯一例外**：可以写入 `$REPO_ROOT/.refactor-loop/runs/implement-{{cluster_id}}.md`（controller 期望的摘要输出位置）和 `$REPO_ROOT/.refactor-loop/runs/scope-extend-{{cluster_id}}.log`（如有 SCOPE_EXTEND 记录）。除此之外 `.refactor-loop/` 一律禁改。
- 禁止 `git commit` / `git push` / `git checkout <branch>`。
- 禁止安装新依赖。
- 禁止跳过测试或加 `[Skip]`。
- 测试禁止用 `Task.Delay` 做断言节奏。

## 附录

`verification_hints` 内容：

{{verification_hints}}

开始执行。
