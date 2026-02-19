# Aevatar.Workflow.Core

`Aevatar.Workflow.Core` 提供 Workflow 领域核心：`WorkflowGAgent`、工作流 DSL、执行模块与模块装配策略。

## 职责

- `WorkflowGAgent`：持有 YAML、构建角色树、发布执行事件。
- `Primitives/*`：`WorkflowDefinition`、`StepDefinition`、Parser。
- `Validation/*`：工作流结构与语义校验。
- `Modules/*`：执行模块（见下方完整清单）。
- `Connectors/*`：命名 connector 注册与调用桥接。

## 模块装配（OCP）

`WorkflowGAgent` 不再内嵌模块推断/特化分支，而是通过统一 Module Pack 扩展：

- `IWorkflowModulePack`
  - 内建模块与扩展模块都通过同一 pack 契约注册。
  - pack 同时贡献：
    - `WorkflowModuleRegistration`（模块名/别名 + 创建逻辑）
    - `IWorkflowModuleDependencyExpander`
    - `IWorkflowModuleConfigurator`
- `WorkflowModuleFactory`
  - 聚合所有 pack 的模块注册并按名称创建模块实例。
  - 同名模块冲突 fail-fast。

模块推断与实例配置仍由以下策略完成：

- `IWorkflowModuleDependencyExpander`
  - 负责根据 workflow 推导所需模块集合。
  - 默认实现：
    - `WorkflowLoopModuleDependencyExpander`（始终引入 `workflow_loop`）
    - `WorkflowStepTypeModuleDependencyExpander`（按 step/type 推导）
    - `WorkflowImplicitModuleDependencyExpander`（补齐隐式依赖，如 `parallel -> llm_call`）
- `IWorkflowModuleConfigurator`
  - 负责模块实例级配置。
  - 默认实现：`WorkflowLoopModuleConfigurator`（向 `WorkflowLoopModule` 注入编译后的 workflow）。

新增模块规则时，优先“新增策略 + DI 注册”，避免修改 `WorkflowGAgent`。

## DI 入口

- `AddAevatarWorkflow()`
  - 注册内建 `WorkflowCoreModulePack`、统一模块工厂与 connector registry。
- `AddWorkflowModulePack<TModulePack>()`
  - 注册扩展 pack（如 Maker pack）。

## 模块清单

### 控制流

| 模块 | 别名 | 说明 |
|---|---|---|
| `workflow_loop` | — | 串行调度步骤，统一处理 retry / on_error / timeout / branch |
| `conditional` | — | 二元条件分支（keyword contains） |
| `switch` | — | 多路分支，按 `on` 值匹配 `branches` |
| `while` | `loop` | 循环直到条件不满足或达上限 |
| `race` | `select` | N 路竞速，取首个成功结果 |

### 执行

| 模块 | 别名 | 说明 |
|---|---|---|
| `llm_call` | — | 发送 ChatRequestEvent 到目标角色 |
| `tool_call` | — | 调用 Agent 注册工具 |
| `connector_call` | `bridge_call` | 调用命名 connector（HTTP/CLI/MCP） |
| `workflow_call` | `sub_workflow` | 递归调用子工作流 |
| `wait_signal` | `wait` | 暂停等待外部信号（human-in-the-loop / webhook） |

### 并行

| 模块 | 别名 | 说明 |
|---|---|---|
| `parallel_fanout` | `parallel`, `fan_out` | 并行扇出 + 可选 vote 合并 |
| `vote_consensus` | `vote` | 从多个候选中选出最佳 |
| `foreach` | `for_each` | 遍历列表并行执行子步骤 |
| `map_reduce` | `mapreduce` | map 阶段并行处理 + reduce 阶段汇总 |

### AI 模式

| 模块 | 别名 | 说明 |
|---|---|---|
| `evaluate` | `judge` | LLM-as-Judge 评估打分，支持阈值分支 |
| `reflect` | — | 自我反思-修正循环（critique → improve → ...） |

### 数据

| 模块 | 别名 | 说明 |
|---|---|---|
| `assign` | — | 变量赋值 |
| `transform` | — | 确定性文本变换（count、join、split 等） |
| `retrieve_facts` | — | Top-K 事实检索 |
| `checkpoint` | — | 变量快照 |
| `guard` | `assert` | 数据校验（json_valid / regex / not_empty 等） |
| `cache` | — | 结果缓存，避免重复执行 |

### 其他

| 模块 | 别名 | 说明 |
|---|---|---|
| `delay` | `sleep` | 定时等待 |
| `emit` | `publish` | 发布自定义事件 |

## 横切关注点（StepDefinition 属性）

`WorkflowLoopModule` 统一处理以下 `StepDefinition` 属性，所有步骤类型自动获得：

- **Retry**：失败重试，支持 `fixed` / `exponential` 退避。
- **OnError**：错误处理策略（`fail` / `skip` / `fallback`）。
- **TimeoutMs**：步骤级超时，超时后合成失败事件。

## 依赖

- `Aevatar.AI.Abstractions`
- `Google.Protobuf` / `Grpc.Tools`
- `YamlDotNet`
- `Microsoft.Extensions.*.Abstractions`
