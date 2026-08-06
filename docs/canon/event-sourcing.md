---
title: "Event Sourcing 基线文档（2026-02-23）"
status: active
owner: eanzhao
---

# Event Sourcing 基线文档（2026-02-23）

## 1. 目标与范围
- 目标：统一 Aevatar 有状态 Actor 的写侧事实源，强制 `Command -> Domain Event -> Apply -> State`。
- 适用范围：`Aevatar.Foundation.Core`、`Aevatar.Foundation.Runtime`、`Aevatar.Foundation.Runtime.Implementations.Local`、`Aevatar.Foundation.Runtime.Implementations.Orleans`。
- 非目标：本文件不定义 ReadModel Provider 细节；统一要求与重构计划见 `docs/architecture/generic-event-sourcing-elasticsearch-readmodel-requirements.md`。
- 非目标：本文件不定义 Actor `EventEnvelope` 消息流的 transport 细节；运行时 envelope 流不是 Event Sourcing 事实源。

## 2. 当前强制语义
1. `EventStore` / `StateEvent` 是唯一业务事实源。
2. `GAgentBase<TState>` 不提供 `StateStore` 事实通道；恢复仅允许来自 EventStore Replay。
3. 领域事件必须由开发者显式构建并持久化，不允许在线自动反推事件。
4. 有状态 Actor 激活必须 Replay；停用必须 flush pending events。
5. ES 行为构造走静态泛型路径，不走 Runtime 反射注入。
6. 默认启用自动快照（可配置），并在快照成功后按版本裁剪历史事件流（可配置）。
7. committed-state observation 使用 Runtime-owned durable publication checkpoint；checkpoint 只描述投递进度，不进入业务 `TState` 或 read model。

## 2.1 与 Runtime 消息流的边界
1. Actor 之间通过 Stream 传递的是 `EventEnvelope`，这是 runtime message envelope。
2. `EventEnvelope` 可以承载 command-like request、signal、reply、timeout fired 或业务事件 payload。
3. 只有 Actor 在处理这些入站消息后显式调用 `PersistDomainEventAsync(...)` / `PersistDomainEventsAsync(...)` 持久化的领域事件，才会成为 `StateEvent` 并进入 `EventStore`。
4. 因此，`EventEnvelope` 流与 `StateEvent` 流不是同一层：前者是 transport/runtime 层，后者是事实/event-sourcing 层。

## 2.2 Actor Evolution Matrix

Actor 演进的统一判定树见 [actor-evolution.md](actor-evolution.md)。

Event Sourcing 侧只保留一个内聚结论：同一 actor、同一 identity、同一事实拥有者内的 state schema 演进使用 lazy state migration；事实拥有者变化的 split / merge / re-key / replace 不属于 EventStore replay 或 query-time rebuild 问题，必须走 projection-driven bootstrap 与新 owner 自提交事实。

## 3. 当前代码事实（权威路径）
- ES 行为契约：`src/Aevatar.Foundation.Core/EventSourcing/IEventSourcingBehavior.cs`
- ES 默认实现：`src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`
- 快照阈值策略：`src/Aevatar.Foundation.Core/EventSourcing/SnapshotStrategy.cs`
- 事件存储与裁剪契约：`src/Aevatar.Foundation.Abstractions/Persistence/IEventStore.cs`
- committed-state publication state：`src/Aevatar.Foundation.Abstractions/EventSourcing/committed_state_publication_state.proto`
- publication state 存储端口：`src/Aevatar.Foundation.Abstractions/Persistence/ICommittedStatePublicationStateStore.cs`
- 状态事件 applier 抽象：`src/Aevatar.Foundation.Core/EventSourcing/IStateEventApplier.cs`
- Typed applier 基类：`src/Aevatar.Foundation.Core/EventSourcing/StateEventApplierBase.cs`
- 状态事件匹配器：`src/Aevatar.Foundation.Core/EventSourcing/StateTransitionMatcher.cs`
- 有状态生命周期：`src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
- Runtime 停用钩子抽象：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHook.cs`
- Runtime 停用钩子分发器：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHookDispatcher.cs`
- Runtime 停用钩子分发实现：`src/Aevatar.Foundation.Runtime/Actor/ActorDeactivationHookDispatcher.cs`
- 本地持久化 EventStore：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`
- 生产持久化 EventStore（Garnet）：`src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetEventStore.cs`
- Local Runtime 注入边界：`src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs`
- Orleans Runtime 注入边界：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
- Orleans actor-owned publication state：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrainCommittedStatePublicationStateStore.cs`
- 防回退门禁：`tools/ci/architecture_guards.sh`

## 4. 生命周期语义（按当前实现）
### 4.1 Activate
- `GAgentBase<TState>.ActivateAsync` 先调用 `base.ActivateAsync` 恢复模块。
- 然后调用 `EnsureEventSourcingConfigured()`：
  - 若已设置 `EventSourcing`，直接使用。
  - 若未设置，则必须通过已绑定的 `IEventSourcingBehaviorFactory<TState>` 创建。
- 执行 `ReplayAsync(actorId)` 恢复 `State`，并从 durable publication checkpoint 后重建缺失的 `CommittedStateEventPublished`。
- 缺失事实按原始 `StateEvent.event_id + version` 依次补发并逐条推进 checkpoint；全部完成后才初始化业务模块并进入 `OnActivateAsync`。
- 缺失 committed version、snapshot 领先 checkpoint 或 checkpoint 冲突都会显式失败 activation，不允许跳过。

### 4.2 Deactivate
- `GAgentBase<TState>.DeactivateAsync` 顺序：
  - `OnDeactivateAsync`
  - `ConfirmEventsAsync`
  - apply + committed-state publish + durable checkpoint（仅当 flush 产生 committed events）
  - `PersistSnapshotAsync`
- 不再调用 `StateStore.SaveAsync` 写事实态。
- 正常领域事件提交完成并 apply 到 actor state 后，也会调用 `PersistSnapshotAsync`。因此持续活跃的 actor 达到快照阈值时即可快照与裁剪，不依赖停用。
- 停用阶段的 `PersistSnapshotAsync` 是同一策略的最后一次检查，不会绕过阈值重复保存同一版本。

### 4.3 Fail-Fast 条件
- 未预设 `EventSourcing` 且容器中无 `IEventStore`：激活失败（`InvalidOperationException`）。
- 持久化 `TState` 快照事件到事件流：提交失败（禁止快照冒充领域事件）。

## 5. 开发者实现规范
1. 命令处理代码必须显式构建领域事件：`RaiseEvent(domainEvent)`。
2. 即使命令入口是通过 `EventEnvelope` 抵达 Actor，也必须在 Actor 内显式构建并持久化领域事件。
3. 推荐直接使用 `PersistDomainEventAsync(...)` / `PersistDomainEventsAsync(...)` 完成“提交 + apply”。
4. 必须保证“可重放同态”：`Replay` 后状态与在线运行状态一致。
5. 推荐通过以下两种方式之一定义 `event -> state`：
   - 在 Agent 中重写 `TransitionState`
   - 通过 DI 注册 `IStateEventApplier<TState>`（复杂领域推荐）

示例（简化）：

```csharp
[EventHandler]
public async Task Handle(IncrementRequested evt)
{
    await PersistDomainEventAsync(new IncrementApplied { Amount = evt.Amount });
}
```

## 6. DI 与容器约定
- `AddAevatarRuntime()` 默认注册 `IEventStore -> InMemoryEventStore`（开发/测试）。
- `AddAevatarRuntime()` 默认注册 `IEventSourcingSnapshotStore<TState> -> InMemoryEventSourcingSnapshotStore<TState>`。
- `AddAevatarRuntime()` 默认注册 `ICommittedStatePublicationStateStore -> InMemoryCommittedStatePublicationStateStore`（仅开发/测试）。
- `AddAevatarRuntime()` 默认注册 `IActorDeactivationHookDispatcher -> ActorDeactivationHookDispatcher`（支持多 hook 顺序分发）。
- 可通过 `AddFileEventStore(...)` 将 `IEventStore` 切换为本地持久化实现：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`。
- 调用 `AddFileEventStore(...)` 时，snapshot 与 publication state store 同时切换到 file-backed Protobuf 实现。
- 可通过 `AddGarnetEventStore(...)` 使用生产持久化实现：`src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/DependencyInjection/ServiceCollectionExtensions.cs`。
- Orleans runtime 当 `PersistenceBackend=Garnet` 时，会自动装配 `IEventStore -> GarnetEventStore`（连接串复用 `GarnetConnectionString`），不再回退 `InMemoryEventStore`。
- 如需自定义 ES 行为，可直接为 Agent 预设 `EventSourcing`，但必须保持相同语义契约。
- 如需解耦 Agent 里的 `TransitionState` 逻辑，可注册多个 `IStateEventApplier<TState>`，按 `Order` 升序匹配应用。
- Agent 侧推荐使用 `StateTransitionMatcher.Match(...).On<TEvent>(...).OrCurrent()`，避免重复 `Any + switch` 样板代码。
- 可通过 `ActorRuntime:EventSourcing:*` 调整自动快照与裁剪策略：
  - `EnableSnapshots`（默认 `true`）
  - `SnapshotInterval`（默认 `200`）
  - `EnableEventCompaction`（默认 `true`）
  - `RetainedEventsAfterSnapshot`（默认 `0`）

## 7. 快照语义
1. 快照仅用于减少回放开销。
2. 快照写入失败不得影响已提交事件事实。
3. 恢复顺序：先快照，再从快照版本之后回放事件增量。
4. 事件裁剪只在“快照写入成功”后触发，避免清理后无快照可恢复。
5. `SnapshotInterval` 表示“自上次成功快照以来新增的 committed event 数量”。一次批量提交即使跨过整除点，也会在实际 committed version 建快照，不要求版本号刚好整除 interval。
6. 快照成功后，`EventSourcingBehavior` 在同一个 actor turn 内调用 `IEventStore.DeleteEventsUpToAsync(...)`；不得用进程内 actor-id 字典延迟保存裁剪意图，也不得依赖 actor 停用才执行。
7. 裁剪失败按 best-effort 记录告警，不回滚已提交事件或已保存快照，并在下一次快照时重试。
8. 裁剪后事件流版本号必须保持单调递增，后续 append 继续基于最新版本并发控制。
9. Event Sourcing 快照只服务于 replay 优化，不等于 runtime 层任何 `EventEnvelope`/message snapshot 或 inspection 视图。
10. 当前 committed version 尚未 durable checkpoint 时不得保存更高版本 snapshot；compaction 上界必须同时受 snapshot 策略与 publication checkpoint 约束。

## 7.1 Committed-state publication recovery

1. 正常顺序固定为 `Append -> Apply -> Publish accepted -> Checkpoint -> Snapshot/Compact`。
2. publication adapter 成功只承诺 configured runtime stream 已接受 envelope，不承诺 observer consumed 或 read model visible。
3. publish 后、checkpoint 前退出允许重复发布；observer envelope 使用 committed `StateEvent.event_id` 作为稳定 identity。
4. activation recovery 只读取 checkpoint 后的 committed range，并在 actor 内 replay 出逐版本 `state_root`；不得进入 query、read adapter 或 projector 调用栈。
5. Orleans 使用现有 durable callback retry 做 bounded backoff。publication retry 先补发 pending fact 并消费 retry envelope，不重新执行业务 handler。
6. retry exhausted 后，typed failure record 保留 version/event id/attempt/error；修复 transport/storage 后通过 actor reactivation 继续，禁止越过 poison gap。
7. 首次升级且 checkpoint 缺失的 actor 以首次 activation 观察到的 EventStore version 建立一次性 baseline；已被旧版本 compact 的历史缺口只能走 ADR 0040 的显式 DR repair。
8. `RepublishCommittedStateAsync` 是 synthetic maintenance republish，不推进自动 checkpoint；自动 recovery 保留原始 event id，不得被 audit materializer 当作 maintenance rebuild 跳过。

完整决策与时序图见 [ADR 0045](../adr/0045-runtime-owned-committed-state-publication-recovery.md)。

## 8. 明确禁止项
1. 把 `TState` 本体当事件写入 `EventStore`。
2. 在核心路径恢复 `ConfirmDerivedEventsAsync` / `IDomainEventDeriver` 旧模型。
3. 在 `GAgentBase<TState>` 恢复 `StateStore.LoadAsync/SaveAsync` 事实通道。
4. 在 Runtime 恢复反射注入 ES（`MakeGenericType` / `GetProperty("EventSourcing")` / `GetProperty("StateStore")`）。
5. 在任何继承链路（含间接继承）上的 `GAgentBase<TState>` 子类中直接写 `State.xxx`（`= / += / ++ / --`）。

## 9. 验证命令
- `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Integration.Tests/Aevatar.Integration.Tests.csproj --nologo`
- `bash tools/ci/architecture_guards.sh`
