# 任务：实现 issue #{{issue_number}} — {{issue_title}}

你以无人值守模式在 worktree `{{worktree_path}}` 中工作，对应分支 `{{branch}}`，基于 `{{base_branch}}` 创建。

## 必读

1. `$REPO_ROOT/CLAUDE.md` 全部强制条款（"顶级架构约束"、"字段命名与 Metadata 决策树"、"Actor / Projection / Codex CLI 调用规范" 等）。
2. `$REPO_ROOT/AGENTS.md` 协作规范（如存在）。
3. **issue 内容**：直接拉取，不要凭记忆——
   ```bash
   gh issue view {{issue_number}} --repo {{repo}} --json title,body,labels,comments
   ```
   - `body` 是需求主体；如果含跨链接/嵌入图，原文阅读后再动手。
   - `comments` 里可能有补丁说明、范围澄清、决策记录——一并读。
   - 如果 issue body 缺少明确验收标准，输出 `IMPLEMENT_DONE:{{issue_number}}:blocked` 并把"缺什么"写进 summary，不要瞎实现。
4. 相关 `docs/canon/` 权威文档（如果 issue 标签或 body 引用了某个 ADR / canon 文件，全文读完再写代码）。
5. 上游已合并 PR 链：以下分支已合并到本分支基础，**不要重复实现已经存在的能力**：
   ```bash
   git -C {{worktree_path}} log --oneline {{base_branch}}..HEAD || true
   git -C {{worktree_path}} log --oneline origin/{{base_branch}}..HEAD || true
   ```

## 硬约束

1. **作用域限定**：只动 issue 直接描述的能力。新增任何文件、改任何"顺手优化"必须打印 `SCOPE_EXTEND: <file> <reason>` 再做；reason 必须能映射回 issue body 的某一句话。
2. **不新增功能**：本 loop 是 milestone 推进器，**不是探索器**。不引入新接口/新 flag/新模块，除非 issue 明确要求；issue 要求的能力必须落到代码，不许留 TODO。
3. **架构合规**：实施过程遇到 CLAUDE.md 与 issue 描述冲突时，**以 CLAUDE.md 为准**，并在 summary "Deviations" 节说明你做了什么取舍以及为何。
4. **proto 改动**：如果 issue 要求改 `.proto`，**必须本地重生成 + 编译通过**；保留字段不重用，删字段加 `reserved`。
5. **测试**：
   - 新增/改的公开行为必须有测试覆盖。
   - 禁止 `Task.Delay` 作断言节奏；用确定性 awaiter。
   - 禁止 `[Skip]` / `[Trait("Category","Manual")]` 让 CI 绿。
   - 测试文件命名 `*Tests.cs`，xUnit + FluentAssertions。
6. **架构守卫**：以下两个守卫必须本地通过（**controller 还会再跑一次**，本地先跑能节省一轮 codex）：
   ```bash
   bash $REPO_ROOT/tools/ci/architecture_guards.sh
   bash $REPO_ROOT/tools/ci/test_stability_guards.sh
   ```
7. **dotnet 命令带 `--nologo`**。
8. **不动外部仓库**：禁止建议改 NyxID / chrono-* 任何代码；只能消费它们已发布的契约。
9. **不要 git commit / git push / git checkout / gh pr create**——controller 负责所有 git 拓扑动作。你只把改动留在 worktree 工作区。

## 流程

1. **读 issue**：跑 `gh issue view` 拉 body + comments，全文读完。
2. **读上下文**：列 `scope_paths` 候选（issue 提到的文件路径 + 你查到的相关产线文件），读完每个文件再动笔。
3. **打印 PLAN**：以 `PLAN:` 前缀输出多行实施计划，每行一项（"改 X 类的 Y 方法"、"在 Z 项目下新增 Foo.cs"），controller 不读 PLAN 但 reviewer 会读。
4. **实施**：按计划改代码；改一类带一段：
   ```
   // Implement (issue #{{issue_number}}):
   //   Behavior: <issue 描述中这块负责的语义，一行>
   //   Why this shape: <为什么这么写，一行；不是 changelog>
   ```
   3-5 行。如果改动**纯粹是 issue 描述里的机械迁移**（无设计决策），注释可省。
5. **编译**：`dotnet build aevatar.slnx --nologo`；失败时修复，最多 5 次迭代。
6. **跑测试**：
   ```bash
   dotnet test <被改动项目对应的 *.Tests.csproj> --nologo --no-build 2>&1 | tail -20
   ```
   挑被你改动的代码所在测试项目；不要跑全量。失败修复，最多 5 次。
7. **跑架构守卫**（见硬约束 6），失败修复。
8. **`git -C {{worktree_path}} add -A && git -C {{worktree_path}} status`** —— 确认改动。**不要 commit**。
9. **写 summary** 到 `{{summary_output_path}}`：
   ```markdown
   ---
   schema: implement-loop-summary-v1
   issue_number: {{issue_number}}
   issue_title: {{issue_title}}
   pr_branch: {{branch}}
   base_branch: {{base_branch}}
   implemented_at: <ISO8601>
   ---

   ## What the issue asked for
   <2-5 行；用你自己的话复述需求，证明你读懂了，不是抄 body>

   ## Files changed
   - path/to/File1.cs (+N -M)
   - path/to/File2.proto (+N -M)
   - test/path/to/File1Tests.cs (+N, new)

   ## Tests run
   ```bash
   <你跑过的命令>
   ```
   - 结果：<N passed, 0 failed, K skipped 原因>

   ## Architectural guard runs
   - tools/ci/architecture_guards.sh — pass / fail+原因
   - tools/ci/test_stability_guards.sh — pass / fail+原因

   ## Deviations from issue body
   <如果你没完全照 issue 实现，说明原因；如果完全照实现，写 "none">

   ## SCOPE_EXTEND records
   <每个 SCOPE_EXTEND 记录一行，含 file + reason + 对应 issue body 句子>

   ## Blockers (only if status == partial or blocked)
   <如有未实现部分，写明阻塞原因；区分"design ambiguity"/"missing tool"/"depends on un-landed work">
   ```
10. **最末行**打印 `IMPLEMENT_DONE:{{issue_number}}:<status>` 其中 status ∈ {ok, partial, blocked}：
    - `ok`：issue 描述的所有验收点都完成、测试通过、守卫通过。
    - `partial`：核心能力完成但 issue 中明确列出的次要项漏了（在 summary "Blockers" 写清）。
    - `blocked`：issue 描述不清 / 缺前置依赖 / 编译无法通过 —— 不要硬交付，把阻塞写在 summary 后停。

## 红线

- worktree 外**唯一可写**：`{{summary_output_path}}` 和（如有 SCOPE_EXTEND）`$REPO_ROOT/.implement-loop/runs/scope-extend-issue-{{issue_number}}.log`。
- 禁止 `git commit` / `git push` / `git checkout <branch>` / `gh pr *`。
- 禁止安装新依赖（如果 issue 明确要求加包，加包前打印 `DEP_ADD: <package> <reason>`，但仍照做）。
- 禁止跳过测试或加 `[Skip]`。
- 禁止 `Task.Delay` 做测试节奏。
- 禁止把"看起来更整洁"的重构混在 issue 实现里（refactor 走 `codex-refactor-loop`，不是这里）。
- 禁止改 `.implement-loop/` 下除 summary 外的任何文件。
- 禁止读取或写入其它 worktree 的内容（`.implement-loop/worktrees/issue-<other>/`）。

开始执行。
