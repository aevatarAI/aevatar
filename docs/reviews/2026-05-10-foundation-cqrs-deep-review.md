---
title: Foundation / CQRS Deep Review
status: active
date: 2026-05-10
scope: U01-U05
---

# 2026-05-10 Foundation / CQRS Deep Review

本轮范围：

- U01 · `Foundation.Abstractions`：契约层。
- U02 · `Foundation.Core`：`GAgentBase` / Pipeline / Hook / RunManager / `StateGuard`。
- U03 · `Foundation.Runtime` + `Implementations.Local`：本地 actor 模型。
- U04 · `Foundation.Runtime.Implementations.Orleans` + Streaming + Kafka + Garnet：生产运行时。
- U05 · `CQRS.Core` / `Projection.Core` / `Projection.Runtime` / `Stores.Abstractions`：读写分离与投影骨架。

## 总体判断

旧审计里对投影链路的低分仍然基本成立。代码已经有明显改善：`EventEnvelope` 已把 retry / callback / forwarding / dispatch control 放进强类型 proto 字段，runtime 与 dispatch port 也有了分离；Projection Scope 已 actor 化，不再只是中间层回调。但是 U05 仍存在一个直接违反架构规则的 P0：`EventStoreProjectionScopeWatermarkQueryPort` 在 query port 中读取 `IEventStore` 并 replay 投影 scope 事件。

另外，U01/U02 的公共契约仍把 write-side actor 实例和可变 protobuf state 暴露给上层。这个问题会不断诱导上层绕过 read model，也会让 Local runtime 的测试习惯掩盖 Orleans/分布式语义差异。

## 发现与方案

### F01 · P0 · U05 · Watermark QueryPort 在查询路径 replay event store

证据：

- [`EventStoreProjectionScopeWatermarkQueryPort.cs:9`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventStoreProjectionScopeWatermarkQueryPort.cs:9) 直接依赖 `IEventStore`。
- [`EventStoreProjectionScopeWatermarkQueryPort.cs:20`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventStoreProjectionScopeWatermarkQueryPort.cs:20) 用 scope actor id 读取事件流。
- [`EventStoreProjectionScopeWatermarkQueryPort.cs:25`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventStoreProjectionScopeWatermarkQueryPort.cs:25) 在 query 方法内 new `ProjectionScopeState` 并逐条 apply。
- [`ChannelRuntimeServiceCollectionExtensions.cs:56`](../../agents/Aevatar.GAgents.Channel.Runtime/DependencyInjection/ChannelRuntimeServiceCollectionExtensions.cs:56) 把这个实现注册为 `IProjectionScopeWatermarkQueryPort`。
- [`ChannelRuntimeTombstoneCompactor.cs:54`](../../agents/Aevatar.GAgents.Channel.Runtime/ChannelRuntimeTombstoneCompactor.cs:54) 在 compactor 运行时读取这个 query port 得到 safe version。

问题：

这正是“禁止 query-time replay”的典型形态：QueryPort 直接读 `IEventStore`，在请求调用栈里重建 state。即使调用方是 tombstone compactor，不是 HTTP query，本质仍然是 read path 临时回放事实。它绕过正式 read model/materialization，未来事件流变大后也会把 compaction 变成 O(n) 回放。

解决方案：

- 新增 `ProjectionScopeWatermarkDocument`，字段至少包含 `scope_id/root_actor_id/projection_kind/mode/session_id/last_successful_version/state_version/released/updated_at_utc`。
- Projection scope actor 提交 `ProjectionScopeWatermarkAdvancedEvent` 后，走同一套 Projection Pipeline 物化 watermark read model。
- `IProjectionScopeWatermarkQueryPort` 改为读 `IProjectionDocumentReader<ProjectionScopeWatermarkDocument, string>`。
- `EventStoreProjectionScopeWatermarkQueryPort` 删除；对应测试从“replay events”改成“read model 查询语义”。

### F02 · P0 · U02/U05 · 批量提交时 `StateEvent.Version` 与 `StateRoot` 不对齐

证据：

- [`GAgentBase.TState.cs:160`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:160) 支持一次 `PersistDomainEventsAsync(IEnumerable<IMessage>)`。
- [`GAgentBase.TState.cs:177`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:177) 先把整批事件提交到 event store。
- [`GAgentBase.TState.cs:179`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:179) 再把整批事件全部 apply 到 `_state`。
- [`GAgentBase.TState.cs:235`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:235) 对 `commitResult.CommittedEvents` 逐条发布 `CommittedStateEventPublished`。
- [`GAgentBase.TState.cs:241`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:241) 每一条 committed event 都带同一个最终 `_state`。
- [`ScriptBehaviorGAgent.cs:184`](../../src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs:184) 真实生产路径会批量 persist facts。
- [`WorkflowRunGAgent.cs:91`](../../src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs:91) workflow 也把批量 persist 暴露给 orchestrator。

问题：

如果一批事件提交出版本 11、12，当前发布两条 `CommittedStateEventPublished`：第一条的 `StateEvent.Version=11`，但 `StateRoot` 已经是 version 12 的最终状态。Projection materializer 通常用 `StateEvent.Version` 作为 read model `StateVersion`，但字段值来自 `StateRoot`。这会造成“版本 11 的 read model 内容实际是版本 12”的语义错位。

解决方案：

- 最干净的修法：批量提交后按事件顺序逐步 apply，每 apply 一个事件就立即发布该版本对应的 state root。
- 如果必须保持一次 event-store commit，也要生成 `List<(StateEvent, StateRootAtThatVersion)>`，不能用最终 `_state` 复用给每个事件。
- 补一个基础测试：一个 agent 一次 persist 两个增量事件，断言两条 `CommittedStateEventPublished` 的 state root 分别是中间态和最终态。

### F03 · P0 · U01/U05 · 公共契约暴露 write-side actor 与可变 state

证据：

- [`IActor.cs:17`](../../src/Aevatar.Foundation.Abstractions/IActor.cs:17) 暴露 `IAgent Agent`。
- [`IAgent.cs:38`](../../src/Aevatar.Foundation.Abstractions/IAgent.cs:38) / [`IAgent.cs:41`](../../src/Aevatar.Foundation.Abstractions/IAgent.cs:41) 暴露 `IAgent<TState>.State`。
- [`ICommandDispatchTarget.cs:10`](../../src/Aevatar.CQRS.Core.Abstractions/Commands/ICommandDispatchTarget.cs:10) 的 `IActorCommandDispatchTarget` 继续要求 `IActor Actor`。
- [`WorkflowRunCommandTarget.cs:40`](../../src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs:40)、[`GAgentDraftRunInteraction.cs:36`](../../src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentDraftRunInteraction.cs:36)、[`ScriptingActorCommandTarget.cs:13`](../../src/Aevatar.Scripting.Infrastructure/Ports/ScriptingActorCommandTarget.cs:13) 都把 actor 实例挂到 command target 上。
- [`GAgentDraftRunInteraction.cs:195`](../../src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentDraftRunInteraction.cs:195) 用 `actor.Agent.GetType()` 判断运行时类型。

问题：

这让上层很容易把 runtime 对象形态当成业务事实，也让 Application/Infrastructure 有机会直接读写 actor 内部状态。Orleans README 已提醒 `IActor.Agent` 在 Orleans 下只是远程代理，不能按 concrete type 判断；但契约本身仍鼓励这种用法。`IAgent<TState>.State` 返回的是可变 protobuf 对象，外部拿到引用后可以改 nested fields，`StateGuard` 也拦不住。

解决方案：

- `IActorCommandDispatchTarget` 改成只保留 `TargetId`，删除 `IActor Actor`。
- `IActor` 的 `Agent` 至少降为 runtime-internal；公开 surface 只保留 lifecycle/topology/dispatch 必需能力。
- `IAgent<TState>.State` 不作为公共契约暴露给上层。测试如需断言状态，应通过 read model 或 runtime 专用 test probe。
- 类型校验统一走 `IAgentTypeVerifier` / kind registry，禁止 fallback 到 `actor.Agent.GetType()`。

### F04 · P0 · U03 · Local runtime 会返回半激活 actor

证据：

- [`LocalActorRuntime.cs:57`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs:57) 计算 actor id。
- [`LocalActorRuntime.cs:58`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs:58) 如果 `_actors` 已有条目直接返回。
- [`LocalActorRuntime.cs:89`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs:89) 在 actor activate 前先放入 `_actors`。
- [`LocalActorRuntime.cs:109`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs:109) 之后才调用 `actor.ActivateAsync(ct)`。
- [`LocalActor.cs:46`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs:46) actor activate 又先订阅 self stream，再 [`LocalActor.cs:104`](../../src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs:104) 激活 agent。

问题：

两个并发 `CreateAsync(id)` 或一个 `CreateAsync(id)` 与一个 `GetAsync(id)` 交错时，第二个调用可能拿到已经进 `_actors` 但 agent 尚未 replay/activate 完成的 actor。随后 `DispatchAsync` 可以进入 mailbox 调 `Agent.HandleEventAsync`，让事件处理跑在状态恢复之前。本地 runtime 是测试基线，这个竞态会让测试结果不稳定，也会与 Orleans 的激活 turn 语义偏离。

解决方案：

- `_actors` 不要直接存 `LocalActor`，改存 activation record：`ConcurrentDictionary<string, Lazy<Task<LocalActor>>>` 或显式 `ActivationLease`。
- 只有 `ActivateAsync` 成功后，`CreateAsync/GetAsync/DispatchAsync` 才返回可处理消息的 actor。
- `LocalActor.ActivateAsync` 把 agent activation 纳入 mailbox gate，或者在 `_activated` 完成前拒绝/排队所有 envelope。
- 增加并发测试：并发 `CreateAsync` 同 id，第二个 await 返回前必须保证 `Agent.ActivateAsync` 完成。

### F05 · P1 · U02 · `StateGuard` 不能真正保护 protobuf state

证据：

- [`StateGuard.cs:14`](../../src/Aevatar.Foundation.Core/StateGuard.cs:14) 用 `AsyncLocal<bool>` 表示当前 scope 是否可写。
- [`GAgentBase.TState.cs:27`](../../src/Aevatar.Foundation.Core/GAgentBase.TState.cs:27) `State` 的 setter 才调用 `StateGuard.EnsureWritable()`。
- `TState` 是 protobuf message，可变字段在 getter 返回后可直接修改 nested property，不会经过 setter。

问题：

这层 guard 只能防止替换整个 `State` 引用，拦不住 `State.Foo = ...` 或 `State.Items.Add(...)`。而且 `AsyncLocal` 会随 `ExecutionContext` 流入 `Task.Run`，如果 handler 在 writable scope 内启动后台任务，后台任务可能继承 `Writable=true`。这不符合“Actor/模块运行态只能在事件处理主线程修改”的要求。

解决方案：

- 把 state mutation 收敛到 `PersistDomainEventAsync` / `TransitionState`，不要把 mutable state 作为可写对象暴露。
- `State` getter 返回 clone 或只读 snapshot；事件处理内部需要 mutable state 时走 internal accessor。
- `StateGuard` 从 bool 改为 turn lease：`AsyncLocal<TurnWriteLease>` 只传递 lease id，`Dispose` 后 lease 失效；后台任务即使捕获上下文也会看到 stale lease。
- 增加测试覆盖 nested field mutation 和 `Task.Run` 捕获 writable scope 的场景。

### F06 · P1 · U04 · Kafka provider 会伪造缺语义的 `EventEnvelope`

证据：

- [`KafkaProviderQueueAdapter.cs:53`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapter.cs:53) 对 stream event 做 switch。
- [`KafkaProviderQueueAdapter.cs:55`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapter.cs:55) 如果本来就是 `EventEnvelope` 就透传。
- [`KafkaProviderQueueAdapter.cs:56`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapter.cs:56) 如果是任意 `IMessage`，则 new 一个 `EventEnvelope`。
- [`KafkaProviderQueueAdapter.cs:61`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapter.cs:61) 新 envelope 的 publisher actor id 是空字符串，默认 audience 是 Children。

问题：

Kafka provider 是生产运行时的一部分，不能在传输层把任意 protobuf 消息补成一个没有 publisher、没有真实 route、没有 propagation 的业务 envelope。这样会把“调用方用错 stream API”伪装成合法事件，破坏 Envelope 语义。

解决方案：

- Kafka provider 只接受 `EventEnvelope`；遇到非 envelope 直接抛异常并记录 provider 名、stream id、event type。
- 如果确实需要边界适配，适配必须在上游 command/envelope factory 完成，不能在 transport provider 内猜 route。
- 增加测试：`QueueMessageBatchAsync` 收到非 `EventEnvelope` 时失败，而不是发布空 publisher envelope。

### F07 · P1 · U04 · Kafka receiver 缺少有界背压

证据：

- [`KafkaProviderQueueAdapterReceiver.cs:19`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapterReceiver.cs:19) 使用无界 `ConcurrentQueue<IBatchContainer>`。
- [`KafkaProviderQueueAdapterReceiver.cs:23`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapterReceiver.cs:23) / [`KafkaProviderQueueAdapterReceiver.cs:24`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapterReceiver.cs:24) 用内存集合追踪 inflight/acked offsets。
- [`KafkaProviderQueueAdapterReceiver.cs:180`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapterReceiver.cs:180) consume loop 持续 enqueue，没有容量上限。

问题：

下游 Orleans consumer 变慢时，receiver 会继续拉 Kafka 并堆到进程内内存。offset 提交又依赖 ack 集合连续推进，慢消息会让 inflight 集合持续增长。这个问题不是 actor 事实源违规，但对生产运行时是实打实的可用性风险。

解决方案：

- 用 bounded channel 取代无界 queue，容量来自 `QueueCacheSize` 或 Kafka provider options。
- 达到高水位时 pause 当前 partition；低水位 resume。
- 暴露 metrics：queued count、inflight offsets、commit lag、oldest inflight age。
- `Shutdown` 时不要无限等待 consume loop；给 Kafka close/commit 单独 timeout。

### F08 · P1 · U05 · Projection failure state 持久化完整 `EventEnvelope`

证据：

- [`projection_scope_messages.proto:28`](../../src/Aevatar.CQRS.Projection.Core/projection_scope_messages.proto:28) 定义 `ProjectionScopeFailure`。
- [`projection_scope_messages.proto:35`](../../src/Aevatar.CQRS.Projection.Core/projection_scope_messages.proto:35) failure state 内包含完整 `aevatar.EventEnvelope envelope`。
- [`projection_scope_messages.proto:81`](../../src/Aevatar.CQRS.Projection.Core/projection_scope_messages.proto:81) failure event 也包含完整 envelope。
- [`ProjectionScopeStateApplier.cs:63`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeStateApplier.cs:63) apply failure 时 clone envelope 写入 actor state。
- [`ProjectionFailureRetentionPolicy.cs:7`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionFailureRetentionPolicy.cs:7) 最多保留 64 条。

问题：

`CommittedStateEventPublished` 的 envelope 可能包含很大的 `state_root`。一旦 projection materializer 失败，scope actor 的 event store 和 state 会复制完整 envelope。64 条失败在大状态场景下会迅速膨胀 projection actor state。它是为了 replay，但 replay payload 不一定应该内联在 actor current state 中。

解决方案：

- `ProjectionScopeFailure` 只保留 failure id、event id、type url、source version、reason、payload hash 和 dead-letter 引用。
- 完整 envelope 写到专门的 projection failure store / blob store，使用 Protobuf bytes，按 TTL/retention 清理。
- Replay 时通过 failure id 读取 dead-letter payload；读取失败时把失败标成不可重放。

### F09 · P1 · U05 · 旧审计中的 live sink 进程内注册表仍未完全收口

证据：

- [`EventSinkProjectionLifecyclePortBase.cs:22`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:22) 注释说明 sink subscriptions 是 process-local transient I/O handles。
- [`EventSinkProjectionLifecyclePortBase.cs:23`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:23) 仍有 `ConcurrentDictionary<object, IAsyncDisposable>`。
- [`EventSinkProjectionLifecyclePortBase.cs:69`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:69) attach 时订阅 session event hub。
- [`EventSinkProjectionLifecyclePortBase.cs:93`](../../src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:93) detach 时按 sink object remove。

判断：

旧审计把它直接归为“中间层事实状态”有点过重。当前 key 是 sink object，不是 actorId/sessionId -> context，且注释明确限定为 I/O handle，所以它不是业务事实源。但它仍然把 live session 的释放正确性绑定到调用方一定执行 detach。HTTP/SSE 断线、后台 detached drain 失败、进程异常退出时，projection scope 的 release 只能靠后续 cleanup 兜底。

解决方案：

- live sink attach 返回显式 subscription id/lease id，而不是用 sink object identity 作为唯一句柄。
- session event hub 侧支持 lease TTL 或 heartbeat，到期自动 dispose。
- `ReleaseActorProjectionAsync` 应能按 lease 清理所有关联 subscription，而不是要求调用方先 detach 每个 sink。

### F10 · P1 · U05 · Detached command completion 依赖进程内后台任务

证据：

- [`DefaultDetachedCommandDispatchService.cs:19`](../../src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:19) 用 `_inflightCount` 追踪后台 drain。
- [`DefaultDetachedCommandDispatchService.cs:20`](../../src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:20) 用进程内 TCS 表达 drain 完成。
- [`DefaultDetachedCommandDispatchService.cs:51`](../../src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:51) dispatch 后启动 detached drain。
- [`DefaultDetachedCommandDispatchService.cs:78`](../../src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:78) 通过 `Task.Run` 跑 `DrainAsync`。
- [`DefaultDetachedCommandDispatchService.cs:165`](../../src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:165) drain finally 内负责 release/cleanup。

问题：

这个设计适合“accepted-only + 尽量清理”的体验，但它仍把 projection lease cleanup 绑在当前进程的后台任务上。进程重启时，后台 drain 消失，live sink 和 projection session release 的确定性不足。它也把“命令已 accepted”与“后台观察完成/清理完成”放进同一个服务类，边界偏厚。

解决方案：

- accepted-only 路径只返回 receipt，不启动进程内 drain；完成态由 projection session / read model / notification pipeline 驱动。
- 如果确实需要 cleanup，建模成 actor-owned session lease：dispatch 后由 projection scope/session actor 根据 terminal event 或 TTL 自己释放。
- `DefaultDetachedCommandDispatchService` 保留为测试/开发便利实现时，命名应体现 best-effort，不应作为生产主链默认。

### F11 · P2 · U04 · Kafka topic 创建默认 `ReplicationFactor = 1`

证据：

- [`KafkaProviderProducer.cs:134`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Transport/KafkaProviderProducer.cs:134) 自动 create topic。
- [`KafkaProviderProducer.cs:140`](../../src/Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Transport/KafkaProviderProducer.cs:140) `ReplicationFactor = 1` 写死。

问题：

U04 是生产运行时，默认创建 RF=1 的 topic 会给主链路事件传输带来单副本风险。即使运维通常会预创建 topic，代码里的自动创建路径也不应该把低可靠配置固化成默认。

解决方案：

- `KafkaProviderTransportOptions` 增加 `TopicReplicationFactor`、`AllowTopicAutoCreate`。
- 生产默认禁用自动创建；开发环境显式开启。
- 启动时只校验 topic 分区数、配置和 producer/consumer 能力，不在生产路径隐式建 topic。

### F12 · P2 · U02 · `RunManager` 只剩 README 口径，没有实现

证据：

- [`Aevatar.Foundation.Core/README.md:19`](../../src/Aevatar.Foundation.Core/README.md:19) 仍把 `RunManager` 列为核心类型。
- 本轮在 `src/Aevatar.Foundation.Core`、`src/Aevatar.Foundation.Abstractions`、`src/Aevatar.Foundation.Runtime`、`test` 中搜索 `RunManager`，除 README 外没有实现或测试引用。

问题：

U02 的 review 清单把 `RunManager` 与 `GAgentBase` / Pipeline / Hook / `StateGuard` 并列，说明它被视为核心语义。但当前代码没有对应实现，README 还保留“latest-wins 的运行上下文管理”的描述。这会让后续设计误以为 Foundation.Core 仍提供 run lifecycle 能力，也会掩盖实际 run/session 语义已经散落到 workflow、CQRS interaction、projection session 等路径。

解决方案：

- 如果 `RunManager` 已被删除：同步删掉 README 中的核心类型条目，并在架构文档里明确 run/session 语义归属。
- 如果能力仍需要：不要恢复一个进程内 manager；应建模成 actor-owned run/session state 或 projection session lease。
- 增加一个文档一致性 guard：README 列出的核心类型必须能在项目内解析到真实类型，或显式标注为历史删除项。

## 分单元结论

| 单元 | 当前判断 | 说明 |
|---|---:|---|
| U01 Foundation.Abstractions | 6/10 | `EventEnvelope` 强类型化方向正确；但 `IActor.Agent` / `IAgent<TState>.State` 仍是最大契约泄漏。 |
| U02 Foundation.Core | 5/10 | event sourcing 主干存在，但批量 committed observation、`StateGuard` 和 `RunManager` 文档漂移都有语义问题。 |
| U03 Foundation.Runtime + Local | 5/10 | 本地 runtime 作为测试基线可用，但激活竞态会制造半激活 actor。 |
| U04 Orleans + Streaming + Kafka + Garnet | 6/10 | Orleans/Garnet 的 Protobuf 与强后端约束是正向进展；Kafka provider 仍缺严格 envelope 与背压边界。 |
| U05 CQRS/Projection | 4/10 | Projection Scope actor 化是进展；query-time replay、live sink 本地句柄、detached drain 说明读写分离骨架还没完全收口。 |

## 旧 review 有效性复核

- “投影一致性 4/10”：仍有效。旧问题中的 Host 直订阅是否已全部收敛，本轮没有复查全量 Host；但 U05 内部发现了 query-time replay 和本地 live sink 句柄，足以维持低分。
- “EventSinkProjectionLifecyclePortBase 的 ConcurrentDictionary 是投影端口违规”：需要修正表述。它不是 actorId -> context 的事实源，更像 process-local I/O handle registry；但 session 生命周期仍有泄漏风险，所以从 P0 降为 P1。
- “读写分离 Application 层 CLEAN”：旧结论只看 Application query services 时可能成立；但当前 `IProjectionScopeWatermarkQueryPort` 已在 Core/Channel runtime compaction 路径中形成 query-time replay，不能再把系统整体评价为 CLEAN。
- “序列化统一 Protobuf”：U01-U05 主链路基本成立。Garnet event store 使用 `StateEvent.ToByteArray()` / `StateEvent.Parser.ParseFrom`，Kafka 传输也是 `EventEnvelope` bytes；本轮未发现 U01-U05 内部 JSON/XML 持久化。

## 本轮审阅命令摘录

- `rg` 扫描目标目录中的 `Metadata` / `Dictionary` / `ConcurrentDictionary` / `Task.Run` / `Task.Delay` / blocking await 模式。
- `rg` 扫描目标目录中的 `IEventStore` / `Replay` / `StateVersion` / `EventEnvelope` / `SubscribeAsync`。
- 阅读旧审计：`docs/audit-scorecard/2026-04-08-architecture-audit-detailed.md`、`docs/audit-scorecard/2026-04-27-daily-pipeline-architecture-review.md`。

未运行 build/test。本次只新增 review 文档，没有修改生产代码。
