# Event Sourcing 基线文档（2026-02-23）

## 1. 目标与范围
- 目标：统一 Aevatar 有状态 Actor 的写侧事实源，强制 `Command -> Domain Event -> Apply -> State`。
- 适用范围：`Aevatar.Foundation.Core`、`Aevatar.Foundation.Runtime`、`Aevatar.Foundation.Runtime.Implementations.Orleans`。
- 非目标：本文件不定义 ReadModel Provider 细节；统一要求与重构计划见 `docs/architecture/generic-event-sourcing-elasticsearch-readmodel-requirements.md`。

## 2. 当前强制语义
1. `EventStore` 是唯一业务事实源。
2. `StateStore` 只能用于快照优化，不是业务真相。
3. 领域事件必须由开发者显式构建并持久化，不允许在线自动反推事件。
4. 有状态 Actor 激活必须 Replay；停用必须 flush pending events。
5. ES 行为构造走静态泛型路径，不走 Runtime 反射注入。

## 3. 当前代码事实（权威路径）
- ES 行为契约：`src/Aevatar.Foundation.Core/EventSourcing/IEventSourcingBehavior.cs`
- ES 默认实现：`src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`
- 有状态生命周期：`src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
- Local Runtime 注入边界：`src/Aevatar.Foundation.Runtime/Actor/LocalActorRuntime.cs`
- Orleans Runtime 注入边界：`src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`
- 防回退门禁：`tools/ci/architecture_guards.sh`

## 4. 生命周期语义（按当前实现）
### 4.1 Activate
- `GAgentBase<TState>.ActivateAsync` 先调用 `base.ActivateAsync` 恢复模块。
- 然后调用 `EnsureEventSourcingConfigured()`：
  - 若已设置 `EventSourcing`，直接使用。
  - 若未设置，则通过 `Services.GetService(typeof(IEventStore))` 解析 `IEventStore`，并静态构造 `EventSourcingBehavior<TState>(eventStore, actorId)`。
- 最后执行 `ReplayAsync(actorId)`，以 Replay 结果恢复 `State`。

### 4.2 Deactivate
- `GAgentBase<TState>.DeactivateAsync` 顺序：
  - `OnDeactivateAsync`
  - `ConfirmEventsAsync`
  - `PersistSnapshotAsync`
- 不再调用 `StateStore.SaveAsync` 写事实态。

### 4.3 Fail-Fast 条件
- 未预设 `EventSourcing` 且容器中无 `IEventStore`：激活失败（`InvalidOperationException`）。
- 持久化 `TState` 快照事件到事件流：提交失败（禁止快照冒充领域事件）。

## 5. 开发者实现规范
1. 命令处理代码必须显式构建领域事件：`RaiseEvent(domainEvent)`。
2. 必须调用 `ConfirmEventsAsync` 提交 pending events。
3. 必须保证“可重放同态”：`Replay` 后状态与在线运行状态一致。
4. 推荐通过重写 `TransitionState` 明确定义事件到状态转换。

示例（简化）：

```csharp
[EventHandler]
public async Task Handle(IncrementRequested evt)
{
    EventSourcing!.RaiseEvent(new IncrementApplied { Amount = evt.Amount });
    await EventSourcing.ConfirmEventsAsync();

    // 当前基线下，最直接的一致性方式是回放后更新内存状态
    var replayed = await EventSourcing.ReplayAsync(Id);
    if (replayed != null)
        State = replayed;
}
```

## 6. DI 与容器约定
- `AddAevatarRuntime()` 默认注册 `IEventStore -> InMemoryEventStore`（开发/测试）。
- 生产环境应替换为持久化实现（Redis/DB/日志存储等）。
- 如需自定义 ES 行为，可直接为 Agent 预设 `EventSourcing`，但必须保持相同语义契约。

## 7. 快照语义
1. 快照仅用于减少回放开销。
2. 快照写入失败不得影响已提交事件事实。
3. 恢复顺序：先快照，再从快照版本之后回放事件增量。

## 8. 明确禁止项
1. 把 `TState` 本体当事件写入 `EventStore`。
2. 在核心路径恢复 `ConfirmDerivedEventsAsync` / `IDomainEventDeriver` 旧模型。
3. 在 `GAgentBase<TState>` 恢复 `StateStore.LoadAsync/SaveAsync` 事实通道。
4. 在 Runtime 恢复反射注入 ES（`MakeGenericType` / `GetProperty("EventSourcing")` / `GetProperty("StateStore")`）。

## 9. 验证命令
- `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
- `bash tools/ci/architecture_guards.sh`
