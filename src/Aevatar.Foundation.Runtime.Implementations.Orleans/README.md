# Aevatar.Foundation.Runtime.Implementations.Orleans

`Aevatar.Foundation.Runtime.Implementations.Orleans` 提供 `IActorRuntime` 的 Orleans 并行实现，保持 Foundation 分层不变：

- `Aevatar.Foundation.Abstractions`：抽象契约（`IActorRuntime/IActor`）。
- `Aevatar.Foundation.Runtime.Implementations.Orleans`：Orleans 基础设施实现（Grain + Runtime 适配）。
- `Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming`：Orleans Stream 适配与拓扑注册能力。
- `Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.MassTransit`：Orleans MassTransit QueueAdapter（仅 Orleans 流后端适配）。
- `Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit`：MassTransit `IStream` 实现。
- `Aevatar.Foundation.Runtime.Transport.Implementations.MassTransitKafka`：MassTransit 的 Kafka 传输实现。
- `Aevatar.Foundation.Runtime.Hosting`：通过 provider 进行装配选择。

## 核心组成

- `Actors/OrleansActorRuntime`：`IActorRuntime` 的 Orleans 实现。
- `Actors/OrleansActor`：客户端侧 `IActor` 代理。
- `Grains/RuntimeActorGrain`：实际承载 `IAgent` 的 Orleans Grain。
- `DependencyInjection/ServiceCollectionExtensions`：DI 注册入口。

## 当前语义边界

- Orleans 模式下 `IActor.Agent` 返回的是远程代理（`IAgent`），不保证可向下转型为具体 `GAgent` 实现。
- 依赖 `actor.Agent is SomeConcreteAgent` 的调用路径仍建议使用默认 `InMemory` provider。

## 使用方式

在宿主层先完成 Orleans `IGrainFactory` 注册，再调用：

```csharp
services.AddAevatarFoundationRuntimeOrleans();
```

或在 Silo：

```csharp
siloBuilder.AddAevatarFoundationRuntimeOrleans();
```

默认 stream backend 为 `InMemory`。

## Grain 持久化后端

Orleans grain state 支持两种后端：

- `InMemory`（默认）：`AevatarOrleansRuntimeOptions.PersistenceBackend = InMemory`
- `Garnet`：`AevatarOrleansRuntimeOptions.PersistenceBackend = Garnet`，并设置 `GarnetConnectionString`

示例：

```csharp
siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
{
    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
    options.GarnetConnectionString = "localhost:6379";
});
```

当持久化后端选择 `Garnet` 时，Event Sourcing 的 `IEventStore` 也会自动切换为 `GarnetEventStore`（不再使用 `InMemoryEventStore`），确保重启后可依赖事件流恢复。

## MassTransitAdapter 启用方式

当需要 Orleans Stream 通过 MassTransit 传输时，显式启用 MassTransit 适配扩展，并选择 Kafka 作为传输实现：

```csharp
services.AddAevatarFoundationRuntimeMassTransitKafkaTransport();
services.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();

siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
{
    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendMassTransitAdapter;
});
siloBuilder.AddAevatarFoundationRuntimeOrleansMassTransitAdapter();
```

这样 Orleans 核心不直接耦合 Kafka 适配实现，消息交换与转发由 Stream/Kafka 层完成。
