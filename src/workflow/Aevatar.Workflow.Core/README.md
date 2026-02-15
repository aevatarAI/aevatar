# Aevatar.Workflow.Core

`Aevatar.Workflow.Core` 提供工作流编排能力，把多个 AI Agent 组织成可执行的认知流程。

## 职责

- 提供工作流根 Agent：`WorkflowGAgent`
- 提供工作流 DSL 解析与校验（YAML -> WorkflowDefinition）
- 提供可插拔认知模块工厂：`WorkflowModuleFactory`
- 提供框架级 connector 调用原语：`connector_call`
- 覆盖流程控制、并行协作、投票共识、工具/connector 调用、数据变换等原语

## 核心组件

- `WorkflowGAgent`：持有 workflow YAML、创建子 Agent 树、驱动执行
- `Primitives/*`：工作流定义、步骤定义、变量、解析器
- `Validation/WorkflowValidator`：编译前校验
- `Modules/*`：`workflow_loop`、`parallel_fanout`、`vote_consensus`、`llm_call` 等
- `Connectors/InMemoryConnectorRegistry`：框架默认命名 connector 注册表
- `IWorkflowModuleDescriptor` / `WorkflowModuleDescriptor<T>`：模块描述器与别名注册
- `ServiceCollectionExtensions.AddAevatarWorkflow()`：一键注册默认模块描述器 + `WorkflowModuleFactory + IConnectorRegistry`
- `cognitive_messages.proto`：工作流执行事件协议

## 模块工厂能力

`WorkflowModuleFactory` 通过 `IWorkflowModuleDescriptor` 注册表按名称创建模块（示例）：

- `workflow_loop` / `conditional` / `while` / `checkpoint`
- `parallel_fanout` / `vote_consensus`
- `llm_call` / `tool_call` / `connector_call`
- `transform` / `retrieve_facts`

扩展方式（开闭原则）：

- 使用 `services.AddWorkflowModule<TModule>("name", "alias")` 注册新模块与别名
- 无需修改 `WorkflowModuleFactory` 核心实现

`connector_call` 约定：

- `parameters.connector`: 命名 connector（必填）
- `parameters.operation`: 可选操作名
- `parameters.timeout_ms` / `parameters.retry`: 调用控制
- `parameters.on_missing=skip` / `parameters.on_error=continue`: 容错策略
- 输出统一写入 `StepCompletedEvent.Output`，观测字段写入 `StepCompletedEvent.Metadata`

## 依赖

- `Aevatar.AI.Abstractions`
- `Google.Protobuf` / `Grpc.Tools`
- `YamlDotNet`
- `Microsoft.Extensions.*.Abstractions`

说明：`WorkflowGAgent` 通过 `IRoleAgentTypeResolver` 解析具体角色 Agent 类型；该解析器应在宿主组合层显式注册（例如 `RoleGAgentTypeResolver`）。
