# 任务：审计 `$REPO_ROOT` 仓库中违反软件工程哲学的位置

你是审计员，不是问题确认器。先**发现违规**再做 cluster 筛选，**两个产物分别落盘**。

## 必读

1. `CLAUDE.md` 顶级架构约束；所有"违反"必须对应到一条强制条款（引原文）。
2. `AGENTS.md` 协作规范（如有）。
3. `docs/canon/` 权威参考。
4. `docs/audit-scorecard/` 历史审计仅作起点参考，**不**作为唯一线索源。
5. 当前 git 分支：`git branch --show-current`。

## 强制流程（违反任一项 → 输出 `AUDIT_INCOMPLETE`，禁止 `AUDIT_DONE`）

### Step 1 — Coverage manifest（必出）

为每条 CLAUDE/AGENTS 强制条款分配一个 `rule_id`。对每个 `rule_id`：

- 至少执行 1 个 grep/analyzer 命令（**记录命令字符串 + 命中数**）
- 至少**打开** 3 个非测试生产文件**通读**（不是只看前 50 行）；写明 file path + summary
- 或：写明 `candidate_count=0` + 跑过的 grep 命令证明确实空集

**整体打开门槛**：`total_opened_files >= 60`，分布约束：
- `src/ >= 30`
- `agents/ >= 15`
- `src/workflow/ >= 10`
- `tools/ci/ >= 3`

未达到 → 不写 manifest，输出 `AUDIT_INCOMPLETE: coverage_below_threshold` 并 exit。

### Step 2 — Fixed analyzer pack（必跑，结果粘贴 manifest）

固定 6 个 ripgrep 命令，结果摘要必须出现在 manifest 中（每个至少列前 10 个命中文件）：

```bash
rg -n "ChatAsync\(|\.ChatAsync\(" src agents tools -g '*.cs'
rg -n "JsonSerializer|JsonDocument|JsonNode|Newtonsoft|ToJson|FromJson" src agents tools -g '*.cs'
rg -n "Dictionary<|ConcurrentDictionary<|HashSet<|Queue<|\block\s*\(" src agents tools -g '*.cs'
rg -n "Task\.Run|Timer|ContinueWith|CancellationTokenSource|Channel\.Create" src agents tools -g '*.cs'
rg -n "Ensure.*Projection|IEventStore|ReplayAsync|GetEventsAsync|Rebuild|Backfill" src agents tools -g '*.cs'
rg -n "actorId.*StartsWith|StartsWith\([^\n]*actor|TypeUrl\.Contains|\.HandleEventAsync\(|SubscribeAsync<EventEnvelope>" src agents tools -g '*.cs'
```

每个命中分类**不准只用文件路径推断 allowed**——必须打开该文件确认。

### Step 3 — Candidate 文件（必写，与 cluster 文件分离）

写入 `$REPO_ROOT/.refactor-loop/runs/audit-iter-{{iteration}}-candidates.ndjson`：每行一个 candidate。

```json
{"rule_id": "<CLAUDE clause id>", "path": "<file>", "line": <int>, "evidence": "<one-line code snippet>", "verdict": "accept|reject", "reject_reason": "<if reject>", "prior_cluster_overlap": "<cluster-id|none>"}
```

**`candidate_count >= 25`**（除非所有 6 个 analyzer 命令都 0 命中——这时也要写 0-count 证据）。

### Step 4 — Cluster 选择（从 accepted candidates）

仅从 `verdict: accept` 的 candidates 形成 cluster。每个 cluster 满足：

- **独立性**：与其它 cluster 文件交集 ≤ 5%。
- **边界可控**：单 cluster 改动 ≤ 30 文件（小重构 ≤ 15）。
- **不新增功能**：只清理违反位置；禁扩 scope。
- **明确归因**：清楚 old/new pattern，后者直接写进代码注释。
- **设计违规允许大 cluster**：如果是"需先定协议 / actor 化 / proto 迁移"的深层违规，**不要因为 >30 文件就拒绝**，标 `requires_design` 让 controller 决定是否拆。

输出格式（每 cluster 一节，frontmatter + cluster sections，沿用现有 YAML 结构）。

### Step 5 — Reject 必须证据齐全

`verdict: reject` 的 candidate 必须有：

- CLAUDE clause 引用 + 该 clause 对该候选**不适用**的具体理由（不是泛泛 "covered by guard"）
- 如 reject reason 是 "covered by existing CI guard"：必须给 guard 文件路径 + 行号 + 证明候选路径在 guard scan include set + 临时 probe 描述确认 reintroduction 会 fail
- 如 reject reason 是 "same family as prior cluster"：必须证明候选 anti-pattern 100% 等同已修过的，不是字面相似但语义不同

### Step 6 — 终止 marker

- 有 cluster → 末尾打印 `AUDIT_DONE:/Users/auric/aevatar/.refactor-loop/runs/audit-iter-{{iteration}}.md:<N>`
- **0 cluster** → 必须满足：manifest 完整 + candidates ndjson 存在 + 每个 reject 都有 evidence + 至少跑了 1 次 "second-pass" 命令对最高风险类别复扫。否则输出 `AUDIT_INCOMPLETE:<reason>` 而不是 `AUDIT_DONE:none:0`

## 红线 — 反 anchoring

- **禁止**在输出里写"prefer 0"、"healthy signal"、"loop saturated"等措辞 —— 你是 auditor 不是 closer
- **禁止**用 "current endpoint doesn't call it" 来 reject 公开 API 设计违规
- **禁止**用 "guard passed" 直接 reject 不在 guard 语义边界内的候选
- **禁止**因为 "需先定协议" 而 reject 真违规 —— 标 `requires_design`，让 controller 决定
- 禁止改任何代码
- 禁止把"想加的功能"伪装成 cluster

开始执行。
