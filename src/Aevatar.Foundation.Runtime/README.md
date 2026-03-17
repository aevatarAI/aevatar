# Aevatar.Foundation.Runtime

**Aevatar.Foundation.Runtime** 是 Aevatar 的运行时基础组件层：提供流、路由、持久化、可观测性与停用钩子等通用能力，供 Local/Orleans 等实现复用。  

它的准确定位是：**构建在 stream 之上的 actor message runtime**。Stream 是传输骨架，Runtime 则在其上提供 Actor 生命周期、寻址、邮箱串行和拓扑语义。

---

## 它做什么

- **传递消息包络**：通过统一 Stream/Router 抽象把 `EventEnvelope` 送到目标节点。
- **存储与去重**：在内存（或可替换的存储）里保存事件记录、路由关系，并做事件去重。
- **流式输出**：把运行过程以流的形式推送给调用方（例如 SSE）。

这里的 `EventEnvelope` 是 runtime message envelope，不等于 Event Sourcing 持久化的领域事件。

你不需要直接写 .NET 代码也能用 Aevatar；使用 **Aevatar.Workflow.Host.Api** 的 HTTP 接口即可。Runtime 主要面向「想理解系统结构」或「要二次开发、替换实现」的读者。

---

## 核心概念（对应到目录）

| 概念 | 目录 | 说明 |
|------|------|------|
| **Actor 生命周期钩子** | `Actor/` | Runtime 停用钩子与分发器（用于空闲清理、事件裁剪触发）。 |
| **Envelope Stream** | `Streaming/` | `EventEnvelope` 的消息流与订阅，用于 Actor 间传输和向前端/下游推送运行消息。 |
| **路由** | `Routing/` | 维护 Agent 树的父子关系，按「方向」把 envelope 发给当前节点、父节点或子节点。 |
| **持久化** | `Persistence/` | Event Sourcing 所需的 EventStore 与快照存储默认实现；可替换为持久化后端。 |
| **可观测性** | `Observability/` | 指标与 tracing 辅助能力。 |

---

## 目录结构一览

```
Runtime/
├── Actor/               # 停用钩子与分发器
├── Streaming/           # 内存流与订阅（如 SSE 推送）
├── Routing/             # 事件路由与层级存储
├── Persistence/         # EventStore、SnapshotStore 与去重
└── Observability/       # 可观测性（如指标）
```

---

## `EventEnvelope` 如何被「路由」

Runtime 维护一棵 **Agent 树**（父/子关系）。每个事件带一个**方向**，路由逻辑按方向决定谁收到：

| 方向 | 含义 |
|------|------|
| **Self** | 只给当前这个 Actor/Agent，不往上也不往下传。 |
| **Up** | 往父 Actor 传（例如子节点完成一步后上报给工作流）。 |
| **Down** | 往所有子 Actor 传（例如工作流把任务分给多个角色）。 |
| **Both** | 同时往父和子传（按需使用）。 |

路由会做**环路检测**（通过事件上的元数据），避免同一条消息在树里转圈。

---

## 持久化与存储（默认是内存）

当前默认实现都是**内存**的，适合开发、演示和单机部署：

- **事件存储**：Event Sourcing 事实源，默认 `InMemoryEventStore`，可替换为 `FileEventStore`。
- **快照存储**：默认 `InMemoryEventSourcingSnapshotStore<TState>`；启用 `AddFileEventStore(...)` 后切换为 `FileEventSourcingSnapshotStore<TState>`。
- **路由层级**：父子关系。

Event Sourcing 默认启用自动快照与事件裁剪（快照成功后清理历史事件）：

- `EnableSnapshots`（默认 `true`）
- `SnapshotInterval`（默认 `200`）
- `EnableEventCompaction`（默认 `true`）
- `RetainedEventsAfterSnapshot`（默认 `0`）

可通过 `ActorRuntime:EventSourcing:*` 配置覆盖。

生产环境可替换为数据库或其它持久化实现，接口由 Aevatar 抽象层定义。当前仓库提供了 Garnet 后端实现：`src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet`（`AddGarnetEventStore(...)`）。

---

## Runtime 与 Event Sourcing 的边界

- Stream 里流动的是 `EventEnvelope`，这是运行时消息层。
- EventStore 里保存的是 `StateEvent`，这是事实层。
- Runtime 不直接等于 EventStore；它负责让 envelope 获得 Actor 语义，并把消息送到正确的 Actor 邮箱。

## 作为使用者 / 集成方

- **只打算用 Aevatar 跑工作流、对话**：直接使用 **Aevatar.Workflow.Host.Api** 的 HTTP 接口即可，无需关心 Runtime 内部。
- **想理解「一次 Chat 请求背后发生了什么」**：请求进入 Api → 由 Runtime 创建或复用 Workflow Actor → `EventEnvelope` 在工作流与角色 Actor 之间按路由传递 → Actor 视情况持久化领域事件 → 结果通过 Streaming / Projection 推回。

---

## 作为开发者（如何接入 Runtime）

本地运行时实现已经拆分到 `Aevatar.Foundation.Runtime.Implementations.Local`。  
在宿主程序中注册 Local 实现后，再向容器请求 `IActorRuntime` 与 `IActorDispatchPort`：

```csharp
using Aevatar.Foundation.Runtime.Implementations.Local.DependencyInjection;

var services = new ServiceCollection();
services.AddAevatarRuntime();
var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
var dispatchPort = sp.GetRequiredService<IActorDispatchPort>();
```

之后由 `IActorRuntime` 负责创建/查询/链接 Actor，`IActorDispatchPort` 负责投递 `EventEnvelope`，两者共同与 Stream/存储交互。  
（若你未使用 .NET，只需知道：Runtime 通过标准配置入口挂接到宿主，无需改 Aevatar 源码即可替换实现或扩展。）

---

## 并行实现（Provider）

Foundation Runtime 目前支持两种并行 Provider：

- `InMemory`（默认）：`Aevatar.Foundation.Runtime.Implementations.Local` 本地实现。
- `Orleans`：`Aevatar.Foundation.Runtime.Implementations.Orleans` 分布式运行时实现。

通过 `ActorRuntime:Provider` 选择：

```json
{
  "ActorRuntime": {
    "Provider": "InMemory"
  }
}
```

`Orleans` 模式要求宿主已注册 Orleans `IGrainFactory`/Client 或 Silo。

---

## 依赖说明（面向开发者）

Runtime 依赖 Aevatar.Foundation.Core（Agent 基类与事件管道）、以及 .NET 的依赖注入与缓存、可观测性库。具体依赖见项目文件；部署时与 Aevatar.Workflow.Host.Api 一起使用即可。
