# Workflow Primitives Demo

本示例逐一演示 Aevatar Workflow 的内置原语与显式编排能力。每个 YAML 工作流聚焦一个或一组能力，配有预置输入，开箱即跑。

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

| YAML `type` | 别名 | Executor | 说明 |
|---|---|---|---|
| `transform` | — | TransformPrimitiveExecutor | 纯函数变换：count、take、join、split、distinct、uppercase、lowercase、trim、reverse_lines、count_words |
| `guard` | `assert` | GuardPrimitiveExecutor | 数据校验：not_empty、json_valid、regex、max_length、contains；支持 on_fail 策略（fail/skip/branch） |
| `conditional` | — | ConditionalPrimitiveExecutor | 简单条件分支，根据输入中是否包含关键词选择 true/false 路径 |
| `switch` | — | SwitchPrimitiveExecutor | 多路分支，按 `on` 参数精确匹配或包含匹配 branch key，未命中走 `_default` |
| `assign` | — | AssignPrimitiveExecutor | 变量赋值，从字面值或 `$` 路径引用上一步输出 |
| `retrieve_facts` | — | RetrieveFactsPrimitiveExecutor | 关键词检索 top-k 事实（输入按行分隔的事实列表） |
| `emit` | `publish` | EmitPrimitiveExecutor | 发布自定义事件，用于可观测性 / Webhook / 跨工作流信号 |
| `delay` | `sleep` | DelayPrimitiveExecutor | 定时延迟 |
| `checkpoint` | — | CheckpointPrimitiveExecutor | 变量快照，用于恢复 |
| `wait_signal` | `wait` | WaitSignalPrimitiveExecutor | 挂起直到外部信号到达 |
| `human_input` | — | HumanInputPrimitiveExecutor | 挂起等待用户输入 |
| `human_approval` | — | HumanApprovalPrimitiveExecutor | 挂起等待人工审批 |

### LLM 原语

| YAML `type` | 别名 | Executor | 说明 |
|---|---|---|---|
| `llm_call` | — | LLMCallPrimitiveExecutor | 向目标 Role Agent 发送 ChatRequest，返回 LLM 响应 |
| `tool_call` | — | ToolCallPrimitiveExecutor | 调用注册的 Agent 工具 |
| `connector_call` | `bridge_call` | ConnectorCallPrimitiveExecutor | 调用框架 Connector（MCP/HTTP/CLI），支持 retry + timeout |
| `evaluate` | `judge` | EvaluatePrimitiveExecutor | LLM-as-Judge 评估：打分 + 阈值分支 |
| `reflect` | — | ReflectPrimitiveExecutor | 自我反思循环：critique → improve → critique，直到 PASS 或达到上限 |

### 并行 & 聚合原语

| YAML `type` | 别名 | Executor | 说明 |
|---|---|---|---|
| `parallel` | `parallel_fanout`, `fan_out` | ParallelFanOutPrimitiveExecutor | 扇出 N 个 worker 并行执行，收齐后合并；可选 vote 步骤 |
| `race` | `select` | RacePrimitiveExecutor | N 路并行，第一个成功的结果胜出 |
| `map_reduce` | `mapreduce` | MapReducePrimitiveExecutor | 输入拆分 → 每项 map → 全部 reduce |
| `vote_consensus` | `vote` | VoteConsensusPrimitiveExecutor | 投票共识 |

### 流程控制原语

| YAML `type` | 别名 | Executor | 说明 |
|---|---|---|---|
| `while` | `loop` | WhilePrimitiveExecutor | 循环执行子步骤，直到条件不满足或达到 max_iterations |
| `foreach` | `for_each` | ForEachPrimitiveExecutor | 遍历分隔列表，对每项执行子步骤，收齐后合并 |
| `workflow_call` | `sub_workflow` | WorkflowRunGAgent sub-workflow executor | 递归调用子工作流 |
| `cache` | — | CachePrimitiveExecutor | 缓存步骤结果，TTL 过期前命中直接返回 |

## Workflow YAML 分类（55 个主 workflow）

说明：

- `workflows/` 目录里目前是 **58** 个 YAML；
- 其中 `48_subworkflow_level1/2/3.yaml` 是 `49_workflow_call_multilevel.yaml` 的子流程定义；
- 文档里说的 **55 个 workflow** 指主流程文件（01-07、08-16、17-38、39-47、49-56）。

| 分组 | 文件范围 | 数量 | 主要展示能力 |
|---|---|---:|---|
| Start Here（Deterministic Basics） | `01-07` | 7 | 基础确定性原语：`transform`、`guard`、`conditional`、`switch`、`assign`、`retrieve_facts`、组合 pipeline |
| LLM Workflows | `08-16` | 9 | 基础 LLM 编排：`llm_call`、chain、`parallel`、`race`、`map_reduce`、`foreach`、`evaluate`、`reflect`、`cache` |
| Custom Step Executors | `17-19` | 3 | 自定义无状态原语：`demo_template`、`demo_csv_markdown`、`demo_json_pick` |
| Explicit Composition Replacements | `20-38` | 19 | 对退役的 role-event-module demo 做一一对应的显式替代，全部改成可确定性执行的 workflow step / switch / chain |
| Human Interaction（Legacy Auto） | `39-42` | 4 | 自动恢复式人机交互：`human_input`/`human_approval` 自动继续，覆盖 approve/reject(fail/skip) 分支 |
| Human Interaction（Manual） | `43-47` | 5 | 手动交互场景：人工输入、人工审批、`wait_signal` 成功与超时、审批+信号混合编排 |
| Workflow Call（Multi-level） | `49` + `48_subworkflow_*` | 1(+3) | 多层子流程调用：`workflow_call` 递归调用 + 子流程文本标准化/去重/反转处理 |
| Connector Integration | `50` | 1 | `connector_call` 端到端：role 连接器白名单 + 本地 CLI connector 调用 |
| Ergonomic Aliases | `51-53` | 3 | ergonomic alias 演示：`cli_call`、`foreach_llm`、`map_reduce_llm` |
| Integration Utility | `54-56` | 3 | 集成辅助场景：`publish/emit`、`tool_call` 失败 fallback、`delay+checkpoint` |

### Explicit Composition Replacements（20-38）

- `20-22`：单一 role-event-module 路由，分别改成单一显式 step
- `23-25`：旧 multiplex 路由，改成 workflow-level `switch`
- `26/30`：旧 multi-role chain，改成显式 deterministic step chain
- `27-35`：旧 extensions / precedence / no-routes / route DSL / unknown-ignored 变体，改成直接表达目标业务逻辑的显式 YAML
- `36-38`：旧 mixed step + role route，改成显式 step pipeline

### 旧 role-event-module 示例到新 YAML 的对应关系

旧示例没有被“功能删除”，而是改成了显式 workflow 编排。对应关系如下：

| 旧 YAML | 新 YAML | 新实现方式 |
|---|---|---|
| `20_role_event_module_template` | `20_explicit_template_route` | 单一 `demo_template` 显式 step |
| `21_role_event_module_csv_markdown` | `21_explicit_csv_markdown_route` | 单一 `demo_csv_markdown` 显式 step |
| `22_role_event_module_json_pick` | `22_explicit_json_pick_route` | 单一 `demo_json_pick` 显式 step |
| `23_role_event_module_multiplex_template` | `23_explicit_multiplex_template_route` | workflow-level `switch` 选择 template 分支 |
| `24_role_event_module_multiplex_csv` | `24_explicit_multiplex_csv_route` | workflow-level `switch` 选择 csv 分支 |
| `25_role_event_module_multiplex_json` | `25_explicit_multiplex_json_route` | workflow-level `switch` 选择 json 分支 |
| `26_role_event_module_multi_role_chain` | `26_explicit_multi_stage_template_csv_json_chain` | template -> csv -> json 显式 step chain |
| `27_role_event_module_extensions_template` | `27_explicit_extensions_template_route` | 去掉 extensions，保留目标 template 业务逻辑 |
| `28_role_event_module_extensions_csv` | `28_explicit_extensions_csv_route` | 去掉 extensions，保留目标 csv 业务逻辑 |
| `29_role_event_module_top_level_overrides_extensions` | `29_explicit_precedence_json_pick` | 去掉隐藏优先级，直接写出应执行的 json 逻辑 |
| `30_role_event_module_extensions_multi_role_chain` | `30_explicit_extensions_multi_stage_chain` | 去掉 extensions，保留显式多阶段链路 |
| `31_role_event_module_extensions_multiplex_json` | `31_explicit_extensions_multiplex_json_route` | 去掉 extensions，保留显式 multiplex json 路由 |
| `32_role_event_module_top_level_overrides_extensions_multiplex` | `32_explicit_precedence_multiplex_csv` | 去掉隐藏优先级，直接写出应执行的 csv 路由 |
| `33_role_event_module_no_routes_template` | `33_explicit_template_without_routes` | 去掉隐式“无路由自动接管”，直接写 template step |
| `34_role_event_module_route_dsl_csv` | `34_explicit_csv_route_dsl_equivalent` | 去掉 route DSL，直接写 csv step |
| `35_role_event_module_unknown_ignored_template` | `35_explicit_template_ignore_unknown_module` | 去掉 unknown ignored 机制，只保留真实 template 逻辑 |
| `36_mixed_step_json_pick_then_role_template` | `36_explicit_json_pick_then_template` | `demo_json_pick -> demo_template` |
| `37_mixed_step_csv_markdown_then_role_template` | `37_explicit_csv_markdown_then_template` | `demo_csv_markdown -> demo_template` |
| `38_mixed_step_template_then_role_csv_markdown` | `38_explicit_template_then_csv_markdown` | `demo_template -> demo_csv_markdown` |

这些替代 YAML 都是可确定性执行的；不依赖已退休的 role 路由字段，也不依赖 role 内部的隐式业务管线。对应回归验证见 `test/Aevatar.Integration.Tests/WorkflowLegacyRoleEventModuleReplacementTests.cs`。

## 仍未做“独立 YAML 演示”的原语

以下原语目前没有单独编号 demo（但在集成测试或组合场景里有覆盖）：

- `while` / `loop`
- `vote_consensus`
