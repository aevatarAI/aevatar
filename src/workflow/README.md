# Workflow 能力架构（`src/workflow`）

`src/workflow` 已切换到“定义 Actor + 运行 Actor”的单一执行主干：

- `WorkflowGAgent` 只承载 workflow definition facts。
- `WorkflowRunGAgent` 一次 run 一个 actor，承载全部运行事实。
- `Foundation` 统一只保留 `IEventModule<TContext>`；workflow step 模块实现 `IEventModule<IWorkflowExecutionContext>`，再通过 bridge 接入 Foundation pipeline。
- `IEventContext` 是通用上下文根接口；`IEventHandlerContext` 与 `IWorkflowExecutionContext` 在此基础上做能力特化。
- workflow 读侧与 AGUI 继续共用同一条 Projection Pipeline。

更完整的设计说明见：

- `docs/design/2026-03-19-workflow-call-actor-to-actor-refactor-plan.md`

## 当前运行语义

- 一个 workflow definition 对应一个 `WorkflowGAgent`。
- 一次 workflow run 对应一个 `WorkflowRunGAgent`。
- registry 注册的 named workflow 必须绑定到规范 definition actor id：`workflow-definition:{workflow_name_lower}`。普通按 workflow name 启动会复用这个 definition actor，不再为每次 run 隐式生成不可达 definition actor。
- inline workflow bundle 不走 registry，也不分配固定 definition actor id；只有在真正创建 run 时才按该次请求派生 definition actor。
- `POST /api/chat` 会解析 workflow source，然后创建新的 run actor 执行。
- source actor inspection 统一走 workflow 专用 `IWorkflowActorBindingReader`；command path 通过 actor-owned query/reply 读取 binding，不再依赖 `actor.Agent` 具体实例形状，也不再暴露 generic raw-state probing。
- `/api/workflows/resume` 与 `/api/workflows/signal` 必须指向 run actor id，而不是 definition actor id。
- run actor 的投影、实时输出、查询默认都以 run actor id 为作用域。
- 显式传入的 definition actor id 是强约束：若该 actor 不存在，则按该 id 创建；若已存在且是 `WorkflowGAgent`，则仅在 workflow name 一致时原地复用或更新 definition payload；若已绑定到不同 workflow name，直接失败；若已存在但不是 workflow definition actor，也直接失败，不再静默退化成新的隐藏 definition actor。
- 内置模块包提供最小跨 actor 发送模块 `actor_send`，直接复用 `IWorkflowExecutionContext.SendToAsync(...)`，不再在 Foundation 上叠一层公共 messaging port。

补充口径：

- workflow 主链路运行在 **stream-backed actor message runtime** 之上。
- Host 输入会先规范化为应用命令模型，再被包装成 `EventEnvelope` 投递到目标 Actor。
- Event Sourcing 的事实层仍然是 `WorkflowRunGAgent` / `WorkflowGAgent` 显式持久化的领域事件，而不是运行时 envelope 流本身。

## 分层关系

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  H["Host"]
  I["Infrastructure"]
  A["Application"]
  P["Projection"]
  C["Core"]

  H --> I
  H --> A
  H --> P
  I --> A
  I --> P
  A --> C
  P --> C
```

## 命令侧主链路

1. Host/API 把输入规范化为 `WorkflowChatRunRequest`。
2. CQRS Core 通过 `ICommandDispatchService` / `DefaultCommandDispatchPipeline` 驱动这次命令。
3. Application 通过 `WorkflowRunCommandTargetResolver` 解析 workflow source，并把目标统一折叠成 `WorkflowRunCommandTarget`。
4. `WorkflowRunCommandTargetBinder` 为 run actor 建立 projection lease 与 live sink，并由 `WorkflowRunAcceptedReceiptFactory` 生成 accepted receipt。
5. `ActorCommandTargetDispatcher` 通过 `IActorDispatchPort` 把 `ChatRequestEvent` 包进 `EventEnvelope` 后投递到 run actor；目标 actor 的获取/创建仍由 `IActorRuntime` 负责。
6. `WorkflowRunGAgent` 在自己的事件管线中驱动 `StartWorkflowEvent -> StepRequestEvent -> StepCompletedEvent -> WorkflowCompletedEvent`。
7. Projection 与 AGUI 从同一条 run actor envelope 流投影出查询模型和实时事件；SSE/WS 路径由 `ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>` 持续回推事件给客户端。
8. `resume/signal` 命令同样先进入 CQRS `ICommandDispatchService`，再由 `WorkflowResumeCommandEnvelopeFactory` / `WorkflowSignalCommandEnvelopeFactory` 构建 run control envelope。

## 状态边界

- definition facts：
  - `WorkflowName`
  - `WorkflowYaml`
  - `InlineWorkflowYamls`
  - 编译结果与版本
- run facts：
  - `RunId`
  - `DefinitionActorId`
  - `Status/Input/FinalOutput/FinalError`
  - 子工作流绑定与调用关系
  - 所有模块运行态（通过 `WorkflowRunState.ExecutionStates` 以 `scope_key -> google.protobuf.Any` 形式持久化）

模块私有字段不再作为 `run/step/session` 的事实源。`delay / wait_signal / human_* / parallel* / map_reduce / llm_call / evaluate / reflect / while / foreach / race / cache` 等运行态都必须落到 run actor 状态里。

补充约束：

- 通用 actor 发送态使用 `ActorSendState`（`target_actor_id + payload`）。

这个 typed wrapper 定义在 `src/workflow/Aevatar.Workflow.Core/workflow_state.proto`，并通过 `WorkflowRunState.ExecutionStates` 持久化。

## 通用 Actor 通信模块

- `actor_send`：从 `send_state_key` 读取 `ActorSendState`，直接调用 `IWorkflowExecutionContext.SendToAsync(...)`，再发出最小 `StepCompletedEvent`。
- protocol-specific query/reply 不再由 `Workflow.Core` 提供通用反射模块；需要 query 时应由协议拥有者定义强类型步骤或在 source actor 中显式完成 request/reply。
- `actor_send` 注册在 `WorkflowCoreModulePack`，普通 workflow host/runtime 装配后即可直接使用。

## Projection 语义

- Projection root 是 run actor id。
- 读侧 `ProjectionScope` 为 `RunIsolated`。
- `/api/agents` 返回的是 workflow run snapshots，不再表示 definition actors 列表。
- Graph/Timeline/Snapshot 查询都围绕 run actor 展开。

## API 语义

- `ChatInput.AgentId` 表示 workflow source actor id。推荐传 definition actor id；如果传的是已绑定 workflow 的 run actor，系统会读取其 definition binding 再创建新的 run actor。
- 对 named workflow，若 source actor 缺少 `DefinitionActorId`，系统会回落到 registry 的规范 definition actor id，而不是创建新的匿名 definition actor。
- `WorkflowChatRunStarted.ActorId` 是新创建的 run actor id。
- resume/signal 请求中的 `ActorId` 必须是 run actor id。

## 目录概览

- `Aevatar.Workflow.Core`
  - definition actor、run actor、模块工厂、内置步骤模块、workflow primitives
- `Aevatar.Workflow.Application`
  - run 用例编排、actor 解析、projection lease 建立、查询门面
- `Aevatar.Workflow.Infrastructure`
  - actor port、capability API、workflow 文件加载、报告落盘
- `Aevatar.Workflow.Projection`
  - workflow read model、projector/reducer、query reader、projection lifecycle
- `Aevatar.Workflow.Presentation.AGUIAdapter`
  - workflow projection 到 AGUI 事件的适配
