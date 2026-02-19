# Aevatar.Workflow.Core

工作流引擎领域内核。持有 YAML DSL 解析、`WorkflowGAgent` 生命周期、全部内置步骤模块与 Connector 桥接。

## 目录结构

```
Aevatar.Workflow.Core/
├── WorkflowGAgent.cs              # 工作流根 Actor
├── WorkflowModuleFactory.cs       # 按名称创建步骤模块的工厂
├── WorkflowModuleDescriptor.cs    # 模块描述器（名称 -> 创建函数）
├── IWorkflowModuleDescriptor.cs   # 描述器接口
├── ServiceCollectionExtensions.cs # DI 入口
├── Primitives/
│   ├── WorkflowDefinition.cs      # WorkflowDefinition、RoleDefinition
│   ├── StepDefinition.cs          # 单步定义：type、role、parameters、flow control
│   ├── WorkflowParser.cs          # YAML 解析（snake_case）
│   └── WorkflowVariables.cs       # 运行时变量：dot-path 读写 + {{var}} 插值
├── Validation/
│   └── WorkflowValidator.cs       # 静态校验：名称、步骤存在性、角色引用、next 引用
├── Modules/                       # 内置步骤模块（均实现 IEventModule）
│   ├── WorkflowLoopModule.cs      # 引擎主循环，按顺序派发步骤
│   ├── LLMCallModule.cs           # llm_call: 向 RoleGAgent 发 ChatRequestEvent
│   ├── ToolCallModule.cs          # tool_call: 调用已注册的 Agent 工具
│   ├── ConnectorCallModule.cs     # connector_call: 按名称调用 HTTP/CLI/MCP connector
│   ├── ParallelFanOutModule.cs    # parallel: 多路扇出 + 结果聚合
│   ├── VoteConsensusModule.cs     # vote: 投票选出最佳候选
│   ├── ForEachModule.cs           # foreach: 按分隔符拆分迭代
│   ├── ConditionalModule.cs       # conditional: 条件分支
│   ├── WhileModule.cs             # while/loop: 循环执行
│   ├── WorkflowCallModule.cs      # workflow_call: 调用子工作流
│   ├── TransformModule.cs         # transform: 纯函数变换（count/take/join/split 等）
│   ├── AssignModule.cs            # assign: 变量赋值
│   ├── CheckpointModule.cs        # checkpoint: 检查点（简化实现）
│   └── RetrieveFactsModule.cs     # retrieve_facts: 关键词检索事实片段
├── Connectors/
│   ├── InMemoryConnectorRegistry.cs # IConnectorRegistry 内存实现
│   ├── HttpConnector.cs             # HTTP connector（白名单安全策略）
│   └── CliConnector.cs              # CLI connector（白名单安全策略）
└── Composition/                   # 模块装配策略
    ├── IWorkflowModuleDependencyExpander.cs
    ├── IWorkflowModuleConfigurator.cs
    ├── WorkflowStepTypeModuleDependencyExpander.cs
    ├── WorkflowImplicitModuleDependencyExpander.cs
    ├── WorkflowLoopModuleDependencyExpander.cs
    ├── WorkflowLoopModuleConfigurator.cs
    └── WorkflowModuleConfiguratorBase.cs
```

## 核心类型

### WorkflowGAgent

工作流根 Actor（`GAgentBase<WorkflowState>`）。职责：

1. 接收 YAML 字符串，调用 `WorkflowParser` 解析 + `WorkflowValidator` 校验。
2. 首次激活时创建角色子 Agent 树（`RoleGAgent`），ID 由 role 定义映射。
3. 通过 `IWorkflowModuleDependencyExpander` 推导所需模块，经 `WorkflowModuleFactory` 创建，再由 `IWorkflowModuleConfigurator` 配置后安装。
4. 收到 `ChatRequestEvent` 后发布 `StartWorkflowEvent`，驱动 `WorkflowLoopModule` 开始执行。
5. 收到 `WorkflowCompletedEvent` 后汇总输出。

### WorkflowLoopModule

引擎主循环。收到 `StartWorkflowEvent` 后，按 `WorkflowDefinition.Steps` 顺序逐步派发 `StepRequestEvent`；收到 `StepCompletedEvent` 后推进到下一步或发布 `WorkflowCompletedEvent`。通过 `_activeRunIds` 跟踪并发 run。

### LLMCallModule

将 `StepRequestEvent` 转换为 `ChatRequestEvent` 并点对点发给目标 `RoleGAgent`。维护 `_pending` 字典做请求/响应关联，收到完成事件后转换为 `StepCompletedEvent`。

### ParallelFanOutModule

Fan-Out/Fan-In 模式。将 `parallel` 步骤拆成 N 个子步骤（按 `children` 或 `workers` 数量），分别派发给不同 role。维护 `_expected`/`_collected` 计数器，全部完成后合并输出。可选触发 `vote` 步骤做共识选择。

### ConnectorCallModule

按名称从 `IConnectorRegistry` 查找 Connector 并执行。支持：
- 角色级 connector 白名单校验
- 重试逻辑（最多 5 次）
- `on_missing: skip`、`on_error: continue` 等容错策略
- 执行元数据记录（耗时、重试次数、超时）

### Connector 安全模型

`HttpConnector` 和 `CliConnector` 均采用白名单策略：
- HTTP：`allowedMethods`、`allowedPaths`、`allowedInputKeys` 限制请求范围
- CLI：`allowedOperations`、`allowedInputKeys` 限制可执行命令

## 模块装配（OCP）

新增模块无需修改 `WorkflowGAgent`，只需：

1. 实现 `IEventModule`
2. DI 注册：`services.AddWorkflowModule<MyModule>("my_step_type", "alias")`

装配流程：

```
WorkflowGAgent.InstallModulesAsync
  -> IWorkflowModuleDependencyExpander[]: 推导模块名集合
  -> WorkflowModuleFactory: 按名称创建实例
  -> IWorkflowModuleConfigurator[]: 配置实例
  -> 安装到 Agent 认知管线
```

## DI 入口

- `AddAevatarWorkflow()`：注册默认模块、工厂、connector registry、expander/configurator 组合。
- `AddWorkflowModule<T>("name", "alias")`：扩展注册自定义模块。

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

- `Aevatar.AI.Abstractions`、`Aevatar.Foundation.Abstractions`、`Aevatar.Foundation.Core`
- `Google.Protobuf`、`Grpc.Tools`（事件序列化）
- `YamlDotNet`（YAML 解析）
- `Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Logging.Abstractions`
