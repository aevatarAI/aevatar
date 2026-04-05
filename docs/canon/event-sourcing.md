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

## 2.1 与 Runtime 消息流的边界
1. Actor 之间通过 Stream 传递的是 `EventEnvelope`，这是 runtime message envelope。
2. `EventEnvelope` 可以承载 command-like request、signal、reply、timeout fired 或业务事件 payload。
3. 只有 Actor 在处理这些入站消息后显式调用 `PersistDomainEventAsync(...)` / `PersistDomainEventsAsync(...)` 持久化的领域事件，才会成为 `StateEvent` 并进入 `EventStore`。
4. 因此，`EventEnvelope` 流与 `StateEvent` 流不是同一层：前者是 transport/runtime 层，后者是事实/event-sourcing 层。

## 3. 当前代码事实（权威路径）
- ES 行为契约：`src/Aevatar.Foundation.Core/EventSourcing/IEventSourcingBehavior.cs`
- ES 默认实现：`src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`
- 事件裁剪调度抽象：`src/Aevatar.Foundation.Core/EventSourcing/IEventStoreCompactionScheduler.cs`
- 状态事件 applier 抽象：`src/Aevatar.Foundation.Core/EventSourcing/IStateEventApplier.cs`
- Typed applier 基类：`src/Aevatar.Foundation.Core/EventSourcing/StateEventApplierBase.cs`
- 状态事件匹配器：`src/Aevatar.Foundation.Core/EventSourcing/StateTransitionMatcher.cs`
- 有状态生命周期：`src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
- Runtime 停用钩子抽象：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHook.cs`
- Runtime 停用钩子分发器：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHookDispatcher.cs`
- Runtime 停用钩子分发实现：`src/Aevatar.Foundation.Runtime/Actor/ActorDeactivationHookDispatcher.cs`
- Runtime 默认裁剪钩子：`src/Aevatar.Foundation.Runtime/Actor/EventStoreCompactionDeactivationHook.cs`
- 本地持久化 EventStore：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`
- 生产持久化 EventStore（Garnet）：`src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetEventStore.cs`
- Local Runtime 注入边界：`src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActorRuntime.cs`
- Orleans Runtime 注入边界：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
- 防回退门禁：`tools/ci/architecture_guards.sh`

## 4. 生命周期语义（按当前实现）
### 4.1 Activate
- `GAgentBase<TState>.ActivateAsync` 先调用 `base.ActivateAsync` 恢复模块。
- 然后调用 `EnsureEventSourcingConfigured()`：
  - 若已设置 `EventSourcing`，直接使用。
  - 若未设置，则必须通过已绑定的 `IEventSourcingBehaviorFactory<TState>` 创建。
- 最后执行 `ReplayAsync(actorId)`，以 Replay 结果恢复 `State`。

### 4.2 Deactivate
- `GAgentBase<TState>.DeactivateAsync` 顺序：
  - `OnDeactivateAsync`
  - `ConfirmEventsAsync`
  - `PersistSnapshotAsync`
- 不再调用 `StateStore.SaveAsync` 写事实态。
- 快照保存成功后仅记录“待清理版本”；历史事件清理由 runtime `IActorDeactivationHookDispatcher` 分发所有 `IActorDeactivationHook`，其中默认裁剪钩子触发 `IEventStoreCompactionScheduler.RunOnIdleAsync(...)` 异步执行。

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
- `AddAevatarRuntime()` 默认注册 `IEventStoreCompactionScheduler -> DeferredEventStoreCompactionScheduler`（记录裁剪意图，空闲期执行）。
- `AddAevatarRuntime()` 默认注册 `IActorDeactivationHook -> EventStoreCompactionDeactivationHook`。
- `AddAevatarRuntime()` 默认注册 `IActorDeactivationHookDispatcher -> ActorDeactivationHookDispatcher`（支持多 hook 顺序分发）。
- 可通过 `AddFileEventStore(...)` 将 `IEventStore` 切换为本地持久化实现：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`。
- 调用 `AddFileEventStore(...)` 时，`IEventSourcingSnapshotStore<TState>` 会切换为 `FileEventSourcingSnapshotStore<TState>`，支持快照与事件裁剪后的持久化恢复。
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
5. 裁剪执行为异步延迟任务，默认在 Actor 空闲停用阶段触发，不阻塞命令写入主路径。
6. 裁剪后事件流版本号必须保持单调递增，后续 append 继续基于最新版本并发控制。
7. Event Sourcing 快照只服务于 replay 优化，不等于 runtime 层任何 `EventEnvelope`/message snapshot 或 inspection 视图。

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
