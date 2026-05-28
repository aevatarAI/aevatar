# 任务：审计 `$REPO_ROOT` 仓库中的架构违规

你是审计员。先发现违规，再从 accepted candidates 形成 cluster。禁止改代码。

## 必读

1. `CLAUDE.md`、`AGENTS.md`、`docs/canon/`。
2. `docs/audit-scorecard/` 仅作线索，不作唯一依据。
3. 当前分支：`git branch --show-current`。

## 必做流程

1. Coverage manifest：为每条强制条款分配 `rule_id`；每条至少记录 1 个 grep/analyzer 命令和命中数，并打开 3 个非测试生产文件通读，或写 `candidate_count=0` 及证据。整体门槛：`total_opened_files >= 60`，其中 `src/ >= 30`、`agents/ >= 15`、`src/workflow/ >= 10`、`tools/ci/ >= 3`。不足则输出 `AUDIT_INCOMPLETE: coverage_below_threshold`。
2. 固定 analyzer pack 必跑，并把摘要贴入 manifest：
   ```bash
   rg -n "ChatAsync\(|\.ChatAsync\(" src agents tools -g '*.cs'
   rg -n "JsonSerializer|JsonDocument|JsonNode|Newtonsoft|ToJson|FromJson" src agents tools -g '*.cs'
   rg -n "Dictionary<|ConcurrentDictionary<|HashSet<|Queue<|\block\s*\(" src agents tools -g '*.cs'
   rg -n "Task\.Run|Timer|ContinueWith|CancellationTokenSource|Channel\.Create" src agents tools -g '*.cs'
   rg -n "Ensure.*Projection|IEventStore|ReplayAsync|GetEventsAsync|Rebuild|Backfill" src agents tools -g '*.cs'
   rg -n "actorId.*StartsWith|StartsWith\([^\n]*actor|TypeUrl\.Contains|\.HandleEventAsync\(|SubscribeAsync<EventEnvelope>" src agents tools -g '*.cs'
   ```
   每个命中必须打开文件确认，不能只按路径推断 allowed。
3. 写 `$REPO_ROOT/.refactor-loop/runs/audit-iter-${ITERATION}-candidates.ndjson`，每行：
   ```json
   {"rule_id":"<clause id>","path":"<file>","line":1,"evidence":"<snippet>","verdict":"accept|reject","reject_reason":"<if reject>","prior_cluster_overlap":"<cluster-id|none>"}
   ```
   `candidate_count >= 25`，除非 analyzer 全 0 且有证据。
4. 从 `accept` candidates 形成 cluster。要求：文件交集 ≤5%；单 cluster 估计 ≤30 文件（小重构 ≤15）；不新增功能；明确 old/new pattern；深层协议/actor/proto 迁移标 `requires_design: true`，不要拒绝。
5. 每个 cluster 输出：
   ```yaml
   id: cluster-NNN-<slug>
   rule_ids: [...]
   severity: high|medium|low
   requires_design: true|false
   files_touched_estimate: N-M
   old_pattern: <one-liner>
   new_pattern: <one-liner>
   ```
   随后写 Evidence 与 Fix boundary。
6. `requires_design: true` 时追加 `human_brief`：中文 `problem_title/problem_statement/why_needs_design/design_question`，一个代表性 `problem_example_file_path`，10-30 行真实代码片段并用 `// problem:` 标出问题；`original_authors` 只允许经 `git blame` 验证且在 maintainer whitelist 中的 handle。
7. Reject 必须有 clause 引用和不适用理由；若说 guard 覆盖，给 guard 文件行号、include set 证明和 probe 描述；若说 prior cluster，证明语义 100% 等同。

## 终止

- 有 cluster：`AUDIT_DONE:/Users/auric/aevatar/.refactor-loop/runs/audit-iter-${ITERATION}.md:<N>`
- 0 cluster：manifest、candidates、reject evidence 完整，且对最高风险类别做 second-pass；否则 `AUDIT_INCOMPLETE:<reason>`。

## 红线

- 不写“prefer 0”“healthy signal”“loop saturated”等 closer 话术。
- 不用“current endpoint doesn't call it”拒绝公开 API 设计违规。
- 不用“guard passed”拒绝不在 guard 语义边界内的候选。
- 真违规但需设计时标 `requires_design`，不要 reject。
- 不把想加功能伪装成 cluster。
