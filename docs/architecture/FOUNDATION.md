# Aevatar Foundation

本文档基于当前仓库代码，描述 Foundation 相关项目的职责边界、核心设计和典型用法。

## 项目分层

```
src/
├── Aevatar.Foundation.Abstractions  # 契约层：接口、Proto、基础类型
├── Aevatar.Foundation.Core          # 核心层：GAgent 基类、Pipeline、上下文与守卫
├── Aevatar.Foundation.Runtime       # 运行时通用层：Stream、路由、持久化、Observability、停用钩子
└── Aevatar.Foundation.Runtime.Implementations.Local  # 本地实现层：Local Actor/Runtime/TypeProbe/DI
```

## 核心概念

| 概念 | 说明 | 关键接口 |
|---|---|---|
| Agent | 业务逻辑单元，处理事件、维护状态 | `IAgent` / `IAgent<TState>` |
| Actor | Agent 的运行容器，提供串行处理与层级关系 | `IActor` |
| Runtime | 构建在 Stream 之上的 Actor 语义层，负责生命周期、寻址、邮箱串行与拓扑管理 | `IActorRuntime` / `IActorDispatchPort` |
| Event Context | 当前 Actor 执行中的消息上下文，负责 publish/send 语义 | `IEventPublisher` / `IEventContext` |
| Stream | `EventEnvelope` 的传输骨架与传播通道 | `IStream` / `IStreamProvider` |

## Aevatar.Foundation.Abstractions

`Aevatar.Foundation.Abstractions` 只放契约，不放实现。主要包括：

- Agent/Actor/Runtime 基础接口：`IAgent`、`IActor`、`IActorRuntime`、`IActorDispatchPort`
- 事件发布与流接口：`IEventPublisher`、`IStream`、`IStreamProvider`
- 事件模块体系：`IEventModule<TContext>`、`IEventModuleFactory<TContext>`、`IEventContext` / `IEventHandlerContext`
- 持久化接口：`IStateStore<TState>`、`IEventStore`
- 上下文与运行控制：`IAgentContextAccessor`、`IRunManager`
- Hook 扩展点：`IGAgentExecutionHook`、`GAgentExecutionHookContext`
- 核心 Proto：`agent_messages.proto`

`EventEnvelope` 保持最小语义字段（`id`、`timestamp`、`payload`、`route`、`propagation`、`runtime`），路由传播细节通过 typed 子消息表达。

补充口径：

- Foundation 只保留稳定原语：`IActorRuntime` 负责 lifecycle/topology/lookup，`IActorDispatchPort` 负责外部 envelope 投递，`IEventPublisher` / `IEventContext` 负责当前 actor 执行中的 publish/send。
- workflow、scripting 等上层如果需要更友好的能力面，应在各自子系统内部适配，不再向 Foundation 增加公共 messaging/session 门面。
- 跨来源协议样例属于测试契约，不进入 Foundation 生产契约层。

这里要明确一个经常被混淆的边界：

- `EventEnvelope` 虽然名字叫 “Event”，但在 Foundation 语义上它是 **runtime message envelope**。
- 它承载的是 Actor 之间通过 Stream 传递的入站/出站消息；payload 既可能是 command-like request、signal、reply、timeout fired，也可能是业务事件。
- Event Sourcing 里的持久化事实是 `StateEvent` + `EventStore`，不是运行时消息流本身。

## 核心主链路（框架最关键理解）

可以把框架主线理解为：

1. **统一消息传输契约**：外部 command、内部 signal、reply、timeout、业务事件等，都以 `EventEnvelope.payload` 形式进入 Actor 消息流。
2. **Runtime 赋予 Actor 语义**：`IActorRuntime` / `IActor` 在 Stream 之上提供 Actor 创建、寻址、激活、邮箱串行和父子拓扑；`IActorDispatchPort` 负责 envelope 的定向投递。
3. **统一路由执行**：`LocalActorPublisher` 对外暴露 `PublishAsync/SendToAsync`；其中 `PublishAsync` 构造 `PublicationRoute.topology(Self/Parent/Children/ParentAndChildren)`，`SendToAsync` 构造 `DirectRoute`。Event Sourcing commit 后的 `PublicationRoute.observer(CommittedFacts)` 由框架内部 `ICommittedStateEventPublisher` 发出，不进入业务 actor 公共能力面；`GAgentBase` 把静态 `[EventHandler]` 与动态 `IEventModule<IEventHandlerContext>` 合并后按优先级执行。
4. **领域事实显式持久化**：有状态 Actor 只有在显式调用 `PersistDomainEventAsync(...)` / `PersistDomainEventsAsync(...)` 后，领域事件才进入 `EventStore` 成为事实源。
5. **统一读侧投影**：同一条 Actor `EventEnvelope` 消息流可被投影为多个读模型（例如 AG-UI SSE 事件、运行报告、业务只读模型）。

关键澄清：

- 当前 AG-UI 主要是 **事件投影**，不是直接把 `State` 映射到前端。
- `State` 是写侧运行态；读侧建议由投影生成独立只读模型（CQRS）。
- Stream 上的 `EventEnvelope` 是运行时消息层；Event Sourcing 的 `StateEvent` 是事实层。两者有关联，但不是同一个概念。

## Aevatar.Foundation.Core

`Aevatar.Foundation.Core` 提供框架核心实现，重点如下：

- `GAgentBase`：无状态 Agent 基类，统一事件分发与 Hook 管线
- `GAgentBase<TState>`：状态型基类，集成 `IStateStore<TState>`
- `GAgentBase<TState, TConfig>`：有效配置型基类（`EffectiveConfig` 由类默认值 + 状态覆盖合并得到）
- `EventPipelineBuilder`：把静态 `[EventHandler]` 与动态 `IEventModule<IEventHandlerContext>` 合并为一个按 `Priority` 排序的流水线
- `StateGuard`：通过 `AsyncLocal` 限制 State 只在允许的生命周期写入
- `RunManager`/`RunContextScope`：latest-wins 运行管理与作用域传播
- `AsyncLocalAgentContext`：上下文在调用链中的注入与提取

### 编排能力边界

- Framework 只保留通用运行时能力（事件、状态、上下文、管线、路由）。
- 业务编排能力统一收敛到 workflow 主链路（`Aevatar.Workflow.Core` 的模块与 YAML）。
- 需要顺序/并行/投票/分支等流程控制时，优先通过 workflow 模块组合实现，不在 Foundation 复刻第二套机制。

### 统一消息 Pipeline

Agent 收到 `EventEnvelope` 后，会将两类处理器合并执行：

1. 静态处理器（反射发现 `[EventHandler]`）
2. 动态模块（运行时注册 `IEventModule<IEventHandlerContext>`）

二者统一按 `Priority` 升序执行，并通过 `IGAgentExecutionHook` 提供前后置观测与错误回调。

### 状态写保护

`StateGuard` 控制状态写入时机：

- 允许写：事件处理或激活期的写 scope
- 禁止写：其他上下文（会抛 `InvalidOperationException`）

这保证了状态修改和消息处理串行模型一致。

## Aevatar.Foundation.Runtime + Local 实现

`Aevatar.Foundation.Runtime`（通用层）包含：

- `InMemoryStream` / `InMemoryStreamProvider`：内存流与订阅分发
- `InMemoryStateStore` / `InMemoryEventStore`：默认内存持久化
- `MemoryCacheDeduplicator`：事件去重
- `IActorDeactivationHook*` / `EventStoreCompactionDeactivationHook`：停用钩子与裁剪触发

`Aevatar.Foundation.Runtime.Implementations.Local`（本地实现层）包含：

- `LocalActorRuntime`：创建/销毁/查找/链接 Actor（按需激活）
- `LocalActor`：邮箱串行处理、父流订阅、子节点传播
- `LocalActorPublisher`：按 `EnvelopeRoute` 的 `direct/publication(topology|observer)` 变体发布事件
- `LocalActorTypeProbe`：运行时类型探测
- `AddAevatarRuntime()`：一键注册本地运行时依赖（含 request/reply client）

口径说明：

- `InMemory*` 组件仅用于开发/测试环境，不作为生产容量治理对象。
- 生产环境应使用持久化实现（仓库已提供 `Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet`），并在生产实现上评估内存增长与容量风险。

### 分布式目标态（生产）

1. `IActorRuntime` 在生产环境提供分布式部署能力，保证同一 `actorId` 全局单激活与邮箱串行；`IActorDispatchPort` 负责把 envelope 投递到目标 actor mailbox。
2. `IStateStore<TState>` / `IEventStore` 使用非 InMemory 持久化实现。
3. 投影相关编排运行态通过 Actor 化承载；中间层服务不持有跨节点事实态。
4. `InMemory*` 仅保留本地开发与自动化测试使用。

`AddAevatarFoundationRuntimeOrleans()` 与本地 `AddAevatarRuntime()` 保持同一口径：都只暴露 `IActorRuntime` / `IActorDispatchPort` / `IEventPublisher` 这组基础原语；生命周期/拓扑仍由 `IActorRuntime` 提供，上层能力不依赖具体 runtime provider。

### Routing 细节

`Routing` 现在由两部分组成：

- 拓扑状态：`LocalActor` / `RuntimeActorGrainState` 自身持有 parent/children
- 消息传播：stream-level forwarding + runtime ingress demux

当前拓扑事实已经直接收口到 runtime actor 自身：

1. Local runtime：`LocalActor` 内存态持有 `parent/children`
2. Orleans runtime：`RuntimeActorGrainState` 持久态持有 `ParentId/Children`
3. `LinkAsync/UnlinkAsync` 同时更新拓扑状态和 stream relay binding

实际消息行为已经收敛为：

1. `DirectRoute` 由 runtime 直接投递到目标 actor inbox
2. `PublicationRoute.topology` 由 stream forwarding / relay binding 负责传播
3. `PublicationRoute.observer` 只给 projection / live sink / observer，可见但不进业务 actor inbox

也就是说，拓扑状态和 fan-out 已不再通过单独 `EventRouter` 对象承载；真正的 fan-out 仍由 stream forwarding / relay binding 执行。

## CQRS 与 Projection 落点

当前实现已经收敛为一套统一链路：

- **订阅与编排内核** 在 `Aevatar.CQRS.Projection.Core`：
  - `ProjectionScopeGAgentBase`：scope actor 基类，持有唯一运行态事实
  - `ProjectionMaterializationScopeGAgentBase`：durable materialization scope actor 基类
  - `ProjectionSessionScopeGAgentBase`：session observation scope actor 基类
  - `ProjectionScopeActorRuntime`：scope actor 的统一 dispatch / replay / observation 入口
- **读模型抽象分层**：
  - `Aevatar.Foundation.Projection`：提供读模型最小公共字段（`RootActorId/CommandId/StateVersion/LastEventId`）与通用能力接口（Timeline / RoleReplies）
  - `Aevatar.AI.Projection`：提供 AI 通用事件 reducer（`TextMessage*` / `Tool*`）和 `IProjectionEventApplier<,,>` 扩展模式
- **WorkflowExecution 业务扩展** 在 `Aevatar.Workflow.Projection`：
  - `WorkflowExecutionProjectionPort`（投影端口）与 `WorkflowExecutionCurrentStateQueryPort` / `WorkflowExecutionArtifactQueryPort`（查询端口实现）
  - 生命周期复用 `Aevatar.CQRS.Projection.Core` 的通用 event-sink port 基类：`EventSinkProjectionLifecyclePortBase<>`
  - `ProjectionSessionScopeActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, WorkflowExecutionSessionScopeGAgent>` 负责 session scope actor 激活
  - `ProjectionSessionScopeReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionSessionScopeGAgent>` 负责 session scope actor 释放
  - `ProjectionMaterializationScopeActivationService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationContext, WorkflowExecutionMaterializationScopeGAgent>` 负责 durable scope actor 激活
  - `ProjectionMaterializationScopeReleaseService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationScopeGAgent>` 负责 durable scope actor 释放
  - `ProjectionSessionEventHub<WorkflowRunEventEnvelope>` 负责 session stream 分发
  - `WorkflowExecutionCurrentStateQueryPort` 负责 authority current-state 查询映射
  - `WorkflowExecutionArtifactQueryPort` 负责 artifact 查询映射
  - `WorkflowExecutionCurrentStateProjector` 负责 authority current-state replica
  - `WorkflowRunInsightReportArtifactProjector` / `WorkflowRunTimelineArtifactProjector` / `WorkflowRunGraphArtifactProjector` 负责 derived durable artifacts
- **Workflow 应用编排** 在 `Aevatar.Workflow.Application`：
  - `ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>` 负责完整交互路径（dispatch + sink consume + finalize）
  - `DefaultDetachedCommandDispatchService<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>` 负责 accepted-only 路径
  - `ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>` / `ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>` 负责 run control 命令入口
  - `WorkflowRunCommandTargetResolver` 负责 workflow source 解析与 run target 构建
  - `WorkflowRunCommandTargetBinder` 负责 projection lease/live sink 绑定与清理兜底
  - `WorkflowRunAcceptedReceiptFactory` 负责 `actorId + commandId + correlationId` receipt 生成
  - `WorkflowExecutionQueryApplicationService` 提供读侧查询
- **宿主职责** 在 `Aevatar.Workflow.Host.Api`：
  - 仅做协议适配（HTTP/SSE/WebSocket）
  - 仅依赖 `Aevatar.Workflow.Application.Abstractions`
  - 暴露 `/api/agents`、`/api/workflows`（运行查询按配置开关）
- **输出分支**：
  - `WorkflowExecutionCurrentStateProjector` 写入 canonical current-state store
  - `WorkflowRunInsightReportArtifactProjector` / `WorkflowRunTimelineArtifactProjector` / `WorkflowRunGraphArtifactProjector` 写入各自 artifact store
  - `WorkflowExecutionAGUIEventProjector`（位于 `Aevatar.Workflow.Presentation.AGUIAdapter`）输出 AG-UI 实时事件（SSE/WS），与 CQRS 读模型共享同一输入 envelope 流

运行语义约束（当前实现）：

- Stream 订阅粒度是 actor 级；run 输出分发粒度是 command/correlation 级。
- `WorkflowExecutionAGUIEventProjector` 仅在 `EventEnvelope.Propagation.CorrelationId` 非空时发布 run-event，并按 `workflow-run:{actorId}:{commandId}` 事件流路由。
- 各 workflow readmodel projector 都只记录 committed `StateVersion` 与 `LastEventId`，用于读侧一致性观察。
- Projection 消费的是 Actor 运行时 envelope 流；EventStore 仍只用于写侧事实持久化与重放。
- 编排层守卫：
  - `tools/ci/architecture_guards.sh` 强制关键编排类保持轻量（行数与依赖数上限），防止职责反弹。

详细关系见：

- `src/Aevatar.CQRS.Projection.Core/README.md`
- `src/workflow/Aevatar.Workflow.Projection/README.md`

## 测试项目

- `test/Aevatar.Foundation.Abstractions.Tests`：契约层测试（ID、属性、Envelope、时间工具）
- `test/Aevatar.Foundation.Core.Tests`：核心行为测试（BDD 场景、Pipeline、Hooks、StateGuard、层级流转）

## 快速上手

### 1) 注入运行时

```csharp
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;

var services = new ServiceCollection();
services.AddAevatarRuntime();
var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
var dispatchPort = sp.GetRequiredService<IActorDispatchPort>();
```

### 2) 创建与连接 Actor

```csharp
var parent = await runtime.CreateAsync<MyAgent>("parent");
var child = await runtime.CreateAsync<MyWorkerAgent>("child");
await runtime.LinkAsync("parent", "child");
```

### 3) 发布消息 payload（运行时会包装为 `EventEnvelope`）

```csharp
await ((GAgentBase)parent.Agent).EventPublisher
    .PublishAsync(new PingEvent { Message = "hello" }, TopologyAudience.Children);
```

## 当前状态说明

仓库处于持续迭代阶段，接口与目录会按架构约束逐步收敛。变更 Foundation 相关接口前，请同步更新 README、测试与本文档；涉及 Runtime provider 语义时，同步更新 `docs/architecture/PROJECT_ARCHITECTURE.md` 与 `docs/architecture/CQRS_ARCHITECTURE.md`。
