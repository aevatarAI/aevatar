---
name: codex-refactor-loop
description: Unattended three-phase refactor loop (analyze → implement → verify) driven by codex CLI in isolated git worktrees. Use when user wants fully autonomous parallel refactoring against CLAUDE.md violations, with /loop dynamic wakeups and per-cluster worktree merges.
---

# Codex Refactor Loop — Unattended Three-Phase Mode

## ⭐ 核心原则:GitHub 是系统状态唯一显示面(强制,per Auric 2026-05-20 "核心要做到的就是要把系统的状态完全反映在 github 上")

**Maintainer 打开 GitHub 必须一眼看到完整状态**,不用读本地 log / state.json / ps process / chat history。任何状态变化在 GitHub **立即可见**。

### 必须 reflect 到 GitHub 的状态变化

| 状态变化 | 触发位置 | GitHub 反映方式 |
|---|---|---|
| 派 codex(任何角色) | spawn 同 turn | `## 📊 状态卡片` post 到关联 issue/PR + label transition |
| Codex 完成(任何角色) | task-notification 处理 | update 卡片(或 post 新卡片说"X 已完成,下一步 Y") |
| 共识达成 | meta-judge consensus | `## ✅ 共识卡片` post(详见 Phase 9 Consensus action) |
| Maintainer 评论被识别 | daemon eyes react 后 | `## 📊 状态 — 已收到 maintainer 评论(daemon 识别)` daemon banner |
| Reflector 决议 | META_RESOLVED:<kind> | `## 🤖 meta-reflector decision: <kind>` post + label 转 |
| Escalate human | label 加 🆘 | banner 说"✅ 需要 maintainer 决策:具体什么决策" |
| Phase transition | controller route | label sync(`🔍`→`✅`→`🛠️`→`🚀`→`👀`→`🔧`→`⚙️`→`🎉`) |
| Stuck 4h timeout | controller sweep | banner 说"等了 4h 自动派 reflector 重新评估" |
| iter 完成 | last cluster merged | rollup PR banner + 派 next iter audit |
| Bug 修复 | skill commit | commit 内容 push 到 auto-refact-dev,maintainer 可看 commit diff |

### 反面(❌ 严禁)

- ❌ Codex 在本地跑但 GitHub 上对应 issue/PR 无任何状态卡片(maintainer 不知道 controller 在干什么)
- ❌ Codex 完成后只更新本地 log,不 post GitHub banner
- ❌ Label 在 GitHub 转了但没配 banner 解释(label list 不解释 why)
- ❌ Banner 用模糊语言("处理中""稍等"),应该具体说当前 phase + 下一步 + ETA / 何时介入
- ❌ 多个 daemon 同时跑但 maintainer 看 GitHub 只看到 eyes,不知道还有 codex 在工作

### 0 codex + active task = bug(强制,per Auric 2026-05-20 "按说这个流程应该一直有 codex 工作的")

**铁律**:任何 active phase issue/PR(`🔍 design-solving` / `🔧 fixing` / `👀 reviewing` / `🛠️ implementing`)存在时,**应至少有 1 codex 在跑**。`ps codex exec | wc -l == 0` AND `gh issue list --label "🔍 design-solving"` non-empty → **bug**。

### Controller 每 wakeup 必派"下一步"(no gap policy)

Controller wakeup 处理 markers 后,**必须在同 turn 内派出下一步 codex**(if any actionable),不留 gap 等下次 wakeup:

| Marker 完成 | 立即派 |
|---|---|
| SOLVER_DONE × 3(同 issue 同 round)| 同 issue 同 round meta-judge |
| META_JUDGE_DONE:consensus | implement codex |
| META_JUDGE_DONE:converge:r+1 | r+1 三 solver |
| META_JUDGE_DONE:escalate:stalled | reflector(per Phase 9 路由表) |
| META_RESOLVED:re-design | fresh round 三 solver with new framing |
| IMPLEMENT_DONE:ok | controller commit/push/open PR + Phase 8 reviewer × 3 |
| REVIEW_DONE × 3 + any reject | fix codex r+1 |
| FIX_DONE | reviewer r+1 |
| TEST_ADD_DONE | controller commit/push 等 CI |
| AUDIT_DONE | bootstrap design issues + cluster-003 类直接 implement |

派出后 ScheduleWakeup;**不允许** "wakeup → sweep → 0 派出 → 下 wakeup" pattern(空 wakeup)。

### Concurrency monitor:`tools/refactor-loop/concurrency_monitor.py`(强制)

300s 周期 daemon,监控 actual vs expected codex 并发数:
- expected = active issue/PR 数(per phase 表)
- actual = `ps codex exec`
- actual < expected/2 持续 2 tick → 告警(写 `.refactor-loop/.concurrency-alert.log` + 通知 controller pending events)
- 不自动 spawn codex(business logic 在 controller)— controller 下次 wakeup 必派

启动:
```bash
nohup python3 tools/refactor-loop/concurrency_monitor.py \
  >> .refactor-loop/logs/concurrency-monitor.log 2>&1 &
disown
```

### 反面(❌ 严禁)

- ❌ wakeup sweep 看到 SOLVER_DONE × 3 但**不派 judge**(留 gap)
- ❌ codex 完成后只删 progress comment,不派下一步
- ❌ wakeup ScheduleWakeup 但本 turn 0 codex spawn(等 wakeup 才动 = lazy / 死循环)
- ❌ 看到 concurrency-alert.log 有 entry 但 controller 不读
- ❌ active issue 0 codex 跑 >= 1 wakeup 周期(说明 controller 漏派)

### Spawn helper:`spawn_with_banner.py`(强制,per Auric 2026-05-20 "#741 也看不到运行状态. 你继续修 skills 吧. 然后需要写脚本你可以写脚本")

Controller 经常 spawn codex 时**忘 post banner**,GitHub 看不到运行状态。强制用 helper:

```bash
python3 tools/refactor-loop/spawn_with_banner.py \
  --cd <worktree> --add-dir /Users/auric/aevatar \
  --prompt <prompt-file> --log <log-file> --timeout 5400 \
  --banner-target <issue-or-pr-num> --banner-kind <issue|pr> \
  --banner-role <test-add|fix|reviewer|implement|solver|judge|reflector> \
  --banner-detail "<short context>"
```

Helper 行为:
1. **先 Post 状态卡片**到 target issue/PR(`## 📊 状态卡片 — <role> 派出`)立即可见
2. Spawn codex(nohup + start_new_session,后台跑)
3. timeout < 3600 拒绝 spawn(per CLAUDE.md floor)
4. Banner 含 codex log 名 / 工作目录 / timeout / role-specific "下一步自动会做" / 不需介入

**禁止**直接调 `spawn-codex.sh`(绕过 banner)— 强制走 `spawn_with_banner.py`。例外只:audit / bootstrap 等完全独立任务(不绑 issue/PR)可不带 banner。

### Controller 自检(每次 wakeup)

per-wakeup sweep step 1.5 之后,**对每个 in-flight codex 验证关联 issue/PR 是否有最新状态卡片**(创建时间 ≥ codex spawn 时间):

```bash
# 对每个 in-flight codex 任务
for log in $(ls -t .refactor-loop/logs/*-r*.log .refactor-loop/logs/implement-*.log .refactor-loop/logs/meta-reflect-*.log 2>/dev/null); do
  # 找到关联 issue/PR
  # 找到 spawn 时间(log mtime / SPAWN 行)
  # gh 查 issue/PR 最新 AI banner 时间
  # 如 banner 早于 spawn 时间 → controller MUST post 新 banner 反映 "<codex> 在跑"
done
```

如发现 in-flight codex 但关联 issue 无对应 banner → **本 turn 必须 post 补**,然后才能 schedule wakeup。

---

You are the **Controller**. You never edit production code yourself. You orchestrate `codex exec` subprocesses that do all analysis, implementation, and verification work in isolated git worktrees.

Each `/loop` wakeup runs **one iteration tick**: inspect `.refactor-loop/state.json`, advance whichever phase is ready, schedule the next wakeup. Stop when `clusters_planned == clusters_done`.

This skill complements `refactor-team` (Agent-subagent based). Use this skill when the user wants:
- True OS-level parallelism via worktrees
- Each phase as an independent `codex exec` process (not a Claude subagent)
- Dynamic `/loop` self-pacing rather than fixed cron

---

## Quick start

```bash
# user types:
/loop <task description... 完全无人值守模式>
```

First wakeup → bootstrap state, dispatch audit codex, schedule fallback wakeup, end turn.

Subsequent wakeups → **derive state from GitHub**(open PR / open issue / labels / CI / log markers),advance any cluster that's ready, schedule next wakeup。**禁止**把 `.refactor-loop/state.json` 当 source of truth(详见下节)。

---

## AI 内容标识符 ⟦AI:AUTO-LOOP⟧(强制,per Auric 2026-05-20 "所有 AI 产生的内容你都加一个特殊标识,这个字符串唯一只有这个 skills 会生成")

**Sentinel**:`⟦AI:AUTO-LOOP⟧`(U+27E6 + ASCII + U+27E7)

设计:
- `⟦` U+27E6 / `⟧` U+27E7 mathematical white square brackets,**人类几乎不可能自然敲出**(中英输入法都没有)
- 字面 `AI:AUTO-LOOP` 字母 + 冒号 + dash,grep 极易
- 整串复制成本高,无明确意图者不会复制
- 唯一仅本 skill 生成 → 程序可靠识别 AI vs 真人

### 强制规则

**所有 AI 生成的对外内容必须末尾带 sentinel**:

| 内容类型 | 必带位置 |
|---|---|
| Controller post 的 status banner / 进度评论 | 末尾独立一行 |
| Codex post 的 review / fix-report / consensus / solver 评论 | 末尾独立一行 |
| Git commit message(controller 与 codex commit) | 末尾独立一行(commit body 末尾) |
| PR title / PR body | body 末尾独立一行(title 不带 — 太短) |
| Push notification | 末尾或独立 |
| `.refactor-loop/runs/*.md` artifact 文件末尾 | 末尾独立一行 |
| GitHub issue body(design issue 自动开的) | 末尾独立一行 |

**不放**:
- 代码注释 `// Refactor (iterN/cluster-XXX): ...`(代码层面不需要识别 AI,这是产线 code 自我说明)
- 内部 log 文件(`*.log`)(spawn-codex.sh banner 等,不出仓库)
- 路径名 / 分支名 / 文件名(避免污染 git tree)

### 识别替代 `^## 🤖` body marker(Phase 7)

之前 Phase 7 comment sweep 用 body `^## 🤖` / `^## 📊` 区分 controller post,有遗漏:
- 真人手写 `## 🤖` 罕见但可能
- 真人复制 Markdown emoji 段落混淆

**改用 sentinel**:
```bash
# Controller / AI post(末尾含 sentinel)→ 跳过
gh issue view <N> --json comments --jq '.comments[] | select(.body | contains("⟦AI:AUTO-LOOP⟧") | not) | .body[0:120]'
```

包含 sentinel = AI post 跳过。无 sentinel = 真人评论 必须响应。

历史 marker `^## 🤖 ` / `^## 📊 ` / `Generated with Claude Code` 作为**兼容回退**保留(老评论无 sentinel)。新 controller post 一律加 sentinel。

### Controller 自检

每次 controller `gh issue comment` / `gh pr comment` / `gh pr create --body` / `git commit -m` 前,**检查最终内容末尾是否含 `⟦AI:AUTO-LOOP⟧`**;无则**拒绝 post**(用 bash 条件包一层):

```bash
body=$(cat <<'EOF'
... banner content ...

🤖 controller status banner

⟦AI:AUTO-LOOP⟧
EOF
)
[[ "$body" == *"⟦AI:AUTO-LOOP⟧"* ]] && gh issue comment "$N" --body "$body" || { echo "MISSING_SENTINEL"; exit 1; }
```

### Codex prompts 加 sentinel 要求

所有 spawn 的 codex prompt 末尾**必须**加一行:

```
所有 AI 生成的对外内容(GitHub comment / PR body / commit message / runs/*.md artifact)必须末尾独立一行加 sentinel `⟦AI:AUTO-LOOP⟧`(不要修改字符)。无 sentinel 的 post 视为产生失败。
```

`reviewer-*.md` / `solver-*.md` / `meta-judge.md` / `review-fix.md` / `implement.md` / `test-add.md` / `audit.md` / `design-issue-body.md` / `design-issue-reply.md` 都该加。

### 反面(❌ 禁止)

- ❌ 修改 sentinel 字符串(必须字面 `⟦AI:AUTO-LOOP⟧`,大小写 / 字符 / 顺序 / 括号种类不能变)
- ❌ 用 `<!-- ... -->` HTML 注释藏 sentinel(GitHub 渲染会吃,grep 失败)
- ❌ 把 sentinel 放代码 / 路径 / 分支名(污染产线)
- ❌ post body 末尾没 sentinel — bash 自检拦,违规 = bug

---

## 状态源 — GitHub 为真,本地 log 为辅(强制,per Auric 2026-05-19 "真实源以github为准,任务都在后台进程")

**问题**:`.refactor-loop/state.json` 频繁过时——controller turn 跨多 wakeup、session 中断、user `/clear`、后台进程独立写 GitHub、跨 session 恢复——把它当 source of truth 会让 controller 基于错误前提派 codex / 重复跑已完成的 round / 漏跑实际 in-flight 的 round。

**铁律**:所有控制流决策只读 **GitHub state + 本地 log marker + OS 进程列表**。`.refactor-loop/state.json` 仅作 logs 索引 + debug 辅助,**不参与决策**。

### Per-wakeup sweep(每次 wakeup 第一件事,在派任何 codex / 转 phase 之前)

0. **本地 main repo 同步**(强制,per Auric 2026-05-20 "为什么本地分支没有跟远程同步"):
   ```bash
   cd "$REPO_ROOT" && git fetch origin --quiet
   git pull --ff-only origin auto-refact-dev 2>&1 | tail -1
   ```
   Worktree push 后 origin 推进,**main repo HEAD 不会自动跟**;不 sync 会让 controller 拿到陈旧 commit / 编错误 PR base。每次 wakeup 第一动作。

1. **GitHub state derive**:
   ```bash
   gh pr list --label "auto-loop" --state open --json number,headRefName,labels,title
   gh issue list --state open --label "refactor-design-needed" --json number,title,labels
   gh issue list --state open --label "phase9-auto-solve" --json number,title,labels
   ```
   开 PR / 开 issue / phase label / human label 是当前 phase 真实状态的唯一来源。

2. **Per-PR CI sweep**(Phase 5 强制):
   ```bash
   for pr in <open auto-loop PR list>; do
     gh pr checks "$pr" --json name,bucket,state
   done
   ```
   任一 bucket=fail → 立刻派 fix codex(per Phase 5)。

3. **In-flight codex 探测**(看 OS 进程,不看 state.json):
   ```bash
   ps -ef | grep -E "(codex exec|spawn-codex)" | grep -v grep   # 真正还在跑的
   ls -lt .refactor-loop/logs/ | head -20                        # 最近完成的 log
   tail -5 <log>                                                  # marker(EXIT/DONE_AT/SOLVER_DONE/...)
   ```

4. **Per-issue Phase 9 进展判定**:从最新 log marker 推断,不读 state.json:
   - `phase9-issueN-rK-{minimal,delete,structural}.log` 全有 `EXIT=0` 且 `phase9-issueN-rK-judge.log` 不存在 → **派 r-K meta-judge**
   - `phase9-issueN-rK-judge.log` 有 `META_JUDGE_DONE:consensus:...` → 派 implement,加 `auto-loop-resume` label
   - `phase9-issueN-rK-judge.log` 有 `META_JUDGE_DONE:converge:round-K+1:...` → 派 r-K+1 三 solver
   - `phase9-issueN-rK-judge.log` 有 `META_JUDGE_DONE:escalate:...` → label `🆘 human:卡死` + PushNotification

5. **Per-PR Phase 8 进展判定**:从 log marker 推断:
   - 三 reviewer 全 `REVIEW_DONE:` + 全 approve → auto-merge
   - 任一 reject → 看 fix log;无 fix log → 派 fix r1;有 fix-rN log `FIX_DONE:` → 派 reviewer rN+1
   - `fix_round > 3` → meta-layer reflect

6. **State.json 仅作 debug**:可以追加 phase transition 记录到 state.json 作为 audit trail,但**不允许读 state.json 的字段决定派什么**。

### 任务都在后台进程(强制)

每个 codex spawn 用 `Bash run_in_background: true`(per "## Codex 调用方式")→ harness 跟踪、Claude Code shells panel 可见、harness 在 exit 时发 task-notification。**任务的真实状态**由三处共同决定:
- OS 进程列表(`ps aux | grep codex`)— 是否还在跑
- log 文件 tail marker — 是否完成 / 完成什么结果(`EXIT=`、`SOLVER_DONE:`、`REVIEW_DONE:`、`FIX_DONE:`、`META_JUDGE_DONE:`、`POSTED:`)
- GitHub 副作用(comment / label / merge / close)— 是否对外可见

Controller turn 间 / session 间 / `/clear` 后,**后台 codex 继续跑不中断**。Controller 醒来时只读这三处 derive 真实状态,**不依赖任何 in-memory / in-context / state.json 维护的状态**。

### 跨 session 恢复(/clear / 新 conversation / 重启)

每次 controller 进 turn 假设**自己刚醒**,不记得任何上下文:
1. 跑 per-wakeup sweep(上面 1–5)
2. 完全从 GitHub + log marker derive 当前每个 PR / issue 在哪一步
3. 派出该派的下一步

这意味着 controller 设计上**完全无状态**(stateless)。每个 turn 自洽。state.json 即便完全删除,也不影响控制流(只丢 debug 历史)。

### 反面(❌ 禁止)

- ❌ 读 `state.json.clusters_active[]` 决定当前在跑哪些 cluster → 状态过时,可能把已完成 cluster 重派
- ❌ 读 `state.json.phase` 决定走哪一 phase → `/clear` 后字段不存在但 GitHub 上 PR / issue 真实存在
- ❌ controller "记得" 上一 turn 派了 fix r3 → cross-turn 不持续,必查 `ls .refactor-loop/logs/fix-pr<N>-r*.log` 找最新 round
- ❌ 把 codex 留在 conversation 同步等(`run_in_background: false`)→ session clear 后丢失,codex 仍在跑但 controller 看不见
- ❌ controller turn 中维护 in-memory `pending_issues = [721, 722, 723]` → 下次 wakeup 一是不在,二是 GitHub 可能已经多 / 少了 issue
- ❌ 假设 state.json 是最新的 → 多 controller 并发 / cross-process 写 race / writer-codex 独立写 GitHub 不写 state → 不可信
- ❌ 任务 spawn 后 controller 主动等(`wait`、sleep 轮询)→ 任务在后台跑,controller 应该排 ScheduleWakeup 然后退出 turn,等 task-notification 唤醒

---

## Phase 0 — Bootstrap (first wakeup only)

If `.refactor-loop/state.json` does not exist:

```bash
mkdir -p .refactor-loop/{logs,runs,clusters,prompts,worktrees,state}
```

Write initial `state.json`:

```json
{
  "schema_version": 1,
  "trunk_branch": "auto-refact-dev",
  "integration_branch": "auto-refact-dev",
  "review_base_branch": "dev",
  "pr_mode": "stacked",
  "max_parallel_clusters": 3,
  "iteration": 1,
  "phase": "audit",
  "clusters_planned": [],
  "clusters_active": [],
  "clusters_done": [],
  "clusters_failed": []
}
```

**Default integration branch**: `auto-refact-dev`. This is the long-lived branch where all auto-refactor cluster PRs land before rolling up to `dev`. On a fresh loop:

```bash
# Idempotent setup — safe to re-run
git fetch origin
git checkout -B auto-refact-dev origin/auto-refact-dev 2>/dev/null \
  || git checkout -b auto-refact-dev origin/dev
git push -u origin auto-refact-dev 2>/dev/null || true
```

Override only when the user explicitly names a different integration branch (e.g., to test a new audit prompt without polluting the canonical one). Existing loops on a different branch can keep their name; the default applies to **new** Phase 0 bootstraps only.

**`pr_mode` choice (set in Phase 0; do not change mid-loop)**:

- `"stacked"` (**default**): each cluster opens its own PR. Hard-dep clusters stack (PR B's base = PR A's branch); soft-dep / independent clusters PR against `integration_branch`. Integration branch eventually opens one rollup PR to `review_base_branch`. Reviewer sees small per-cluster PRs and can ack independently; cost is rebase-on-reject when an upstream cluster is changed. This is the right shape for typical refactor loops (3+ clusters, reviewable independently).
- `"single"`: all clusters merge to `integration_branch` and a single PR targets `review_base_branch`. Simple; reviewer sees one big PR. Use only when the loop is expected to produce ≤ 2 clusters or the user explicitly asks for a single PR.

If the user doesn't specify, default `"stacked"` and surface in bootstrap PushNotification: "Using stacked-PR mode; pass `pr_mode: single` to override."

Create top-level TaskCreate items: audit / dispatch / merge.

---

## Phase 1 — Audit (one codex + controller validation)

1. Copy `prompts/audit.md` (this skill's template) to `.refactor-loop/prompts/audit-iter-N.md`.
2. Replace `{{iteration}}` placeholder.
3. Dispatch:

   ```bash
   .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
     --cd "$REPO_ROOT" \
     --prompt .refactor-loop/prompts/audit-iter-N.md \
     --log .refactor-loop/logs/audit-iter-N.log \
     --timeout 3600
   ```

   Use Bash with `run_in_background: true`. 3600s (60 min) is the project-wide minimum for codex jobs (see CLAUDE.md "Codex CLI 调用规范"); audit may legitimately need most of it to complete the coverage manifest.

4. Schedule wakeup 1500–1800s as safety net (task notification is primary wake).
5. **End turn.**

When task notification fires → **controller validation** before accepting the audit:

- a. Check log tail for the terminal marker: `AUDIT_DONE:...:<N>` or `AUDIT_INCOMPLETE:<reason>`.
- b. If `AUDIT_INCOMPLETE` → log reason, re-dispatch audit with the missing pieces called out in the prompt header (e.g., "previous audit returned INCOMPLETE because <reason>; deliver the missing artifact this run"). Do NOT proceed to Phase 2 with an incomplete audit.
- c. Verify the two output files exist: `audit-iter-N.md` AND `audit-iter-N-candidates.ndjson`. Missing either → treat as INCOMPLETE.
- d. Verify the candidate file has `>= 25` entries unless the audit body explicitly explains why every analyzer pack command returned 0 hits.
- e. Verify the audit body contains the 6 fixed-analyzer-pack commands by name with hit counts.
- f. Verify reject reasons cite a CLAUDE clause + per-candidate evidence (not blanket "covered by guard"). Sample 3 random rejects; if any lack evidence → INCOMPLETE.
- g. Verify `coverage_manifest.total_opened_files >= 60` with the documented sub-distribution.

Anti-anchoring: **do not** include phrases like "prefer 0", "loop saturated", "healthy signal" in the audit prompt body. These bias codex toward terminating instead of digging. Use the mechanical thresholds in `prompts/audit.md` as the only stop criteria.

After validation: read `audit-iter-N.md`, populate `clusters_planned`, split into batches (max `max_parallel_clusters` per batch) by **file/project disjointness**:

- Two clusters that touch the same `.csproj` or share a file path go in different batches.
- Two clusters that touch the same proto file → different batches.

### requires_design clusters → open GitHub issue, do NOT auto-implement

For every cluster with `requires_design: true`:

1. Open a GitHub issue via `gh issue create`:
   ```bash
   gh issue create \
     --title "[refactor-design] <cluster-id>: <one-line problem from audit>" \
     --label "refactor-design-needed,auto-loop" \
     --body "$(envsubst < .claude/skills/codex-refactor-loop/prompts/design-issue-body.md)"
   ```
   The body template at `prompts/design-issue-body.md` includes: the cluster's YAML block from audit, full evidence section, the audit's `Fix boundary` paragraph, and an explicit "decision needed" checklist (proto schema? new contract? backward-compat strategy? whether to split into multiple PRs?).
2. Record in state.json:
   ```json
   "design_pending": [
     {"cluster_id": "cluster-NNN", "issue_number": 234,
      "opened_at": "<ISO8601>", "last_checked": "<ISO8601>",
      "last_comment_count": 0, "status": "awaiting_design"}
   ]
   ```
3. Skip the cluster in Phase 2 (do NOT batch it).
4. PushNotification: "iter<N> opened design issue #<num> for cluster-<id>. Auto-loop paused on this cluster pending human design decision."

Update state, advance to Phase 2 (with requires_design clusters excluded).

---

## Phase 2 — Implement (parallel codexes, one per cluster in current batch)

For each cluster in the current batch:

1. Create worktree:

   ```bash
   git worktree add -b refactor/iterN-<cluster-id> \
     .refactor-loop/worktrees/<cluster-id> HEAD
   ```

2. Materialize prompt: copy `prompts/implement.md`, replace placeholders (`{{cluster_id}}`, `{{worktree_path}}`, `{{branch}}`, `{{old_pattern}}`, `{{new_principle}}`, `{{scope_paths}}`, `{{verification_hints}}`). Save to `.refactor-loop/prompts/implement-<cluster-id>.md`.

3. Dispatch via `spawn-codex.sh --cd <worktree>` with `--timeout 5400` (90 min).

4. Update `clusters_active` with `bg_task` id.

After all parallel dispatches, schedule wakeup 1800s safety net. **End turn.**

When each task notification fires → check log tail for `IMPLEMENT_DONE:<cluster-id>:<status>`:
- `ok` → advance that cluster to Phase 3 (verify).
- `partial` / `blocked` → move to `clusters_failed`, log reason, optionally re-dispatch with corrected prompt.

Do **not** advance the whole batch in lockstep; verify each cluster independently as soon as its implement finishes.

---

## Phase 3 — Verify (one codex per cluster, independent of implement codex)

For each cluster whose implement finished `ok`:

1. Materialize `prompts/verify.md` → `.refactor-loop/prompts/verify-<cluster-id>.md`.
2. Dispatch in the same worktree (verify reads `git diff HEAD`, runs full test/guard suite, gates merge):

   ```bash
   .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
     --cd <worktree> \
     --prompt .refactor-loop/prompts/verify-<cluster-id>.md \
     --log .refactor-loop/logs/verify-<cluster-id>.log \
     --timeout 3600
   ```

3. End turn after dispatching all ready verifies. Wait for task notifications.

Verify output marker: `VERIFY_DONE:<cluster-id>:<verdict>` where verdict ∈ `{pass, rework, abort}`.

- `pass` → advance to Phase 4 (merge).
- `rework` → re-dispatch implement codex with verifier's findings appended.
- `abort` → move to `clusters_failed`, surface in PushNotification.

---

## Phase 4 — Merge & Push (controller, not codex)

**cwd discipline (critical)**: `git merge`, `git push`, and `gh pr create` MUST run from `$REPO_ROOT`, never from a worktree directory. Cwd persists across Bash invocations in the harness, so chained commands that include `cd .refactor-loop/worktrees/<id>` leak cwd into the next call. Always either start the trunk-side command with `cd "$REPO_ROOT" && …` or run it in a separate Bash invocation after the worktree-scoped commit. If you see `Already up to date.` after a merge, that is the signature of cwd leak — diagnose and redo from `$REPO_ROOT`.

For each `pass` cluster, serially:

1. **Commit in worktree**: `cd <worktree> && git add -A && git commit -m "<msg>"`.

2. **Local CI on the cluster branch** (still in worktree):
   ```bash
   bash tools/ci/architecture_guards.sh
   bash tools/ci/test_stability_guards.sh
   # plus any cluster-specific guards from audit.verification_hints
   ```
   On fail → `git reset --soft HEAD~1` (undo the commit), mark cluster `rework`, re-dispatch implement codex with the failure log.

3. **Push cluster branch**: `cd $REPO_ROOT && git push origin refactor/iterN-<cluster-id>`.

4. **Branch off** by `pr_mode`:

### Phase 4a — `pr_mode: "single"`

5a. Merge cluster branch into `integration_branch`:
    ```bash
    cd "$REPO_ROOT" && git merge --no-ff refactor/iterN-<cluster-id> \
      -m "Merge cluster-<id>: <short title>"
    ```
6a. Re-run local CI on integration_branch (catches inter-cluster interaction).
7a. `git push origin <integration_branch>`.
8a. Goto Phase 5 (remote CI watch).

### Phase 4b — `pr_mode: "stacked"`

5b. **Choose PR base** per the cluster's `dependencies` field from the audit:
    - `dependencies: []` (independent, soft-dep, or batch-disjoint) → base = `integration_branch`.
    - `dependencies: ["cluster-XXX", ...]` (hard-dep — won't compile without the prerequisite) → base = the prerequisite cluster's branch (use the **first**, primary one; document others in PR description).

    **All cluster PRs target the integration branch by default. Never PR directly to `review_base_branch` (dev).** The rollup PR (Phase 4b step 10b, one per iteration) is the only PR that targets `review_base_branch`. Rationale: cluster PRs stay small and reviewer-friendly; the integration branch holds the cumulative refactor state with merge-conflict resolution done once; the rollup PR is the human gate where iter-level rationale (scorecard, cluster ledger, CI guard adds) lives.

    Edge case — if a maintainer accidentally retargets a cluster PR to `review_base_branch`, the next Phase 6 sweep detects the mismatch and posts a comment requesting retarget (does NOT auto-edit, to respect maintainer intent).

6b. **Open PR** (**body MUST be bilingual per SKILL.md "Bilingual rule"**):

    Structure the body as:

    ```markdown
    ## Summary / 摘要 (bilingual; see SKILL.md Bilingual rule)

    ### English

    iter<N> <cluster-id> (<severity>, <rule_ids>).

    - **Old**: <old_pattern, full sentence from human_brief.problem_statement_en if present else cluster.old_pattern>
    - **New**: <new_pattern, full sentence>

    Violated: <CLAUDE.md / AGENTS.md clause one-liner>.

    ### 中文

    iter<N> <cluster-id>（<严重度>，<rule_ids>）。

    - **Old**：<old_pattern 完整中文一句，来自 human_brief.problem_statement_zh；老 cluster 缺 zh 时由 controller 把英文 old_pattern 翻成中文>
    - **New**：<new_pattern 完整中文一句>

    违反：<对应 CLAUDE.md/AGENTS.md 条款中文摘录>。

    ## Scope / 范围 (language-neutral file list)

    <N files changed (+X/-Y). Targeted test pass counts. Architecture guards green.>

    See [implement summary](./.refactor-loop/runs/implement-<cluster-id>.md) and [audit](./.refactor-loop/runs/audit-iter-<N>.md#<cluster-anchor>).

    ## Stacked-PR

    Part of iter<N> batch <X>. Base = `<base_branch>`. Rollup target = `<review_base_branch>`.

    🤖 Auto-loop / codex-refactor-loop iter<N>
    ```

    Run via:
    ```bash
    cd "$REPO_ROOT" && \
    gh pr create \
      --base "<base_branch>" \
      --head "refactor/iterN-<cluster-id>" \
      --title "<cluster id>: <short imperative title — same English title; PR title is not bilingual since GitHub UI truncates>" \
      --body-file <generated_body_file>
    ```

    Controller must run the equivalence test (SKILL.md Bilingual rule §"Equivalence test") on the generated body before `gh pr create`. If 中文 section is missing or visibly shorter than English, regenerate or fall back to a one-paragraph machine-translation as last resort (and PushNotification flagging the legacy fallback so operator can fix).

7b. **立刻给 PR 加 `auto-loop` label**(per Auric 2026-05-19 "我发现你会掉监控"):`gh pr edit <PR> --add-label "auto-loop"`。**漏加 → comment-monitor 不监控该 PR 评论 → maintainer 评论无 react 无回复**。漏加是 P0 bug,等同失保。Phase 4b 在 `gh pr create` 成功后立刻 chain 这条 `gh pr edit`,不能延后到下一 turn。

7b. Record the PR number in `state.clusters_active[i].pr_number`.
8b. **Stack rebase on upstream merge**: when an upstream (dependency) cluster's PR merges into `integration_branch`, immediately:
    - For each downstream cluster whose `dependencies` contained it:
      - `git -C <worktree> rebase --onto integration_branch <old_upstream_branch>` (or `gh pr edit <pr> --base integration_branch` if stacked-on-stacked is no longer needed).
      - Re-run local CI in worktree; on conflict, mark cluster `rework` and re-dispatch implement codex with conflict diff.
      - Force-push the cluster branch: `git push --force-with-lease origin refactor/iterN-<cluster-id>`.
9b. Goto Phase 5 (remote CI watch on the cluster's PR).
10b. After **all** iteration clusters have their PRs merged into `integration_branch`, ensure exactly one rollup PR exists from `integration_branch` to `review_base_branch`:
     ```bash
     gh pr list --head "<integration_branch>" --base "<review_base_branch>" --json number --jq '.[0].number'
     # If empty, gh pr create --base "<review_base_branch>" --head "<integration_branch>" --title "Refactor iter<N>: rollup" --body <scorecard.md>
     ```

After merge of the cluster branch into its target → `git worktree remove .refactor-loop/worktrees/<cluster-id>`. **Do NOT** delete the cluster branch yet under `stacked` mode — downstream PRs may still reference it as base; let GitHub auto-delete on merge.

If no clusters left in current batch → start next batch (Phase 2 again). If no batches left → start next iteration (Phase 1 again) or **start Phase 5 if there is an open PR for the trunk/cluster branches**.

### Phase 4 stack-depth cap

Hard cap: any single dependency stack ≥ 5 PRs deep triggers a controller halt. Reason: rebase blast-radius compounds — reviewer changes to the bottom PR force-rebase the entire stack, and reviewers stop landing PRs that get rebased twice. On cap:
- send PushNotification with the stack contents,
- merge all completed lower PRs into `integration_branch` immediately (collapse stack to a single base),
- continue remaining clusters from the collapsed base.

---

## Phase 5 — Remote CI watch (controller, after push)

Local CI passing is necessary but not sufficient. Remote CI runs additional jobs that don't fit on the controller machine (kafka integration, projection provider e2e, host composition smoke, codecov, etc.). Phase 5 watches them and treats remote failures the same way Phase 3 treats verify failures: dispatch a focused fix codex, loop back through verify/merge.

### When Phase 5 fires

After every push to `<trunk_branch>` that is the head of an open PR. Detect open PR with:

```bash
PR_NUMBER=$(gh pr list --head "<trunk_branch>" --json number --jq '.[0].number')
```

If no open PR → skip Phase 5 (local CI is sufficient).

### Arm the watch

```bash
# Poll every 60s; emit one event per failed check; exit when all checks settled.
prev=""
while true; do
  state=$(gh pr checks "$PR_NUMBER" --json name,bucket,state)
  cur=$(jq -r '.[] | "\(.name)\t\(.bucket)\t\(.state)"' <<<"$state" | sort)
  comm -13 <(printf '%s\n' "$prev") <(printf '%s\n' "$cur") | awk -F'\t' '$2=="fail"{print $0}'
  prev=$cur
  if jq -e 'all(.bucket != "pending")' <<<"$state" >/dev/null; then
    failed=$(jq -r '[.[] | select(.bucket=="fail") | .name] | length' <<<"$state")
    echo "REMOTE_CI_DONE:failed=$failed"
    break
  fi
  sleep 60
done
```

Arm as a Monitor with `persistent: true`. Each emitted line is a notification you wake on. Stop only on the `REMOTE_CI_DONE:` line.

### Triage on failure

For each `bucket: fail` check:

1. Fetch the failure logs:
   ```bash
   RUN_URL=$(gh pr checks "$PR_NUMBER" --json name,link --jq '.[] | select(.name=="<check>") | .link')
   RUN_ID=$(basename "$(dirname "$RUN_URL")")  # parse from link
   gh run view "$RUN_ID" --log-failed > .refactor-loop/logs/remote-ci-<check>-<sha>.log 2>&1 || \
     gh run view "$RUN_ID" --log | tail -200 > .refactor-loop/logs/remote-ci-<check>-<sha>.log
   ```

2. Classify:
   - **Flaky / infra-only** (network timeout, registry unreachable, runner OOM that doesn't recur): retry by `gh workflow run` or pushing an empty whitespace commit; document under `clusters_failed` with reason `flaky`.
   - **Real failure tied to merged work**: dispatch a `prompts/remote-ci-fix.md` codex (see template) with the failure log + last 10 cluster commits as input. Treat the resulting fix as a mini-cluster: implement → controller verify (re-run local guards + the specific failing test) → commit → push → Phase 5 again.
   - **Pre-existing failure unrelated to merged work** (failure exists on `dev` base too): document, do not fix in this PR; surface via PushNotification.

3. `codecov/patch` specifically: this measures coverage on **lines added by this PR**, i.e. the refactor's own new/modified production lines. A refactor-induced patch-coverage drop is the loop's own responsibility — the loop just shipped new code without tests, that is exactly what the loop must close before merge. Treat as a **real failure**:
   - Pull the codecov patch detail via API (`https://api.codecov.io/api/v2/github/<owner>/repos/<repo>/pulls/<num>`) to identify `patch.misses` + `patch.partials` line ranges per file.
   - Cross-reference with the cluster ledger: each uncovered patch line belongs to a known cluster.
   - Dispatch `prompts/test-add.md` codex per cluster with the uncovered file:line list, target threshold (default 80% patch coverage), and "tests must exercise behavior the cluster introduced (e.g., IHttpClientFactory typed-client path, head-index cursor compaction trigger, compiled-delegate exception path, projection session lease lifecycle)".
   - Test-add codex output joins the cluster's branch and re-pushes; codecov re-evaluates.
   - **Exception** (info-only ack): if `head_totals.coverage - base_totals.coverage > -0.5%` (i.e. project coverage barely moved) AND the cluster summary explicitly declared deletion-heavy refactor, you may ack the codecov failure with a PushNotification explaining the math; do not silently dismiss.

### Loop control under Phase 5

- Cap remote-ci fix attempts per check at **2**. After 2 attempts on the same check → mark `clusters_failed` reason `remote-ci-stuck`, send PushNotification, stop the loop.
- Phase 5 may overlap with Phase 2 of the next iteration. If a new cluster's local CI passes but remote CI is still failing on a prior commit → push anyway (CI re-runs on each push); the watch picks up the latest checks.

---

## Phase 6 — Integration branch auto-sync with `review_base_branch` (heartbeat)

Runs **first** on every controller wakeup, before Phase 7 design-issue sweep and before any new Phase 2 cluster work. Goal: keep `integration_branch` continuously up-to-date with `review_base_branch` so cluster PRs base on fresh code and the eventual rollup PR has minimal merge conflicts.

### Phase 6 现在由独立 daemon 自主完成(per Auric 2026-05-20 "写一个独立脚本, 自动 merge dev 到 auto-refact-dev 分支. 如果有冲突让脚本调用 codex 解决冲突合并. daemon 运行")

**`tools/refactor-loop/dev_sync_daemon.py`** 是独立 daemon,**600s 周期**自主跑 sync,不依赖 controller wakeup:

```bash
nohup python3 tools/refactor-loop/dev_sync_daemon.py \
  >> .refactor-loop/logs/dev-sync-daemon.log 2>&1 &
disown
```

Daemon 工作流:
1. cd `$REPO_ROOT`,确认 HEAD = `auto-refact-dev`(不是则 skip)
2. Working tree dirty → skip(controller 在工作)
3. 但若 `.git/MERGE_HEAD` 存在 + 无 in-flight codex → **dispatch codex resolve**(防止上次 codex 死)
4. `git fetch origin` + `git rev-list --count HEAD..origin/dev`
5. behind=0 → idle skip
6. behind>0 → 尝试 `git merge --ff-only`,成功则 push;失败则 `git merge --no-ff`(merge commit)
7. **冲突** → 写 `prompts/dev-sync-conflict-<ts>.md` + spawn-codex resolve(timeout 5400s)
8. codex 在同一 worktree resolve 文件 + `git add` + `git merge --continue`(不 push,daemon 后续 push)
9. codex 完成 marker:`DEV_SYNC_RESOLVED:<files>` 或 `DEV_SYNC_BLOCKED:<reason>`

### Daemon vs controller 分工

| 任务 | 谁做 |
|---|---|
| dev → auto-refact-dev sync(常规 + 冲突解决) | **daemon**(600s 自主) |
| 处理 design issue / Phase 9 / Phase 8 fix loop | controller(wakeup) |
| 派 reviewer / fix / implement codex | controller |
| 监控 daemon liveness + restart | controller per-wakeup |
| Sync 异常 escalation(DEV_SYNC_BLOCKED) | controller 读 daemon log + escalate |

### Controller 每 wakeup 责任(改为只 verify daemon)

```bash
# Phase 6 现在 controller 只 verify daemon 健康
ps -ef | grep dev-sync-daemon.sh | grep -v grep | wc -l  # 必须 >=1
tail -10 .refactor-loop/logs/dev-sync-daemon.log | grep -E "(DEV_SYNC_BLOCKED|FAIL|FATAL)" | tail -3
```

若 daemon 死 → restart `nohup ... >> log 2>&1 & disown`。
若发现 `DEV_SYNC_BLOCKED` → controller post 卡片到 rollup PR / 通知 maintainer。

### 反面(❌ 禁止)

- ❌ controller 自己跑 `git merge dev` 同步(daemon 已做,会 race / 冲突)
- ❌ daemon push 后 controller 不 fetch 就 commit(stale base bug)
- ❌ Daemon 派 codex 自己 push(daemon 决定 push 时机,codex 只 resolve + merge --continue)
- ❌ 多 daemon 实例(`pgrep -c dev-sync-daemon` 必须 = 1)

### Sync procedure

```bash
cd "$REPO_ROOT" && git fetch origin
git checkout "$INTEGRATION_BRANCH"
git pull --ff-only origin "$INTEGRATION_BRANCH" 2>/dev/null || true

# Compute divergence
ahead=$(git rev-list --count "origin/$REVIEW_BASE_BRANCH..HEAD")
behind=$(git rev-list --count "HEAD..origin/$REVIEW_BASE_BRANCH")

if (( behind == 0 )); then
  echo "integration is up-to-date with $REVIEW_BASE_BRANCH; no sync needed"
  exit 0
fi

# Try fast-forward first, then no-ff merge
if git merge --ff-only "origin/$REVIEW_BASE_BRANCH" 2>/dev/null; then
  echo "fast-forwarded integration with $REVIEW_BASE_BRANCH (+$behind commits)"
else
  if git merge --no-ff -m "Sync integration with $REVIEW_BASE_BRANCH" "origin/$REVIEW_BASE_BRANCH"; then
    echo "merge-committed $behind commits from $REVIEW_BASE_BRANCH into integration"
  else
    git merge --abort
    echo "SYNC_CONFLICT: $behind commits in $REVIEW_BASE_BRANCH conflict with integration"
    # PushNotification: "integration branch sync conflicted with dev; manual rebase needed"
    exit 1
  fi
fi

# Run local CI on the post-sync integration head
bash tools/ci/architecture_guards.sh && bash tools/ci/test_stability_guards.sh
if [[ $? -ne 0 ]]; then
  echo "SYNC_CI_FAIL: post-merge guards failed"
  # PushNotification + halt (do not push a broken integration)
  exit 1
fi

git push origin "$INTEGRATION_BRANCH"
```

### Sync cadence

- Every controller wakeup (cheap when `behind == 0`).
- On conflict or post-merge CI fail → halt + PushNotification; do not push. Resume sync only after operator clears the issue.
- After successful sync, **rebase all open cluster PRs** onto the new integration head (force-with-lease per PR branch). This keeps stacked PR semantics correct: each cluster PR's diff stays scoped to its own changes, not the dev merge.

### Why this matters

- Without auto-sync, the integration branch drifts from dev and the eventual rollup PR becomes one giant conflict resolution.
- Cluster PR diffs viewed by reviewers should be just the cluster's changes; if integration is stale, the PR shows a noisy diff that mixes cluster work with "what dev added since" which is reviewer-hostile.
- Sync conflicts are rare but real (e.g., a dev PR refactored the same area). Surfacing them as halts is better than silently posting a busted integration.

### State tracking

In `state.json`:

```json
"integration_sync": {
  "last_sync_at": "<ISO8601>",
  "last_sync_added_commits": <int>,
  "last_sync_result": "ff | merge | up_to_date | conflict | ci_fail",
  "consecutive_failures": <int>
}
```

`consecutive_failures >= 3` → escalate to PushNotification with "integration sync stuck — manual review needed" and pause auto-sync until operator clears.

---

## Phase 7 — Design-issue watch (sweep on every wakeup)

Runs **after Phase 6 sync** and **before** any new Phase 2 / 3 / 4 / 5 cluster work on every controller wakeup (whether triggered by user `/loop`, ScheduleWakeup, or task-notification). Goal: detect when a paused-for-design cluster has a maintainer response and resume it.

### Sweep procedure

For each `state.design_pending[i]`:

```bash
issue_json=$(gh issue view "$ISSUE_NUMBER" --json comments,state,labels)
new_count=$(jq -r '.comments | length' <<<"$issue_json")
prev_count=$LAST_COMMENT_COUNT   # from state
state=$(jq -r '.state' <<<"$issue_json")
labels=$(jq -r '[.labels[].name] | join(",")' <<<"$issue_json")
```

Classify:

**🔴 真人评论 vs controller 评论识别(强制,per Auric 2026-05-20 "为什么许多 issues 我回复了没及时处理")**:`gh` CLI authenticated user = `loning`,与 maintainer Auric/Loning **同账号**;`comments[].author.login` **无法区分**真人 vs controller。**必须按 body 内容判断**:

**主判定(强制)**:body 含 `⟦AI:AUTO-LOOP⟧` sentinel → AI post 跳过;不含 → 真人评论必须响应(详见上方 "## AI 内容标识符 ⟦AI:AUTO-LOOP⟧" 节)。

**兼容回退**(老 AI 评论无 sentinel 的过渡期):
- body 第一行匹配 `^## 🤖 ` / `^## 📊 ` → AI post 跳过
- body 末尾含 `🤖 controller status banner` / `🤖 Auto-loop` / `Generated with Claude Code` → AI post 跳过
- 上述都不匹配且无 sentinel → 真人评论

Comment sweep 命令(主):
```bash
gh issue view <N> --json comments --jq '.comments[] | select((.body | contains("⟦AI:AUTO-LOOP⟧") | not) and (.body | startswith("## 🤖") | not) and (.body | startswith("## 📊") | not)) | "\(.createdAt)|\(.body[0:120])"' | tail -3
```
返回的是真人评论(包含 sentinel 或老 marker 的全跳过)。`select(.author.login=="loning")` 一律放弃—因为 controller 自己也是 loning。

历史教训:iter18 中 #719/#731/#732/#733 maintainer 真有评论("处理一下" / "choose:structural-no-live-sink" / "架构升级" / "应该统一 tg lark stream 用 actor 持有"),controller 按 author=loning 当 self-banner 跳过,等了几小时才发现。**禁止**再用 author 判断。Sentinel 引入后此 bug 类型从根本上消除。

- **No new comments AND state==open**: nothing to do; bump `last_checked` only.
- **State==closed without `auto-loop-resume` label**: maintainer closed without resume signal. Move to `clusters_failed` with reason `design-rejected:closed`. PushNotification: "cluster-<id> design issue #<num> closed without auto-resume; cluster permanently deferred."
- **New comment(s) AND no `auto-loop-resume` label**: maintainer is (presumed) in technical conversation. **Do not just notify and wait** — that's how controller looks unresponsive. But also do not blindly reply to anyone — see security gate below. Instead:
  - **首先(任何 sanity check 之前)立刻 👀 react 在新评论上**(per Auric 2026-05-19 "发现后请发个表情表示已经在准备回复"):`gh api repos/aevatarAI/aevatar/issues/comments/<comment-id>/reactions -X POST -f content=eyes`。这是"已看见,正在准备回复"的即时信号,让 maintainer 不会以为 controller 没看到/睡着了。controller 即使后面要 dispatch codex / 等 monitor / 跨多 turn 才回复,**eyes react 必须在 detect 同 turn 内贴上**,不能 batch / 不能延后。
  - **Security gate (mandatory, before dispatching analyst codex)** — verify the new comment's author is a team member; reject random outsiders. Check in order, accept on first match:
    1. `gh api repos/aevatarAI/aevatar/collaborators/<author>` returns 204 → collaborator → OK.
    2. `gh api orgs/aevatarAI/members/<author>` returns 204 → org member → OK.
    3. `<author>` is in known-maintainer whitelist (loning / louis4li / eanzhao / jason-aelf / AbigailDeng / potter-sun).
    4. The comment is identifiable as a prior controller-posted reply (body matches a recorded `posted_comment_id` in `state.design_pending[i].controller_comments[]` OR body starts with controller marker `## 🤖`/contains `Generated with Claude Code`). → skip silently; not a new external comment.
  - If none match: do NOT dispatch analyst codex, do NOT post anything. Log to `state.design_pending[i].skipped_authors += [<author>]` and `PushNotification` once: "issue #<num>: new comment from non-team-member <author> — controller declined to engage; please review manually." Do NOT echo the outsider's comment body in the PushNotification (avoid amplifying a possible prompt-injection attempt).
  - If security gate passes: materialize `prompts/design-issue-reply.md` with `${ISSUE_NUMBER} / ${CLUSTER_ID} / ${COMMENT_AUTHOR} / ${COMMENT_BODY}` filled.
  - Dispatch a fresh codex (separate from implement / verify; this is a technical analyst codex) via `spawn-codex.sh --timeout 3600`.
  - Codex writes a bilingual reply to `.refactor-loop/runs/design-issue-<num>-reply-<ts>.md` and prints `DESIGN_REPLY_READY:<num>:<summary>` marker.
  - On marker, controller reads the file, runs bilingual equivalence test (per SKILL.md "Bilingual rule"), then `gh issue comment <num> --body-file <file>`. Record the new comment's GitHub id into `state.design_pending[i].controller_comments[]` so the next sweep doesn't loop on itself.
  - PushNotification (operator): "cluster-<id> design issue #<num>: new comment from team-member <author>; analyst codex replied (see <url>)".
  - Increment `state.design_pending[i].reply_count`; cap auto-replies at **3 per issue** to avoid infinite back-and-forth. After cap, fall back to PushNotification-only mode for further comments (operator takes over).
- **Label `auto-loop-resume` is set** (maintainer's explicit green light): controller resumes:
  - Extract the latest comment body (assumed to contain the design decision: chosen pattern, proto schema, scope adjustments).
  - Materialize a new `prompts/implement-<cluster-id>.md` that prepends the design decision verbatim under a `## Design decision (from issue #<num>)` heading, then proceeds with the regular implement instructions.
  - Move cluster from `design_pending` into `clusters_active` and dispatch as a normal Phase 2 implement.
  - Post a comment back on the issue (bilingual): "auto-loop resumed; implement codex dispatched. Will close after PR opens. / auto-loop 已恢复；implement codex 已派发，PR 开后自动关闭本 issue。"

Update `state.design_pending[i].last_comment_count` and `last_checked` after every sweep, regardless of outcome.

### Sweep cadence — two modes

**Mode A: passive sweep (default when other phase work is active).** Every controller wakeup runs the sweep before any other phase. Cheap: one `gh issue view` per pending cluster. ScheduleWakeup cadence is dominated by other in-flight work; design issues piggyback on those wakeups.

**独立 comment-monitor 脚本(per Auric 2026-05-19 "要写个脚本挂个循环监控" + 2026-05-20 "应该脚本监控,写日志,monitor 监控处理")**

设计:**daemon 脚本 → 写持续 log → controller sweep 读 log → 处理**。三段解耦:

1. **Daemon 脚本**(`tools/refactor-loop/comment-monitor.sh`)forever 跑,30s 轮询 GitHub:
   - 自己 `gh api .../reactions content=eyes` 给 team-member 新评论加 👀(脚本内 side-effect,不需 controller)
   - emit `new-team-comment: <issue> <author> <comment-id> eyes-reacted-at=<ISO8601>` 到 **stdout**
   - emit `new-outsider-comment: <issue> <author> <id>` 同
   - state 存 `.refactor-loop/comment-monitor-state.json`(comment_id → seen),重启不重发

2. **持续 log 文件**(强制,per Auric 2026-05-20 修复 stdout 丢失 bug):
   ```bash
   nohup bash tools/refactor-loop/comment-monitor.sh >> .refactor-loop/logs/comment-monitor.log 2>&1 &
   disown
   ```
   **禁止** `> /dev/null`(之前的 bug)— 否则 controller 看不到 event。所有 daemon(`comment-monitor.sh` / `codex-progress-reporter.sh`)都 append 写自己的 `.log` 文件。

3. **Controller wakeup sweep 读 log**(per-wakeup step 1.5,加在 GitHub state derive 之后):
   ```bash
   # 拿到上次 sweep 后到现在的新 event
   prev_offset=$(cat .refactor-loop/comment-monitor.offset 2>/dev/null || echo 0)
   cur_offset=$(wc -l < .refactor-loop/logs/comment-monitor.log)
   if (( cur_offset > prev_offset )); then
     # 新 event 数 = cur - prev
     sed -n "$((prev_offset+1)),$((cur_offset))p" .refactor-loop/logs/comment-monitor.log \
       | grep "^new-team-comment:" | while read -r line; do
       # 解析 issue / author / id,触发 maintainer-reply-resets-the-round 流程
       process_new_team_comment "$line"
     done
     echo "$cur_offset" > .refactor-loop/comment-monitor.offset
   fi
   ```

4. **Daemon liveness 检查**:每次 wakeup `ps -ef | grep comment-monitor.sh` 至少 1 个;0 个 → restart with log redirect。同理 `codex-progress-reporter.sh`。

历史 bug(2026-05-20):
- ❌ daemon 用 `> /dev/null` 启动 → stdout event 全丢 → controller 看不到 → #733 maintainer 评论被 daemon eyes-react ✓ 但 controller 不知道有新评论
- ✅ 修法:`>> .refactor-loop/logs/<daemon>.log 2>&1`(append,持续可读)

**eyes react 在脚本里完成**,即使 controller 跨多 turn 才回复 / log offset 滞后,maintainer 已经看见眼睛 — 这部分是 daemon side-effect 不丢。

**Mode A1: 每次 controller wakeup 强制 comment sweep(per Auric 2026-05-19 "不要漏")**

Monitor 任务(下面 Mode B)在 harness 里会 silent die 过几次,**不能单点信赖**。每次 controller wakeup(/loop tick 或 task-notification 唤醒)的第一件事:

1. 列开 design issues:`gh issue list --state open --label "refactor-design-needed,phase9-auto-solve" --json number`
2. 对每个 issue + 每个 open PR(`gh pr list --state open --json number`),拉所有评论 id + author + timestamp
3. 和 `.refactor-loop/state.json` 里的 `last_seen_comment_id_per_issue` 对比,找出新评论
4. 对每条新评论:
   - check author 通过 [team-member security gate](#new-commentss-and-no-auto-loop-resume-label)
   - skip 自己 controller / writer-codex 发的(`## 🤖` marker / Generated with Claude Code 后缀 / 已记的 controller comment_id)
   - **立刻 👀 react**: `gh api repos/aevatarAI/aevatar/issues/comments/<id>/reactions -X POST -f content=eyes`
   - 记到 `state.pending_replies[]`,后续 dispatch writer-codex 处理
5. 更新 `state.last_seen_comment_id_per_issue`

每次 wakeup 都跑这个 sweep,不管 Monitor 是死是活。即使 Monitor 没漏,sweep 再跑一次也只是 idempotent(eyes 不会重复加)。

**Mode B: active 60s Monitor with auto-discovery (when design_pending is the ONLY remaining work).** Instead of sleeping 1h between checks, arm a persistent Monitor that **discovers issues by label on every tick** (never hardcoded issue numbers — new issues opened mid-session are picked up automatically), polls them at 60s cadence, and emits an event line the **first** time any issue's `(state, labels, comment_count)` tuple changes. The conversation wakes <60s after the maintainer adds the `auto-loop-resume` label / closes the issue / comments / opens a new design issue.

**Hard rule — Monitor discovery is dynamic, not enumerated**: hardcoding `PENDING_ISSUES=(681 682 684)` will miss any issue opened after the Monitor arms. The required pattern queries `gh issue list --label "refactor-design-needed,phase9-auto-solve"` on every loop iteration so new issues join coverage automatically. A controller that hardcodes issue lists into Monitor commands is broken — re-arm with discovery as soon as the gap is caught.

```bash
# Auto-discovery Monitor — emits one line per detected change across ALL open issues
# carrying refactor-design-needed OR phase9-auto-solve labels. New issues opened mid-session
# are picked up on the next 60s tick without re-arming.
declare -A LAST=()
while true; do
  # Discover current open issues with either Phase 7 or Phase 9 label (union)
  issues=$(gh issue list --state open \
    --label "refactor-design-needed" --json number -q '.[].number' 2>/dev/null; \
    gh issue list --state open \
    --label "phase9-auto-solve" --json number -q '.[].number' 2>/dev/null) | sort -u
  cur_state=""
  for issue in $issues; do
    data=$(gh api repos/aevatarAI/aevatar/issues/$issue \
      --jq '{state, labels: ([.labels[].name] | sort | join(",")), comments}' 2>/dev/null)
    [ -z "$data" ] && continue
    state=$(echo "$data" | jq -r '.state // "?"')
    labels=$(echo "$data" | jq -r '.labels // ""')
    count=$(echo "$data" | jq -r '.comments // 0')
    sig="${state}|${labels}|${count}"
    if [ "$sig" != "${LAST[$issue]}" ]; then
      resume=0
      echo ",$labels," | grep -q ",auto-loop-resume," && resume=1
      echo "design-issue-event: $issue $state $labels $count resume=$resume"
      LAST[$issue]="$sig"
    fi
    cur_state+="$issue|$sig"$'\n'
  done
  # Exit if any issue hit auto-loop-resume or closed (controller needs to act now)
  if echo "$cur_state" | grep -qE "\|auto-loop-resume|\|CLOSED\|"; then
    echo "DESIGN_EVENT_DONE: state change requires controller wakeup"
    break
  fi
  sleep 60
done
```

Arm via Monitor tool with `persistent: true` and `timeout_ms: 3600000` (1h ceiling). At 1h ceiling the Monitor exits; the controller's next ScheduleWakeup (3600s) re-arms it. If Monitor crashes early, ScheduleWakeup still catches it.

**Controller-level gap check (mandatory every wakeup)**: before relying on existing Monitor, the controller MUST verify it's still alive AND its discovery pattern is current. Run `gh issue list --state open --label "refactor-design-needed,phase9-auto-solve" --json number,title` and confirm the Monitor's emit history covers each open issue at least once. Gap → TaskStop the stale Monitor and re-arm with discovery. **Never trust a Monitor that was armed before the latest set of issues opened.**

**Mode transition**:
- Mode A → B: when active work drains to only design_pending (no `clusters_active`, no `rollup_pr` awaiting CI) → arm Mode B Monitor and set ScheduleWakeup 3600s as fallback.
- Mode B → A: when Monitor emits `DESIGN_EVENT_DONE` and the resumption flow starts a new Phase 2/3/4 cycle → TaskStop the design Monitor (avoid double-armed monitors).

**Stop the loop entirely** (omit ScheduleWakeup, no Monitor, send final PushNotification with summary) only when **no design_pending AND no clusters_active AND no rollup_pr awaiting CI**. Otherwise the loop must keep heartbeating to catch design responses.

### Why two modes

- Mode A is correct when batch implements/verifies are running; the controller already wakes frequently on task-notifications, so 1h sweep cadence is fine — design issues piggyback.
- Mode B avoids 1h detection latency without burning conversation cache: the 60s poll runs inside the Monitor's persistent process, not in the conversation. The conversation only wakes when the Monitor emits a meaningful event line.
- Manual override always works: user typing `/loop` wakes controller immediately regardless of Mode.

### Manual override

If the user manually edits state.json and sets `design_pending[i].status = "resume"`, the next sweep treats it as if `auto-loop-resume` label was applied (escape hatch when label can't be set on the host).

---

## Phase 8 — Multi-codex PR review with consensus merge

Runs when a cluster PR's remote CI is green (Phase 5 settled with pass) and the PR is mergeable. Goal: 3 (or more) independent codex reviewers from **different angles** verify the PR; **unanimous approve → auto-merge to `integration_branch`**; any reject → human review required.

### Default reviewer roles

- **Architect** (`prompts/reviewer-architect.md`): CLAUDE.md / AGENTS.md clause compliance.
- **Tests** (`prompts/reviewer-tests.md`): test coverage on net-new logic, no `[Skip]` / `Task.Delay` sneaking in, no loosened assertions.
- **Quality** (`prompts/reviewer-quality.md`): naming / dead code / over-engineering / readability / refactor self-doc clarity.

Optional (add when cluster touches the relevant area, audit's `rule_ids` decides): Perf (future), Security (future).

### Dispatch (parallel)

For each cluster PR with `CI green AND mergeable AND not yet auto-reviewed`:

```bash
for role in architect tests quality; do
  envsubst < .claude/skills/codex-refactor-loop/prompts/reviewer-${role}.md \
    > .refactor-loop/prompts/review-pr${PR_NUMBER}-${role}.md
  .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
    --cd "$REPO_ROOT" \
    --prompt .refactor-loop/prompts/review-pr${PR_NUMBER}-${role}.md \
    --log .refactor-loop/logs/review-pr${PR_NUMBER}-${role}.log \
    --timeout 3600 &
done
```

All reviewers in parallel background; one task-notification per reviewer when done.

### Consensus rules

Each reviewer outputs `REVIEW_DONE:${PR}:${role}:<approve|comment|reject>` marker.

| Combined verdicts                       | Action |
|---|---|
| **All approve**                         | Auto-merge: `gh pr merge ${PR} --merge --auto`. Post bilingual "auto-merged after consensus" comment. Cluster moves to `clusters_done`. |
| **All approve except 1 comment**        | Same auto-merge. Surface comment's "Evidence" in merge comment. |
| **2 approve + 1 comment**               | Same auto-merge with surfaced comment. |
| **3+ comment, 0 reject**                | Surface all comments in PR review comment; **do not** merge; PushNotification: "PR #N: 3 comments, no rejects — human decision recommended." |
| **Any reject**                          | **Enter fix-retry loop** (see next subsection). Do NOT escalate to human on first reject. |
| **Reviewer crashes / no marker**        | Re-dispatch that reviewer once. Second crash → `reject:reviewer-stuck`, escalate. |

### Fix-retry loop (AI iterates until consensus)

Policy: AI keeps iterating until unanimous-approve consensus, OR until escalation criteria are hit. Default `max_fix_rounds = 3` per PR (per Auric 2026-05-19 "2 轮太少,改到 3 轮"(2026-05-20 "共识轮次由 6 轮改为 3 轮"))。

Loop:

1. **Round entry** — `state.pr_reviews[PR].fix_round += 1`. If `fix_round > max_fix_rounds`, escalate (see below).
2. **Dispatch fix codex** in PR's own worktree:
   ```bash
   .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
     --cd "$PR_WORKTREE" --add-dir "$REPO_ROOT" \
     --prompt .refactor-loop/prompts/fixes/fix-pr${PR}-round-${N}.md \
     --log .refactor-loop/logs/fix-pr${PR}-round-${N}.log \
     --timeout 3600
   ```
   Fix codex reads all 3 reviewer outputs, applies in-scope fixes, validates locally, writes `FIX_REPORT.md`, emits `FIX_DONE:${PR}:round-${N}:applied-<N>:rejected-<M>:blocked-<K>` OR `FIX_BLOCKED:${PR}:round-${N}:<reason>:<short>`.
3. **Controller commits + pushes** the fix codex's changes to the PR's HEAD branch (codex itself doesn't push, per hard rule 4). Commit message includes round number and applied/blocked counts.
4. **Re-dispatch all 3 reviewers** against the new HEAD SHA (drop prior consensus).
5. **Re-evaluate**:
   - Unanimous approve → auto-merge (per table above).
   - Same reject reasons as previous round (no progress) → escalate.
   - New reject reasons but still <unanimous → go to step 1.

### Escalation criteria ("十分难搞" — truly stuck)

Escalate to human ONLY when:

- `fix_round > max_fix_rounds` (default 3) and still not unanimous → **不要直接升 human,先升 meta-layer**(see "## Meta-layer escalation" 下文)。Meta-layer 也无法解 OR 命中 architecture-philosophy 硬条件 → 才升 human。
- Fix codex emits `FIX_BLOCKED:<PR>:round-<N>:human-decision:<...>` (e.g. reviewer demands deleting a feature, splitting into 3 PRs, renaming a cross-cluster type).
- Fix codex emits `FIX_BLOCKED:<PR>:round-<N>:conflict:<...>` (reviewers' demands contradict each other and codex cannot resolve).
- Two consecutive rounds produce IDENTICAL reject text for the same reviewer (the fix didn't address the demand and codex isn't making progress).
- A reviewer's demand requires touching another in-flight cluster's PR (would create cross-PR dependency).

Escalation action:
- Add `needs-human-review` label on PR.
- Post bilingual PR comment with: round history (N rounds tried), reject evidence per round, what fix codex tried, why it's stuck.
- `PushNotification`: "PR #N stuck at round N — human decision needed: <one-line reason>".
- State: `pr_reviews[PR].consensus = "stuck-human-review"`.

### Anti-spiral safeguards

- Round-N reviewer outputs MUST be diffed against round-(N-1). If reviewer text didn't change but verdict didn't change either → that reviewer is stuck on a non-addressable demand → escalate.
- Each fix round must reduce total reject count OR change which reviewer rejects. If neither → escalate.
- Cumulative PR diff size grows by ≤ +30% per round; if a fix round adds more code than the original PR → controller flags scope-runaway and escalates.

### GitHub traceability (mandatory — every Phase 8 action posts to the PR)

All review/fix/consensus/escalation behavior MUST be observable on GitHub so the whole loop is traceable without reading local `.refactor-loop/` artifacts. Bilingual EN+ZH per hard rule #8.

**Hard rule (per Auric 2026-05-19): all natural-language GitHub posts go through `prompts/github-post-writer.md` codex, NOT directly composed by controller.**

The controller's only inline composition allowed for GitHub:
- Status one-liners (≤ 80 chars, e.g. "labels updated").
- Mechanical link / SHA / cluster id mentions.
- Programmatic label edits + merge actions.

EVERYTHING ELSE(reviewer verdict、fix-done body、consensus 公告、escalation rationale、design issue body、cross-post 通知、PR description 包括 rollup PR)由**正在跑的那个 codex 自己 post**,**不需要专门的 writer-codex 中介**(per Auric 2026-05-19 "没必要设置专门发github的角色,让各角色直接调用gh就好了"):

- solver / meta-judge / fix / reviewer / clarifier / investigator / analyst / implement codex 各自跑完内部 artifact 后,自己 `gh issue comment` 或 `gh pr comment` post 中文 user-facing 摘要
- 所有 prompts 末尾都有 `## GitHub post (强制)` 块引用 `prompts/_github-post-rules.md` 共享规则
- body 必须 `## 🤖 <headline>` 开头(comment-monitor.sh 据此识别 controller-post 跳 react)
- 中文 only / TL;DR ≤ 6 行 / raw artifact 折叠 `<details>` / 若 situation 给 `original_authors:` 加 `📢 cc`
- codex 自己抓 gh 输出的 URL,打 `POSTED:<role>:<N>:<URL>:<headline>` 或 `POST_FAILED:...`
- controller 只读 log 末尾 marker,**不读 body**

历史曾用过的 `prompts/github-post-writer.md` 专职 writer-codex 已 deprecated(文件保留为 `*.deprecated` 仅作历史参考)。

Rationale: 减少一跳 + 减少 controller 上下文负担 + 写 post 的 codex 本身就是最了解 artifact 的人,质量比 "翻译者" 更高。controller 边界仍是 git topology(commit/push/checkout)+ PR/issue 创建/merge/close lifecycle 决策,这些 codex 不动(per `_github-post-rules.md` "你不能调的" 列表)。

**@-mention rule (per Auric 2026-05-19 "找到违反原则的地方,请直接at那个违反原则的人进来讨论"):**

Every design issue body AND every escalation comment MUST include an "📢 cc 原作者 / cc original authors" section with `@<github-handle>` of the top 1-3 commit authors per evidence file (via `git blame --line-porcelain | uniq -c`). Handle mapping (current team):

| git author | GitHub handle |
|---|---|
| eanzhao | @eanzhao |
| louis.li | @louis4li |
| loning / Loning | @loning |
| jason | @jason-aelf |
| AbigailDeng | @AbigailDeng |
| potter / potter-sun | @potter-sun |

The audit codex captures `original_authors` per cluster (top blame authors across evidence files); the writer-codex emits the @-mention block from that input. If git blame extraction fails or returns unknown handle, fall back to "@loning" alone with a note that auto-mention was incomplete.

Required PR comments (controller posts via `gh pr comment <PR> --body-file <file>`):

| Phase 8 event | PR comment content |
|---|---|
| Reviewer round N complete | Bilingual table of 3 verdicts + reject demands per role + "next action" (fix-retry dispatched OR auto-merge OR escalation). Link to commit SHA reviewed. |
| Fix codex round N complete (FIX_DONE) | Bilingual FIX_REPORT excerpt: applied / rejected-as-false-positive / blocked counts, build+test status, files changed. Link to fix commit SHA. |
| Fix codex blocked (FIX_BLOCKED) | Bilingual: which reason category (conflict / human-decision / build-broken), reviewer demand text, controller's escalation decision. |
| Consensus reached (unanimous approve) | Bilingual: round count, final reviewer outputs, "auto-merging now". Then merge + a second "merged at <commit>" comment. |
| Escalation triggered | Add `needs-human-review` label. Comment includes: full round history, latest verdicts, why escalation criteria hit, what controller tried. PushNotification mirrors the headline. |
| Reviewer crash | Bilingual: which reviewer, log path, re-dispatch attempt. Second crash → escalate per above. |

Required GitHub labels (controller applies/removes):
- `phase8-reviewing`: a reviewer round is in flight
- `phase8-fixing`: a fix codex round is in flight
- `phase8-consensus-pending`: consensus computation in progress
- `needs-human-review`: escalated
- `phase8-merged`: auto-merged after consensus (removed by merge action)

Local-only files (logs, raw codex output, internal state) stay in `.refactor-loop/` and are NOT posted (would spam the PR). The PR comment must summarize enough that a reader can decide whether to read the local artifact, and link the exact local path.

Forbidden:
- Posting the same content twice in the same round.
- Posting reviewer/fix output without the bilingual sections.
- Auto-merging without first posting the "consensus reached" comment.
- Escalating without first posting the escalation rationale comment.

### State tracking

```json
"pr_reviews": {
  "<PR_NUMBER>": {
    "head_sha": "<sha at review dispatch>",
    "dispatched_at": "<ISO8601>",
    "reviewers": {
      "architect": {"verdict": "approve|comment|reject", "rationale_path": "...", "log": "..."},
      "tests": {...},
      "quality": {...}
    },
    "consensus": "auto-merge | block-human-review | partial-comment",
    "merged_at": "<ISO8601|null>",
    "auto_merge_commit": "<sha|null>"
  }
}
```

### Re-review on push

If PR is pushed after consensus (rebase, requested change), head SHA changes. Next Phase 8 sweep: if `state.pr_reviews[PR].head_sha != current head SHA` → drop prior consensus, re-dispatch all reviewers against new head. Never auto-merge stale consensus.

### Idempotency

Skip a PR in Phase 8 if any of:
- already merged / closed
- `needs-human-review` label present (operator handling)
- consensus recorded for current head SHA AND not stale

### Why three angles, not one

A single reviewer codex would weigh all dimensions and might trade tests for architecture or vice versa. Three independent codexes with bounded scopes are harder to convince than one — a real defect tends to hit one role hard rather than all three lightly. Consensus across orthogonal angles is the actual signal.

---

## Phase 9 — Multi-solver design consensus (alternative to manual maintainer decisions)

Runs when a `state.design_pending[i]` cluster has been open for one full Phase 7 sweep with no maintainer answer, OR when the operator manually sets `design_pending[i].auto_solve = true`. Goal: 3 independent solver codexes propose framings from different biases; a 4th meta-judge codex arbitrates; **3/3 unanimous → auto-dispatch implement** (skip maintainer decision); split or philosophy-touching → escalate to maintainer.

Per Auric's policy (2026-05-19): **3/3 unanimous required** — "早暴露问题比晚暴露问题好" — anything less goes through convergence (max 2 rounds) or escalation.

### Default solver roles

| Solver | Bias | Prompt |
|---|---|---|
| **minimal** | smallest viable change; documented rule exception OK if scope is genuinely narrow | `prompts/solver-minimal.md` |
| **structural** | CLAUDE-philosophy-aligned; new abstraction allowed if justified; never proposes rule exception | `prompts/solver-structural.md` |
| **delete** | question necessity; propose delete / defer / collapse-and-redirect; abstain if feature genuinely needed | `prompts/solver-delete.md` |

A 4th **meta-judge** codex arbitrates (`prompts/meta-judge.md`).

### Dispatch (parallel)

For each cluster needing Phase 9:

```bash
for role in minimal structural delete; do
  envsubst < .claude/skills/codex-refactor-loop/prompts/solver-${role}.md \
    > .refactor-loop/prompts/phase9/solve-issue${ISSUE_NUMBER}-r${ROUND}-${role}.md
  .claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
    --cd "$REPO_ROOT" \
    --prompt .refactor-loop/prompts/phase9/solve-issue${ISSUE_NUMBER}-r${ROUND}-${role}.md \
    --log .refactor-loop/logs/phase9-issue${ISSUE_NUMBER}-r${ROUND}-${role}.log \
    --timeout 3600 &
done
```

All 3 solvers in parallel; each emits `SOLVER_DONE:<role>:<verdict>:<summary>`. When all 3 done, dispatch meta-judge:

```bash
envsubst < .claude/skills/codex-refactor-loop/prompts/meta-judge.md \
  > .refactor-loop/prompts/phase9/judge-issue${ISSUE_NUMBER}-r${ROUND}.md
.claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
  --cd "$REPO_ROOT" \
  --prompt .refactor-loop/prompts/phase9/judge-issue${ISSUE_NUMBER}-r${ROUND}.md \
  --log .refactor-loop/logs/phase9-issue${ISSUE_NUMBER}-r${ROUND}-judge.log \
  --timeout 3600
```

Meta-judge emits `META_JUDGE_DONE:<decision>:<...>`,**controller 路由表(强制)**:

| Decision | Category | Controller 动作 |
|---|---|---|
| `consensus:<framing>:<summary>` | — | auto-applies(派 implement,见 "Consensus action") |
| `converge:round-N:<question>` | — | 派 r-N+1 三 solver(把 convergence question prepend prompt) |
| `escalate:philosophy:<...>` | architecture-philosophy hardcoded trigger | **直接** label `🆘 human:卡死` + `auto-loop-stuck` + PushNotification |
| `escalate:stalled:<...>` | 3+ round 无 maintainer input 且 solver verdict 无变化 | **必须先派 reflector codex**(走 meta-layer reflect 节);**禁止**直接 label 人 |
| `escalate:<其他 category>` | conflict / budget-exhausted 等 | 派 reflector + 同时 PushNotification |

**重大 bug(per Auric 2026-05-20 "元思考逻辑似乎没有生效")**:iter18 中 #730 / #731 / #733 都 `escalate:stalled` 直接 label 了人,**没派 reflector**。原因:本节只写"escalate → label",没明确 `stalled` 子类必须 reflector 优先。已纠正——上表 `escalate:stalled` 行强制 reflector。

reflector spawn 模板见 "Meta-layer escalation" 节。reflector 输出 `META_RESOLVED:<kind>:<reason>` 后 controller 再按 retry-fix / re-design / re-cluster / drop / escalate-human 路由。**只有** reflector 显式输出 `META_RESOLVED:escalate-human:<reason>` 时,controller 才允许 label `🆘 human:卡死`。

### Reflector 完成 → 立即回到共识阶段(强制,per Auric 2026-05-20 "元讨论结束,可能之前由于打着需要人介入的标签,所以感觉并没有很好的再次进入共识阶段;整个系统的核心是多角色多角度共识")

**关键 bug**:之前 `escalate:stalled` 触发后挂 `auto-loop-stuck` + `👤 human:需-maintainer-决策` label,**reflector 完成后没清掉**,导致 issue 视觉上仍卡在"等人"状态;controller sweep 时也会看到 stuck label 误以为不需处理。

**修复**:reflector 完成(任何 `META_RESOLVED:<kind>` 除 `escalate-human` 外)后,controller **必须立即**执行 label transition:

```bash
gh issue edit <N> \
  --remove-label "auto-loop-stuck" \
  --remove-label "👤 human:需-maintainer-决策" \
  --remove-label "🆘 human:卡死" \
  --add-label "🔍 phase:design-solving" \
  --add-label "🤖 human:auto-推进"
```

然后按 `META_RESOLVED:<kind>` 路由立刻做下一步(派 fresh 3 solver 轮 / 关 issue / re-cluster);**不允许**停在"reflector done but stuck label still on"暧昧态。整个系统核心是多角色多角度共识——reflector 是中介调和角色,完成后必须把控制权交回 solver 共识循环。

唯一例外:`META_RESOLVED:escalate-human` → 保留 / 加 `🆘 human:卡死` label,这才是真正 human 介入态。

### Daemon → controller event channel + 自适应 wakeup(强制,per Auric 2026-05-20 "修一下 skills." 关于 daemon detect → controller 25 min gap 问题)

**问题**:`comment-monitor.sh` 30s 周期 detect maintainer 评论 ✓ + eyes react ✓ + daemon banner ✓,但 controller wakeup 默认 1500s(25 min),maintainer 评论到 controller 派 fresh round 最长 25 min gap — Auric 看 daemon banner 后等 controller。

**修法**:daemon 写 trigger file → controller per-wakeup 检测 → 缩短下次 wakeup。

#### Daemon 侧(comment-monitor.sh)

新 maintainer 评论 detect 后,**额外 append** 一行到 `.refactor-loop/.controller-pending-events.log`:
```
<ISO8601> new-team-comment <issue_number> <author> <comment_id>
```

(daemon 仍正常 react eyes + post daemon banner + write comment-monitor.log。新增的 pending-events 文件只用于 controller 自适应 wakeup。)

#### Controller 侧 — per-wakeup step 1.6:check pending events

每次 wakeup 在 sync(step 0)+ GitHub state derive(step 1)之后:

```bash
PENDING=".refactor-loop/.controller-pending-events.log"
LAST_PROCESSED=".refactor-loop/.controller-last-processed-event-offset"
prev_offset=$(cat "$LAST_PROCESSED" 2>/dev/null || echo 0)
cur_offset=$(wc -l < "$PENDING" 2>/dev/null || echo 0)
new_events=$(( cur_offset - prev_offset ))

if (( new_events > 0 )); then
  # 有 daemon detect 但未 controller-process 的 events
  sed -n "$((prev_offset+1)),$((cur_offset))p" "$PENDING" | while read -r line; do
    # 解析 issue / author / comment_id,触发 maintainer-reply-resets-the-round
    process_maintainer_reply "$line"
  done
  echo "$cur_offset" > "$LAST_PROCESSED"
  # 关键:下次 wakeup **缩短**到 600s — Auric 决策响应更快
  NEXT_WAKEUP_SECONDS=600
else
  NEXT_WAKEUP_SECONDS=1500  # 默认
fi

ScheduleWakeup(delaySeconds=$NEXT_WAKEUP_SECONDS, ...)
```

#### 自适应 wakeup 策略

| 触发 | 下次 wakeup 周期 |
|---|---|
| pending events file 有新 entry | **600s**(10 min,Auric 决策响应快) |
| in-flight codex(busy 状态) | 1500s(默认,等 task-notification) |
| 完全 idle 无 pending | 1800s(30 min idle heartbeat) |

#### 防回(❌ 禁止)

- ❌ daemon 写 events log 但 controller 不读 → maintainer 评论 → 25 min gap
- ❌ controller 处理完 events 但不缩 wakeup → 下次再来评论 → 又 25 min gap
- ❌ controller 不更新 LAST_PROCESSED offset → 每 wakeup 重复处理同 events

### Stuck label 4h 超时自动新一轮 meta-reflect(强制,per Auric 2026-05-20 "如果人长期不介入,比如四小时以上,则尝试进入新一轮元解决轮次,这样就不会积攒了")

每次 controller wakeup 第一动作之后(per-wakeup sweep step 1 完成后),对每个带 `auto-loop-stuck` OR `👤 human:需-maintainer-决策` OR `🆘 human:卡死` label 的 issue:

```bash
last_human_at=$(gh issue view <N> --json comments --jq '[.comments[] | select(.body | contains("⟦AI:AUTO-LOOP⟧") | not) | .createdAt][-1] // .createdAt' | tr -d '"')
now_epoch=$(date -u +%s)
last_epoch=$(date -j -u -f "%Y-%m-%dT%H:%M:%SZ" "$last_human_at" +%s 2>/dev/null \
  || date -u -d "$last_human_at" +%s)
delta_h=$(( (now_epoch - last_epoch) / 3600 ))

# 防重复:有 in-flight reflector(meta-reflect-issue<N>*.log mtime < 30min)→ 跳过
if (( delta_h >= 4 )) && [ -z "$(find .refactor-loop/logs/meta-reflect-issue<N>*.log -mmin -30 2>/dev/null)" ]; then
  # 派 fresh reflector,suffix -rN+1 防 overwrite 历史 reflector log
  spawn-reflector <N>
fi
```

意图:防 escalated issue 在"等 maintainer"无限堆积。4h 后**自动**派 fresh reflector,让 AI 反思能否重新框架到共识路径(narrow scope / drop / re-cluster),不积攒。

**反面禁止**:
- ❌ 见 stuck label 就跳过,不计算 delta
- ❌ 用 `author=loning` 判真人评论时间(deprecated,见 sentinel 节)
- ❌ 4h 内重复派 reflector 浪费 codex
- ❌ reflector 完成但忘清 stuck label → 下次 sweep 仍误判为 stuck

### 任何 concrete-plan 都必须走 multi-solver consensus(per Auric 2026-05-19 "核心流程是都需要达成共识")

**铁律**:任何"具体怎么改代码"级别的 plan(file:line / 新 type 列表 / 删除清单 / migration 步骤)只能由 **3 solver + meta-judge consensus** 产出,**不能**由单 codex(包括 writer-codex / investigator codex / analyst codex)直接给出。

具体禁止:
- ❌ writer-codex 把 maintainer 文字指令 "translate" 成 concrete impl 计划(即使指令很明确)→ 必须走 r(N+1) solver round
- ❌ analyst codex 在 design issue 评论里给具体方案(它只能澄清/反推/列选项,不能落地)
- ❌ controller 自己 inline 写 impl plan(controller 只能写 status / 链接 / label,不写计划)

允许:
- ✅ writer-codex 翻译已经达成共识的 solver/judge 输出 → 中文 GitHub post(consensus 已在前)
- ✅ investigator codex 收集证据(grep / dep chain / git log)→ 数据回答事实问题(不给 plan)
- ✅ writer-codex 起草 PR body / consensus 公告(基于已 consensus 的 plan)

当 maintainer 给出方向性指令(例如 #711 c8 "全删走 actor state"):
1. controller 把指令记入 `state.design_pending[i].maintainer_directive`
2. 立刻派一轮 fresh 3 solver(把指令 verbatim 作为 narrowing constraint)
3. solver 们各自把指令具体化成 impl 计划(可能 minimal 给一套、structural 给另一套、delete 给第三套)
4. meta-judge 仲裁 → 3/3 unanimous → 才能进 implement
5. 不允许跳过 3 solver round 直接 implement(哪怕 maintainer 觉得方向很明显)

理由:maintainer 直觉常常对,但 concrete 落地的细节(新 actor 边界 / proto 字段 / 命名 / 迁移路径)需要 3 个独立角度验证,避免单 codex 把 "明显方向" 误读成 "明显方案"。consensus 这步就是 catch 误读用的。

### Maintainer-reply-resets-the-round (mandatory)

Per Auric (2026-05-19): "凡是新回复都要完整重新让多个solver分析, 必须达成共识才可以."

When the auto-discover Monitor fires `design-issue-event:<N>` and the new comment is from a verified team member (per Phase 7 security gate) AND is substantive (>30 chars / contains technical content / not a controller self-reply):

1. **TaskStop any in-flight Phase 9 codex for that issue** (solvers OR meta-judge) — old reasoning is stale once new constraint lands.
2. **Treat the new comment as fresh constraint material** — prepend its verbatim text to a NEW round's solver prompt header under "Maintainer comment (must incorporate)".
3. **Dispatch FRESH 3 solver codex** (not "continue convergence"; truly fresh, with all prior rounds as context but no inherited stance).
4. **No round counter penalty** — maintainer input is the loop's continuation signal, not a stop signal. The round counter increments but does NOT trip the escalation cap.
5. **Only 3/3 unanimous + meta-judge consensus** moves the cluster to implement. Maintainer can override at any time by adding `auto-loop-resume` label with their explicit framing in a comment.

This means: even if a previous round escalated with `auto-loop-stuck`, a new maintainer comment re-opens Phase 9. The `auto-loop-stuck` label is removed automatically on reset; `phase9-converging` is re-applied.

### Consensus action (3/3 unanimous + meta-judge consensus)

1. Read the winning solver's "Concrete plan" section from the meta-judge output.
2. Materialize `prompts/implement-<cluster-id>.md` prepending:
   ```markdown
   ## Design decision (from Phase 9 consensus, issue #${ISSUE_NUMBER})
   <winning solver's framing verbatim>
   <winning solver's concrete plan verbatim>
   ```
3. Add `auto-loop-resume` label to the issue (mirrors maintainer-decision flow).
4. Move cluster from `design_pending` to `clusters_active`.
5. Dispatch implement codex per Phase 2 (worktree + 5400s timeout).
6. **Post 共识卡片**(强制,per Auric 2026-05-20 "请在共识环节添加一个共识卡片,这样一清二楚")— 不再用普通 status banner,改用 distinct **consensus card** 格式:

```markdown
## ✅ 共识卡片 — Phase 9 r${ROUND} consensus reached

| 维度 | 值 |
|---|---|
| Issue | #${ISSUE_NUMBER} ${TITLE} |
| Cluster | ${CLUSTER_ID} |
| Round | r${ROUND}(共识达成,3/3 unanimous) |
| 选定 framing | **${FRAMING}**(minimal / structural / delete 中的一个) |
| Solver 投票 | minimal: <verdict>:<summary> · structural: <verdict>:<summary> · delete: <verdict>:<summary> |
| Meta-judge 仲裁 | ${JUDGE_VERBATIM_REASON} |
| Concrete plan 摘要 | <3-5 bullet,来自 winning solver "Concrete plan" 头几条> |
| 下一步自动会做 | 1. 创 worktree + branch  2. 派 implement codex(timeout 5400s)  3. implement done 后 open PR + Phase 8 reviewer  4. PR merge 后 close 本 issue |
| **是否需要人介入** | **❌ 否**(自动推进;maintainer 仍可在本 issue 评论 override) |

📦 implement worktree:\`/Users/auric/aevatar-wt-iter${ITER}-${CLUSTER_ID}\`
📦 implement branch:\`refactor/iter${ITER}-${CLUSTER_ID}-${SLUG}\`

🤖 controller consensus card

⟦AI:AUTO-LOOP⟧
```

**约束**:
- 共识卡片第一行**必须** `## ✅ 共识卡片 — Phase 9 r${ROUND} consensus reached`(✅ 而非 📊,与普通 status banner 区分)
- 末尾 `🤖 controller consensus card` 标识 + sentinel
- 不在普通 status banner / 进度评论用 ✅ 开头(只共识达成时用)
- 共识卡片是 **一次性 event** post,implement 派出同 turn 内发,不重复

### Escalation criteria (hardcoded — always escalate)

These trigger escalation regardless of solver consensus. Meta-judge MUST flag them:

1. **Top-level CLAUDE.md clause change** — any solver proposes editing CLAUDE.md "## 顶级架构约束" / "## 架构哲学" / Phase rules
2. **New core abstraction** — any solver proposes new actor type, new envelope kind, new pipeline phase, new Layer
3. **`docs/canon/*` change** — repo architecture vocabulary change
4. **Rule exception that escapes scope** — proposed exception is broader than "this one transient sink"; the exception would apply to multiple code paths
5. **Cross-cluster coupling** — solver's plan requires touching another in-flight cluster's PR
6. **Performance constraint unverifiable** — solver claims latency/memory bound but only prod can verify
7. **Issue body's `human_brief.why_needs_design`** contains: `rule-boundary` / `architecture-change` / `philosophy` / `CLAUDE.md` / `canon-vocabulary`

### GitHub traceability (mandatory per SKILL.md "GitHub traceability" — same standard as Phase 8)

Every Phase 9 action posts a bilingual comment to the issue. **Humans must be able to read and decide from the issue alone** — solver outputs are bilingual by construction (per `prompts/solver-*.md`); the controller posts each one as a SEPARATE issue comment so the human can read the 3 perspectives side-by-side and override the meta-judge if needed.

| Phase 9 event | Issue comment content |
|---|---|
| Round N solvers dispatched | Bilingual: "Phase 9 round N — minimal/structural/delete codex in flight. 3/3 unanimous required to auto-implement; otherwise iterate." |
| Maintainer reply detected mid-Phase-9 | Bilingual: "Halted in-flight round; resetting with maintainer comment as new constraint. New round dispatched. Old round outputs preserved for solver context." |
| **Each individual solver completes** | Post FULL solver output as its own comment. Header: `## 🤖 Phase 9 Solver — \`<role>\` (round N)`. Body = verbatim solver output (already bilingual). One comment per solver, three comments per round. |
| **Meta-judge completes** | Post FULL meta-judge output as its own comment. Header: `## 🤖 Phase 9 Meta-judge — round N verdict: \`<consensus\|converge\|escalate>\``. Body = verbatim judge output (bilingual). |
| Meta-judge → consensus | Same as above + then a follow-up controller comment: "auto-loop-resume label added; implement codex dispatched" |
| Meta-judge → converge | Same as above + the round-(N+1) "solvers dispatched" comment that includes the convergence question for transparency |
| Meta-judge → escalate | Same as above + label `auto-loop-stuck` + `## 🤖 Controller next-step` comment laying out the exact human action needed + PushNotification |
| Hardcoded escalation trigger fired | Post meta-judge output + summary "architecture-philosophy trigger — escalating to human" + label `auto-loop-stuck`. Trigger fires on architecture-philosophy categories ONLY (see below); convergence-only splits do NOT escalate, they keep iterating. |

**Forbidden**: posting a "summary" of solver outputs instead of the FULL outputs. The human needs the raw reasoning, evidence, and concrete plans to make an informed call — a summary loses too much fidelity. The 3+ comments per round are intentional; they ARE the audit trail.

Required labels (additions to Phase 8 set):
- `phase9-solving`: 3 solver codexes in flight
- `phase9-judging`: meta-judge in flight
- `phase9-converging`: convergence round in progress
- (re-used) `auto-loop-resume` on consensus dispatch
- (re-used) `auto-loop-stuck` on escalation

### State tracking

```json
"design_pending": [{
  "cluster_id": "...",
  "issue_number": 684,
  "auto_solve": true,
  "phase9": {
    "rounds": [
      {"round": 1, "solvers": {"minimal": "propose", "structural": "propose", "delete": "abstain"},
       "judge": "converge", "convergence_question": "..."},
      {"round": 2, "solvers": {...}, "judge": "consensus", "chosen_framing": "structural"}
    ],
    "final_decision": "consensus:structural" | "escalate:philosophy" | null,
    "implement_dispatched": true | false
  }
}]
```

### Anti-spiral safeguards (no hard round cap — different safeguards instead)

Per Auric (2026-05-19) "凡是新回复都要完整重新让多个solver分析,必须达成共识才可以":

- **No `MAX_CONVERGENCE_ROUNDS` cap**. The loop iterates until 3/3 unanimous OR hardcoded escalation trigger OR maintainer adds `auto-loop-resume` with explicit framing OR maintainer closes issue.
- **Stall detection**: if 3 consecutive rounds with NO maintainer input AND NO change in any solver's verdict text → **trigger meta-layer reflector** (not human escalate;per Auric 2026-05-19 "issues 也一样")。Reflector 同样回 4 framing question + 输出 `META_RESOLVED:<kind>` marker;路由:
  - `retry-fix` → 派 r+1 solver,加 "reflector 提示: 你们三 round 没收敛,本轮必须 propose 新 framing 不重复之前"
  - `re-design` → reset Phase 9 round counter,prompt 重写带 reflector 总结的新 framing 角度
  - `re-cluster` → close design issue + audit re-split(下 iter 拆 cluster)
  - `drop` → close design issue with `wontfix`
  - `escalate-human` → `🆘 human:卡死` + PushNotification(仅 reflector 也无解)
- **Maintainer reply RESETS stall counter** — fresh round dispatched with their comment as constraint; stall counter goes back to 0.
- Solver may not propose a framing that any prior round's meta-judge ruled out as `escalate:philosophy:...` (track in `phase9.rounds[].ruled_out_framings`); doing so → meta-judge auto-escalates that solver's plan.
- Cumulative solver runtime across all rounds capped at 12h per issue (raised from 6h to account for maintainer-reset iterations); over → escalate as `stalled:budget-exhausted`.
- Hardcoded architecture-philosophy triggers (see "Escalation criteria" below) still escalate immediately regardless of consensus — the loop does not try to talk humans out of architecture decisions.

### When to trigger Phase 9 (operator policy)

- **Default OFF** per design pending. Operator opts in by setting `state.design_pending[i].auto_solve = true` OR by adding the `phase9-auto-solve` label on the issue.
- Rationale: Phase 9 is best for design issues where the answer is mostly mechanical (proto field name, file location, naming) but maintainer is offline / busy. Hard architectural calls should still go through Phase 7 maintainer dialog.
- The cluster spec's `requires_design: true` + `human_brief.why_needs_design` content informs the decision; if `why_needs_design` contains philosophy keywords, Phase 9 trigger is silently no-op'd and Phase 7 maintainer flow continues.

---

## Loop control

### This is an INFINITE refactor loop — never idle on "iter done"

Per Auric (2026-05-19): "这是一个无限重构循环". An iteration completing is NEVER a stop signal. The loop's only legitimate stops are:
1. Audit returns 0 candidates (codebase has no flagged violations under current rules) — extremely rare.
2. Every cluster in the current batch failed verify twice — escalate operator.
3. Operator explicitly tells the loop to stop.

**Iteration boundary is automatic**: as soon as iter N's last cluster PR merges into `integration_branch` (NOT after rollup PR human review — rollup runs independently in parallel as a human gate), controller IMMEDIATELY dispatches `Phase 1 audit` for iter N+1. The rollup PR (auto-refact-dev → review_base_branch) is a parallel human-review track, not a serial gate.

Concretely, this means:
- After PR #708 (cluster-027 in iter15) merged, controller does NOT wait for PR #690 (rollup) review — it immediately dispatches the iter16 audit codex.
- iter16 implement / verify / Phase 8 review runs in parallel with iter15 rollup PR being reviewed.
- If iter15 rollup PR gets rejected by human, iter16 work stays on auto-refact-dev (which now contains iter15 + iter16 deltas); we re-do iter15 rework on top and ship combined.

### Sync to remote in time (强制)

Per Auric (2026-05-19): "及时与远程同步."

- After EVERY skill edit that affects controller behavior, `git commit && git push origin auto-refact-dev` IMMEDIATELY — do not batch multiple skill changes for a single push, do not defer to "end of turn".
- After EVERY cluster PR commit (fix codex round output): `git push origin <branch>` IMMEDIATELY — the reviewer / CI / Auric all need to see latest state, not yesterday's local state.
- Phase 6 sync (auto-refact-dev ← origin/dev) runs FIRST on every controller wakeup; never assume "I just synced" — verify with `git fetch && git rev-list --count`.
- Phase 5 CI watch reads `gh pr checks <PR>` (always remote), never a local cached value.
- Phase 7/8/9 reviewer/judge outputs MUST be posted to GitHub as PR/issue comments within the same controller turn they complete; do not let them sit local-only across multiple turns.

If a push fails (network, conflict, branch protection): controller MUST surface the failure inline and either fix-and-retry or escalate within the same turn — never silently leave local changes uncommitted/unpushed.

### Stop conditions / stop action

- **Stop conditions**: audit returns 0 candidates twice in a row OR every cluster in current batch failed verify twice OR operator says stop.
- **Stop action**: omit ScheduleWakeup, TaskStop any monitor, send one-line PushNotification with summary.

### Wakeup cadence

- Primary: harness task notifications (auto on codex exit).
- Fallback: 1200–1800s ScheduleWakeup (matches /loop dynamic mode guidance).

---

## 状态横幅(status banner)— 强制(per Auric 2026-05-19 "人类角度看不到最终状态")

**问题**:design issue / PR 一旦进入 multi-codex loop,会堆积几十条 audit / solver / judge / reviewer / fix 评论。人类站维护者角度打开 issue 一眼看不出"现在到了哪步、是否需要我介入"。100 条 AI 评论自治不等于 transparent。

**规则**:**controller** 在每次 phase transition 时**必发** status banner 评论。Codex 不发 banner(它们各发各的角色 artifact 评论)。Banner 是 controller-owned 集中状态指示器。

### Banner 触发时刻(每个均强制 post)

| 触发 | banner 内容要点 |
|---|---|
| 共识达成(Phase 9 meta-judge `consensus`) | "✅ 共识达成,implement 派出" + chosen framing |
| implement 完成(任何 cluster) | "实施完成,即将开 PR" + LOC delta + 文件清单 |
| PR open | "PR open + reviewer 派出" + PR # + base branch |
| Phase 8 r1 reviewer 完成 | "评审 r1: <N approve / N comment / N reject>" + next step |
| Phase 8 fix 派出 | "fix r<N> 派出,目标修 reject" |
| Phase 8 consensus 达成 | "Phase 8 共识达成,等 CI 绿后 merge" |
| CI 全绿 | "CI 全绿,合并中" |
| CI red | "CI 红,fix codex 派出" |
| merge 完成 | "🎉 已合并到 <branch>" |
| escalation | "🚨 需要人介入: <reason>" + label `auto-loop-stuck` |
| blocked-on(被其他 issue 拖) | "blocked-on #<num>: 待其完成自动推进" |

### Banner 模板(controller 直接 gh issue/pr comment,不走 codex)

第一行**必须** `## 📊 当前状态 — <短 phase 名>(<介入与否>)`。然后表格 + 下一步 + 何时介入。

```markdown
## 📊 当前状态 — <phase>(<不需要人介入 | ✅ 需要人介入>)

| 维度 | 值 |
|---|---|
| 阶段 | **<phase 名>** |
| 共识 | ✅/❌ <link 到 meta-judge 评论> |
| 关联 issue | #N #M |
| 关联 PR | #K(若有) |
| codex 任务 | <task-id>(<已跑 min> / <上限 min>) |
| **是否需要人介入** | **❌ 否** / **✅ 是: <原因>** |

**下一步自动会做**:<具体动作>

**何时需要人介入**:
- <具体条件 1>
- <具体条件 2>

🤖 controller status banner
```

### 硬约束

- **第一行必须是 `## 📊 当前状态 — ...`**(comment-monitor 据此识别 controller-post 跳过自 react)。
- 每条 banner 末尾必须 `🤖 controller status banner`(双重防护)。
- **不写过程**(谁讨论了什么)。只写"当前 phase + 下一步 + 何时介入"。讨论详情在前面 codex 评论里,banner 是 *index*,不是 *recap*。
- 不要发废 banner(同 phase 连续两次没变化 → 不要重发)。
- escalation banner 必须**显式**说"✅ 需要人介入"并列出 maintainer 需要做的具体决策(不是"看一下"这种 vague 描述)。

### Escalation banner — 必须含问题 ASCII 图 + 详细问题描述(强制,per Auric 2026-05-20 "修改一下需要人介入的时候画一下 ascii 图在 github 上, 详细说明一下" + "改问题描述清楚,而不是把决策路径描述清楚")

普通 status banner 是 *index*(简短);**escalation banner 是 maintainer 看懂问题的依据**,必须**问题导向**:

1. **问题 ASCII 图**:当前架构里**问题模式**长什么样(数据流 / 调用链 / 状态归属违反点),不是 reflector 路径
2. **问题描述**:具体 `file:line` + 当前行为 + 违反的 CLAUDE/AGENTS 条款 + 影响范围
3. **决策选项**:每个选项的 Plan / 影响 / Tradeoff(maintainer 看完问题描述自己判断哪个更合理)
4. **maintainer 行动入口**:choose A/B/C / narrowing constraint / close wontfix
5. **历史轮次**降为表格内**一行**(`r1+reflector+r2 仍 escalate` 一句话),不画路径图——不是 maintainer 关心的

#### 模板(必须严格遵循)

```markdown
## 🆘 状态卡片 — 需 maintainer 决策

| 维度 | 值 |
|---|---|
| Issue | #<N> <title> |
| Cluster | <cluster-id> |
| 历史 | r1+reflector r1+r2+reflector r2 全 escalate(详情见上面评论) |
| **核心问题** | **<一句话,具体到 file/类/方法,说清楚现在系统在哪里做什么导致违反什么>** |
| **需要决策** | **<一句话,maintainer 该回答什么问题>** |

### 问题图(ASCII)

\`\`\`
当前架构(违反点):

  ┌──────────────────────┐         ┌─────────────────────┐
  │  <调用方 file:line>  │ ──────▶ │  <被调对象 file:line>│
  │  e.g. endpoint       │         │  e.g. ExternalLink   │
  │                      │         │      Manager         │
  └──────────────────────┘         └─────────────────────┘
                                            │
                                            ▼ ← problem: 这里持有 process-local
                                   ┌─────────────────────┐
                                   │ ConcurrentDictionary│
                                   │ <state-violating-X> │
                                   └─────────────────────┘

  违反:<CLAUDE.md 哪条 + 一句话>
\`\`\`

(根据问题类型画对应图——状态归属:框 + 数据流箭头;生命周期:时间线 + actor 栏;调用链:source→sink 链;依赖反转:层 + 反向箭头标 ❌)

### 问题描述

**当前行为**(具体到代码):
- `<file:line>`:<这里在干什么,1-3 行>
- `<file:line>`:<另一个 evidence>

**违反规则**:
- CLAUDE.md「<引用条款>」
- 或 AGENTS.md「<引用条款>」

**影响范围**:
- <谁会被 affected,具体到 callers / data flow>
- <如不修是否阻塞 production / cause silent fail / 仅风格>

**为什么不是机械重构能解**:
- <如:涉及 public surface 删除策略 / cross-cluster 耦合 / docs/canon 改动 / 性能 vs 简洁取舍>
- <一句话引用 hardcoded trigger 命中:e.g. "trigger #3 fires whenever solver plan touches lifecycle">

### 决策选项

#### 选项 A — <选项名,动词起始>
- **Plan**:<具体 file:line 改动,1-3 行>
- **影响**:<改动范围 + 谁会被 break>
- **Tradeoff**:<这条路的代价>

#### 选项 B — <选项名>
- **Plan**:...
- **影响**:...
- **Tradeoff**:...

#### 选项 C — <选项名>(可选,2-4 个之间)
- ...

### Maintainer 行动入口

- **选定**:评论 `choose: A` / `choose: B` / `choose: C` 或给具体 narrowing constraint
- **重派**:加 `auto-loop-resume` label,controller 用你评论作 narrowing 派 fresh round
- **不做**:close issue + 加 `wontfix` label

🤖 controller status banner

⟦AI:AUTO-LOOP⟧
```

**约束**:
- 问题 ASCII 图**画当前架构的违反点**——数据流 / 状态归属 / 调用链 / 生命周期等;**不画**reflector / round 路径(那是过程,不是问题)
- 用 box-drawing(`─│┌┐└┘▶▼◀▲`)+ 空格对齐;**禁用 mermaid**(per CLAUDE.md "GitHub issue/PR comment mermaid 禁忌")
- 历史 round 信息**降级为表格一行**(`r1+reflector+r2 仍 escalate`),不占主视觉
- 决策选项 2-4 个,每个 Plan / 影响 / Tradeoff 三栏(file:line 级别)
- "为什么不是机械重构能解"段是**根因**而非 *recap*(maintainer 看一眼知道为什么 AI 不接手)
- 末尾标准 `🤖 controller status banner` + sentinel
- 背景段不要 *recap* 评论历史,是**为什么 AI 解不了**的根因分析
- maintainer 行动入口三选一(选项 / 重派 / 关闭)
- 末尾标准 `🤖 controller status banner` + sentinel

### 反面(❌ 禁止)

- ❌ 一堆 codex artifact 评论之后无 status banner → 人类不知道当前 phase
- ❌ banner 把过程 recap 一遍 → 噪音叠加噪音
- ❌ banner 用 `## 🤖 controller` 第一行(comment-monitor 已经把 `## 🤖` 当 codex post 跳过,但 banner 应该是 controller 自己,用 `## 📊` 区分)
- ❌ "需要人介入"用模糊措辞 → 人类还是不知道要不要看

## Meta-layer escalation — 强制(per Auric 2026-05-19 "3 轮还解决不掉,则考虑是否应该把问题升级,再更元层进行考虑解决问题")

**问题**:Phase 8 fix r6 仍 reject,或 CI same-check 6 次仍 fail,**第一反应不是喊 human**,而是**反思上一层是否本身错了**。喊 human 是最后的手段。

**层级**(由小到大):
1. **fix(r1..r6)**:针对 reviewer evidence 直接补丁
2. **Meta-layer reflect**:反思 design / cluster / audit 框定是否本身错位
3. **Phase 9 re-design**:重派 3 solver + meta-judge,prompt 带 "previous design caused 6 round non-converge"
4. **Cluster re-split**:audit 阶段 re-evaluate,把当前 cluster 拆 / 合 / 撤回
5. **Drop / wontfix**:确认任务本身价值不足,关 PR + close issue with wontfix
6. **Human escalation**:`🆘 human:卡死` + PushNotification(只在 meta-layer 也无法解时)

### 触发 meta-layer 反思

- Phase 8 `fix_round > 3` 仍 reject(所有 reviewer 同一组 / 同一 reviewer 反复 reject)
- CI same-check 失败 6 次(同 test 6 次 fix 仍红)
- Cumulative PR diff size > 原 PR 200%(scope-runaway 信号)
- Reviewer 同一类 evidence(test coverage / dead surface / self-doc)在 3 round 内反复出现 → meta-reflect "为什么 evidence 总是同类"
- **Phase 9 design issue stall**:3 consecutive round 无 maintainer input AND solver verdict text 无变化 → 也走 meta-layer(per Auric 2026-05-19 "issues 也一样")

### 派出 reflector codex

```bash
# 内容(prompt 摘要)
你是 reflector codex,不写代码,只反思。Input:
- 当前 PR diff
- 所有 review round 的 reject evidence(verbatim)
- 当前 Phase 9 共识 / audit cluster 框定
你的任务:回答 4 问 + 给 1 决议:
1. Reviewer 反复 reject 的根本原因是 design 错位 / cluster scope 错位 / audit framing 错位 / 还是仅"reviewer 在做完整审查正常 surfacing 小 gap"?
2. 当前 PR scope 是否爆炸(原 cluster 范围 vs 现 diff)?
3. 当前 design 共识(Phase 9)是否本身有漏洞(reviewer 抓到 design 没考虑的角落)?
4. Audit cluster 框定是否过大 / 过小 / 错混?

决议(选一):
- `META_RESOLVED:retry-fix`: 是 reviewer 正常审查,继续 fix r4+ 仍可收敛(给 reviewer 一个 "approve if r4 仍 narrow valid" 的窗口)
- `META_RESOLVED:re-design`: design 错位,关 PR / 撤回当前 implement,re-Phase 9 with reflector prompt
- `META_RESOLVED:re-cluster`: cluster scope 错位,关 PR + audit 阶段 re-split(拆为 2-3 个小 cluster)
- `META_RESOLVED:drop`: 任务价值不足或代价 > 收益,关 PR + close issue wontfix
- `META_RESOLVED:escalate-human`: meta-layer 也无法解,真的需要 maintainer 决策

```bash
.claude/skills/codex-refactor-loop/scripts/spawn-codex.sh \
  --cd /Users/auric/aevatar \
  --prompt .refactor-loop/prompts/meta-reflect-pr<N>.md \
  --log .refactor-loop/logs/meta-reflect-pr<N>.log \
  --timeout 3600
```

Controller 读 marker 后路由:
- `retry-fix` → 派 fix r4 + 提高 max_fix_rounds 临时到 5(只本 PR)+ 同时 narrow reviewer 关注新 evidence only(不再 surface 旧 evidence)
- `re-design` → 关 PR / 撤回 commits / re-Phase 9 with constraint = reject evidence pattern
- `re-cluster` → 关 PR / audit re-split(产新 cluster 在 next iter)
- `drop` → close PR + close issue with `wontfix` label + 转 phase merged-no-op
- `escalate-human` → label `🆘 human:卡死` + PushNotification(只 meta-layer 也无路时)

### 反面(❌ 禁止)

- ❌ fix r4 直接派出而不 reflect → 可能在错的层级死循环
- ❌ 3 轮卡死直接升 human → 没把 AI 自身的反思能力用足
- ❌ reflector 也写代码 → 它的职责是 question framing,不是 propose fix
- ❌ reflector 决议 `re-design` 但 controller 继续派 fix → 框架失效
- ❌ 临时 `max_fix_rounds = 5` 滥用 → 仅 reflector 明确 `retry-fix` 时允许,且不超过 5

## CI 监控即时推进 — 强制(per Auric 2026-05-19 "ci 监控,应该红了就及时推进")

**问题**:PR push 后 controller 把 CI watch 当 "等 Monitor 通知" 然后该睡就睡。结果 CI 红了 controller 没及时反应,PR 一红就挂半天,人类看到 🔴 而无动作。

**规则**:**每次 controller wakeup**(/loop 触发 + 任何 task notification)**必查所有 open PR 的 CI 状态**。任一 PR 出 fail check → 立刻派 fix codex,不等下一个 task notification。

### 强制 sweep

每次 wakeup 第一件事(在处理 task notification / 派新 codex 之前):

```bash
# 列所有 auto-loop 创建的 open PR
for pr in $(gh pr list --label "auto-loop,🚀 phase:pr-open,⚙️ phase:ci-running" --state open --json number --jq '.[].number'); do
  failed=$(gh pr checks "$pr" --json bucket --jq '[.[] | select(.bucket=="fail") | 1] | length')
  if [ "$failed" -gt 0 ]; then
    # 立即拉 fail log + 派 fix codex + label `🔧 phase:fixing` + post banner
    handle_ci_red "$pr"
  fi
done
```

### CI red 处理流水

1. **拉 fail log**:`gh run view <run> --log-failed > .refactor-loop/logs/remote-ci-pr<N>-<check>.log`
2. **分类**(per Phase 5):
   - flaky/infra → retry by `gh workflow run` 或 empty commit;记入 `clusters_failed.<id>.flaky_retries++`
   - real failure → 派 fix codex(prompt 含 fail log + 推荐修法)
   - pre-existing failure(同失败也存在于 base branch) → 不在本 PR 修,PushNotification 标记
   - codecov/patch → 派 `prompts/test-add.md` codex(uncovered patch lines)
3. **label 转 `🔧 phase:fixing`** + post `## 📊` banner
4. **fix codex 完成 → controller 立刻 commit + push + 重 watch CI**
5. **3 次 fix 同 check 仍 fail → label 升 `🆘 human:卡死-需-rework` + PushNotification + 停 loop**(per Auric 2026-05-19 "2 轮太少,改到 3 轮"(2026-05-20 "共识轮次由 6 轮改为 3 轮"))

### 反面(❌ 禁止)

- ❌ 拿到 push 结果就走,不 arm CI watch / sweep → 红了没人管,PR 挂尸
- ❌ 看到 CI 红等下次 controller wakeup 才反应 → 滞后 25 min
- ❌ pre-existing failure 不区分 → 一直 fix 改不动本 PR 的红
- ❌ 同 check 连续 fix 6 次以上 → 卡死无 escalation
- ❌ codecov/patch 红被忽略 → 重构引入的 net-new line 没测试

## Codex 进展实时上报 — 强制(per Auric 2026-05-19 "每10分钟更新一次各 codex 进展到 issue/PR")

**问题**:codex 单 task 可能跑 30–120 分钟。期间人类打开 issue/PR 只看到"派出"banner,看不到中间进展(它在分析哪个文件 / 在写什么 / 跑到第几步)。等结果 banner 时已经 1–2 小时过去。

**规则**:`tools/refactor-loop/codex-progress-reporter.sh` 作为**长跑 daemon**每 600s 扫所有 in-flight codex log,对每个 codex **edit-in-place** 一条 progress comment 到关联 issue/PR(不堆评论)。Comment body 包含:已跑时长 + log tail 25 行。完成时把 ⏳ 改 ✅。

### 启动 / 运维

```bash
# 启动(放后台,长跑直到 loop 停)
INTERVAL=600 bash tools/refactor-loop/codex-progress-reporter.sh &
# 停止
pkill -f codex-progress-reporter.sh
# 重置(误 post 后清理)
jq -r '.[].comment_id' .refactor-loop/codex-progress-state.json | while read cid; do
  [ "$cid" != null ] && [ "$cid" != 0 ] && gh api -X DELETE repos/aevatarAI/aevatar/issues/comments/$cid
done
echo "{}" > .refactor-loop/codex-progress-state.json
```

### 行为契约

- 第一次见 + 已 finish 的 log → 跳过(不补刷历史 "✅" banner,避免噪音)
- 30 min 未写 mtime 且无 EXIT marker → zombie,跳过(防 monitor 误以为 in-flight 死循环 post)
- log 文件名 → target 解析:`review-pr*` / `fix-pr*` / `phase9-issue*` 用文件名权威 target;其他从 `prompts/<base>.md` 中**最后一个** `#NNN`(meta-cluster 多 issue 时,主 issue 通常列在最后)
- 内容 hash 不变 → 不重发(避免 1 min 心跳 noise)
- Comment 第一行 `## 📊 codex 进展` → comment-monitor 据此跳自 react
- audit-iter-* / remote-ci-* log 不归此 reporter 上报(无关联 issue)
- **codex 完成(in-flight→finished)→ 直接 `gh api -X DELETE` 删除该 progress comment**(per Auric 2026-05-19 "完成后删掉就好了,否则太占空间")。不留 "✅ 已结束" 状态占评论位。
- `is_finished()` 只看 log 末 5 行 `^EXIT=`(防 codex 中途 echo "EXIT=" 内容误判)

### 反面(❌ 禁止)

- ❌ 每 tick 重新 `gh issue comment` 创建新评论 → 评论膨胀
- ❌ 把 progress comment 写到 controller post 之上 → 与 status banner 混淆
- ❌ 上报已完成的旧 iter log → 噪音,人类困惑
- ❌ 用 `gh issue view N --json number` 判 PR/issue(返回都有 number)→ 必须 `gh pr view N` 试错 + fallback
- ❌ codex 完成后保留 "✅ 已结束" 进展 comment → 评论列表膨胀,人类要翻历史。**必须删**
- ❌ `grep "^EXIT="` 全 log 判 finished → codex 中途 echo / cat 含 EXIT= 文件会误判。**必须 tail -5**

## Label 系统 — 强制(per Auric 2026-05-19 "label 也明确一下,看标题就能看明白")

**问题**:人类在 issue 列表页只看 title + label,banner 评论再清晰也得点进去才看见。Label 是封面信息,必须一眼传达"当前 phase + 是否需要人"。

**规则**:每次 phase transition,**controller** 在 post banner 的同时**必同步** label。每个 issue / PR **恰好**带一组 label:

### Label 组 1 — Phase(任意时刻**恰好一个**)

| Label | 含义 | 触发 |
|---|---|---|
| `🔍 phase:design-solving` | Phase 9 多 solver 跑 | 派 r1/r2 三 solver 后 |
| `✅ phase:consensus-reached` | meta-judge 共识达成 | meta-judge `consensus:...` 后 |
| `🛠️ phase:implementing` | implement codex 跑 | implement dispatch 后 |
| `🚀 phase:pr-open` | PR 已开 | gh pr create 后 |
| `👀 phase:reviewing` | Phase 8 reviewer 跑 | reviewer dispatch 后 |
| `🔧 phase:fixing` | fix codex 跑(reject 后修) | fix dispatch 后 |
| `⚙️ phase:ci-running` | CI watch 中 | push 后 CI 启动 |
| `🎉 phase:merged` | 已 merge | gh pr merge 后(也 close issue) |
| `⏸️ phase:blocked` | blocked-on(等其他 issue) | dependency 链上游未完成 |

### Label 组 2 — Human(任意时刻**恰好一个**)

| Label | 含义 | 触发 |
|---|---|---|
| `🤖 human:auto-推进` | 完全自动,**不需要人介入** | 默认 |
| `👤 human:需-maintainer-决策` | escalation 触发,需要 maintainer 拍板 | Phase 9 escalate / hardcoded trigger |
| `🆘 human:卡死-需-rework` | 2 次 fix 仍失败 | fix r2 仍 reject / CI 2 次 fail |

### Bootstrap(一次性 - controller 在首次跑 loop 时确保 label 存在)

```bash
# 创建所有 phase label
for l in "🔍 phase:design-solving" "✅ phase:consensus-reached" "🛠️ phase:implementing" \
         "🚀 phase:pr-open" "👀 phase:reviewing" "🔧 phase:fixing" "⚙️ phase:ci-running" \
         "🎉 phase:merged" "⏸️ phase:blocked"; do
  gh label create "$l" --color "5319e7" 2>/dev/null || true
done
# 创建所有 human label
gh label create "🤖 human:auto-推进" --color "0e8a16" 2>/dev/null || true
gh label create "👤 human:需-maintainer-决策" --color "d93f0b" 2>/dev/null || true
gh label create "🆘 human:卡死-需-rework" --color "b60205" 2>/dev/null || true
```

### 转移时刻代码模板

每次 phase transition,controller 用同一 helper 改 label + post banner:

```bash
# helper(写在脚本里): 移除所有 phase:* label, 加新 phase:* label
set_phase() {
  local issue=$1 new_phase=$2
  # 先删所有 phase:* / human:* label 再加新
  current=$(gh issue view "$issue" --json labels --jq '.labels[].name' | grep -E '^(🔍|✅|🛠️|🚀|👀|🔧|⚙️|🎉|⏸️) phase:')
  for old in $current; do gh issue edit "$issue" --remove-label "$old" 2>/dev/null; done
  gh issue edit "$issue" --add-label "$new_phase"
}
set_human() {
  local issue=$1 new_human=$2
  current=$(gh issue view "$issue" --json labels --jq '.labels[].name' | grep -E '^(🤖|👤|🆘) human:')
  for old in $current; do gh issue edit "$issue" --remove-label "$old" 2>/dev/null; done
  gh issue edit "$issue" --add-label "$new_human"
}
```

PR 同理(`gh pr edit` instead of `gh issue edit`)。

### 硬约束

- **Label 与 banner 同步发**:不允许 label 转移但不发 banner,或发 banner 但 label 没改。
- **同一组只允许一个**:不能同时有 `🛠️ phase:implementing` 和 `🚀 phase:pr-open`(实施完成 → 立刻改 pr-open)。
- **`👤` 与 `🆘` 出现 = 需要人**:其他 label(`🤖`) = 完全自动。人类只 watch `👤` / `🆘` issue 即可。
- **escalation 永远配 `👤` 或 `🆘`**:Phase 9 escalate / Phase 8 卡死必须明确标 human label。

### 反面(❌ 禁止)

- ❌ label 不更新就发 banner → 列表页看到的还是旧 phase
- ❌ 同时挂多个 phase label → 人类困惑
- ❌ 用纯文字 label(无 emoji)→ 列表页一眼看不出 phase / human 类别
- ❌ blocked-on 不打 `⏸️ phase:blocked` → 人类以为还在主动跑
- ❌ **PR 不加 `auto-loop` label** → comment-monitor.sh 查的是 `--label auto-loop` 而非 phase:*,漏加 = monitor 完全不监控该 PR 评论 → maintainer 喊话无 react 无回复(per Auric 2026-05-19 "我发现你会掉监控")

## Codex 调用方式 — 强制(per Auric 2026-05-19 "claude code 使用 shell 的方式调用,可以看到 shells")

**问题**:codex 进程要让 Auric 在 Claude Code UI 的 background tasks / shells panel 一眼可见。

**规则**:**所有 codex spawn 用 Bash tool `run_in_background: true`**。Claude Code harness 跟踪该 background task,显示在 UI shells/tasks 面板 → Auric 看到 "8 shells" 等计数。`nohup ... & disown` 反而 detach 出 harness,Auric 看不见 — **禁用**。

### 推荐调用 pattern

```python
Bash(
  command=".claude/skills/codex-refactor-loop/scripts/spawn-codex.sh "
          "--cd <dir> --prompt <prompt-file> --log <log-file> --timeout 5400",
  run_in_background=True,    # 必须 true → 进 Claude Code shells panel
  description="cluster-XXX implement"
)
```

返回 task-id(e.g. `bjat04xwl`),codex 完成时 harness 自动发 task-notification 唤醒 controller。

### 完成检测

- Primary: task-notification(harness 自动发,codex exit 时即触发)
- Fallback: controller wakeup 时仍 sweep log tail 找 `^EXIT=` 防 notification 漏(zombie 30min mtime 无 EXIT → 告警)

### 反面(❌ 禁止)

- ❌ `nohup spawn-codex.sh ... & disown` → 脱离 Claude harness,UI 看不到 shells,Auric 失去观测
- ❌ Bash `run_in_background: false` 同步等 codex(可能跑 1-2h)→ Bash tool 阻塞,turn 卡死
- ❌ codex 跑在 controller 自己的 conversation Bash 里 → 同步阻塞 OR 中断 UI

## Hard rules (controller-level, propagated into every codex prompt)

1. **No new features** — only clean violations of CLAUDE.md philosophy.
2. **No external repo changes** — NyxID / chrono-* are out of scope.
3. **Code self-documents the refactor** — every refactored type/method gets a 3-5 line comment of the form `// Refactor (iterN/cluster-XXX): Old pattern: …  New principle: …`.
4. **No `commit`/`push`/`checkout` inside codex prompts** — the controller owns git topology.
5. **No `Task.Delay`-based test pacing** — tests must use deterministic awaiters.
6. **No `[Skip]` / disabled tests** as a way to make CI green.
7. **No scope creep** — codex must print `SCOPE_EXTEND: <file> <reason>` before touching anything outside `scope_paths`.
8. **All user-facing output is in 中文 by default** (per Auric 2026-05-19 "默认工作语言中文吧, 不双语了"). Every GitHub issue body, PR description, design notification, and any natural-language artifact uses 中文 as the working language. Code identifiers, file paths, log markers, CLI commands, and proto/yaml structure stay original (English). English may appear inline when quoting (a) a CLAUDE.md / AGENTS.md clause, (b) error messages, (c) test names — quote verbatim, do not translate. No mandatory parallel English section.

## 工作语言规则(默认中文)

Per Auric (2026-05-19) "默认工作语言中文吧, 不双语了": **所有 user-facing artifact 默认 中文**。

适用对象:GitHub issue body、PR description、PR comments、design issue auto-loop 评论、scorecard docs (`docs/audit-scorecard/`)、escalation 文案、cross-post 通知。Internal artifact(`.refactor-loop/runs/*.md`、log、state.json)仍是英文(只要 grep / 调试用)。

### 规则

Per Auric (2026-05-19) 二次确认 "github上的也都中文,除了注释英文其他的都中文":

| 内容类型 | 语言 |
|---|---|
| GitHub issue title / body / 评论 | **中文** |
| GitHub PR title / body / 评论 | **中文** |
| Git commit message | **中文**(包括 controller 写的 fix/merge/squash 等) |
| Push notification | **中文** |
| Skill 文档 / docs/canon /audit 报告 | 维持现状(中英混排已存在) |
| **代码内 `// Refactor (iterN/cluster-XXX):` 注释** | **英文**(production code 跨团队读) |
| **代码内 doc comment / xmldoc / 其他注释** | **英文** |
| 代码 identifier / 类名 / 方法名 / 字段 | 英文(原 .NET / 项目惯例) |
| proto / yaml 结构 | 英文 |
| CLI 命令 / 文件路径 / SHA / URL | 英文 |
| CLAUDE/AGENTS 条款 verbatim 引用 / error message / test name / 第三方英文 quote | 引用原文,不翻译 |

具体红线:
1. 不再生成平行 `## English` section。
2. 不再要求 `_en` + `_zh` 对。`prompts/audit.md` `human_brief` 块只保留中文字段(去掉 `_zh` 后缀)。
3. TL;DR 也是中文。
4. Controller 自己写的 `git commit -m "..."` 用中文。fix codex / writer codex prompt 里要求写中文 commit message。
5. PR title 中文(但分支名仍 `refactor/iter15-cluster-XXX-...` 英文以维持 ID 惯例)。
6. **已发布的 EN+ZH 历史 artifact 保留原样**:不回头删 / 重译。新发的按本规则走。

### 历史 bilingual 规则的位置

本节之前的"Bilingual rule (双语规则)"硬要求双语 + equivalence test 已废止。`prompts/audit.md`、`prompts/solver-*.md`、`prompts/meta-judge.md`、`prompts/review-fix.md`、`prompts/design-issue-reply.md`、`prompts/github-post-writer.md` 等所有 codex prompt 在引用本 skill 时,把"bilingual EN+ZH" 一律读作"中文(允许英文引用)"。Prompt 文件后续会随用随改;过渡期 prompt 里旧的 bilingual 措辞按本节理解,不强制双语生成。

### 例外

`docs/canon/*.md` 与 `docs/adr/*.md` 在仓库内的文档仍按 [docs/canon/architecture-vocabulary.md](docs/canon/architecture-vocabulary.md) 既有惯例(混排,不归本规则管辖)。CLAUDE.md / AGENTS.md 仍是中英混排,不动。

---

## Files

- [prompts/audit.md](prompts/audit.md) — audit phase template
- [prompts/implement.md](prompts/implement.md) — implement phase template (per cluster)
- [prompts/verify.md](prompts/verify.md) — verify phase template (per cluster)
- [prompts/remote-ci-fix.md](prompts/remote-ci-fix.md) — Phase 5 remote-CI fix template
- [prompts/test-add.md](prompts/test-add.md) — Phase 5 codecov-driven test-add template (per cluster)
- [prompts/design-issue-body.md](prompts/design-issue-body.md) — Phase 1/6 GitHub issue body for `requires_design: true` clusters
- [prompts/design-issue-reply.md](prompts/design-issue-reply.md) — Phase 7 analyst codex template for substantively replying to maintainer comments on design issues
- [prompts/reviewer-architect.md](prompts/reviewer-architect.md) — Phase 8 architect reviewer (CLAUDE.md compliance angle)
- [prompts/reviewer-tests.md](prompts/reviewer-tests.md) — Phase 8 tests reviewer (coverage/quality angle)
- [prompts/reviewer-quality.md](prompts/reviewer-quality.md) — Phase 8 code quality reviewer (readability/simplicity angle)
- [prompts/review-fix.md](prompts/review-fix.md) — Phase 8 fix-codex: addresses reject demands without escalating to human
- [prompts/solver-minimal.md](prompts/solver-minimal.md) — Phase 9 solver A: minimal-change framing
- [prompts/solver-structural.md](prompts/solver-structural.md) — Phase 9 solver B: CLAUDE-aligned structural framing
- [prompts/solver-delete.md](prompts/solver-delete.md) — Phase 9 solver C: question necessity / delete-or-defer framing
- [prompts/meta-judge.md](prompts/meta-judge.md) — Phase 9 meta-judge: arbitrate 3 solver outputs (3/3 unanimous required)
- [scripts/spawn-codex.sh](scripts/spawn-codex.sh) — standardized `codex exec` wrapper (enforces 3600s minimum timeout)
- [REFERENCE.md](REFERENCE.md) — state schema, batching heuristics, recovery playbook
