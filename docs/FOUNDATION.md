# Aevatar Foundation

本文档基于当前仓库代码，描述 Foundation 相关项目的职责边界、核心设计和典型用法。

## 项目分层

```
src/
├── Aevatar.Foundation.Abstractions  # 契约层：接口、Proto、基础类型
├── Aevatar.Foundation.Core          # 核心层：GAgent 基类、Pipeline、上下文与守卫
└── Aevatar.Foundation.Runtime       # 运行时层：Local Actor、Stream、路由、内存存储、DI 装配
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
- 持久化接口：`IStateStore<TState>`、`IEventStore`、`IAgentManifestStore`
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
- `GAgentBase<TState, TConfig>`：配置型基类，配置持久化到 manifest
- `EventPipelineBuilder`：把静态 `[EventHandler]` 与动态 `IEventModule` 合并为一个按 `Priority` 排序的流水线
- `StateGuard`：通过 `AsyncLocal` 限制 State 只在允许的生命周期写入
- `RunManager`/`RunContextScope`：latest-wins 运行管理与作用域传播
- `AsyncLocalAgentContext`：上下文在调用链中的注入与提取

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

## Aevatar.Foundation.Runtime

`Aevatar.Foundation.Runtime` 提供本地运行时实现，包含：

- `LocalActorRuntime`：创建/销毁/查找/链接/恢复 Actor
- `LocalActor`：邮箱串行处理、父流订阅、子节点传播
- `LocalActorPublisher`：按 `EventDirection` 路由事件
- `InMemoryStream` / `InMemoryStreamProvider`：内存流与订阅分发
- `EventRouter` / `InMemoryRouterStore`：层级路由与路由快照存储
- `InMemoryStateStore` / `InMemoryEventStore` / `InMemoryManifestStore`：默认内存持久化
- `MemoryCacheDeduplicator`：事件去重
- `AddAevatarRuntime()`：一键注册本地运行时依赖

口径说明：

- `InMemory*` 组件仅用于开发/测试环境，不作为生产容量治理对象。
- 生产环境应替换为 Redis/持久化实现，并在生产实现上评估内存增长与容量风险。

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
  - `WorkflowExecutionProjectionService` 管理 actor 投影上下文与查询
  - `WorkflowExecutionReadModelProjector` 负责事件驱动 read model 落库
  - 业务字段映射通过 `IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TEvent>` 扩展
- **Workflow 应用编排** 在 `Aevatar.Workflow.Application`：
  - `WorkflowChatRunApplicationService` 统一命令执行入口
  - `WorkflowExecutionQueryApplicationService` 提供读侧查询
- **宿主职责** 在 `Aevatar.Workflow.Host.Api`：
  - 仅做协议适配（HTTP/SSE/WebSocket）
  - 仅依赖 `Aevatar.Workflow.Application.Abstractions`
  - 暴露 `/api/agents`、`/api/workflows`（运行查询按配置开关）
- **输出分支**：
  - `WorkflowExecutionReadModelProjector` 写入 read model store
  - `WorkflowExecutionAGUIEventProjector`（位于 `Aevatar.Workflow.Presentation.AGUIAdapter`）输出 AG-UI 实时事件（SSE/WS），与 CQRS 读模型共享同一输入事件流

运行语义约束（当前实现）：

- Stream 订阅粒度是 actor 级；live sink 分发粒度是 command/correlation 级。
- `WorkflowExecutionAGUIEventProjector` 严格按 `EventEnvelope.CorrelationId` 匹配 sink，不做“空 correlation 广播”。
- `WorkflowExecutionReadModelProjector` 仅在 read model 发生实际变更时记录 `StateVersion` 与 `LastEventId`，用于读侧一致性观察。

详细关系见：

- `src/Aevatar.CQRS.Projection.Core/README.md`
- `src/workflow/Aevatar.Workflow.Projection/README.md`

## 测试项目

- `test/Aevatar.Foundation.Abstractions.Tests`：契约层测试（ID、属性、Envelope、时间工具）
- `test/Aevatar.Foundation.Core.Tests`：核心行为测试（BDD 场景、Pipeline、Hooks、StateGuard、层级流转）

## 快速上手

### 1) 注入运行时

```csharp
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

仓库处于持续迭代阶段，接口与目录会按架构约束逐步收敛。变更 Foundation 相关接口前，请同步更新 README、测试与本文档。
