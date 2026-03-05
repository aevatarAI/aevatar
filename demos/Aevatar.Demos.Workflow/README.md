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

## Workflow YAML 分类（55 个主 workflow）

说明：

- `workflows/` 目录里目前是 **58** 个 YAML；
- 其中 `48_subworkflow_level1/2/3.yaml` 是 `49_workflow_call_multilevel.yaml` 的子流程定义；
- 文档里说的 **55 个 workflow** 指主流程文件（01-07、08-16、17-38、39-47、49-56）。

| 分组 | 文件范围 | 数量 | 主要展示能力 |
|---|---|---:|---|
| Start Here（Deterministic Basics） | `01-07` | 7 | 基础确定性原语：`transform`、`guard`、`conditional`、`switch`、`assign`、`retrieve_facts`、组合 pipeline |
| LLM Workflows | `08-16` | 9 | 基础 LLM 编排：`llm_call`、chain、`parallel`、`race`、`map_reduce`、`foreach`、`evaluate`、`reflect`、`cache` |
| Custom Step Modules | `17-19` | 3 | 自定义步骤模块：`demo_template`、`demo_csv_markdown`、`demo_json_pick` |
| Role Event Modules | `20-38` | 19 | Role 级 `event_modules`/`event_routes` 配置、multiplex 路由、`extensions` 兼容与覆盖优先级、混合 step+role 执行链 |
| Human Interaction（Legacy Auto） | `39-42` | 4 | 自动恢复式人机交互：`human_input`/`human_approval` 自动继续，覆盖 approve/reject(fail/skip) 分支 |
| Human Interaction（Manual） | `43-47` | 5 | 手动交互场景：人工输入、人工审批、`wait_signal` 成功与超时、审批+信号混合编排 |
| Workflow Call（Multi-level） | `49` + `48_subworkflow_*` | 1(+3) | 多层子流程调用：`workflow_call` 递归调用 + 子流程文本标准化/去重/反转处理 |
| Connector Integration | `50` | 1 | `connector_call` 端到端：role 连接器白名单 + 本地 CLI connector 调用 |
| Ergonomic Aliases | `51-53` | 3 | ergonomic alias 演示：`cli_call`、`foreach_llm`、`map_reduce_llm` |
| Integration Utility | `54-56` | 3 | 集成辅助场景：`publish/emit`、`tool_call` 失败 fallback、`delay+checkpoint` |

### Role Event Modules（20-38）子分组速览

- `20-22`：单模块路由（template / csv / json）。
- `23-25`：多模块 multiplex 路由。
- `26`：多角色链式 role event module。
- `27-32`：`extensions` 配置与 top-level 覆盖优先级验证。
- `33`：无 route 配置时的行为。
- `34`：route DSL 路由语法。
- `35`：未知模块忽略与容错。
- `36-38`：普通 step 模块与 role event module 的混合链路。

## 仍未做“独立 YAML 演示”的原语

以下原语目前没有单独编号 demo（但在集成测试或组合场景里有覆盖）：

- `while` / `loop`
- `vote_consensus`
