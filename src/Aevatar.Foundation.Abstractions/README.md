# Aevatar.Foundation.Abstractions

`Aevatar.Foundation.Abstractions` 是 Aevatar 的公共契约层。

## 职责

- 定义 Agent / Actor / Runtime 基础接口
- 定义事件发布与流接口
- 定义持久化、传播、回调、connector 抽象
- 提供共享 Proto 消息和基础属性标记

本项目只放契约，不放运行时实现。

## 主要内容

```text
Aevatar.Foundation.Abstractions/
├── IAgent.cs
├── IActor.cs
├── IActorRuntime.cs
├── IEventPublisher.cs
├── Attributes/
├── Connectors/
├── Context/
├── Deduplication/
├── Hooks/
├── Persistence/
├── Propagation/
├── Runtime/
├── Streaming/
├── TypeSystem/
└── agent_messages.proto
```

## 核心接口

- `IAgent` / `IAgent<TState>`
- `IActor`
- `IActorRuntime`
- `IEventPublisher`
- `IStream` / `IStreamProvider`
- `IConnector` / `IConnectorCatalog`
- `IStateStore<TState>` / `IEventStore`
- `IGAgentExecutionHook`
- `IActorRuntimeCallbackScheduler`

## 当前边界

Foundation 抽象层已经删除旧的动态事件模块契约。

现在的框架约束是：

- 事件处理走静态 handler
- 业务扩展走上层专用 typed seam
- 跨事件事实必须进入 actor state

## Proto

`agent_messages.proto` 定义公共传输消息，包括：

- `EventDirection`
- `EventEnvelope`
- `StateEvent`
- `ParentChangedEvent`
- `ChildAddedEvent`
- `ChildRemovedEvent`

## 设计原则

1. 纯契约
2. 最小依赖
3. 单一语义
4. 不为历史兼容保留空壳
