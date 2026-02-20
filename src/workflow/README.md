# Workflow 能力架构（`src/workflow`）

本文档描述 `src/workflow` 的完整实现关系。当前语义是：一次 `Run` 本质上就是向 `WorkflowGAgent` 触发一次 `ChatRequestEvent`，后续全部通过事件流驱动执行与投影。`commandId` 保留在 CQRS/Application 侧，不注入 Actor 事件 payload 或 Envelope metadata。

## 0. 运行语义约束（2026-02-19 更新）

- 一个 `Workflow` 对应一个 `WorkflowGAgent`（一个 Actor）。
- Actor 首次创建时绑定 workflow；绑定后不允许切换到另一个 workflow。
- 带 `actorId` 的 run 请求只能“继续在该 Actor 上运行”；不能借同一个 `actorId` 切 workflow。
- 若需要执行另一个 workflow，必须创建新的 Actor。

## 1. 分层与项目依赖图

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  subgraph Host["Host Layer"]
    H["Aevatar.Workflow.Host.Api"]
  end

  subgraph Infra["Infrastructure Layer"]
    I["Aevatar.Workflow.Infrastructure"]
    AGUIA["Aevatar.Workflow.Presentation.AGUIAdapter"]
  end

  subgraph App["Application Layer"]
    A["Aevatar.Workflow.Application"]
    AB["Aevatar.Workflow.Application.Abstractions"]
  end

  subgraph Projection["Projection Layer"]
    P["Aevatar.Workflow.Projection"]
  end

  subgraph Domain["Domain Layer"]
    C["Aevatar.Workflow.Core"]
  end

  subgraph Shared["Shared CQRS"]
    CQRSC["Aevatar.CQRS.Core"]
    CQRSP["Aevatar.CQRS.Projection.Core"]
    F["Aevatar.Foundation.* / Aevatar.AI.Abstractions"]
  end

  H --> CQRSC
  H --> CQRSP
  H --> I
  H --> AB
  I --> A
  I --> P
  I --> AGUIA
  A --> AB
  A --> C
  A --> CQRSC
  A --> F
  P --> AB
  P --> C
  P --> CQRSP
  AGUIA --> P
  AGUIA --> C
  AGUIA --> F
  C --> F
```

## 2. Run 执行主链路（命令侧）

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
sequenceDiagram
  participant Client as "Client"
  participant Api as "ChatEndpoints"
  participant CmdSvc as "ICommandExecutionService"
  participant AppSvc as "WorkflowChatRunApplicationService"
  participant Resolver as "WorkflowRunActorResolver"
  participant Port as "IWorkflowExecutionProjectionPort"
  participant WFAgent as "WorkflowGAgent"
  participant Sink as "WorkflowRunEventChannel"

  Client->>Api: "POST /api/chat 或 WS command"
  Api->>CmdSvc: "ExecuteAsync(WorkflowChatRunRequest)"
  CmdSvc->>AppSvc: "ExecuteAsync"
  AppSvc->>Resolver: "ResolveOrCreateAsync"
  Resolver-->>AppSvc: "ActorId + BoundWorkflowName"
  AppSvc->>Port: "EnsureActorProjectionAsync(...) -> ProjectionLease"
  AppSvc->>Port: "AttachLiveSinkAsync(lease, sink)"
  AppSvc->>WFAgent: "HandleEventAsync(EventEnvelope(ChatRequestEvent))"
  WFAgent-->>Sink: "投影分支持续写入 WorkflowRunEvent"
  AppSvc->>Sink: "ReadAllAsync + StreamAsync"
  Sink-->>Api: "WorkflowOutputFrame 流"
  Api-->>Client: "SSE/WS 实时输出"
```

## 3. 统一 Projection Pipeline（读侧 + AGUI）

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  ES["Actor Event Stream(EventEnvelope)"]
  LIFE["ProjectionLifecycleService"]
  REG["ProjectionSubscriptionRegistry"]
  HUB["ActorStreamSubscriptionHub(EventEnvelope)"]
  DIS["ProjectionDispatcher"]
  COOR["ProjectionCoordinator"]

  RM["WorkflowExecutionReadModelProjector"]
  RED["Reducers(Start/Step/TextEnd/Completed)"]
  STORE["IProjectionReadModelStore(WorkflowExecutionReport)"]

  AGP["WorkflowExecutionAGUIEventProjector"]
  MAP["EventEnvelopeToAGUIEventMapper + Handlers"]
  BUS["ProjectionSessionEventHub<WorkflowRunEvent>\nworkflow-run:{actorId}:{commandId}"]
  CH["WorkflowRunEventChannel"]

  QP["WorkflowExecutionProjectionService(IWorkflowExecutionProjectionPort)"]
  QS["WorkflowExecutionQueryApplicationService"]
  APIQ["/api/actors/* Query Endpoints"]

  LIFE --> REG
  REG --> HUB
  HUB --> ES
  ES --> DIS
  DIS --> COOR

  COOR --> RM
  RM --> RED
  RED --> STORE

  COOR --> AGP
  AGP --> MAP
  MAP --> BUS
  QP --> BUS
  BUS --> CH

  STORE --> QP
  QP --> QS
  QS --> APIQ
```

## 4. Workflow Core 内部类关系

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart TD
  CR["ChatRequestEvent"]
  WG["WorkflowGAgent"]
  PV["WorkflowParser + WorkflowValidator"]
  TREE["EnsureAgentTreeAsync(IActorRuntime)"]
  EXP["IWorkflowModuleDependencyExpander[]"]
  PACK["IWorkflowModulePack[]"]
  FACT["WorkflowModuleFactory"]
  CFG["IWorkflowModuleConfigurator[]"]
  MOD["Workflow Modules(Loop/LLM/Tool/Connector/Parallel...)"]
  EVT["Workflow Domain Events(Start/Step/Completed...)"]
  OUT["ChatResponseEvent / TextMessageEndEvent"]

  CR --> WG
  WG --> PV
  WG --> TREE
  WG --> PACK
  WG --> EXP
  PACK --> FACT
  PACK --> EXP
  PACK --> CFG
  EXP --> FACT
  FACT --> MOD
  CFG --> MOD
  MOD --> EVT
  EVT --> WG
  WG --> OUT
```

## 5. ReadModel 查询链路

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
sequenceDiagram
  participant Runtime as "ActorStreamSubscriptionHub"
  participant Stream as "Actor Stream(EventEnvelope)"
  participant Dispatcher as "ProjectionDispatcher"
  participant Projector as "WorkflowExecutionReadModelProjector"
  participant Store as "IProjectionReadModelStore"
  participant Query as "IWorkflowExecutionQueryApplicationService"
  participant Api as "GET /api/actors/{actorId}"

  Runtime->>Stream: "订阅 Actor 流"
  Stream-->>Dispatcher: "OnEvent(actorId, envelope)"
  Dispatcher->>Projector: "ProjectAsync"
  Projector->>Store: "Upsert(read model)"
  Api->>Query: "GetActorSnapshotAsync(actorId)"
  Query->>Store: "Get(actorId)"
  Store-->>Api: "WorkflowActorSnapshot"
```

## 6. 关键实现约束

- Host 仅做协议适配与 DI 组合，不承载业务编排。
- 一个 workflow 对应一个 actor；workflow 与 actor 绑定后不可变。
- 传入 `actorId` 的 run 请求不允许切换 workflow；workflow 变更必须创建新 actor。
- `WorkflowGAgent` 子 Actor ID 使用 `"{parentActorId}:{roleId}"` 命名空间，避免跨 workflow 根 Actor 冲突。
- Actor 事件域不承载 CQRS 命令语义：不在 `EventEnvelope` metadata 与 `StartWorkflowEvent` 中传递 `commandId`。
- `WorkflowExecutionProjectionService` 以 `ActorId` 为共享投影上下文键，同一 Actor 多次触发共享读模型与事件流。
- Projection 启动并发（`Ensure/Release`）由 `projection:{rootActorId}` 协调 Actor 串行裁决，不依赖进程内 `SemaphoreSlim`。
- `AttachLiveSink/DetachLiveSink` 通过 `workflow-run:{actorId}:{commandId}` 事件流订阅/退订，不在 `WorkflowExecutionProjectionContext` 维护 sink 事实态。
- CQRS 与 AGUI 复用同一输入事件流（统一 `ProjectionCoordinator`），通过不同 Projector 分支输出。
- AGUI `runId` 优先使用 `correlationId`（命令维度），`threadId` 维持 actor 维度。
- Workflow 能力执行状态查询统一由 Projection ReadModel 提供，不引入独立状态机层。
- `/api/agents` 仅返回 `WorkflowGAgent`，避免混入其他能力 Actor。
- workflow 文件加载为启动期 fail-fast：重复名称或未知 YAML 字段直接失败，不做静默覆盖。
- Workflow 内建模块与扩展模块统一走 `IWorkflowModulePack` 注册；`WorkflowModuleFactory` 聚合创建并对同名模块冲突 fail-fast。

## 7. Metadata 语义备注（防混淆）

- `EventEnvelope.Metadata` 是包络级传输/追踪元信息，参与内部传播策略，不等同业务结果字段。
- `StepCompletedEvent.Metadata` 是业务事件级元信息（如 `maker.*`、`connector.*`、`parallel.*`）。
- Workflow ReadModel 记录的是 `StepCompletedEvent.Metadata`（`CompletionMetadata` 与 timeline `Data`）。
- 实时输出链路当前仅保证 run/step 基本事件；`StepCompletedEvent.Metadata` 默认不直接透传到 `WorkflowRunEvent`。
