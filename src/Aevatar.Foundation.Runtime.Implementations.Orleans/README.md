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

## Kafka 入站投递与失败语义

Kafka 入站链路的阶段不可互换：

| 阶段 | 含义 | 是否允许推进 Kafka offset |
| --- | --- | --- |
| `consumed` | receiver 已从 Kafka poll 到 record，并登记 inflight offset | 否 |
| `delivered-to-observer` | Orleans persistent stream 已调用并 await subscriber `OnNextAsync` | 否 |
| `handler-succeeded` | `RuntimeActorGrain` 已完成路由和 agent handler | 否；还需 Orleans 回调 receiver |
| `handler-retry-scheduled` | handler 失败，但新的 typed retry envelope 已成功写入 stream 或 durable callback scheduler | 原 record 的 observer 可正常返回，之后才可进入 ACK；retry envelope 使用独立 attempt identity |
| `handler-terminally-failed` | retry disabled/exhausted，或 actor 无法处理 envelope | 由 `failure_disposition` 决定，不能称为 handler success 或 transport ACK |
| `messages-delivered` | Orleans 确认该 batch 已成功交付给全部目标 consumer，并调用 `MessagesDeliveredAsync(...)` | receiver 只把对应 offset 标为 acknowledged |
| `offset-committed` | receiver 已将连续 acknowledged watermark 提交给 Kafka | 是；只有这一阶段影响 rebalance/crash 后的 Kafka 起点 |

`RuntimeActorGrain` 的默认 terminal-failure policy 是 bounded failure，而不是无限 poison retry：

- handler retry 成功排程时，原 delivery 返回；后续 attempt 使用递增的 retry attempt 和稳定 operation identity。
- retry disabled/exhausted 时记录 error log，并增加
  `aevatar.runtime.envelope_terminal_failures_total{failure_reason,failure_disposition="returned"}`；
  observer 可以继续正常返回，但只有 Orleans 后续调用 `MessagesDeliveredAsync(...)` 才使原 offset 可被 receiver
  确认。这是显式 terminal failure，不是成功处理或已经 ACK。
- envelope 明确设置 `Runtime.Dispatch.PropagateFailure=true` 时，terminal exception 会穿透 observer，指标使用
  `failure_disposition="propagated"`，`MessagesDeliveredAsync(...)` 不应发生，Kafka offset 保持可重投。
  该选项只适用于调用方能够修复或隔离 poison payload 的受控链路，不是默认策略。
- runtime 不做 pre-handler process-local duplicate filtering。每次 provider redelivery 都进入 authoritative actor，
  因而 handler failure、进程重启和跨节点重投不会被本地 cache entry 抑制。业务正确性必须由 actor committed
  state、稳定 command/operation identity 或外部权威 idempotency contract 保证。
- envelope 的稳定 operation id（当前由 typed `Runtime.Deduplication.OperationId` wire contract 承载）与
  retry attempt 共同描述 delivery identity；该 identity 不记录完成事实，也不提供 exactly-once。没有证据证明
  本地短窗 completed-envelope filter 有足够减载价值，因此
  #3145 选择删除而不是保留新的 performance-only seam。

其他失败边界：

- Kafka record 无法反序列化为 `EventEnvelope` 时，receiver 记录
  `failure_reason="invalid_envelope"`、`failure_disposition="returned"`，跳过 payload；receiver 随后将该 record
  标记为可提交，但只有连续水位实际 commit 后才是 `offset-committed`。
- actor identity 缺失或初始化失败时，记录 `failure_reason="actor_unavailable"`。默认 disposition 为
  `returned`；显式 `PropagateFailure` 时异常穿透并保留重投能力。
- handler 成功后、offset commit 前进程退出，或 commit 前发生 rebalance，Kafka 可以重投。重投会再次进入
  handler；业务 side effect 必须使用 actor-owned committed state 或下游权威幂等键。
- offset 已 commit 后进程退出不会触发 Kafka 重投；业务完成事实必须由 actor committed event 表达，不能从
  offset commit 推断。

当前仓库没有通用 durable poison-envelope owner、quarantine store 或 DLQ。Operator 应对上述 terminal-failure
counter 告警，并用结构化日志中的 actor/envelope/type 定位根因；只有在 payload 仍可从 Kafka retention 或上游
事实重新生成时，才能在修复后执行显式 offset replay 或重新发布。通用 durable quarantine、授权 replay 和
retention/cleanup ownership 必须作为独立后续设计完成，不能由进程内字典或无限 partition retry 代替。
