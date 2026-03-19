# Aevatar.Foundation.Runtime.Implementations.Orleans

`Aevatar.Foundation.Runtime.Implementations.Orleans` 提供 `IActorRuntime` 与 `IActorDispatchPort` 的 Orleans 并行实现，保持 Foundation 分层不变：

- `Aevatar.Foundation.Abstractions`：抽象契约（`IActorRuntime/IActorDispatchPort/IActor`）。
- `Aevatar.Foundation.Runtime.Implementations.Orleans`：Orleans 基础设施实现（Grain + Runtime 适配）。
- `Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming`：Orleans Stream 适配与拓扑注册能力。
- `Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider`：Orleans Kafka provider-native backend（QueueAdapter/Receiver 形态）。
- `Aevatar.Foundation.Runtime.Hosting`：通过 provider 进行装配选择。

## 核心组成

- `Actors/OrleansActorRuntime`：`IActorRuntime` 的 Orleans 实现。
- `Actors/OrleansActorDispatchPort`：`IActorDispatchPort` 的 Orleans 实现。
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

## KafkaProvider 启用方式

当需要 Orleans Stream 走 Kafka queue/partition 一一映射语义时，启用 `KafkaProvider`：

```csharp
services.AddAevatarFoundationRuntimeOrleansKafkaProviderTransport(options =>
{
    options.BootstrapServers = "localhost:9092";
    options.TopicName = "aevatar-orleans-kafka-provider";
    options.ConsumerGroup = "aevatar-orleans-kafka-provider";
    options.TopicPartitionCount = 8;
});

siloBuilder.AddAevatarFoundationRuntimeOrleans(options =>
{
    options.StreamBackend = AevatarOrleansRuntimeOptions.StreamBackendKafkaProvider;
    options.QueueCount = 8;
    options.PersistenceBackend = AevatarOrleansRuntimeOptions.PersistenceBackendGarnet;
});
```

这条路径现在应理解为：

- Orleans Persistent Streams 风格的 Kafka provider backend
- 不依赖 `MassTransit`
- `QueueId <-> PartitionId` 一一映射
- `MessagesDeliveredAsync(...)` 之后才推进 Kafka offset commit
