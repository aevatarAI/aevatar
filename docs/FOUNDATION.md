# Aevatar Foundation

本文档只描述当前有效的 Foundation 模型。

## 分层

```text
src/
├── Aevatar.Foundation.Abstractions
├── Aevatar.Foundation.Core
├── Aevatar.Foundation.Runtime
└── Aevatar.Foundation.Runtime.Implementations.Local
```

- `Abstractions`：Agent/Actor/Runtime/Streaming/Connector/Callback 等契约
- `Core`：`GAgentBase`、状态写保护、静态事件处理管线
- `Runtime`：通用路由、去重、回调、持久化抽象
- `Local`：本地 Actor Runtime 实现

## 核心概念

| 概念 | 说明 | 关键接口 |
|---|---|---|
| Agent | 业务逻辑单元，处理事件并维护状态 | `IAgent` / `IAgent<TState>` |
| Actor | Agent 的运行容器，保证串行处理 | `IActor` |
| Runtime | Actor 生命周期与拓扑管理器 | `IActorRuntime` |
| Stream | 事件传播通道 | `IStream` / `IStreamProvider` |
| Envelope | 统一传输契约 | `EventEnvelope` |

## 当前主链路

1. 业务事件被包装成 `EventEnvelope`。
2. Runtime 按 `EventDirection` 路由到目标 Actor/Stream。
3. `GAgentBase` 只执行静态 `[EventHandler]` / `[AllEventHandler]`。
4. `IGAgentExecutionHook` 提供前后置观测与错误回调。
5. 写侧事件再进入 Projection / ReadModel / 实时推送链路。

当前已经删除旧的 Foundation 级动态事件模块管线。

结论：

`Foundation` 只提供静态 handler + hook + runtime callback 这些稳定通用机制，不再承载“可插拔业务事件模块”。

## Aevatar.Foundation.Abstractions

契约层当前主要包含：

- `IAgent` / `IAgent<TState>`
- `IActor` / `IActorRuntime`
- `IEventPublisher`
- `IStream` / `IStreamProvider`
- `IConnector` / `IConnectorCatalog`
- `IStateStore<TState>` / `IEventStore`
- `IGAgentExecutionHook`
- `IActorRuntimeCallbackScheduler`
- `agent_messages.proto`

`EventEnvelope` 保持最小语义字段：

- `id`
- `timestamp`
- `payload`
- `publisher_id`
- `direction`
- `correlation_id`
- `target`
- `metadata`

## Aevatar.Foundation.Core

`Core` 的关键边界：

- `GAgentBase`：无状态基类，负责静态 handler 分发
- `GAgentBase<TState>`：状态型基类，集成事件回放与状态存储
- `StaticEventHandlerDispatcher`：静态 handler 匹配与调用
- `StateGuard`：只允许在 handler / activate scope 中改状态

### 状态约束

状态写入只允许发生在：

- 事件处理主线程
- actor 激活期

这条约束确保：

- 状态修改顺序与事件处理顺序一致
- reactivation / replay 语义稳定
- 不需要靠 `lock/ConcurrentDictionary` 修补 actor 边界

## Aevatar.Foundation.Runtime

通用运行时负责：

- Stream 路由
- dedup
- callback 调度
- 持久化抽象
- Actor deactivate hooks

`Local` 实现负责：

- `LocalActorRuntime`
- `LocalActor`
- `LocalActorPublisher`
- `AddAevatarRuntime()`

说明：

- `InMemory*` 只用于开发和测试
- 生产语义应落在分布式 runtime + 非内存持久化实现

## 扩展原则

扩展性仍然保留，但不再走 Foundation 动态事件模块。

- Workflow 业务步骤：`IWorkflowPrimitiveExecutor`
- AI 横切能力：middleware / hook
- 跨事件事实：actor + state + domain events
- 基础设施发现：catalog / port

一句话：

`Foundation` 负责稳定运行时机制，不负责可插拔业务编排。
