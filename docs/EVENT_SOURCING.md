# Event Sourcing 使用指南

本文说明如何在 Aevatar 中**开启并使用 Event Sourcing（事件溯源）**：状态不直接持久化快照，而是通过「追加状态变更事件 + 重放」来恢复与演进状态。

## 何时使用

- **需要完整变更历史**：可审计、可重放、可按时间点重建状态。
- **需要乐观并发**：多实例写同一 Agent 时，通过版本号冲突检测避免覆盖。
- **与 CQRS 配合**：写侧只追加事件，读侧通过投影或快照查询。

不启用时，有状态 Agent 使用 `IStateStore<TState>` 的 Load/Save 即可，无需 Event Sourcing。

## 架构要点

- **ES 以 Mixin 方式提供**：不要求继承额外基类，通过 DI 注入 `IEventSourcingBehavior<TState>`，在 Agent 内显式调用。
- **存储**：`IEventStore` 负责事件的追加与按版本查询；运行时默认提供 `InMemoryEventStore`，生产可替换为持久化实现。
- **状态恢复**：激活时从 `IEventStore` 重放事件，通过 `TransitionState` 得到当前状态；变更时先 `RaiseEvent`，再 `ConfirmEventsAsync` 持久化。

## 1. 注册服务

确保运行时已注册 `IEventStore`（`AddAevatarRuntime()` 已默认注册 `InMemoryEventStore`）。若使用自定义存储，先替换为你的实现：

```csharp
// 使用默认内存事件存储（开发/测试）
services.AddAevatarRuntime();   // 已包含 TryAddSingleton<IEventStore, InMemoryEventStore>

// 或替换为持久化实现
services.Replace(ServiceDescriptor.Singleton<IEventStore, MySqlEventStore>());
```

为**每个有状态 Agent 类型**提供 `IEventSourcingBehavior<TState>`。因构造需要 `agentId`，多 Agent 实例时需按 Agent 创建行为实例，例如通过工厂或在使用处解析：

```csharp
// 方式 A：单例（仅当该类型只有一个 Agent 实例时适用）
services.AddSingleton<IEventSourcingBehavior<MyState>>(sp =>
{
    var store = sp.GetRequiredService<IEventStore>();
    return new EventSourcingBehavior<MyState>(store, "my-agent-id");
});

// 方式 B：多实例时在创建 Agent 处按 agentId 构造（推荐）
// 在 Actor/宿主创建 Agent 时：var behavior = new EventSourcingBehavior<MyState>(eventStore, agent.Id);
// 再通过构造函数或属性注入到该 Agent 实例。
```

即：每个 Agent 实例对应一个 `EventSourcingBehavior<TState>` 实例（同一 `agentId`），由创建 Agent 的代码负责构造并注入。

## 2. 在有状态 Agent 中使用

假设你有一个 `GAgentBase<MyState>` 的 Agent，状态类型为 Protobuf 消息 `MyState`。

### 2.1 注入行为

在 Agent 中注入 `IEventSourcingBehavior<MyState>`（通过构造函数或可写属性，由创建 Agent 的宿主/Actor 在实例化后赋值）：

```csharp
public class MyAgent : GAgentBase<MyState>
{
    private readonly IEventSourcingBehavior<MyState>? _es;

    public MyAgent(IEventSourcingBehavior<MyState>? es) => _es = es;
    // 或不注入时 _es 为 null，则退化为仅用 StateStore 的普通有状态 Agent
}
```

### 2.2 激活时从事件重放恢复状态

在 `OnActivateAsync` 中，若启用了 ES，优先从事件存储重放得到初始状态；否则仍从 `StateStore` 加载（与现有逻辑兼容）：

```csharp
protected override async Task OnActivateAsync(CancellationToken ct)
{
    if (_es != null)
    {
        var replayed = await _es.ReplayAsync(Id, ct);
        if (replayed != null)
            State = replayed;   // 在 StateGuard 写范围内设置
    }
    // 若未启用 ES，基类已从 StateStore 加载，此处可做其它初始化
}
```

注意：若使用 ES 恢复状态，通常不再依赖 `StateStore.LoadAsync` 的 snapshot；可在同一 `ActivateAsync` 作用域内只做 Replay，或配合快照策略（见后文）减少重放长度。

### 2.3 在事件处理中记录并确认变更

在 `[EventHandler]` 中，不直接改 `State` 后依赖 `StateStore.Save`，而是通过 ES 记录变更并确认：

```csharp
[EventHandler]
public async Task HandleCountRequest(CountRequest evt, IEventHandlerContext ctx, CancellationToken ct)
{
    if (_es == null) { /* 回退：直接改 State + 依赖 Deactivate 时 StateStore.Save */ return; }

    _es.RaiseEvent(new CountChanged { Delta = evt.Delta });
    await _es.ConfirmEventsAsync(ct);

    // 可选：立即反映到内存状态，便于本 Agent 后续逻辑或查询
    var replayed = await _es.ReplayAsync(Id, ct);
    if (replayed != null)
        State = replayed;
}
```

也可以在一次处理中多次 `RaiseEvent`，最后统一调用一次 `ConfirmEventsAsync`。

### 2.4 实现状态转换（重放时生效）

`ReplayAsync` 会按顺序将已持久化的事件应用到状态上，转换逻辑由 `TransitionState` 定义。默认的 `EventSourcingBehavior<TState>.TransitionState` 返回 `current` 不变；要真正从事件恢复状态，需要提供转换逻辑。

**方式 A：派生 EventSourcingBehavior 并重写 TransitionState**

```csharp
public class MyEventSourcingBehavior : EventSourcingBehavior<MyState>
{
    public override MyState TransitionState(MyState current, IMessage evt)
    {
        switch (evt)
        {
            case CountChanged e:
                return new MyState { Count = current.Count + e.Delta };
            default:
                return current;
        }
    }
}
```

注册时使用 `MyEventSourcingBehavior` 替代 `EventSourcingBehavior<MyState>`。

**方式 B：在 Agent 内使用自定义行为类型**

若行为由你自定义类实现 `IEventSourcingBehavior<MyState>`，在工厂或创建处返回该实现即可，重放时即使用你实现的 `TransitionState`。

事件在存储中以 `StateEvent` 形式保存，`EventData` 为 `Any`；重放时传入的是 `Any`（实现 `IMessage`），若在 `TransitionState` 中需要具体类型，可对 `evt` 做类型判断或使用 `Any.Unpack<T>()` 解出具体消息类型再处理。

## 3. 停用时确认未持久化事件（可选）

若希望在 Deactivate 时把尚未确认的 pending 事件写盘，可在 `OnDeactivateAsync` 中调用：

```csharp
protected override async Task OnDeactivateAsync(CancellationToken ct)
{
    if (_es != null)
        await _es.ConfirmEventsAsync(ct);
}
```

这样未在 handler 中调用 `ConfirmEventsAsync` 的变更也会在停用前被持久化。

## 4. 快照策略（可选）

重放大量事件可能较慢，可配合快照减少重放长度：定期将当前状态写入 `IStateStore`，重放时先加载最近快照再只重放该版本之后的事件。当前 Core 提供快照策略类型（如 `ISnapshotStrategy`、`IntervalSnapshotStrategy`），尚未在 `EventSourcingBehavior` 内自动衔接；若需，可在你的 Agent 或自定义 Behavior 中在 `ConfirmEventsAsync` 后根据策略调用 `StateStore.SaveAsync` 做快照，并在 `ReplayAsync` 中先尝试从 `StateStore` 加载再只拉取该版本之后的事件重放。

## 5. 小结

| 步骤 | 说明 |
|------|------|
| 注册 `IEventStore` | 运行时默认已注册 `InMemoryEventStore`，可按需替换。 |
| 注册 / 创建 `IEventSourcingBehavior<TState>` | 按 Agent 类型/实例注册或通过工厂按 `agentId` 创建。 |
| 激活时重放 | 在 `OnActivateAsync` 中调用 `_es.ReplayAsync(Id, ct)` 并赋值 `State`。 |
| 处理中记录并确认 | 在 Handler 中 `_es.RaiseEvent(evt)`，再 `await _es.ConfirmEventsAsync(ct)`。 |
| 实现 `TransitionState` | 在自定义 Behavior 中重写，使重放时事件能正确应用到状态。 |
| 停用时确认（可选） | 在 `OnDeactivateAsync` 中调用 `ConfirmEventsAsync` 刷盘 pending 事件。 |

不注入 `IEventSourcingBehavior<TState>` 时，Agent 行为与现有有状态 Agent 一致，仅使用 `IStateStore` 的 Load/Save，无需改动现有代码即可保持兼容。
