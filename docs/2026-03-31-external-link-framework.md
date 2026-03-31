# Actor 外部长连接框架（External Link）

## 1. 背景

当前 Actor 与外部服务的交互只有 `IConnector`（无状态 request-response）一条路径。
业务场景中，Actor 经常需要与外部服务保持双向长连接：行情推送（WebSocket）、消息队列订阅（MQTT）、流式 RPC（gRPC streaming）、设备通信（TCP）等。

现有架构约束不允许 Actor 直接持有物理连接句柄——Actor 是单线程事件驱动的，I/O 回调不能直接读写运行态。需要一套框架级能力，在 Infrastructure 层持有连接，Actor 层只消费/发送 Protobuf 事件。

### 1.1 设计目标

| 目标 | 说明 |
|------|------|
| 双向通信 | Actor 既能接收外部推送，也能主动发送 |
| Actor 生命周期绑定 | activate 建连，deactivate 断开，迁移后在新节点重建 |
| 传输无关 | WebSocket / gRPC stream / MQTT / TCP / SSE 统一抽象 |
| 单线程安全 | 入站消息走标准 EventEnvelope → Actor event pipeline |
| 自动重连 | 指数退避 + 状态变化全部事件化 |
| 多连接 | 单个 Actor 可同时维护多条不同外部连接 |

### 1.2 设计原则

1. **边界适配，不替换内核**：连接管理在 Infrastructure 层，Actor 内部只看到事件。
2. **回调只发信号**：I/O 线程收到消息后，包装为 EventEnvelope 通过 `IActorDispatchPort` 投递，不直接改状态。
3. **声明式**：Actor 只声明"我要连什么"，框架自动管理生命周期。
4. **Protobuf 优先**：连接事件、状态变化全部 Protobuf 定义。
5. **传输插件化**：每种协议独立实现，DI 注册，按 `transportType` 路由。

## 2. 整体架构

```
┌──────────────────────────────────────────────────────┐
│                    GAgent (Actor)                      │
│                                                        │
│  [EventHandler] HandleMessage(...)   ← 入站：标准事件   │
│  _linkPort.SendAsync(linkId, msg)    → 出站：抽象端口   │
│                                                        │
│  implements IExternalLinkAware                         │
│    → GetLinkDescriptors()  // 声明需要哪些连接          │
└───────────────┬──────────────────────┬─────────────────┘
                │ EventEnvelope        │ IExternalLinkPort
                │ (via DispatchPort)   │
┌───────────────▼──────────────────────▼─────────────────┐
│              ExternalLinkManager (per-actor)            │
│                                                        │
│  ManagedLink["market-feed"]  ←→  WebSocketTransport    │
│  ManagedLink["event-bus"]    ←→  MqttTransport         │
│                                                        │
│  职责：建连、重连、编解码、事件投递                        │
└───────────────────────────┬────────────────────────────┘
                            │ 物理连接
                    ┌───────▼───────┐
                    │  外部服务       │
                    └───────────────┘
```

## 3. 抽象层设计

### 3.1 连接描述符

Actor 声明"我要连什么"，不关心怎么连。

```csharp
// src/Aevatar.Foundation.Abstractions/ExternalLinks/ExternalLinkDescriptor.cs

public sealed record ExternalLinkDescriptor(
    string LinkId,                // Actor 内唯一标识，如 "market-feed"
    string TransportType,         // "websocket" / "grpc-stream" / "mqtt" / "tcp"
    string Endpoint,              // 连接地址
    ExternalLinkOptions Options   // 可选配置
);

public sealed record ExternalLinkOptions
{
    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxReconnectAttempts { get; init; } = 0;   // 0 = 无限重试
    public string PayloadCodec { get; init; } = "protobuf"; // "protobuf" / "json"
}
```

### 3.2 Actor 声明接口

```csharp
// src/Aevatar.Foundation.Abstractions/ExternalLinks/IExternalLinkAware.cs

public interface IExternalLinkAware
{
    IReadOnlyList<ExternalLinkDescriptor> GetLinkDescriptors();
}
```

### 3.3 Actor 侧发送端口

Actor 注入此接口发送出站消息，不接触物理连接。

```csharp
// src/Aevatar.Foundation.Abstractions/ExternalLinks/IExternalLinkPort.cs

public interface IExternalLinkPort
{
    Task SendAsync(string linkId, IMessage payload, CancellationToken ct = default);
    Task DisconnectAsync(string linkId, CancellationToken ct = default);
}
```

### 3.4 传输实现契约

每种协议实现一个 Transport + Factory。

```csharp
// src/Aevatar.Foundation.Abstractions/ExternalLinks/IExternalLinkTransport.cs

public interface IExternalLinkTransport : IAsyncDisposable
{
    string TransportType { get; }
    Task ConnectAsync(ExternalLinkDescriptor descriptor, CancellationToken ct);
    Task SendAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);

    // 由 runtime 设置——回调只发信号，不直接改 Actor 状态
    Func<ReadOnlyMemory<byte>, CancellationToken, Task>? OnMessageReceived { set; }
    Func<ExternalLinkStateChange, string?, CancellationToken, Task>? OnStateChanged { set; }
}

// src/Aevatar.Foundation.Abstractions/ExternalLinks/IExternalLinkTransportFactory.cs

public interface IExternalLinkTransportFactory
{
    bool CanCreate(string transportType);
    IExternalLinkTransport Create();
}
```

### 3.5 Protobuf 事件定义

```protobuf
// src/Aevatar.Foundation.Abstractions/external_link_messages.proto

syntax = "proto3";
package aevatar.foundation;
import "google/protobuf/any.proto";
import "google/protobuf/timestamp.proto";

// --- 连接状态事件（Infrastructure → Actor）---

message ExternalLinkConnectedEvent {
  string link_id = 1;
  google.protobuf.Timestamp connected_at = 2;
}

message ExternalLinkDisconnectedEvent {
  string link_id = 1;
  string reason = 2;
  bool will_reconnect = 3;
  int32 reconnect_attempt = 4;
}

message ExternalLinkReconnectingEvent {
  string link_id = 1;
  int32 attempt = 2;
  int32 delay_ms = 3;
}

message ExternalLinkErrorEvent {
  string link_id = 1;
  string error_message = 2;
  string error_code = 3;
}

// --- 数据事件（Infrastructure → Actor）---

message ExternalLinkMessageReceivedEvent {
  string link_id = 1;
  google.protobuf.Any payload = 2;       // codec 解码后的业务消息
  bytes raw_payload = 3;                  // codec 无法解码时的原始字节
  google.protobuf.Timestamp received_at = 4;
}

// --- 状态枚举 ---

enum ExternalLinkStateChange {
  EXTERNAL_LINK_STATE_CHANGE_UNSPECIFIED = 0;
  CONNECTED = 1;
  DISCONNECTED = 2;
  RECONNECTING = 3;
  ERROR = 4;
  CLOSED = 5;   // 主动关闭，不再重连
}
```

## 4. 核心实现层

### 4.1 ExternalLinkManager

per-actor 实例，随 Actor activate/deactivate 创建/销毁。

```
// src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkManager.cs

职责：
- 持有 Dictionary<string linkId, ManagedLink>（Infrastructure 层运行态，非中间层业务状态）
- Actor activate → 遍历 descriptors → 创建 transport → ConnectAsync
- Actor deactivate → 遍历所有 link → DisconnectAsync + DisposeAsync
- 入站消息：codec 解码 → 构建 EventEnvelope → IActorDispatchPort.DispatchAsync(actorId, ...)
- 出站消息：从 linkId 找到 ManagedLink → codec 编码 → transport.SendAsync(bytes)
- 重连循环：在 Task.Run 中运行，每次尝试前投递 ReconnectingEvent 给 Actor
```

### 4.2 ManagedLink

单条连接的运行态封装。

```
// src/Aevatar.Foundation.Core/ExternalLinks/ManagedLink.cs

包含：
- ExternalLinkDescriptor descriptor
- IExternalLinkTransport transport
- CancellationTokenSource reconnectCts   // 取消重连循环
- int currentAttempt
- bool isConnected
```

### 4.3 ExternalLinkPort

`IExternalLinkPort` 的实现，持有对 `ExternalLinkManager` 的引用。

```
// src/Aevatar.Foundation.Core/ExternalLinks/ExternalLinkPort.cs

SendAsync(linkId, payload):
  → manager.GetLink(linkId)
  → codec.Encode(payload) → bytes
  → transport.SendAsync(bytes)

DisconnectAsync(linkId):
  → manager.DisconnectLink(linkId)  // 标记 CLOSED，不再重连
```

## 5. 消息流转详解

### 5.1 入站（外部 → Actor）

```
I/O 线程：Transport.OnMessageReceived(bytes)
  │
  ▼
ExternalLinkManager：
  codec.Decode(bytes) → IMessage payload
  构建 EventEnvelope {
    payload = Any.Pack(ExternalLinkMessageReceivedEvent {
      link_id, payload = Any.Pack(业务消息), received_at
    })
    route = DirectRoute { actor_id = ownerActorId }
  }
  │
  ▼
IActorDispatchPort.DispatchAsync(actorId, envelope)
  │
  ▼
Actor inbox → 事件处理主线程
  [EventHandler] HandleMessage(ExternalLinkMessageReceivedEvent evt)
  → 单线程安全 ✓
```

### 5.2 出站（Actor → 外部）

```
Actor 事件处理主线程：
  _linkPort.SendAsync("market-feed", subscribeRequest)
  │
  ▼
ExternalLinkPort → ExternalLinkManager.GetLink("market-feed")
  │
  ▼
codec.Encode(subscribeRequest) → bytes
  │
  ▼
transport.SendAsync(bytes)  // 直接写物理连接，无需回到 Actor 线程
```

### 5.3 连接状态变化

```
I/O 线程：Transport.OnStateChanged(DISCONNECTED, reason)
  │
  ▼
ExternalLinkManager：
  判断是否重连（descriptor.Options.MaxReconnectAttempts）
  │
  ├─ 需要重连：
  │   构建 ExternalLinkDisconnectedEvent { will_reconnect = true }
  │   → DispatchAsync 给 Actor
  │   启动重连循环（Task.Run）：
  │     loop:
  │       delay = min(baseDelay * 2^attempt, maxDelay) + jitter
  │       构建 ExternalLinkReconnectingEvent { attempt, delay_ms }
  │       → DispatchAsync 给 Actor
  │       await Task.Delay(delay)
  │       transport.ConnectAsync(descriptor)
  │       成功 → 构建 ExternalLinkConnectedEvent → DispatchAsync → break
  │       失败 → attempt++ → continue
  │
  └─ 不重连：
      构建 ExternalLinkDisconnectedEvent { will_reconnect = false }
      → DispatchAsync 给 Actor
```

## 6. Actor 生命周期集成

### 6.1 Activate

```
RuntimeActorGrain.ActivateAsync()
  → agent.ActivateAsync()         // 标准 GAgent 激活
  → if agent is IExternalLinkAware:
      descriptors = agent.GetLinkDescriptors()
      manager = new ExternalLinkManager(actorId, descriptors, dispatchPort, transportFactories)
      linkPort = new ExternalLinkPort(manager)
      // 注入 linkPort 到 agent（通过 DI 或属性注入）
      manager.StartAllAsync()     // 并发建连
```

### 6.2 Deactivate

```
RuntimeActorGrain.DeactivateAsync()
  → manager.StopAllAsync()        // 断开所有连接，取消所有重连循环
  → manager.DisposeAsync()        // 释放所有 transport
  → agent.DeactivateAsync()       // 标准 GAgent 停用
```

### 6.3 Actor 迁移

```
Node A deactivate → 断开所有连接
Node B activate   → GetLinkDescriptors() → 重新建连
```

连接本身无需持久化。Actor 的业务状态（如"订阅了哪些 symbol"）在 Actor State 里，
activate 后通过 `OnActivateAsync` 重新发送订阅命令即可。

## 7. 传输实现（插件化）

每种协议独立项目，实现 `IExternalLinkTransport` + `IExternalLinkTransportFactory`，DI 注册。

| TransportType | 项目 | 依赖 |
|---------------|------|------|
| `websocket` | `Aevatar.Foundation.ExternalLinks.WebSocket` | `System.Net.WebSockets` |
| `grpc-stream` | `Aevatar.Foundation.ExternalLinks.Grpc` | `Grpc.Net.Client` |
| `mqtt` | `Aevatar.Foundation.ExternalLinks.Mqtt` | `MQTTnet` |
| `tcp` | `Aevatar.Foundation.ExternalLinks.Tcp` | `System.Net.Sockets` |

建议先实现 `websocket` 作为参考实现，其他按需扩展。

DI 注册示例：

```csharp
services.AddSingleton<IExternalLinkTransportFactory, WebSocketTransportFactory>();
services.AddSingleton<IExternalLinkTransportFactory, MqttTransportFactory>();
// runtime 内部按 transportType 路由到对应 factory
```

## 8. GAgent 使用示例

```csharp
public class MarketFeedGAgent : GAgentBase<MarketFeedState>, IExternalLinkAware
{
    private readonly IExternalLinkPort _linkPort;

    public MarketFeedGAgent(IExternalLinkPort linkPort)
    {
        _linkPort = linkPort;
    }

    // --- 声明连接 ---

    public IReadOnlyList<ExternalLinkDescriptor> GetLinkDescriptors() =>
    [
        new("binance-ws", "websocket", "wss://stream.binance.com/ws",
            new ExternalLinkOptions { ReconnectBaseDelay = TimeSpan.FromSeconds(2) })
    ];

    // --- 连接事件（标准 EventHandler，单线程安全）---

    [EventHandler]
    public Task HandleConnected(ExternalLinkConnectedEvent evt)
    {
        Logger.LogInformation("Link {LinkId} connected at {Time}", evt.LinkId, evt.ConnectedAt);
        // 连接建立后发送订阅请求
        return _linkPort.SendAsync(evt.LinkId,
            new SubscribeRequest { Channel = "btcusdt@ticker" });
    }

    [EventHandler]
    public Task HandleDisconnected(ExternalLinkDisconnectedEvent evt)
    {
        Logger.LogWarning("Link {LinkId} disconnected: {Reason}, willReconnect={WillReconnect}",
            evt.LinkId, evt.Reason, evt.WillReconnect);
        return Task.CompletedTask;
    }

    // --- 业务消息 ---

    [EventHandler]
    public async Task HandleMessage(ExternalLinkMessageReceivedEvent evt)
    {
        if (evt.LinkId != "binance-ws") return;

        var ticker = evt.Payload.Unpack<TickerUpdate>();
        await PersistDomainEventAsync(new PriceUpdatedEvent
        {
            Symbol = ticker.Symbol,
            Price = ticker.Price,
            Timestamp = evt.ReceivedAt
        });
    }
}
```

## 9. 与现有模式的关系

| 现有模式 | 关系 |
|---------|------|
| `IConnector` | 互补。IConnector 处理无状态 request-response；ExternalLink 处理有状态长连接。不替换。 |
| `IActorDispatchPort` | 复用。入站消息通过 DispatchAsync 投递给 Actor，与 Actor 间消息走同一路径。 |
| `IActorRuntimeCallbackScheduler` | 不依赖。重连用 Task.Delay（Infrastructure 层），不占用 Actor 的 durable timer 资源。 |
| Orleans Streaming | 不复用。Orleans Stream 是 Actor 间内部通道；ExternalLink 是 Actor 与外部系统的桥接。 |
| `IEventModule` | 可选组合。如果需要在 pipeline 层面统一处理所有 ExternalLink 事件，可以写一个 EventModule。 |

## 10. 文件清单

```
src/Aevatar.Foundation.Abstractions/ExternalLinks/
  ├── ExternalLinkDescriptor.cs        # 连接描述符 + 选项
  ├── IExternalLinkAware.cs            # Actor 声明接口
  ├── IExternalLinkPort.cs             # Actor 侧发送端口
  ├── IExternalLinkTransport.cs        # 传输实现契约
  └── IExternalLinkTransportFactory.cs # 传输工厂

src/Aevatar.Foundation.Abstractions/
  └── external_link_messages.proto     # Protobuf 事件定义

src/Aevatar.Foundation.Core/ExternalLinks/
  ├── ExternalLinkManager.cs           # per-actor 连接管理器
  ├── ExternalLinkPort.cs              # IExternalLinkPort 实现
  ├── ManagedLink.cs                   # 单条连接运行态
  └── ExternalLinkCodec.cs             # 消息编解码

src/Aevatar.Foundation.ExternalLinks.WebSocket/  # 参考传输实现
  ├── WebSocketTransport.cs
  ├── WebSocketTransportFactory.cs
  └── ServiceCollectionExtensions.cs
```

## 11. 验证计划

1. **编译**：`dotnet build aevatar.slnx`
2. **单元测试**：Mock `IExternalLinkTransport`，验证 Manager 的建连/断开/重连/消息转发逻辑
3. **集成测试**：内存 Transport 实现 + 真实 GAgent，验证端到端事件流转
4. **架构守卫**：`bash tools/ci/architecture_guards.sh` 通过
5. **迁移测试**：模拟 Actor deactivate → activate，验证连接恢复 + 业务状态重放

## 12. 待讨论

- [ ] 认证信息如何传递？放在 `ExternalLinkDescriptor` 里还是通过 Actor State 动态提供？
- [ ] 连接级别的背压（backpressure）策略：入站消息积压时是否需要限流？
- [ ] 是否需要支持"连接池"模式（多个 Actor 共享同一物理连接）？当前设计是 per-actor 独占。
- [ ] 出站 `SendAsync` 失败时的语义：静默丢弃 / 抛异常 / 发送 ErrorEvent？
