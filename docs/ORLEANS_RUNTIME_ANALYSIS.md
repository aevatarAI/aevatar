# Orleans Runtime 分析文档（v3）

## 一、背景与目标

当前 `aevatar` 项目的 Foundation 层已经实现了完整的本地运行时 (`Aevatar.Runtime`)，基于内存 Actor 模型运行。目标是新增一个 Orleans 运行时 (`Aevatar.Runtime.Orleans`)，使 Agent 能够运行在 Orleans 分布式集群上，获得：

- **分布式扩展**：Agent 自动分布到多个 Silo 节点
- **虚拟 Actor**：Agent 按需激活/休眠，无需手动生命周期管理
- **持久化状态**：Grain State 由 Orleans 基础设施托管
- **消息解耦**：外部入口和内部传播统一通过 MassTransit 实现异步 fire-and-forget

参考实现：`aevatar-agent-framework/src/Aevatar.Agents.Runtime.Orleans`（27 个文件，1000+ 行 Grain 核心代码）。本项目是简化版，只实现核心业务映射。

### 本文落地约束（最终决策）

1. 只保留一套流抽象：复用 `IStream` / `IStreamProvider`，不新增 `IMessageStream*` 到当前仓库契约层。  
2. `IActor` 契约保持不变：只以 `HandleEventAsync` 作为外部事件入口。  
3. `DestroyAsync` 先按 soft destroy 实现（停用 + 解除拓扑），hard purge 作为可选增强。  
4. 消息投递语义按 at-least-once 设计，必须配套幂等和环路保护。  

---

## 二、三个关键架构决策

在进入细节之前，先明确三个影响全局的架构选择：

### 决策 1：仅使用 MassTransit Stream，不用 Orleans Stream

参考实现支持 Orleans Stream 和 MassTransit 双通道（通过 `OrleansStreamFactory` 按配置切换）。本项目 **只保留 MassTransit**。

**为什么选 MassTransit 而不是 Orleans Stream**：

| 维度 | Orleans Stream | MassTransit |
|---|---|---|
| 传输层 | Orleans 内部 PubSub（Silo 间直连） | RabbitMQ / Kafka / Azure SB 等 |
| 消息持久化 | 依赖 Stream 存储配置 | **队列天然持久** |
| 跨服务通信 | 仅限 Orleans 集群内 | **跨服务、跨进程** |
| Dead-letter | 需自行实现 | **内置 DLQ** |
| 重试策略 | 简单 | **完整 Retry/Circuit Breaker** |
| 背压处理 | 有限 | **Consumer 端精细控制** |
| 监控 | 需自建 | **成熟 Observability** |

**对实现的影响**：
- 不需要 `OrleansStreamAdapter` / `OrleansStreamProviderAdapter`（原方案中的 Orleans Stream 适配器）
- 需要实现 `IStreamProvider` 的 MassTransit 适配（复用当前契约）
- **Grain 不订阅自己的 Stream**：所有入站事件由 `MassTransitEventHandler`（MassTransit Consumer）统一消费，再通过 RPC 调用 Grain
- `MassTransitEventHandler` 是 **唯一** 的消费入口（MassTransit Consumer → Grain RPC 的桥接）

### 决策 2：Actor 分 Silo 端和 Client 端

这是一个关键的架构分层，因为 **Silo 端和 Client 端调用 Grain 的方式不同**：

| 调用方 | 获取 Grain 引用 | 场景 |
|---|---|---|
| **Client 端**（API/Controller） | `IClusterClient`（实现了 `IGrainFactory`） | 外部请求进入 |
| **Silo 端**（Grain 内部） | `GrainFactory`（`Grain` 基类属性）或 DI 的 `IGrainFactory` | Grain 间通信 |

虽然 Orleans 7+ 中 `IClusterClient` 继承 `IGrainFactory`，但它们的 **宿主上下文不同**：

```
Client 进程:
  ┌────────────────────────────────────┐
  │ API Controller / HTTP Handler      │
  │   └── IClusterClient              │  ← 连接到 Orleans 集群（层级查询/停用）
  │                                    │
  │ OrleansActorRuntime (Client)       │  ← 用 IClusterClient + IStreamProvider
  │   └── 创建 OrleansClientActor      │
  │                                    │
  │ OrleansClientActor                 │
  │   ├── HandleEventAsync             │  ← ★ 发到 MassTransit Stream（fire-and-forget）
  │   └── 层级查询 → Grain RPC         │  ← [AlwaysInterleave]，不排队
  │                                    │
  │ IStreamProvider (MassTransit)      │  ← ★ 结果返回通道
  │   └── stream.SubscribeAsync(...)   │  ← 订阅 Agent 输出事件
  └────────────────────────────────────┘
              │ MassTransit Queue（事件入站↓ + 传播↓ + 结果↑）
              ▼
Silo 进程:
  ┌────────────────────────────────────┐
  │ MassTransitEventHandler            │  ← MassTransit Consumer（唯一事件入口）
  │   └── IGrainFactory.GetGrain → RPC │
  │                                    │
  │ GAgentGrain                        │
  │   ├── Agent 实例（业务逻辑）         │
  │   ├── GrainFactory                 │  ← Silo 内部 Grain 间调用
  │   └── GrainEventPublisher          │  ← 发布事件到 MassTransit Stream
  └────────────────────────────────────┘
```

**因此需要两个 Actor 实现**：

1. **`OrleansClientActor`**：Client 端代理
   - 持有 `IGAgentGrain` 引用（层级查询/停用）和 `IStream`（事件入站）
   - `HandleEventAsync` → 发到 MassTransit Stream（fire-and-forget，立即返回）
   - 层级查询 → Grain RPC（只读，`[AlwaysInterleave]` 不排队）
   - 对应参考实现中的 `OrleansGAgentActor`

2. **`GrainEventPublisher`**：Silo 端内部发布者（注入到 Agent 的 `IEventPublisher`）
   - `PublishAsync` → 直接调用 Grain 的 `PropagateEventAsync`（不经 Stream 自回环）
   - `SendToAsync` → 发到目标 Agent 的 MassTransit Stream（异步解耦）
   - 持有 Grain 引用和 `IStreamProvider`
   - 对应参考实现中的 `GrainEventPublisher`（内嵌类）

### 决策 3：PublishEvent 通过 MassTransit Stream，而非直接 HandleAsync RPC

这是最重要的架构决策。**如果只用直接 RPC (`HandleEventAsync`)，就失去了消息发送和消费的分离**。

**两种事件传递方式对比**：

```
方式 A：直接 RPC（同步耦合）
  调用方 ──HandleEventAsync(bytes)──→ 目标 Grain
  • 调用方阻塞等待处理完成
  • 无消息缓冲
  • 目标不可用则直接失败
  • 无 fan-out 能力

方式 B：MassTransit Stream（异步解耦）
  调用方 ──ProduceAsync(envelope)──→ MassTransit Queue ──Consumer──→ 目标 Grain
  • 调用方 fire-and-forget，立即返回
  • 消息在队列中缓冲
  • 目标暂不可用时消息在队列中缓冲（配合 durable queue 可实现至少一次投递）
  • 天然 fan-out（多 Consumer 订阅同一 Topic）
  • 重试、死信、背压全部内置
```

**本项目事件流设计**：

```
PublishAsync (广播传播):
  Agent 业务代码调用 GrainEventPublisher.PublishAsync(event, direction)
    → 直接调用 Grain 内部的 PropagateEventAsync（不经过 Stream 自回环）
    → PropagateEventAsync 按方向发送到 children/parent 的 MassTransit Stream

SendToAsync (点对点):
  Client/Silo 两端都通过目标 Agent 的 MassTransit Stream 异步发送
```

**完整事件生命周期**：

```
1. 外部请求进入（fire-and-forget，Client 立即返回）:
   API 构建 EventEnvelope (Id, PublisherId, Direction, Payload)
     → OrleansClientActor.HandleEventAsync(envelope)
     → stream.ProduceAsync(envelope)  [发到 MassTransit Queue，立即返回]

2. MassTransitEventHandler 消费（统一入口）:
   MassTransit Consumer 从队列消费消息
     → MassTransitEventHandler.HandleEventAsync(agentId, envelope)
     → IGrainFactory.GetGrain<IGAgentGrain>(agentId)
     → Grain.HandleEventAsync(bytes)  [RPC 进入 Grain 单线程]

3. Grain 处理:
   GAgentGrain.HandleEventAsync(bytes):
     → 反序列化 EventEnvelope
     → Agent.HandleEventAsync(envelope)  [业务逻辑，同步等待]
     → PropagateEventAsync(envelope)     [fire-and-forget，不阻塞]
       → Down: 并行发送到所有 children 的 MassTransit Stream
       → Up:   发送到 parent 的 MassTransit Stream
       → Both: 并行 Down + Up
     → 下游 Grain 通过步骤 2 异步接收（同一路径，递归）

4. Agent 内部主动发布:
   Agent 业务代码调用 EventPublisher.PublishAsync(event, direction)
     → GrainEventPublisher 直接调用 Grain.PropagateEventAsync
     → 按方向发送到 children/parent 的 MassTransit Stream
     → 目标 Grain 通过步骤 2 异步接收

5. 结果返回到 Client（异步，通过 Stream 订阅）:
   Agent 处理完毕后 PublishAsync(resultEvent, Up) 沿层级向上传播
     → 最终到达根 Agent 的 MassTransit Stream
     → Client 端 streams.GetStream(rootActorId).SubscribeAsync(handler)
     → handler 收到 WorkflowCompletedEvent / TextMessageEndEvent 等输出事件
   与 LocalRuntime 用法完全一致（参考 samples/maker/Program.cs）
```

**这个设计保证了**：
- **全链路异步**：外部入口和内部传播统一走 MassTransit Stream（fire-and-forget），Client 不阻塞
- **单一消费入口**：所有事件（外部 + 内部传播）由 `MassTransitEventHandler` 统一消费，Grain 不订阅自己的 Stream
- **结果返回异步**：Client 通过 `IStreamProvider.GetStream(actorId).SubscribeAsync(...)` 订阅 Agent 输出事件（与 LocalRuntime 模式一致）
- **无自回环**：`GrainEventPublisher.PublishAsync` 直接调用 `PropagateEventAsync`，不经过自己的 Stream
- **天然韧性**：Grain 不可用时消息缓冲在队列中，retry/DLQ/backpressure 全由 MassTransit 提供

**硬约束（实现必须遵守）**：
1. **入口语义约束**：外部 `IActor.HandleEventAsync` 走 MassTransit Stream（fire-and-forget），`Task` 完成即消息入队成功；业务处理结果通过 Stream 订阅异步返回。`MassTransitEventHandler` 是 Grain 的**唯一事件入口**。
2. **顺序约束**：同一 `actorId` 的事件处理必须串行可观测。实现上使用非重入 Grain + 受控 Consumer 并发；不得在同一 Grain 内并发执行 `Agent.HandleEventAsync`。
3. **索引约束（Manifest）**：`CreateAsync` 写入 manifest，`DestroyAsync` 删除 manifest，`Restore/GetAll` 仅依赖 manifest 作为可枚举索引来源；禁止通过 Orleans 原生 API 直接“枚举所有 Grain”。

---

## 三、现有架构对照

### 3.1 当前 aevatar 的 Abstractions 契约

| 契约接口 | 职责 | 文件 |
|---|---|---|
| `IAgent` / `IAgent<TState>` | 业务逻辑单元 | `Aevatar.Abstractions/IAgent.cs` |
| `IActor` | Agent 运行容器（串行、层级） | `IActor.cs` |
| `IActorRuntime` | Actor 生命周期与拓扑管理 | `IActorRuntime.cs` |
| `IEventPublisher` | 事件发布（方向广播 + 点对点） | `IEventPublisher.cs` |
| `IStream` / `IStreamProvider` | 事件传播通道 | `IStream.cs` / `IStreamProvider.cs` |
| `IStateStore<TState>` | 状态持久化 | `Persistence/IStateStore.cs` |
| `IEventStore` | 事件溯源存储 | `Persistence/IEventStore.cs` |
| `IAgentManifestStore` | Agent 元数据持久化 | `Persistence/IAgentManifestStore.cs` |

### 3.2 当前本地运行时实现（Aevatar.Runtime）

```
Aevatar.Runtime/
├── Actor/
│   ├── LocalActorRuntime.cs      → IActorRuntime（进程内 ConcurrentDictionary）
│   ├── LocalActor.cs             → IActor（SemaphoreSlim 邮箱串行）
│   └── LocalActorPublisher.cs    → IEventPublisher（方向路由）
├── Streaming/
│   ├── InMemoryStream.cs         → IStream
│   └── InMemoryStreamProvider.cs → IStreamProvider
├── Persistence/                  → 内存存储实现
├── Routing/                      → 层级路由 + 环路保护
└── DependencyInjection/          → AddAevatarRuntime()
```

### 3.3 参考 Orleans 实现中的关键角色映射

| 参考实现文件 | 角色 | 本项目对应 |
|---|---|---|
| `OrleansGAgentGrain.cs` | Silo 端 Grain（持有 Agent） | `GAgentGrain.cs` |
| `OrleansGAgentActor.cs` | Client 端代理（Stream 发布） | `OrleansClientActor.cs`（同样 Stream fire-and-forget） |
| `OrleansGAgentActorFactory.cs` | Client 端 Actor 工厂（用 IClusterClient） | 合并到 `OrleansActorRuntime.cs` |
| `GrainEventPublisher`（内嵌类） | Silo 端 EventPublisher（Agent 内部发布） | `GrainEventPublisher.cs` |
| `UnifiedGrainEventPublisher.cs` | Silo 端统一发布（可选替代） | 合并到 `GrainEventPublisher` |
| `OrleansMassTransitEventHandler.cs` | MassTransit Consumer → Grain 桥接 | `MassTransitEventHandler.cs` |
| `OrleansStreamFactory.cs` | Orleans/MassTransit 选择器 | **不需要**（只有 MassTransit） |

---

## 四、核心架构图

```
┌─────────────────────────────────────────────────────────────┐
│                    Client Side (API 进程)                     │
│                                                              │
│  OrleansActorRuntime (IActorRuntime)                         │
│    ├── IClusterClient ← 连接 Orleans 集群                     │
│    ├── IAgentManifestStore ← Agent 索引                       │
│    ├── CreateAsync → GetGrain + Initialize + WriteManifest    │
│    ├── GetAsync    → IsInitialized? 返回代理 : 返回 null       │
│    ├── LinkAsync   → Grain.SetParentAsync + AddChildAsync     │
│    ├── UnlinkAsync → Grain.ClearParentAsync + RemoveChild     │
│    └── DestroyAsync→ Unlink + Deactivate + DeleteManifest    │
│                                                              │
│  OrleansClientActor (IActor)                                 │
│    ├── HandleEventAsync  → stream.ProduceAsync (fire-and-forget)│
│    └── 层级查询           → Grain RPC ([AlwaysInterleave])    │
│                                                              │
│  IStreamProvider (MassTransit) ← 事件入站 + 结果返回           │
│    ├── stream.ProduceAsync(envelope)  → 发送事件              │
│    └── stream.SubscribeAsync(handler) → 接收输出事件          │
└──────────────────────────┬──────────────────────────────────┘
                           │ MassTransit Queue（事件入站↓ + 传播↓ + 结果↑）
                           │
┌──────────────────────────▼──────────────────────────────────┐
│                    Silo Side (Orleans 进程)                    │
│                                                              │
│  MassTransit Consumer（唯一消费入口）                           │
│    └── MassTransitEventHandler                               │
│          └── IGrainFactory.GetGrain → Grain.HandleEventAsync  │
│                                                              │
│  GAgentGrain (Orleans Grain, 非 Reentrant)                   │
│    ├── IPersistentState<OrleansAgentState>                    │
│    │     └── AgentTypeName, AgentId, ParentId, Children      │
│    ├── IAgent _agent (业务逻辑实例, 在 Silo 中执行)             │
│    │                                                         │
│    ├── OnActivateAsync:                                      │
│    │     → 恢复 Agent（如果有持久化的 AgentTypeName）            │
│    │     → Agent.ActivateAsync()（ES Agent 在此重放事件）       │
│    │                                                         │
│    ├── HandleEventAsync(bytes): ← MassTransitHandler 唯一入口 │
│    │     → 反序列化 EventEnvelope                              │
│    │     → 检查 metadata["__publishers"] 防环路                │
│    │     → Agent.HandleEventAsync(envelope) [同步]            │
│    │     → PropagateEventAsync(envelope) [fire-and-forget]    │
│    │         Down → 并行 ProduceAsync 到 children Stream      │
│    │         Up   → ProduceAsync 到 parent Stream             │
│    │                                                         │
│    └── GrainEventPublisher (注入到 Agent 内部的 IEventPublisher)│
│          ├── PublishAsync → 直接调用 PropagateEventAsync       │
│          └── SendToAsync  → ProduceAsync 到目标 Stream        │
└─────────────────────────────────────────────────────────────┘
```

---

## 五、需要实现的文件清单

```
src/Aevatar.Runtime.Orleans/
├── IGAgentGrain.cs                    # Grain 接口
├── GAgentGrain.cs                     # Grain 核心实现
├── OrleansAgentState.cs               # Grain 持久化状态模型
├── OrleansClientActor.cs              # Client 端 IActor 代理 (用 IClusterClient)
├── OrleansActorRuntime.cs             # IActorRuntime 实现 (Client 端)
├── GrainEventPublisher.cs             # Silo 端 IEventPublisher (注入到 Agent)
├── MassTransitEventHandler.cs         # MassTransit Consumer → Grain 桥接
├── Stream/
│   ├── MassTransitStream.cs           # IStream 的 MassTransit 实现
│   └── MassTransitStreamProvider.cs   # IStreamProvider 的 MassTransit 实现
├── Constants.cs                       # 常量
└── DependencyInjection/
    ├── OrleansClientExtensions.cs     # Client 端 DI: AddAevatarOrleansClient()
    └── OrleansSiloExtensions.cs       # Silo 端 DI: AddAevatarOrleansSilo()
```

**共 12 个文件**。与 v1 方案相比：

| 变更 | 原因 |
|---|---|
| ~~OrleansStreamAdapter.cs~~ → `MassTransitStream.cs` | 只用 MassTransit，不用 Orleans Stream |
| ~~OrleansStreamProviderAdapter.cs~~ | 删除，不需要 Orleans Stream 适配 |
| ~~OrleansActor.cs~~ → `OrleansClientActor.cs` | 明确是 Client 端代理 |
| ~~OrleansEventPublisher.cs~~ → `GrainEventPublisher.cs` | 明确是 Silo 端发布者 |
| 新增 `MassTransitEventHandler.cs` | MassTransit Consumer → Grain 桥接（必须） |
| DI 分拆为 Client + Silo | 两端注册的服务不同 |

---

## 六、各文件详细设计

### 6.1 IGAgentGrain.cs — Grain 接口

```csharp
public interface IGAgentGrain : IGrainWithStringKey
{
    // 初始化
    Task<bool> InitializeAgentAsync(string agentTypeName);
    [AlwaysInterleave] Task<bool> IsInitializedAsync();

    // 事件处理 (仅由 MassTransitEventHandler RPC 调用，唯一入口)
    // 非 [AlwaysInterleave]：必须串行执行，保证 Agent 状态安全
    Task HandleEventAsync(byte[] envelopeBytes);

    // 层级管理 (标记 [AlwaysInterleave]：只修改 Grain 元数据，不碰 Agent 状态)
    [AlwaysInterleave] Task AddChildAsync(string childId);
    [AlwaysInterleave] Task RemoveChildAsync(string childId);
    [AlwaysInterleave] Task SetParentAsync(string parentId);
    [AlwaysInterleave] Task ClearParentAsync();
    [AlwaysInterleave] Task<IReadOnlyList<string>> GetChildrenAsync();
    [AlwaysInterleave] Task<string?> GetParentAsync();

    // 描述
    [AlwaysInterleave] Task<string> GetDescriptionAsync();

    // 停用
    Task DeactivateAsync();
}
```

### 6.2 OrleansAgentState.cs — Grain 状态

```csharp
[GenerateSerializer]
public class OrleansAgentState
{
    [Id(0)] public string? AgentTypeName { get; set; }
    [Id(1)] public string AgentId { get; set; } = "";
    [Id(2)] public string? ParentId { get; set; }
    [Id(3)] public List<string> Children { get; set; } = new();
}
```

只存 **元数据**（类型名、层级关系），业务 State 仍走 `IStateStore<TState>`。若 Agent 启用了 Event Sourcing，业务 State 由 `IEventStore` 中的事件重放得到，`IStateStore<TState>` 用于可选的快照。

### 6.3 GAgentGrain.cs — Grain 核心实现

```csharp
// 不标 [Reentrant]：HandleEventAsync 必须串行执行，保证 Agent 状态安全
// 只读方法通过接口上的 [AlwaysInterleave] 允许并发
public class GAgentGrain : Grain, IGAgentGrain
{
    private readonly IPersistentState<OrleansAgentState> _state;
    private IAgent? _agent;
    private IStreamProvider _streamProvider;     // MassTransit Stream 工厂（仅用于发布）

    // ── Grain 生命周期 ──
    OnActivateAsync:
      → 从 DI 获取 IStreamProvider (MassTransit)
      → 如果有持久化的 AgentTypeName → 恢复 Agent
      → Agent.ActivateAsync()（ES Agent 在此重放事件）
      // 注意: Grain 不订阅自己的 Stream，入站事件由 MassTransitEventHandler 统一消费

    OnDeactivateAsync:
      → Agent.DeactivateAsync()

    // ── 初始化 ──
    InitializeAgentAsync(typeName):
      → Type.GetType(typeName) → 创建 Agent (优先从 DI 解析，fallback Activator.CreateInstance)
      // 注意: Silo 进程必须引用 Agent 类型所在程序集，跨进程部署时需确保一致
      → 注入依赖（与 LocalActorRuntime.InjectDependencies 对齐）:
          gab.SetId(actorId)                           // Agent ID
          gab.EventPublisher = new GrainEventPublisher(...)  // IEventPublisher
          gab.Logger = loggerFactory.CreateLogger(agentType.Name)
          gab.Services = ServiceProvider               // Silo 的 IServiceProvider
          gab.ManifestStore = ServiceProvider.GetService<IAgentManifestStore>()
          InjectStateStore(agent)                      // 反射注入 IStateStore<TState>
          InjectEventSourcingBehavior(agent, actorId)  // 反射注入 IEventSourcingBehavior<TState>（若 ES）
      → Agent.ActivateAsync()（恢复 State/Modules/Config/Hooks，ES Agent 重放事件）
      → 持久化 GrainState

    // ── 事件处理 (仅由 MassTransitEventHandler RPC 调用，唯一入口) ──
    HandleEventAsync(bytes):
      → EventEnvelope.Parser.ParseFrom(bytes)
      → 幂等检查: _deduplicator.IsDuplicate(envelope.Id) → return 跳过重复事件
      → 检查 metadata["__publishers"] 是否包含自身 id → 防环路
      → // ── Observability: Tracing + Metrics（与 LocalActor 对齐） ──
        using var activity = AevatarActivitySource.StartHandleEvent(
            this.GetPrimaryKeyString(), envelope.Id)
        var sw = Stopwatch.StartNew()
      → Agent.HandleEventAsync(envelope)  [同步等待业务完成]
      → sw.Stop()
        AgentMetrics.EventsHandled.Add(1, tag("agent_id", id), tag("agent_type", typeName))
        AgentMetrics.HandlerDuration.Record(sw.Elapsed.TotalMilliseconds,
            tag("agent_id", id), tag("agent_type", typeName))
      → _ = PropagateEventAsync(envelope).ContinueWith(LogOnFault) [fire-and-forget + 异常日志]

    // ── 事件传播 (fire-and-forget，不阻塞 HandleEventAsync 返回) ──
    internal PropagateEventAsync(envelope):
      → 在 envelope.metadata["__publishers"] 中追加自身 id
      → var children = _state.State.Children.ToList()   // ★ 快照 children，防止并发修改
      → var parentId = _state.State.ParentId
      → switch (envelope.Direction):
          Self  → 不传播
          Down  → Task.WhenAll: 并行发到所有 children 的 Stream
          Up    → 发到 parent 的 Stream（如果有）
          Both  → Task.WhenAll: 并行 Down + Up

    SendToStreamAsync(targetId, envelope):
      → _streamProvider.GetStream(targetId)
      → stream.ProduceAsync(envelope) // fire-and-forget

    // ── 层级管理 (标记 [AlwaysInterleave]，可与 HandleEventAsync 并发) ──
    [AlwaysInterleave] AddChildAsync / RemoveChildAsync
    [AlwaysInterleave] SetParentAsync / ClearParentAsync
      → 修改 GrainState → WriteStateAsync()
}
```

**关于 `[Reentrant]` 的设计决策**：

Orleans Grain 默认非重入（turn-based concurrency）：同一时刻只有一个方法执行，新请求排队等待。

- **不用 `[Reentrant]`**：保证 `HandleEventAsync` 串行执行，Agent 状态不会被并发修改。`GAgentBase.HandleEventAsync` 内部使用的 `StateGuard`（`AsyncLocal`）不是真正的锁，交叉执行会导致状态丢失。
- **性能缓解措施**：
  1. `PropagateEventAsync` 使用 fire-and-forget（`_ = PropagateEventAsync(...)`），不阻塞返回，将 Grain 单次处理耗时从 ~200ms 降到 ~20-30ms
  2. 只读方法（`IsInitializedAsync`、`GetChildrenAsync`、`GetParentAsync`、`GetDescriptionAsync`）通过 `[AlwaysInterleave]` 允许并发
  3. 层级管理方法（`AddChildAsync`、`RemoveChildAsync`、`SetParentAsync`、`ClearParentAsync`）只修改 `OrleansAgentState` 元数据，不碰 Agent 业务状态，也标 `[AlwaysInterleave]`。注意：`PropagateEventAsync` 在发送前必须对 `Children` / `ParentId` 做快照（`.ToList()`），避免层级方法并发修改集合
  4. 横向扩展靠多 Grain 并行（每个 Agent 是独立 Grain），而非单 Grain 内并发

### 6.4 OrleansClientActor.cs — Client 端代理

```csharp
/// <summary>
/// Client-side Actor proxy.
/// HandleEventAsync sends to MassTransit Stream (fire-and-forget).
/// Results arrive via Stream subscription on the Client side.
/// </summary>
public class OrleansClientActor : IActor
{
    private readonly IGAgentGrain _grain;           // Grain RPC 引用（仅用于层级查询/停用）
    private readonly IStream _stream;               // 自身 MassTransit Stream（事件入站通道）

    public OrleansClientActor(string id, IGAgentGrain grain, IStream stream)
    {
        Id = id; _grain = grain; _stream = stream;
    }

    public string Id { get; }

    // Agent 在 Silo 内部，Client 端不可直接访问 Agent 实例和 State
    // 结果获取方式: 通过 IStreamProvider.GetStream(Id).SubscribeAsync(...) 订阅输出事件
    public IAgent Agent => throw new NotSupportedException(
        "Agent runs inside Grain (Silo). Subscribe to stream for output events.");

    // Grain 已在 CreateAsync 时初始化，这里空操作
    public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task DeactivateAsync(CancellationToken ct = default) => _grain.DeactivateAsync();

    // ★ 关键：发到 MassTransit Stream（fire-and-forget），立即返回
    // MassTransitEventHandler 消费消息后调用 grain.HandleEventAsync
    // 结果通过 Stream 订阅异步返回
    public async Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default)
    {
        await _stream.ProduceAsync(envelope, ct);
        // fire-and-forget: 消息入队即返回，不等 Grain 处理完毕
        // Client 不阻塞，Grain 按自己的节奏从 MassTransit 队列消费
        // 与 Silo 内部传播走同一条路径（MassTransitEventHandler 统一消费）
    }

    // 层级查询走 RPC (只读，低频，[AlwaysInterleave] 不排队)
    public Task<string?> GetParentIdAsync() => _grain.GetParentAsync();
    public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => _grain.GetChildrenAsync();
}
```

**为什么 HandleEventAsync 走 Stream 而非同步 RPC**：
- **Client 不阻塞**：LLM 调用 5-30s、Workflow 分钟级，同步 RPC 会让 Client 长时间持连排队等待
- **统一入口**：外部和内部事件都走 `MassTransitEventHandler` 消费，不再有 RPC + Stream 两条路径
- **天然缓冲**：Grain 不可用时消息在 MassTransit 队列中缓冲，恢复后继续消费（RPC 直接失败）
- **内置韧性**：retry、DLQ、backpressure 全由 MassTransit 提供，无需 Client 自行实现
- **结果已走 Stream**：Orleans 端 `actor.Agent` 不可用，结果本就通过 Stream 订阅返回，RPC 同步等无实际价值

### 6.5 OrleansActorRuntime.cs — IActorRuntime（Client 端）

```csharp
public class OrleansActorRuntime : IActorRuntime
{
    private readonly IClusterClient _client;              // ★ Client 端用 IClusterClient
    private readonly IStreamProvider _streamProvider;     // ★ MassTransit Stream 工厂
    private readonly IAgentManifestStore _manifestStore;  // ★ Agent 索引

    public OrleansActorRuntime(IClusterClient client, IStreamProvider streamProvider,
        IAgentManifestStore manifestStore)
    {
        _client = client;
        _streamProvider = streamProvider;
        _manifestStore = manifestStore;
    }

    public async Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
        where TAgent : IAgent
        => await CreateAsync(typeof(TAgent), id, ct);

    public async Task<IActor> CreateAsync(Type agentType, string? id = null, CancellationToken ct = default)
    {
        var actorId = id ?? AgentId.New(agentType);
        var grain = _client.GetGrain<IGAgentGrain>(actorId);
        await grain.InitializeAgentAsync(agentType.AssemblyQualifiedName!);
        // Manifest 生命周期: 创建成功后写入索引
        await _manifestStore.SaveAsync(actorId, new AgentManifest
        {
            AgentId = actorId,
            AgentTypeName = agentType.AssemblyQualifiedName!
        }, ct);
        var stream = _streamProvider.GetStream(actorId);
        return new OrleansClientActor(actorId, grain, stream);
    }

    public async Task<IActor?> GetAsync(string id)
    {
        var grain = _client.GetGrain<IGAgentGrain>(id);
        // Orleans GetGrain 对虚拟 Actor 始终返回引用，需检查是否已初始化
        if (!await grain.IsInitializedAsync())
            return null;
        var stream = _streamProvider.GetStream(id);
        return new OrleansClientActor(id, grain, stream);
    }

    public async Task<bool> ExistsAsync(string id)
        => await _client.GetGrain<IGAgentGrain>(id).IsInitializedAsync();

    public async Task LinkAsync(string parentId, string childId, CancellationToken ct = default)
    {
        var parentGrain = _client.GetGrain<IGAgentGrain>(parentId);
        var childGrain = _client.GetGrain<IGAgentGrain>(childId);
        await parentGrain.AddChildAsync(childId);
        await childGrain.SetParentAsync(parentId);
    }

    public async Task UnlinkAsync(string childId, CancellationToken ct = default)
    {
        var childGrain = _client.GetGrain<IGAgentGrain>(childId);
        var parentId = await childGrain.GetParentAsync();
        if (parentId != null)
            await _client.GetGrain<IGAgentGrain>(parentId).RemoveChildAsync(childId);
        await childGrain.ClearParentAsync();
    }

    public async Task DestroyAsync(string id, CancellationToken ct = default)
    {
        // soft destroy: 解除拓扑 + 停用 + 删除 manifest
        var grain = _client.GetGrain<IGAgentGrain>(id);

        // 解除 parent 关系
        var parentId = await grain.GetParentAsync();
        if (parentId != null)
            await _client.GetGrain<IGAgentGrain>(parentId).RemoveChildAsync(id);

        // 解除所有 children 关系（并行）
        var children = await grain.GetChildrenAsync();
        await Task.WhenAll(children.Select(childId =>
            _client.GetGrain<IGAgentGrain>(childId).ClearParentAsync()));

        await grain.ClearParentAsync();
        await grain.DeactivateAsync();

        // Manifest 生命周期: 停用后删除索引（避免 GetAll/Restore 返回僵尸记录）
        await _manifestStore.DeleteAsync(id, ct);
    }

    // Orleans 无法原生枚举所有 Grain，依赖 IAgentManifestStore 索引
    public async Task<IReadOnlyList<IActor>> GetAllAsync()
    {
        var manifests = await _manifestStore.ListAsync();
        return manifests.Select(m => new OrleansClientActor(
            m.AgentId,
            _client.GetGrain<IGAgentGrain>(m.AgentId),
            _streamProvider.GetStream(m.AgentId)
        )).ToList();
    }

    // Orleans 虚拟 Actor 按需激活，无需手动恢复
    public Task RestoreAllAsync(CancellationToken ct = default) => Task.CompletedTask;
}
```

### 6.6 GrainEventPublisher.cs — Silo 端发布者

```csharp
/// <summary>
/// Silo-side IEventPublisher injected into Agent instances.
/// Uses GrainFactory (Grain base property) + MassTransit Stream.
/// </summary>
internal class GrainEventPublisher : IEventPublisher
{
    private readonly string _actorId;
    private readonly GAgentGrain _grain;           // 宿主 Grain（直接调用 PropagateEventAsync）
    private readonly IStreamProvider _streamProvider;

    public GrainEventPublisher(string actorId, GAgentGrain grain, IStreamProvider streamProvider)
    {
        _actorId = actorId; _grain = grain; _streamProvider = streamProvider;
    }

    // 广播发布: 直接调用 Grain 的 PropagateEventAsync（不经过 Stream 自回环）
    public Task PublishAsync<TEvent>(TEvent evt, EventDirection direction = EventDirection.Down,
        CancellationToken ct = default) where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = _actorId,
            Direction = direction,
        };
        // ★ 关键: 直接调 Grain 内部方法，不发到自己的 Stream
        // 避免自回环: publish → stream → consumer → grain.HandleEvent → agent.HandleEvent（重复处理）
        _ = _grain.PropagateEventAsync(envelope)
            .ContinueWith(t => _grain.Logger.LogError(t.Exception,
                "PropagateEventAsync failed for {ActorId}", _actorId),
                TaskContinuationOptions.OnlyOnFaulted);  // fire-and-forget + 异常日志
        return Task.CompletedTask;
    }

    // 点对点: 发到目标 Agent 的 MassTransit Stream (异步解耦)
    public async Task SendToAsync<TEvent>(string targetActorId, TEvent evt,
        CancellationToken ct = default) where TEvent : IMessage
    {
        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = _actorId,
            Direction = EventDirection.Self,
            TargetActorId = targetActorId,
        };
        var targetStream = _streamProvider.GetStream(targetActorId);
        await targetStream.ProduceAsync(envelope, ct);
    }
}
```

**设计要点**：
- `PublishAsync` **直接调用 `PropagateEventAsync`**（Grain 内部方法调用），不经过自己的 MassTransit Stream。避免"发到自己 Stream → Consumer 消费 → 又调 HandleEventAsync → Agent 重复处理"的自回环死循环。
- `SendToAsync` 走 MassTransit Stream（异步解耦），目标 Grain 通过 `MassTransitEventHandler` 消费。
- `PropagateEventAsync` 内部按 Direction 将消息发到 children/parent 的 MassTransit Stream。

### 6.7 MassTransitEventHandler.cs — Consumer → Grain 桥接

```csharp
/// <summary>
/// Bridges MassTransit Consumer to Orleans Grain.
/// MassTransit Consumer 收到消息后调用此 Handler，
/// Handler 通过 IGrainFactory 将消息路由到正确的 Grain。
/// </summary>
public class MassTransitEventHandler : IMassTransitEventHandler
{
    private readonly IGrainFactory _grainFactory;  // Silo 端用 IGrainFactory

    public async Task<bool> HandleEventAsync(string agentId, EventEnvelope envelope)
    {
        var grain = _grainFactory.GetGrain<IGAgentGrain>(agentId);
        await grain.HandleEventAsync(envelope.ToByteArray());
        return true;
    }
}
```

**这是 MassTransit 和 Orleans 的桥梁**：
- MassTransit Consumer 在 Silo 进程中运行
- Consumer 反序列化消息，提取 agentId
- 调用此 Handler，Handler 通过 `IGrainFactory`（Silo 内部）找到 Grain
- Grain 在自己的单线程上下文中处理事件

### 6.8 DI 注册 — 分 Client 端和 Silo 端

```csharp
// ── Client 端 (API 进程) ──
public static IServiceCollection AddAevatarOrleansClient(
    this IServiceCollection services)
{
    services.AddSingleton<IActorRuntime, OrleansActorRuntime>();
    // IClusterClient 由 Orleans Client 配置提供（UseOrleansClient / Co-host 自动注入）

    // ★ IStreamProvider: Client 端也需要，用于订阅 Agent 输出事件（结果返回通道）
    // 与 Silo 端共用同一 MassTransit 集群，Client 作为 Consumer 订阅 Agent Stream
    services.TryAddSingleton<IStreamProvider, MassTransitStreamProvider>();

    // IAgentManifestStore 由 Persistence 层注册（InMemoryManifestStore / 持久化实现）
    // 注意: InMemoryManifestStore 在 Aevatar.Runtime 中，需要项目引用
    services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();
    return services;
}

// ── Silo 端 (Orleans 进程) ──
public static ISiloBuilder AddAevatarOrleansSilo(
    this ISiloBuilder builder)
{
    builder.ConfigureServices(services =>
    {
        // ── MassTransit 桥接 ──
        services.AddSingleton<IMassTransitEventHandler, MassTransitEventHandler>();

        // ── Streaming ──
        services.TryAddSingleton<IStreamProvider, MassTransitStreamProvider>();

        // ── Persistence（与 LocalRuntime 对齐） ──
        // 业务状态持久化: GAgentBase<TState> 的 StateStore.Load/Save
        // ★ 缺少此注册会导致 Grain 休眠后业务状态丢失
        services.TryAddSingleton(typeof(IStateStore<>), typeof(InMemoryStateStore<>));
        // 事件溯源存储
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        // Agent 元数据: Module 恢复、Config 恢复、Manifest 生命周期
        services.TryAddSingleton<IAgentManifestStore, InMemoryManifestStore>();

        // ── Deduplication ──
        // MassTransit at-least-once 下的幂等保障
        services.TryAddSingleton<IEventDeduplicator, MemoryCacheDeduplicator>();

        // ── Context（Workflow / Agent 间上下文传播） ──
        services.TryAddSingleton<IRunManager, RunManager>();
        services.TryAddSingleton<IAgentContextAccessor, AsyncLocalAgentContextAccessor>();

        // Grain 自动注册（Orleans 扫描 Aevatar.Runtime.Orleans 程序集）
    });
    return builder;
}
```

**MassTransit 拓扑配置**（在 Silo Host 的 `Program.cs` 中）：

```csharp
// MassTransit 拓扑配置示例（RabbitMQ）
services.AddMassTransit(x =>
{
    // 注册 Consumer：每个 agent stream 对应一个 topic/queue
    // 命名规则: aevatar.agent.{actorId}
    x.AddConsumer<AgentEventConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.ReceiveEndpoint("aevatar-agent-events", e =>
        {
            e.ConfigureConsumer<AgentEventConsumer>(ctx);
            // 按 actorId 路由: 消息中的 TargetActorId 或从 topic 名提取
        });
    });
});
```

**MassTransit 拓扑说明**：
- 每个 Agent 的事件通过统一的 exchange/topic 发布，消息体包含 `targetActorId`
- `AgentEventConsumer` 从消息中提取 `targetActorId`，委托给 `MassTransitEventHandler`
- `MassTransitEventHandler` 通过 `IGrainFactory.GetGrain<IGAgentGrain>(targetActorId)` 路由到正确的 Grain
- 使用 `ConcurrencyLimit` 控制单个 Consumer 的并发度
- DLQ / Retry 策略由 MassTransit 配置提供（`UseMessageRetry`、`UseDelayedRedelivery`）

**Client 端 MassTransit 配置**（结果返回通道）：

Client 进程也需要配置 MassTransit 以订阅 Agent 输出事件。`MassTransitStreamProvider` 的 `GetStream(actorId).SubscribeAsync(...)` 内部使用 MassTransit Consumer 监听指定 Agent 的 stream。Client 和 Silo **共用同一 MassTransit 集群**（RabbitMQ/Kafka），但分属不同 Consumer Group：

- **Silo Consumer**（`AgentEventConsumer`）：消费传播事件，路由到 Grain 处理（事件入站）
- **Client Consumer**（`SubscribeAsync` 内部创建）：消费 Agent 发布的输出事件，回调给调用方（结果返回）

两者通过不同的 queue/consumer-group 隔离，互不干扰。

---

## 七、Abstractions 层接口策略（最终）

当前 `Aevatar.Abstractions` 的 `IStream` / `IStreamProvider` 已满足本项目需求。本文最终方案：**复用现有接口，不新增 `IMessageStream*`**。

**最终方案：复用现有 IStream / IStreamProvider**

现有接口已经足够抽象：

```csharp
public interface IStream {
    Task ProduceAsync<T>(T message, ...) where T : IMessage;
    Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, ...) where T : IMessage;
}
public interface IStreamProvider {
    IStream GetStream(string actorId);
}
```

MassTransit 适配器实现这两个接口即可。后续如果确实需要 category/topic 细粒度路由，可在不破坏兼容性的前提下给 `IStreamProvider` 增加可选重载。

**注意命名空间冲突**：Orleans 自身有 `Orleans.Streams.IStreamProvider`。本项目的 `Aevatar.IStreamProvider` 与之完全不同。实现文件中需要显式 `using Aevatar;` 并避免 `using Orleans.Streams;` 污染，或在必要时用 fully-qualified name 消歧义。

---

## 八、概念映射总表

| aevatar 概念 | 本地运行时 | Orleans 运行时 |
|---|---|---|
| `IAgent` | 直接实例化 | **Grain 内部实例化** |
| `IActor` | `LocalActor` (邮箱串行) | `OrleansClientActor` (Stream fire-and-forget) |
| `IActorRuntime` | `LocalActorRuntime` (ConcurrentDict) | `OrleansActorRuntime` (IClusterClient) |
| `IEventPublisher` | `LocalActorPublisher` (注入 Agent) | `GrainEventPublisher` (注入 Agent) |
| `IStream` | `InMemoryStream` | `MassTransitStream` |
| `IStreamProvider` | `InMemoryStreamProvider` | `MassTransitStreamProvider` |
| 串行保证 | `SemaphoreSlim` | **Orleans Grain 非重入（turn-based）** |
| 状态持久化 | `InMemoryStateStore` | `IPersistentState` (元数据) + `IStateStore` (业务) |
| Event Sourcing | `EventSourcingBehavior` (可选 Mixin) | 同上，Grain 按实例注入 |
| 层级关系 | `EventRouter` 内存 | **Grain State 持久化** |
| Agent 恢复 | `RestoreAllAsync` 扫描 manifest | **Orleans 虚拟 Actor 按需激活** |
| 外部入口 | `IActor.HandleEventAsync` (同步) | `OrleansClientActor` → MassTransit Stream (fire-and-forget) |
| 内部传播 | 进程内 Stream 发布 + 订阅 | **MassTransit Queue (fire-and-forget)** |
| Consumer 桥接 | 不需要 | `MassTransitEventHandler`（唯一消费入口） |
| 结果返回 | `streams.GetStream(id).SubscribeAsync(...)` | 同左（Client 端 `MassTransitStreamProvider` 订阅） |

---

## 九、实现优先级与步骤

### Phase 1：最小可运行（MVP）

| 步骤 | 文件 | 说明 | 工作量 |
|---|---|---|---|
| 1 | `Aevatar.Runtime.Orleans.csproj` | 项目骨架 + NuGet 引用 | 15 min |
| 2 | `Constants.cs` + `OrleansAgentState.cs` | 常量 + Grain 状态模型 | 15 min |
| 3 | `IGAgentGrain.cs` | Grain 接口定义 | 20 min |
| 4 | `MassTransitEventHandler.cs` | Consumer → Grain 桥接 | 30 min |
| 5 | `GrainEventPublisher.cs` | Silo 端 EventPublisher | 1 h |
| 6 | `GAgentGrain.cs` | **核心 Grain 实现** | 3-4 h |
| 7 | `OrleansClientActor.cs` | Client 端 Actor 代理 | 1 h |
| 8 | `OrleansActorRuntime.cs` | Client 端 Runtime | 1 h |
| 9 | `Stream/MassTransitStream.cs` | IStream 实现 | 30 min |
| 10 | `Stream/MassTransitStreamProvider.cs` | IStreamProvider 实现 | 20 min |
| 11 | `DependencyInjection/*` | Client + Silo DI 注册 | 30 min |

**MVP 总计约 8-10 小时**

### Destroy 语义（必须先定）

为避免和本地运行时语义冲突，`DestroyAsync` 约定如下：

- 默认 `soft destroy`：
  - 解除 parent/child 拓扑关系
  - 调用 `DeactivateAsync`
  - 删除 `IAgentManifestStore` 记录（避免 `GetAll/Restore` 返回僵尸）
  - 保留 `IStateStore<TState>` 业务状态（可恢复）
- 可选 `hard purge`（后续增强）：
  - 包含 soft destroy 全部操作
  - 额外清理 `OrleansAgentState` 元数据
  - 额外清理 `IStateStore<TState>` 业务状态

若短期不扩展 `DestroyAsync` 方法签名，则只实现 `soft destroy`，并在运行日志中明确。

### Manifest 生命周期约束（必须）

- `CreateAsync`：创建成功后写入 `IAgentManifestStore`（`agentId` + `agentTypeName`）。
- `DestroyAsync`：soft destroy 完成后删除 manifest（避免 `GetAll/Restore` 返回僵尸记录）。
- `GetAllAsync`：只从 manifest 枚举，然后构造 `OrleansClientActor` 引用。
- `RestoreAllAsync`：Orleans 下返回 `Task.CompletedTask`；若有离线修复流程，只修复 manifest，不批量激活 Grain。

### Event Sourcing 在 Orleans 中的适配

新增的 `IEventSourcingBehavior<TState>` 采用 Mixin 注入模式（不要求继承额外基类），Agent 可选启用 ES。Orleans 运行时需要适配以下要点：

#### 1. Grain 必须按 Agent 实例构造并注入 Behavior

`EventSourcingBehavior<TState>` 绑定到具体 `agentId`，不能做全局单例。`GAgentGrain.InjectAgentDependencies` 中需要：

```csharp
// 在 Grain 内创建 Agent 后：
private void InjectEventSourcingBehavior(IAgent agent, string agentId)
{
    var agentType = agent.GetType();
    // 遍历基类查找 GAgentBase<TState> 的 TState
    var type = agentType;
    while (type != null)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GAgentBase<>))
        {
            var stateType = type.GetGenericArguments()[0];
            var behaviorType = typeof(EventSourcingBehavior<>).MakeGenericType(stateType);
            var eventStore = ServiceProvider.GetService<IEventStore>();
            if (eventStore == null) break;

            var behavior = Activator.CreateInstance(behaviorType, eventStore, agentId);
            // 查找 Agent 上的 IEventSourcingBehavior<TState> 属性并注入
            var propName = "EventSourcing";
            var prop = agentType.GetProperty(propName);
            if (prop != null && prop.CanWrite)
                prop.SetValue(agent, behavior);
            break;
        }
        type = type.BaseType;
    }
}
```

#### 2. Silo 端必须注册持久化 IEventStore

`InMemoryEventStore` 在 Grain 重激活后数据丢失，生产环境需要持久化实现：

```csharp
// OrleansSiloExtensions.cs 中：
builder.ConfigureServices(services =>
{
    // InMemoryEventStore 仅用于开发
    // 生产环境替换为: MongoEventStore / SqlEventStore 等
    services.TryAddSingleton<IEventStore, InMemoryEventStore>();
});
```

#### 3. Snapshot 策略在 Orleans 下更重要

Orleans 虚拟 Actor 会频繁激活/休眠，每次 `OnActivateAsync` 都要 `ReplayAsync` 全量事件。如果事件量大，激活延迟会显著增加。建议：

- 默认使用 `IntervalSnapshotStrategy(100)` 而非 `NeverSnapshotStrategy`
- Grain 在 `ConfirmEventsAsync` 后检查策略，按需调用 `IStateStore<TState>.SaveAsync` 写快照
- `ReplayAsync` 优先从快照版本起重放（需要 `IEventStore.GetEventsAsync(agentId, fromVersion)` 配合）

#### 4. 与 Grain 单线程模型的兼容

- `RaiseEvent` + `ConfirmEventsAsync` 在 Grain 内天然串行执行，无并发问题
- `IEventStore.AppendAsync` 的 `expectedVersion` 乐观并发仍然有效（防止异常场景下的重复追加）
- ES Agent 的 `OnActivateAsync` 中 `ReplayAsync` 在 Grain 激活期执行，不会与事件处理竞争

#### 5. 不影响非 ES Agent

Mixin 模式保证了：不注入 `IEventSourcingBehavior<TState>` 的 Agent 行为与现有完全一致，仅使用 `IStateStore` 的 Load/Save。Orleans Runtime 不需要为非 ES Agent 做任何额外处理。

---

### Phase 2：集成验证

- 配置 MassTransit + RabbitMQ/Kafka
- 配置 Orleans Silo + Storage Provider
- 端到端测试：API → MassTransit → Grain → Agent → 层级传播 → 结果返回

### Phase 3：可选增强

| 增强项 | 来源 |
|---|---|
| CQRS 投影 | 参考 `CQRS/` |
| 上下文桥接 | 参考 `Context/` |
| 订阅健康检查 | 参考 `Subscription/` |
| 持久化 IEventStore 实现 | MongoDB / SQL 等（替换 InMemoryEventStore） |
| Snapshot 自动化 | 在 Behavior 内自动衔接 ISnapshotStrategy + IStateStore |

> **注意**：Event Sourcing 核心（`IEventSourcingBehavior` Mixin 注入、`ReplayAsync` 恢复、`IEventStore` 注册）已纳入 Phase 1 基础实现，不再是可选项。Phase 3 中的 ES 相关项是**生产增强**（持久化存储、自动快照）。

---

## 十、NuGet 依赖

```xml
<!-- Aevatar.Runtime.Orleans.csproj -->
<ItemGroup>
  <!-- Orleans -->
  <PackageReference Include="Microsoft.Orleans.Sdk" />

  <!-- MassTransit -->
  <PackageReference Include="MassTransit" />
  <!-- 传输层选一个: -->
  <!-- <PackageReference Include="MassTransit.RabbitMQ" /> -->
  <!-- <PackageReference Include="MassTransit.Kafka" /> -->

  <!-- 项目引用 -->
  <ProjectReference Include="../Aevatar.Core/Aevatar.Core.csproj" />
  <ProjectReference Include="../Aevatar.Abstractions/Aevatar.Abstractions.csproj" />
  <!-- MVP 默认: 为了直接复用 InMemoryEventStore，增加此引用 -->
  <ProjectReference Include="../Aevatar.Runtime/Aevatar.Runtime.csproj" />
</ItemGroup>
```

> 若不希望 `Aevatar.Runtime.Orleans` 依赖 `Aevatar.Runtime`，可将 `InMemoryEventStore` 下沉到 `Aevatar.Core` 后再移除此引用。

Silo Host 额外需要：
```xml
<PackageReference Include="Microsoft.Orleans.Server" />
<PackageReference Include="Microsoft.Orleans.Persistence.Memory" /> <!-- 或 MongoDB/Azure -->
```

---

## 十一、风险与注意事项

1. **MassTransit Consumer 注册**：需要在 Silo 端注册 MassTransit Consumer（`AgentEventConsumer`），确保能正确将消息路由到 `MassTransitEventHandler`。消息体中须包含 `targetActorId`，Consumer 据此路由到对应 Grain。

2. **环路保护**：使用 `metadata["__publishers"]` 链路去重。`HandleEventAsync` 在处理前检查 publishers 列表，`PropagateEventAsync` 在传播前追加自身 id。由于 `GrainEventPublisher.PublishAsync` 直接调用 `PropagateEventAsync`（不经过 Stream 自回环），不存在 publish → consume → re-handle 的死循环。

3. **Client/Silo 分离部署**：如果 API 进程和 Silo 进程分开部署，`IClusterClient` 需要配置正确的集群连接。如果 co-hosted（API 和 Silo 在同一进程），可以直接用 `IGrainFactory`。

4. **Protobuf 序列化**：Grain 接口使用 `byte[]` 传输 EventEnvelope。MassTransit 消息体也用 `byte[]`（或自定义序列化器）。两层序列化需要保持一致。

5. **层级操作的事务性**：`LinkAsync` 同时修改父 Grain 和子 Grain 的状态，是跨 Grain 操作，无法保证原子性。需要设计补偿逻辑（或接受最终一致）。

6. **Cognitive 层兼容**：`WorkflowGAgent` 通过 `IActorRuntime.CreateAsync` 创建子 Agent，然后 `LinkAsync` 建立层级。需要确保 Orleans Runtime 的行为与 Local Runtime 一致，特别是 Link 之后事件传播的语义。

7. **Event Sourcing Behavior 注入依赖反射**：`InjectEventSourcingBehavior` 通过反射遍历基类查找 `GAgentBase<TState>` 的泛型参数，然后动态构造 `EventSourcingBehavior<TState>`。如果 Agent 使用自定义 Behavior（重写了 `TransitionState`），需要额外注册机制（如 DI 中按类型注册工厂），否则默认的 `TransitionState` 不会应用事件。建议：在 Silo DI 中支持 `services.AddEventSourcingBehavior<TState, TBehavior>()` 注册自定义 Behavior 类型，Grain 注入时优先从 DI 解析。

8. **投递语义与顺序保证**：MassTransit 默认 at-least-once，需要补充：
   - 幂等键（建议 `EventEnvelope.Id` + `IEventDeduplicator`）
   - 同一 Agent 的顺序策略（按 `actorId` 分区或限制 consumer 并发）
   - 重试与死信队列策略（retry/backoff/DLQ）

9. **Type.GetType 跨进程约束**：`InitializeAgentAsync` 在 Silo 端用 `Type.GetType(assemblyQualifiedName)` 反射创建 Agent。如果 Client 和 Silo 分离部署，Silo 进程必须引用所有 Agent 类型所在的程序集。建议在 Silo Host 的 `.csproj` 中显式引用所有 Agent 项目。

10. **InMemory 实现依赖位置**：`InMemoryEventStore`、`InMemoryStateStore<>`、`InMemoryManifestStore` 均在 `Aevatar.Runtime` 项目中。如果 `Aevatar.Runtime.Orleans` 仅引用 `Aevatar.Core` + `Aevatar.Abstractions`，Silo DI 注册会编译失败。解决方案：(a) `Aevatar.Runtime.Orleans` 的 `.csproj` 增加对 `Aevatar.Runtime` 的引用；(b) 将所有 InMemory 实现移到 `Aevatar.Core`；(c) Silo Host 项目直接引用 `Aevatar.Runtime`。推荐 (c)，MVP 阶段最小改动。

11. **Silo DI 注册完整性（新发现 ★）**：以下服务在 `LocalRuntime.ServiceCollectionExtensions.AddAevatarRuntime()` 中注册，但之前的 Silo DI 伪代码中缺失，已补齐。实现时请对照 `ServiceCollectionExtensions.cs` 逐项核验：

    | 服务接口 | Silo 注册实现 | 说明 |
    |---|---|---|
    | `IStateStore<>` | `InMemoryStateStore<>` | 业务状态持久化，缺少则 Grain 休眠后丢状态 |
    | `IAgentManifestStore` | `InMemoryManifestStore` | Module/Config 恢复，缺少则 ActivateAsync 会 null |
    | `IEventDeduplicator` | `MemoryCacheDeduplicator` | MassTransit at-least-once 幂等保障 |
    | `IRunManager` | `RunManager` | Workflow 执行/取消管理 |
    | `IAgentContextAccessor` | `AsyncLocalAgentContextAccessor` | Agent 间上下文传播 |

12. **Observability 对齐**：本地 Runtime 已实现 `AevatarActivitySource`（OpenTelemetry 分布式追踪）和 `AgentMetrics`（System.Diagnostics.Metrics 指标采集），包含 `events_handled`、`handler_duration_ms`、`active_actors` 等 metric。Orleans Grain 必须在 `HandleEventAsync` 入口/出口处同样接入，否则无法在 Grafana/Prometheus 中观测 Grain 事件处理性能。已在 `GAgentGrain.HandleEventAsync` 伪代码中补齐。

---

## 十二、兼容性测试清单（建议纳入 DoD）

1. **接口兼容**
   - `Aevatar.Abstractions` 无 breaking change
   - `IActorRuntime` 行为与 `LocalActorRuntime` 对齐（Create/Get/Link/Unlink/Destroy）

2. **事件语义**
   - `HandleEventAsync` 外部入口走 MassTransit Stream（fire-and-forget，`await` 返回即消息入队成功）
   - `PublishAsync` / `SendToAsync` / `PropagateEventAsync` 走 MassTransit 异步传播
   - 业务结果通过 Stream 订阅异步返回
   - 重复投递时业务不重复执行（幂等验证）

3. **路由与环路**
   - Down/Up/Both 传播正确
   - `metadata["__publishers"]` 生效，复杂拓扑无死循环

4. **生命周期**
   - Grain 重激活后可恢复处理
   - `DestroyAsync` soft 语义可观测且一致

5. **Event Sourcing**
   - ES Agent 激活时 `ReplayAsync` 正确恢复 State
   - `RaiseEvent` + `ConfirmEventsAsync` 在 Grain 内持久化到 `IEventStore`
   - Grain 休眠后重新激活，State 通过事件重放恢复一致
   - 非 ES Agent 不受影响（Mixin 不注入时行为不变）
   - Snapshot 策略配合 `IStateStore` 减少重放长度（可选）

6. **DI 完整性**
   - Silo 端所有服务可解析（`IStateStore<>`、`IAgentManifestStore`、`IEventDeduplicator`、`IRunManager`、`IAgentContextAccessor`）
   - 缺少任一注册时，启动或首次事件处理抛出明确异常而非 NullReference
   - Client 端 `IStreamProvider` 可解析且能 Subscribe/Produce

7. **Observability**
   - `HandleEventAsync` 每次调用产生 `Aevatar.Agents` ActivitySource span
   - `aevatar.agent.events_handled` counter 递增
   - `aevatar.agent.handler_duration_ms` histogram 有记录
   - Grafana/Prometheus 仪表板可观测 Grain 级别指标

8. **Idempotency（幂等）**
   - 相同 `EventEnvelope.Id` 连续投递 N 次，业务只执行 1 次
   - `IEventDeduplicator` 窗口过期后可再次处理（TTL 可配）

9. **Cognitive 回归**
   - `WorkflowGAgent` + `RoleGAgent` 在 Orleans runtime 下可跑通主流程

10. **性能基线（MVP）**
   - 单 Agent 热点压测（同一 `actorId`）：持续 5 分钟无错误、无死锁
   - 同一 `actorId`：P95 `< 80ms`，P99 `< 150ms`（不含下游外部依赖）
   - 多 Agent 并行（>= 1000 actorIds）：吞吐随实例数线性增长趋势可观测

---

## 十三、总结

本次 Orleans Runtime 的实现策略是 **"全链路异步 MassTransit + 单一消费入口"**：

- **仅 MassTransit Stream**：外部入口和内部层级传播统一走 MassTransit（fire-and-forget），不使用 Orleans Stream
- **外部入口 fire-and-forget**：`OrleansClientActor.HandleEventAsync` 发到 MassTransit Stream 后立即返回，Client 不阻塞（LLM 5-30s / Workflow 分钟级场景下避免排队超时）
- **单一消费入口**：所有事件（外部 + 内部传播）由 `MassTransitEventHandler` 统一消费，Grain 不订阅自己的 Stream（避免双重消费）
- **无自回环**：`GrainEventPublisher.PublishAsync` 直接调用 Grain 内部的 `PropagateEventAsync`，不经过自己的 MassTransit Stream
- **非重入 Grain**：不使用 `[Reentrant]`，保证 Agent 状态安全；只读和层级操作通过 `[AlwaysInterleave]` 允许并发；`PropagateEventAsync` fire-and-forget 减少单次处理耗时
- **结果返回走 Stream 订阅**：Client 端注册 `MassTransitStreamProvider`，通过 `streams.GetStream(actorId).SubscribeAsync(...)` 接收 Agent 输出事件（与 LocalRuntime 用法一致）
- **天然韧性**：Grain 不可用时消息缓冲在队列中，retry/DLQ/backpressure 全由 MassTransit 提供
- **Client/Silo 分离**：Client 端用 `IClusterClient`（层级查询）+ `IStreamProvider`（事件入站 + 结果订阅）；Silo 端用 `IGrainFactory` + MassTransit 消费
- **Silo DI 与 LocalRuntime 完全对齐**：`IStateStore<>`、`IAgentManifestStore`、`IEventDeduplicator`、`IRunManager`、`IAgentContextAccessor` 全部注册，确保 Agent 在 Grain 内拥有与本地 Runtime 一致的依赖环境
- **Observability**：`GAgentGrain.HandleEventAsync` 接入 `AevatarActivitySource`（分布式追踪）和 `AgentMetrics`（事件计数 + 处理耗时），与 LocalActor 观测能力对齐
- **幂等保障**：`HandleEventAsync` 入口处增加 `IEventDeduplicator` 检查，配合 MassTransit at-least-once 实现 exactly-once 业务语义
- **12 个文件**实现完整契约，不改动 `Aevatar.Abstractions` 和 `Aevatar.Core`
- **可替换**：通过 DI 切换 `AddAevatarRuntime()` → `AddAevatarOrleansClient()` 即可
- **Event Sourcing 兼容**：Grain 按 Agent 实例注入 `IEventSourcingBehavior<TState>` Mixin，激活时自动 `ReplayAsync`；Silo 端注册 `IEventStore`；Snapshot 策略减少重放延迟
