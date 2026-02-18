# Workflow 子系统架构（`src/workflow`）

本文档描述 `src/workflow` 的完整实现关系。当前语义是：一次 `Run` 本质上就是向 `WorkflowGAgent` 触发一次命令事件（`ChatRequestEvent`），后续全部通过事件流驱动执行与投影。

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

  subgraph Shared["Shared Runtime / CQRS"]
    RH["Aevatar.CQRS.Runtime.Hosting"]
    CQRSC["Aevatar.CQRS.Core"]
    CQRSP["Aevatar.CQRS.Projection.Core"]
    F["Aevatar.Foundation.* / Aevatar.AI.Abstractions"]
  end

  H --> RH
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
  Resolver-->>AppSvc: "ActorId + WorkflowName"
  AppSvc->>Port: "EnsureActorProjectionAsync(actorId, commandId)"
  AppSvc->>Port: "AttachLiveSinkAsync(actorId, sink)"
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
  COOR["ProjectionCoordinator"]

  RM["WorkflowExecutionReadModelProjector"]
  RED["Reducers(Start/Step/TextEnd/Completed)"]
  STORE["IProjectionReadModelStore(WorkflowExecutionReport)"]

  AGP["WorkflowExecutionAGUIEventProjector"]
  MAP["EventEnvelopeToAGUIEventMapper + Handlers"]
  CTX["WorkflowExecutionProjectionContext.LiveSinks"]
  CH["WorkflowRunEventChannel"]

  QP["WorkflowExecutionProjectionService(IWorkflowExecutionProjectionPort)"]
  QS["WorkflowExecutionQueryApplicationService"]
  APIQ["/api/actors/* Query Endpoints"]

  LIFE --> REG
  REG --> HUB
  HUB --> ES
  ES --> COOR

  COOR --> RM
  RM --> RED
  RED --> STORE

  COOR --> AGP
  AGP --> MAP
  MAP --> CTX
  CTX --> CH

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
  FACT["IEventModuleFactory[]"]
  CFG["IWorkflowModuleConfigurator[]"]
  MOD["Workflow Modules(Loop/LLM/Tool/Connector/Parallel...)"]
  EVT["Workflow Domain Events(Start/Step/Completed...)"]
  OUT["ChatResponseEvent / TextMessageEndEvent"]

  CR --> WG
  WG --> PV
  WG --> TREE
  WG --> EXP
  EXP --> FACT
  FACT --> MOD
  CFG --> MOD
  MOD --> EVT
  EVT --> WG
  WG --> OUT
```

## 5. 关键实现约束

- Host 仅做协议适配与 DI 组合，不承载业务编排。
- `WorkflowExecutionProjectionService` 以 `ActorId` 为共享投影上下文键，同一 Actor 多次触发共享读模型与事件流。
- CQRS 与 AGUI 复用同一输入事件流（统一 `ProjectionCoordinator`），通过不同 Projector 分支输出。
