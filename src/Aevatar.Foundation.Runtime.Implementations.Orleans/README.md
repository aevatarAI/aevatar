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
    options.ReceiverBufferCapacity = 1024;
    options.ReceiverBufferHighWatermark = 768;
    options.ReceiverBufferLowWatermark = 512;
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
- `ConsumerGroup` 提供 committed-offset 与 lag 的 Kafka namespace，不接管 Orleans queue ownership
- `MessagesDeliveredAsync(...)` 之后才推进 Kafka offset commit

每个 queue receiver 使用固定容量 buffer，并强制校验：

```text
0 < ReceiverBufferLowWatermark
  < ReceiverBufferHighWatermark
  <= ReceiverBufferCapacity
```

Orleans Persistent Streams queue balancer 是 receiver ownership 的唯一来源。每个
`IQueueAdapterReceiver` 在一个生命周期内通过 `KafkaQueuePartitionMapper` 将 `QueueId` 一次性映射到
固定 partition，并使用手动 `Assign`；它不使用 Kafka topic subscription，也不把 Kafka group rebalance
作为第二套 queue ownership。

达到 high watermark 后，consumer owner loop 对这个固定 partition 执行 `Pause`；只有 depth
下降到 low watermark 或以下才执行 `Resume`，中间区间保持原状态，避免 pause/resume 抖动。
pause 期间 owner loop 仍每 100 ms 调用 `Consume(timeout)`，让 librdkafka 推进 broker/protocol 处理；
这不表示手动 assignment consumer 参与 Kafka group heartbeat/rebalance。consumer 的 create、assign、
pause/resume、consume、commit、seek、close/dispose 都只在这个 owner loop/thread 执行。Orleans 释放并
重新获取同一个 QueueId 时，旧 consumer 完整 shutdown，新 consumer 重新 assign 同一个固定 partition。
非 fatal `ConsumeException` 会记录 consume-error metric，并在同一个 owner loop 上退避重试；fatal
`ConsumeException` 会终止 owner loop，且 `GetQueueMessagesAsync`、`MessagesDeliveredAsync` 和
`Shutdown` 都传播同一个 lifecycle fault。每一代 lifecycle 独立拥有 CTS、initialize task、owner-loop
task、shutdown task 和 fault。同一代的重复或并发 `Initialize` / `Shutdown` 分别共享同一个 task；shutdown
先发布 cleanup task，再取消并等待 in-flight initialize，最后停止 owner loop。shutdown 期间发起的
initialize 只发布一代 successor，并等待 predecessor cleanup 完成后才进入 transport-ready。旧代 continuation
即使已经把 owner-loop delegate 排入 scheduler，也必须在 owner-loop thread 上、lifecycle lock 内重新核对
cancellation 和 generation identity，之后才能 create/Assign consumer，因此不会在 shutdown 后越界启动旧代。

buffer 只是进程内 transport working state，不是事实源。它不改变 ACK 契约：offset 仍然只在
`MessagesDeliveredAsync(...)` 标记 acknowledged 后，按连续 watermark commit；低水位恢复不能
越过尚未 ACK 的 offset hole。

### Buffer 基准

普通 `KafkaReceiverBackpressureTests` 只硬断言 fixed capacity、并发传递无丢失、零 rejected write、
offset checksum 和 pause/resume 等确定性语义，不使用 wall-clock 阈值作为 CI 门禁。receiver-shape
性能对照保留为显式 opt-in 的 controlled diagnostic：旧无界 queue 与新有界 buffer 执行相同的 fake
`Consume`、routing header 解码、Protobuf parse、`StreamId` / batch 构造和 puller drain，只替换
buffer 实现；独立进程做 3 次 warmup、9 轮交错原始采样并输出中位数，但不据此判定单次测试 pass/fail。
诊断使用独立的 10 分钟默认 watchdog，而不是单元测试共享的 5 秒 timeout；慢速或受限机器可通过
`AEVATAR_KAFKA_RECEIVER_PERFORMANCE_WATCHDOG_SECONDS` 显式放宽，watchdog 只用于发现挂死。
2026-08-02 在 .NET 10 Debug、本地 arm64 的一次受控运行中，中位数为：

| 实现 | receiver-shape msg/s | CPU us/msg | allocation B/msg |
| --- | ---: | ---: | ---: |
| 旧 `ConcurrentQueue`（无界） | 284,001 | 1.15 | 972.2 |
| 新 fixed-capacity SPSC buffer | 426,349 | 1.16 | 968.0 |

本次中位数比值为 150.1%，但 9 个单样本比值分布在 45.9% 到 313.4%，明确说明本地 wall-clock
比较不具备稳定 CI 判定能力。诊断仍硬校验两条路径均零 rejected write 且 offset checksum 相同；
吞吐、CPU 和 allocation 只作为原始观测。纯 buffer 热路径也只输出诊断，不再保留 500,000 pairs/s
绝对门槛。

同一次 controlled diagnostic 的 overload 曲线中，无界 queue 在 backlog 32,768 时 retained 32,768 条并新增分配 1,052,032 B；
capacity 1,024 的新 buffer retained 1,024 条且预分配后 overload 新增分配为 0。完整方法、重复
运行、纯 buffer 诊断和 `256 / 1,024 / 4,096 / 16,384 / 32,768` backlog 曲线见
[`docs/raw/2026-08-02-kafka-receiver-backpressure-benchmark.md`](../../docs/raw/2026-08-02-kafka-receiver-backpressure-benchmark.md)。
回归测试另以 capacity 64 用独立 owner/puller 线程传递 50,000 条消息，验证无丢失且观测 depth
不超过 capacity。

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
| `offset-committed` | receiver 已将连续 acknowledged watermark 提交给 Kafka | 是；只有这一阶段影响 receiver restart、ownership handoff 或 crash 后的 Kafka 起点 |

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
- envelope 的稳定 operation id（当前由 typed `Runtime.DeliveryIdentity.OperationId` wire contract 承载）与
  retry attempt 共同描述 delivery identity；该 identity 不记录完成事实，也不提供 exactly-once。没有证据证明
  本地短窗 completed-envelope filter 有足够减载价值，因此
  #3145 选择删除而不是保留新的 performance-only seam。

其他失败边界：

- Kafka record 无法反序列化为 `EventEnvelope` 时，receiver 记录
  `failure_reason="invalid_envelope"`、`failure_disposition="returned"`，跳过 payload；receiver 随后将该 record
  标记为可提交，但只有连续水位实际 commit 后才是 `offset-committed`。
- actor identity 缺失或初始化失败时，记录 `failure_reason="actor_unavailable"`。默认 disposition 为
  `returned`；显式 `PropagateFailure` 时异常穿透并保留重投能力。
- handler 成功后、offset commit 前进程退出，或 commit 前发生 Orleans queue ownership handoff，Kafka 可以重投。重投会再次进入
  handler；业务 side effect 必须使用 actor-owned committed state 或下游权威幂等键。
- offset 已 commit 后进程退出不会触发 Kafka 重投；业务完成事实必须由 actor committed event 表达，不能从
  offset commit 推断。

当前仓库没有通用 durable poison-envelope owner、quarantine store 或 DLQ。Operator 应对上述 terminal-failure
counter 告警，并用结构化日志中的 actor/envelope/type 定位根因；只有在 payload 仍可从 Kafka retention 或上游
事实重新生成时，才能在修复后执行显式 offset replay 或重新发布。通用 durable quarantine、授权 replay 和
retention/cleanup ownership 必须作为独立后续设计完成，不能由进程内字典或无限 partition retry 代替。
