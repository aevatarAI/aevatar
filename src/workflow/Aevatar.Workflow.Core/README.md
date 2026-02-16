# Aevatar.Workflow.Core

`Aevatar.Workflow.Core` 提供 Workflow 领域核心：`WorkflowGAgent`、工作流 DSL、执行模块与模块装配策略。

## 职责

- `WorkflowGAgent`：持有 YAML、构建角色树、发布执行事件。
- `Primitives/*`：`WorkflowDefinition`、`StepDefinition`、Parser。
- `Validation/*`：工作流结构与语义校验。
- `Modules/*`：`workflow_loop`、`llm_call`、`parallel_fanout`、`connector_call` 等执行模块。
- `Connectors/*`：命名 connector 注册与调用桥接。

## 模块装配（OCP）

`WorkflowGAgent` 不再内嵌模块推断/特化分支，而是通过组合策略扩展：

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
  - 注册默认模块描述器、模块工厂、connector registry。
  - 同时注册默认 expander/configurator 组合。
- `AddWorkflowModule<TModule>("name", "alias")`
  - 扩展新模块与别名。

## 依赖

- `Aevatar.AI.Abstractions`
- `Google.Protobuf` / `Grpc.Tools`
- `YamlDotNet`
- `Microsoft.Extensions.*.Abstractions`
