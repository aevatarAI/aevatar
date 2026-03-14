# Aevatar.Workflow.Core

工作流领域内核。当前职责边界是：

- `WorkflowGAgent`：definition actor，只承载 workflow 定义事实。
- `WorkflowRunGAgent`：run actor，一次 run 一个 actor，承载全部执行事实。
- `IEventModule<IWorkflowExecutionContext>`：workflow step 模块统一抽象。
- `WorkflowExecutionBridgeModule`：把 `IEventModule<IWorkflowExecutionContext>` 适配进 `Foundation` 的 `IEventModule<IEventHandlerContext>` 管线。
- `WorkflowExecutionKernel`：run actor 内的执行内核，负责主循环、推进、完成与失败收敛。

## 核心对象

- `WorkflowGAgent`
  - 绑定 `WorkflowYaml`
  - 维护 `WorkflowName / InlineWorkflowYamls / Compiled / CompilationError / Version`
  - 不再直接执行 workflow
- `WorkflowRunGAgent`
  - 持有 `WorkflowRunState`
  - 安装 workflow modules
  - 创建 run-scoped `RoleGAgent` 子 actor
  - 处理 `ChatRequestEvent`、`ReplaceWorkflowDefinitionAndExecuteEvent`、`WorkflowCompletedEvent`
  - 通过 event sourcing 持久化 run lifecycle 与 `ExecutionStates`
- `SubWorkflowOrchestrator`
  - 管理 `workflow_call` 的子 run 创建、绑定、完成与对账

## 运行态持久化

`WorkflowRunState` 是 run actor 的唯一事实源，当前至少包含：

- `DefinitionActorId`
- `WorkflowYaml / WorkflowName / InlineWorkflowYamls`
- `RunId / Status / Input / FinalOutput / FinalError`
- `ExecutionStates`
- 子工作流 binding / invocation 关系

模块运行态通过 `IWorkflowExecutionContext.LoadState/SaveState/ClearState` 读写 `WorkflowRunState.ExecutionStates`。这意味着：

- 模块状态跟随 run actor replay
- callback fired 事件能在 actor 内完成对账
- 不再依赖模块私有 `Dictionary/HashSet`

## 关键模块语义

- `WorkflowExecutionKernel`
  - 主循环、current step、variables、retry、timeout 都在 actor-owned execution state 中
- `DelayModule`
  - 仅调度 durable timeout，pending lease 保存在 run actor
- `WaitSignalModule`
  - pending signal waiters 与 timeout lease 保存在 run actor
- `HumanInputModule` / `HumanApprovalModule`
  - pending human tasks 保存在 run actor
- `LLMCallModule` / `EvaluateModule` / `ReflectModule`
  - 外部调用 correlation 与 watchdog 状态保存在 run actor
- `ParallelFanOutModule` / `ForEachModule` / `MapReduceModule` / `RaceModule`
  - 聚合中间态保存在 run actor

## 目录要点

- `WorkflowGAgent.cs`
  - definition actor
- `WorkflowRunGAgent.cs`
  - run actor
- `Execution/`
  - workflow execution context adapter、bridge module、execution kernel
- `Modules/`
  - 所有内置步骤模块
- `Primitives/`
  - workflow definition / parser / role / step primitives
- `Composition/`
  - 模块依赖推导与配置装配

## 约束

- callback 线程只能发内部事件，不能直接推进业务状态
- 模块不能在私有字段中持有 `run/step/session` 权威状态
- 业务推进必须在 run actor 事件处理线程内完成
- 新增 actor 只按事实源边界，不按模块数量切分
