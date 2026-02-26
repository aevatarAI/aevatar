# Workflow Primitives Demo

本示例逐一演示 Aevatar Workflow 的所有内置原语（Event Module），每个 YAML 工作流聚焦一个或一组原语，配有预置输入，开箱即跑。

## 运行方式

```bash
cd demos/Aevatar.Demos.Workflow

# 列出所有可用 demo
dotnet run

# 运行单个 demo
dotnet run -- 01_transform

# 运行所有确定性 demo（无需 LLM API Key）
dotnet run -- --deterministic

# 运行所有 demo（需要 LLM API Key）
export DEEPSEEK_API_KEY="sk-..."
dotnet run -- --all
```

## 原语总览

### 确定性原语（无需 LLM）

| YAML `type` | 别名 | 模块 | 说明 |
|---|---|---|---|
| `transform` | — | TransformModule | 纯函数变换：count、take、join、split、distinct、uppercase、lowercase、trim、reverse_lines、count_words |
| `guard` | `assert` | GuardModule | 数据校验：not_empty、json_valid、regex、max_length、contains；支持 on_fail 策略（fail/skip/branch） |
| `conditional` | — | ConditionalModule | 简单条件分支，根据输入中是否包含关键词选择 true/false 路径 |
| `switch` | — | SwitchModule | 多路分支，按 `on` 参数精确匹配或包含匹配 branch key，未命中走 `_default` |
| `assign` | — | AssignModule | 变量赋值，从字面值或 `$` 路径引用上一步输出 |
| `retrieve_facts` | — | RetrieveFactsModule | 关键词检索 top-k 事实（输入按行分隔的事实列表） |
| `emit` | `publish` | EmitModule | 发布自定义事件，用于可观测性 / Webhook / 跨工作流信号 |
| `delay` | `sleep` | DelayModule | 定时延迟 |
| `checkpoint` | — | CheckpointModule | 变量快照，用于恢复 |
| `wait_signal` | `wait` | WaitSignalModule | 挂起直到外部信号到达 |
| `human_input` | — | HumanInputModule | 挂起等待用户输入 |
| `human_approval` | — | HumanApprovalModule | 挂起等待人工审批 |

### LLM 原语

| YAML `type` | 别名 | 模块 | 说明 |
|---|---|---|---|
| `llm_call` | — | LLMCallModule | 向目标 Role Agent 发送 ChatRequest，返回 LLM 响应 |
| `tool_call` | — | ToolCallModule | 调用注册的 Agent 工具 |
| `connector_call` | `bridge_call` | ConnectorCallModule | 调用框架 Connector（MCP/HTTP/CLI），支持 retry + timeout |
| `evaluate` | `judge` | EvaluateModule | LLM-as-Judge 评估：打分 + 阈值分支 |
| `reflect` | — | ReflectModule | 自我反思循环：critique → improve → critique，直到 PASS 或达到上限 |

### 并行 & 聚合原语

| YAML `type` | 别名 | 模块 | 说明 |
|---|---|---|---|
| `parallel` | `parallel_fanout`, `fan_out` | ParallelFanOutModule | 扇出 N 个 worker 并行执行，收齐后合并；可选 vote 步骤 |
| `race` | `select` | RaceModule | N 路并行，第一个成功的结果胜出 |
| `map_reduce` | `mapreduce` | MapReduceModule | 输入拆分 → 每项 map → 全部 reduce |
| `vote_consensus` | `vote` | VoteConsensusModule | 投票共识 |

### 流程控制原语

| YAML `type` | 别名 | 模块 | 说明 |
|---|---|---|---|
| `while` | `loop` | WhileModule | 循环执行子步骤，直到条件不满足或达到 max_iterations |
| `foreach` | `for_each` | ForEachModule | 遍历分隔列表，对每项执行子步骤，收齐后合并 |
| `workflow_call` | `sub_workflow` | WorkflowCallModule | 递归调用子工作流 |
| `cache` | — | CacheModule | 缓存步骤结果，TTL 过期前命中直接返回 |

### 引擎原语（自动加载）

| YAML `type` | 模块 | 说明 |
|---|---|---|
| `workflow_loop` | WorkflowLoopModule | 工作流主循环引擎，处理步骤调度、变量、重试、超时、分支解析 |

## Demo 列表

### 确定性 Demo（01-07）

| 编号 | 文件 | 演示原语 | 说明 |
|---|---|---|---|
| 01 | `01_transform.yaml` | `transform` | 管道式变换：uppercase → count |
| 02 | `02_guard.yaml` | `guard` | 三重校验：not_empty → json_valid → contains |
| 03 | `03_conditional.yaml` | `conditional` + `transform` | 条件分支：包含 "URGENT" 走大写，否则小写 |
| 04 | `04_switch.yaml` | `switch` + `transform` | 多路分支：按 bug/feature/question 路由到不同变换 |
| 05 | `05_assign.yaml` | `assign` + `transform` | 变量赋值后变换 |
| 06 | `06_retrieve_facts.yaml` | `retrieve_facts` | 从事实列表中检索 top-3 与 "programming language" 相关的条目 |
| 07 | `07_pipeline.yaml` | `guard` + `assign` + `retrieve_facts` + `transform` | 多原语组合管道：校验 → 赋值 → 检索 → 计数 |

### LLM Demo（08-16）

| 编号 | 文件 | 演示原语 | 说明 |
|---|---|---|---|
| 08 | `08_llm_call.yaml` | `llm_call` | 单次 LLM 调用 |
| 09 | `09_llm_chain.yaml` | `llm_call` (chain) | 两步链：Analyst 分析 → Advisor 提方案 |
| 10 | `10_parallel.yaml` | `parallel` | 3 个角色并行回答同一问题，合并结果 |
| 11 | `11_race.yaml` | `race` | 3 路竞速，第一个成功响应胜出 |
| 12 | `12_map_reduce.yaml` | `map_reduce` | 3 个主题分别 map 生成要点，reduce 合成摘要 |
| 13 | `13_foreach.yaml` | `foreach` | 遍历 3 个技术名，对每个调用 LLM 生成描述 |
| 14 | `14_evaluate.yaml` | `evaluate` + `llm_call` | 先写诗，再用 LLM-as-Judge 打分 |
| 15 | `15_reflect.yaml` | `reflect` | 自我反思循环：反复 critique + improve |
| 16 | `16_cache.yaml` | `cache` + `llm_call` | 缓存包装的 LLM 调用 |

## 目录结构

```
demos/Aevatar.Demos.Workflow/
├── Aevatar.Demos.Workflow.csproj
├── Program.cs
├── README.md
└── workflows/
    ├── 01_transform.yaml
    ├── 02_guard.yaml
    ├── 03_conditional.yaml
    ├── 04_switch.yaml
    ├── 05_assign.yaml
    ├── 06_retrieve_facts.yaml
    ├── 07_pipeline.yaml
    ├── 08_llm_call.yaml
    ├── 09_llm_chain.yaml
    ├── 10_parallel.yaml
    ├── 11_race.yaml
    ├── 12_map_reduce.yaml
    ├── 13_foreach.yaml
    ├── 14_evaluate.yaml
    ├── 15_reflect.yaml
    └── 16_cache.yaml
```

## 未在 Demo 中覆盖的原语

以下原语因需要外部依赖或交互式输入，未编写独立 YAML demo：

- `emit` / `publish` — 发布自定义事件，使用 EventDirection.Both（需要外部流订阅消费）
- `tool_call` — 需要注册 IAgentToolSource
- `connector_call` — 需要配置 Connector（参见 Demos.Maker）
- `workflow_call` — 需要已注册的子工作流
- `while` / `loop` — 需要 LLM 子步骤产生 "DONE" 终止信号
- `vote_consensus` — 通常作为 parallel 的 vote_step_type 子步骤使用
- `delay` / `sleep` — 纯等待，无输出
- `checkpoint` — 快照恢复，需要配合状态管理
- `wait_signal` — 需要外部信号源
- `human_input` / `human_approval` — 需要交互式 UI

这些原语在集成测试和 Demos.Maker 中有完整覆盖。
