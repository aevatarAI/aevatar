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
| Runtime | Actor 生命周期与拓扑管理器 | `IActorRuntime` |
| Stream | 事件传播通道 | `IStream` / `IStreamProvider` |

## Aevatar.Foundation.Abstractions

`Aevatar.Foundation.Abstractions` 只放契约，不放实现。主要包括：

- Agent/Actor/Runtime 基础接口：`IAgent`、`IActor`、`IActorRuntime`
- 事件发布与流接口：`IEventPublisher`、`IStream`、`IStreamProvider`
- 事件模块体系：`IEventModule`、`IEventModuleFactory`、`IEventHandlerContext`
- 持久化接口：`IStateStore<TState>`、`IEventStore`
- 上下文与运行控制：`IAgentContextAccessor`、`IRunManager`
- Hook 扩展点：`IGAgentExecutionHook`、`GAgentExecutionHookContext`
- 核心 Proto：`agent_messages.proto`

`EventEnvelope` 保持最小语义字段（id、timestamp、payload、publisher、direction、correlation、target、metadata），路由传播细节放在运行时实现中。

## 核心主链路（框架最关键理解）

可以把框架主线理解为：

1. **统一传输契约**：所有业务事件先被包进 `EventEnvelope.payload`，再进入运行时流。
2. **统一路由执行**：`LocalActorPublisher` 按 `EventDirection`（`Self/Down/Up/Both`）路由到目标 Stream。
3. **统一处理管线**：`GAgentBase` 把静态 `[EventHandler]` 与动态 `IEventModule` 合并后按优先级执行。
4. **统一读侧投影**：同一条 `EventEnvelope` 可被投影为多个读模型（例如 AG-UI SSE 事件、运行报告、业务只读模型）。

关键澄清：

- 当前 AG-UI 主要是 **事件投影**，不是直接把 `State` 映射到前端。
- `State` 是写侧运行态；读侧建议由投影生成独立只读模型（CQRS）。

## Aevatar.Foundation.Core

`Aevatar.Foundation.Core` 提供框架核心实现，重点如下：

- `GAgentBase`：无状态 Agent 基类，统一事件分发与 Hook 管线
- `GAgentBase<TState>`：状态型基类，集成 `IStateStore<TState>`
- `GAgentBase<TState, TConfig>`：有效配置型基类（`EffectiveConfig` 由类默认值 + 状态覆盖合并得到）
- `EventPipelineBuilder`：把静态 `[EventHandler]` 与动态 `IEventModule` 合并为一个按 `Priority` 排序的流水线
- `StateGuard`：通过 `AsyncLocal` 限制 State 只在允许的生命周期写入
- `RunManager`/`RunContextScope`：latest-wins 运行管理与作用域传播
- `AsyncLocalAgentContext`：上下文在调用链中的注入与提取

### 编排能力边界

- Framework 只保留通用运行时能力（事件、状态、上下文、管线、路由）。
- 业务编排能力统一收敛到 workflow 主链路（`Aevatar.Workflow.Core` 的模块与 YAML）。
- 需要顺序/并行/投票/分支等流程控制时，优先通过 workflow 模块组合实现，不在 Foundation 复刻第二套机制。

### 统一事件 Pipeline

Agent 收到 `EventEnvelope` 后，会将两类处理器合并执行：

1. 静态处理器（反射发现 `[EventHandler]`）
2. 动态模块（运行时注册 `IEventModule`）

二者统一按 `Priority` 升序执行，并通过 `IGAgentExecutionHook` 提供前后置观测与错误回调。

### 状态写保护

`StateGuard` 控制状态写入时机：

- 允许写：事件处理或激活期的写 scope
- 禁止写：其他上下文（会抛 `InvalidOperationException`）

这保证了状态修改和消息处理串行模型一致。

## Aevatar.Foundation.Runtime + Local 实现

`Aevatar.Foundation.Runtime`（通用层）包含：

- `InMemoryStream` / `InMemoryStreamProvider`：内存流与订阅分发
- `EventRouter` / `InMemoryRouterStore`：层级路由与路由快照存储
- `InMemoryStateStore` / `InMemoryEventStore`：默认内存持久化
- `MemoryCacheDeduplicator`：事件去重
- `IActorDeactivationHook*` / `EventStoreCompactionDeactivationHook`：停用钩子与裁剪触发

`Aevatar.Foundation.Runtime.Implementations.Local`（本地实现层）包含：

- `LocalActorRuntime`：创建/销毁/查找/链接 Actor（按需激活）
- `LocalActor`：邮箱串行处理、父流订阅、子节点传播
- `LocalActorPublisher`：按 `EventDirection` 路由事件
- `LocalActorTypeProbe`：运行时类型探测
- `AddAevatarRuntime()`：一键注册本地运行时依赖

口径说明：

- `InMemory*` 组件仅用于开发/测试环境，不作为生产容量治理对象。
- 生产环境应使用持久化实现（仓库已提供 `Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet`），并在生产实现上评估内存增长与容量风险。

### 分布式目标态（生产）

1. `IActorRuntime` 在生产环境提供分布式部署能力，保证同一 `actorId` 全局单激活与邮箱串行。
2. `IStateStore<TState>` / `IEventStore` 使用非 InMemory 持久化实现。
3. 投影相关编排运行态通过 Actor 化承载；中间层服务不持有跨节点事实态。
4. `InMemory*` 仅保留本地开发与自动化测试使用。

### Routing 细节

`Routing` 现在由两部分组成：

- 路由执行：`EventRouter`
- 层级持久化：`IRouterHierarchyStore` + `InMemoryRouterStore`

`EventRouter.RouteAsync(...)` 的核心行为：

1. 检查 `metadata["__publishers"]`，如果当前 Actor 已处理过则直接跳过（环路保护）
2. 当前 Actor 先处理事件
3. 按 `EventDirection` 转发到父/子节点

这让路由逻辑和运行时实现解耦：Actor 可以专注于消费和传播，层级快照则交给 Store 管理。

## CQRS 与 Projection 落点

当前实现已经收敛为一套统一链路：

- **订阅与编排内核** 在 `Aevatar.CQRS.Projection.Core`：
  - `ActorStreamSubscriptionHub<TMessage>`：按 `actorId` 复用底层 stream 订阅
  - `ProjectionSubscriptionRegistry<,>`：维护 actor 级投影上下文激活态
  - `ProjectionCoordinator<,>`：一对多分发 projector
  - `ProjectionLifecycleService<,>`：统一 `start/wait/complete`
- **读模型抽象分层**：
  - `Aevatar.Foundation.Projection`：提供读模型最小公共字段（`RootActorId/CommandId/StateVersion/LastEventId`）与通用能力接口（Timeline / RoleReplies）
  - `Aevatar.AI.Projection`：提供 AI 通用事件 reducer（`TextMessage*` / `Tool*`）和 `IProjectionEventApplier<,,>` 扩展模式
- **WorkflowExecution 业务扩展** 在 `Aevatar.Workflow.Projection`：
  - `WorkflowExecutionProjectionLifecycleService`（生命周期端口）与 `WorkflowExecutionProjectionQueryService`（查询端口）
  - 两者复用 `Aevatar.CQRS.Projection.Core` 的通用基类：`ProjectionLifecyclePortServiceBase<>` / `ProjectionQueryPortServiceBase<>`
  - `WorkflowProjectionActivationService` 负责 projection 启动与上下文激活
  - `WorkflowProjectionReleaseService` 负责 idle 检测与 stop/release
  - `IProjectionOwnershipCoordinator` 负责 ownership acquire/release（由 Core 抽象直接注入）
  - `WorkflowProjectionSinkSubscriptionManager` 负责 live sink attach/detach
  - `WorkflowProjectionLiveSinkForwarder` 负责 run-event 推送与失败策略桥接
  - `WorkflowProjectionSinkFailurePolicy` 负责 sink 异常降级与错误事件发布
  - `WorkflowProjectionReadModelUpdater` 负责 read model 元信息更新
  - `WorkflowProjectionQueryReader` 负责 read model 查询映射
  - `WorkflowExecutionReadModelProjector` 负责事件驱动 read model 落库
  - 业务字段映射通过 `IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TEvent>` 扩展
- **Workflow 应用编排** 在 `Aevatar.Workflow.Application`：
  - `WorkflowChatRunApplicationService` 仅做请求校验与流程入口编排
  - `WorkflowRunContextFactory` 负责 run 上下文与 projection lease 初始化
  - `WorkflowRunExecutionEngine` 负责执行/输出泵送/终态收敛
  - `WorkflowRunCompletionPolicy` 负责终态判定（`RUN_FINISHED` / `RUN_ERROR`）
  - `WorkflowRunResourceFinalizer` 负责 detach/release/sink dispose 兜底
  - `WorkflowExecutionQueryApplicationService` 提供读侧查询
- **宿主职责** 在 `Aevatar.Workflow.Host.Api`：
  - 仅做协议适配（HTTP/SSE/WebSocket）
  - 仅依赖 `Aevatar.Workflow.Application.Abstractions`
  - 暴露 `/api/agents`、`/api/workflows`（运行查询按配置开关）
- **输出分支**：
  - `WorkflowExecutionReadModelProjector` 写入 read model store
  - `WorkflowExecutionAGUIEventProjector`（位于 `Aevatar.Workflow.Presentation.AGUIAdapter`）输出 AG-UI 实时事件（SSE/WS），与 CQRS 读模型共享同一输入事件流

运行语义约束（当前实现）：

- Stream 订阅粒度是 actor 级；run 输出分发粒度是 command/correlation 级。
- `WorkflowExecutionAGUIEventProjector` 仅在 `EventEnvelope.CorrelationId` 非空时发布 run-event，并按 `workflow-run:{actorId}:{commandId}` 事件流路由。
- `WorkflowExecutionReadModelProjector` 仅在 read model 发生实际变更时记录 `StateVersion` 与 `LastEventId`，用于读侧一致性观察。
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
```

### 2) 创建与连接 Actor

```csharp
var parent = await runtime.CreateAsync<MyAgent>("parent");
var child = await runtime.CreateAsync<MyWorkerAgent>("child");
await runtime.LinkAsync("parent", "child");
```

### 3) 发布事件

```csharp
await ((GAgentBase)parent.Agent).EventPublisher
    .PublishAsync(new PingEvent { Message = "hello" }, EventDirection.Down);
```

## 当前状态说明

仓库处于持续迭代阶段，接口与目录会按架构约束逐步收敛。变更 Foundation 相关接口前，请同步更新 README、测试与本文档；涉及 Runtime provider 语义时，同步更新 `docs/PROJECT_ARCHITECTURE.md` 与 `docs/CQRS_ARCHITECTURE.md`。
