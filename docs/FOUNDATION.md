# Aevatar Foundation

本文档基于当前仓库代码，描述 Foundation 相关项目的职责边界、核心设计和典型用法。

## 项目分层

```
src/
├── Aevatar.Abstractions  # 契约层：接口、Proto、基础类型
├── Aevatar.Core          # 核心层：GAgent 基类、Pipeline、上下文与守卫
└── Aevatar.Runtime       # 运行时层：Local Actor、Stream、路由、内存存储、DI 装配
```

## 核心概念

| 概念 | 说明 | 关键接口 |
|---|---|---|
| Agent | 业务逻辑单元，处理事件、维护状态 | `IAgent` / `IAgent<TState>` |
| Actor | Agent 的运行容器，提供串行处理与层级关系 | `IActor` |
| Runtime | Actor 生命周期与拓扑管理器 | `IActorRuntime` |
| Stream | 事件传播通道 | `IStream` / `IStreamProvider` |

## Aevatar.Abstractions

`Aevatar.Abstractions` 只放契约，不放实现。主要包括：

- Agent/Actor/Runtime 基础接口：`IAgent`、`IActor`、`IActorRuntime`
- 事件发布与流接口：`IEventPublisher`、`IStream`、`IStreamProvider`
- 事件模块体系：`IEventModule`、`IEventModuleFactory`、`IEventHandlerContext`
- 持久化接口：`IStateStore<TState>`、`IEventStore`、`IAgentManifestStore`
- 上下文与运行控制：`IAgentContextAccessor`、`IRunManager`
- Hook 扩展点：`IGAgentHook`、`GAgentHookContext`
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

## Aevatar.Core

`Aevatar.Core` 提供框架核心实现，重点如下：

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

二者统一按 `Priority` 升序执行，并通过 `IGAgentHook` 提供前后置观测与错误回调。

### 状态写保护

`StateGuard` 控制状态写入时机：

- 允许写：事件处理或激活期的写 scope
- 禁止写：其他上下文（会抛 `InvalidOperationException`）

这保证了状态修改和消息处理串行模型一致。

## Aevatar.Runtime

`Aevatar.Runtime` 提供本地运行时实现，包含：

- `LocalActorRuntime`：创建/销毁/查找/链接/恢复 Actor
- `LocalActor`：邮箱串行处理、父流订阅、子节点传播
- `LocalActorPublisher`：按 `EventDirection` 路由事件
- `InMemoryStream` / `InMemoryStreamProvider`：内存流与订阅分发
- `EventRouter` / `InMemoryRouterStore`：层级路由与路由快照存储
- `InMemoryStateStore` / `InMemoryEventStore` / `InMemoryManifestStore`：默认内存持久化
- `MemoryCacheDeduplicator`：事件去重
- `AddAevatarRuntime()`：一键注册本地运行时依赖

### Routing 细

`Routing` 现在由两部分组成：

- 路由执行：`EventRouter`
- 层级持久化：`IRouterHierarchyStore` + `InMemoryRouterStore`

`EventRouter.RouteAsync(...)` 的核心行为：

1. 检查 `metadata["__publishers"]`，如果当前 Actor 已处理过则直接跳过（环路保护）
2. 当前 Actor 先处理事件
3. 按 `EventDirection` 转发到父/子节点

这让路由逻辑和运行时实现解耦：Actor 可以专注于消费和传播，层级快照则交给 Store 管理。

## CQRS 与 Projection 落点

在当前实现里，Projection 的典型落点在 Host 层：

- 订阅根 Actor Stream，读取 `EventEnvelope`
- 投影为 AG-UI 事件并通过 SSE 推送
- 并行聚合为运行报告（JSON/HTML）

如果需要业务读模型（read-only model）：

- 建议新增独立 projector（订阅同一事件流）
- 将结果写入只读存储（内存/Redis/DB）
- 暴露独立 Query API 供前端查询

## 测试项目

- `test/Aevatar.Abstractions.Tests`：契约层测试（ID、属性、Envelope、时间工具）
- `test/Aevatar.Core.Tests`：核心行为测试（BDD 场景、Pipeline、Hooks、StateGuard、层级流转）

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

仓库仍处于初始化迭代阶段（尚无正式提交历史），接口和目录可能继续调整。建议在变更 Foundation 接口前，先同步更新对应 README、测试与本文档。
