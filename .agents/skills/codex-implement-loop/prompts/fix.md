# 任务：修复 review round {{review_round}} 在 PR #{{pr_number}}（issue #{{issue_number}}）上的发现

你以无人值守模式在 worktree `{{worktree_path}}` 中工作，对应分支 `{{branch}}`，原 implement codex 已在此 worktree 完成了上一轮代码。

## 必读（按顺序）

1. **Review 报告**：`{{review_report_path}}` —— 这是 subagent reviewer 本轮的判决书，每个 `F<N>` finding 必须逐条响应。最后一行 `REVIEW_VERDICT:rework:...` 是你被 dispatch 的原因。
2. **PR 当前 diff**（PR 头部的最新状态）：
   ```bash
   gh pr diff {{pr_number}} --repo $REPO
   # 等价：git diff origin/{{base_branch}}...HEAD
   ```
3. **上一轮 implement summary**：`$REPO_ROOT/.implement-loop/runs/implement-issue-{{issue_number}}.md` —— issue 是什么需求、上一轮做了什么。
4. **历史 review + fix**（如果 `{{review_round}} > 1`）：`$REPO_ROOT/.implement-loop/reviews/pr{{pr_number}}-round*.md` 和 `$REPO_ROOT/.implement-loop/runs/fix-pr{{pr_number}}-round*.md` —— 不要把上一轮已经驳回过的"修复"再交一次。
5. **CLAUDE.md** + reviewer 引用的 `docs/canon/`。
6. **原 issue body**：`gh issue view {{issue_number}} --repo $REPO --json title,body,comments` —— reviewer 说"issue 要求 X 但 PR 没做"时，回 issue 原文确认 reviewer 没误读。

## 把 reviewer 的 findings 分类，逐条处理

对 review 报告里每个 `F<N>` finding（按 reviewer 的 severity 排序，blocking 优先）：

- **(A) 修得动 + 在 PR scope 内** —— 按 reviewer 的 "What would change your verdict" 改文件。`scope` = PR 当前已改过的文件 + 同 issue 显然相关的紧邻文件。
- **(B) 修得动 + 需要 scope-extend** —— 打印 `SCOPE_EXTEND: <file> <reason>` 后再改。reason 必须能映射回 issue body 或 reviewer 的具体引用，不能是"顺手清理"。
- **(C) Reviewer 误读** —— 不改代码；在 fix summary 的 "Rejected as false positive" 列项中给出反驳证据（"reviewer 引用的 file:line 在本 PR 三点 diff 中不存在 / reviewer 引用的 CLAUDE 条款其实允许这种写法 / reviewer 的 issue body 引用与 issue 原文不符"）。**不要在代码里加注释驳斥 reviewer**——所有反驳走 summary。
- **(D) Reviewer 之间矛盾**（与本轮自身或上一轮其他 finding 互斥） —— 不动；在 summary 标为 conflict，末尾打印 `FIX_DONE:{{pr_number}}:round-{{review_round}}:blocked` 并 `FIX_BLOCKED_REASON: conflict: <短描述>`。
- **(E) 超出 fix codex 权限**（reviewer 要求删除整个 feature / 拆成多个 PR / 改一个其它 cluster 也在依赖的核心类型） —— 不动；summary 标 human-decision，末尾打印 `FIX_BLOCKED_REASON: human-decision: <短描述>`。

## 硬约束

1. **只解 reviewer 的 finding，不主动添新功能**。即使你看到代码里别处有问题，那是下一个 issue 或独立 PR 的事。
2. **不动 git history**：禁止 amend / rebase / squash；fix 走新提交（controller 会做 commit）。
3. **保留 implement 阶段的 `// Implement (issue #N):` 注释块**；如果修复让原注释不准了，更新它，不要删。修复点本身额外加：
   ```
   // Fix (review round {{review_round}}, F<N>):
   //   <reviewer 指出的问题，一行>
   //   <你这次怎么改的，一行>
   ```
   3-4 行即可；机械改动无需注释。
4. **跑测试**：每个修复涉及的代码所在测试项目，按下面命令重跑，必须全过：
   ```bash
   dotnet test <project>.Tests.csproj --nologo --no-build 2>&1 | tail -20
   ```
5. **架构守卫**（controller 会再跑一遍，本地先过节省一轮）：
   ```bash
   bash $REPO_ROOT/tools/ci/architecture_guards.sh
   bash $REPO_ROOT/tools/ci/test_stability_guards.sh
   ```
6. **不要 commit / push / checkout / gh pr** —— controller 包办。
7. **dotnet 命令带 `--nologo`**。
8. **不安装新依赖**。
9. **不动外部仓库**。

## 流程

1. 读 review 报告、读 PR diff、读 issue body、读历史轮（如有）。
2. 给每个 finding 分类（A/B/C/D/E）。
3. 打印 `PLAN:` 多行，每行 `F<N>: <分类> <一句话动作>`。
4. **应用 (A) 和 (B) 的修复**：每个 finding 改完后立即验证（编译 + 对应测试）。
5. 跑架构守卫（硬约束 5）。
6. `git -C {{worktree_path}} add -A && git -C {{worktree_path}} status`；**不要 commit**。
7. 写 fix summary 到 `{{fix_summary_path}}`：
   ```markdown
   ---
   schema: implement-loop-fix-v1
   pr_number: {{pr_number}}
   issue_number: {{issue_number}}
   review_round: {{review_round}}
   applied_count: N
   rejected_count: M
   blocked_count: K
   fixed_at: <ISO8601>
   ---

   ## Applied
   - F1 (A) `path/file.cs:LineRange`: <做了什么修复> (addresses reviewer "What would change your verdict")
   - F2 (B) `path/other.cs`: SCOPE_EXTEND reason: <…>; <做了什么>

   ## Rejected as false positive
   - F3: reviewer 引用了 `xxx.cs:42`，但三点 diff 中本 PR 没改该文件（证据：`gh pr diff ... | grep xxx.cs` 返回空）

   ## Blocked (conflict / human-decision)
   - F4: <reviewer 的要求> vs <本轮另一 finding 或 CLAUDE 条款 X>，无法同时满足

   ## Build / test results
   - dotnet build: pass
   - tests: <命令> → N passed, 0 failed
   - architecture_guards.sh: pass
   - test_stability_guards.sh: pass

   ## Self-assessment: will the next review pass?
   <一段：你认为下一轮 reviewer 会不会给 pass；如果觉得不会，列哪些 finding 你处理得不彻底但又无法做得更好；这段不是装腔，是给 controller / 人类后续决策的输入>
   ```
8. **最末行**打印（精确格式）：
   ```
   FIX_DONE:{{pr_number}}:round-{{review_round}}:<status>
   ```
   `<status>` ∈ {ok, blocked}：
   - `ok`：所有 (A) 和需要的 (B) 修复完成，build/test/guard 全过。
   - `blocked`：存在 (D) conflict 或 (E) human-decision 类 finding，且未被其它 fix 抵消，无法 closeout。

   如果 status == blocked，**额外**打印一行：
   ```
   FIX_BLOCKED_REASON:<short reason>
   ```

## 红线

- worktree 外**唯一可写**：`{{fix_summary_path}}` 和（如有 SCOPE_EXTEND）`$REPO_ROOT/.implement-loop/runs/scope-extend-issue-{{issue_number}}.log`。
- 禁止 `git commit` / `git push` / `git checkout` / `git rebase` / `git amend` / `gh pr *`。
- 禁止改与 reviewer findings 无关的代码（哪怕你觉得别处更糟，也不是你的事）。
- 禁止安装新依赖。
- 禁止 disable / skip 测试。
- 禁止 `Task.Delay` 测试节奏。
- 禁止把上一轮 reviewer 已经驳回过的修复（在 `pr{{pr_number}}-round{{review_round-1}}.md` 里）重复交一次——controller 会发现并把整轮判为 stuck。
- 禁止读取或写入其它 issue 的 worktree (`.implement-loop/worktrees/issue-<other>/`)。
- 禁止在代码里加文字反驳 reviewer；反驳走 summary。

开始执行。
