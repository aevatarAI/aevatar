# Aevatar.Foundation.Runtime

**Aevatar.Foundation.Runtime** 是 Aevatar 的「运行时」层：它负责把多个 Agent 组织起来、在它们之间传递事件，并管理状态与流式输出。  

---

## 它做什么

- **组织 Agent**：按父子关系把 Agent 排成一棵树（例如一个工作流根节点 + 多个角色子节点）。
- **传递事件**：把「用户输入」「步骤请求」「LLM 回复」等事件准确送到对应的 Agent。
- **存储与去重**：在内存（或可替换的存储）里保存 Agent 状态、事件记录、路由关系，并做事件去重。
- **流式输出**：把运行过程以流的形式推送给调用方（例如 SSE）。

你不需要直接写 .NET 代码也能用 Aevatar；使用 **Aevatar.Hosts.Api** 的 HTTP 接口即可。Runtime 主要面向「想理解系统结构」或「要二次开发、替换实现」的读者。

---

## 核心概念（对应到目录）

| 概念 | 目录 | 说明 |
|------|------|------|
| **Actor 运行时** | `Actor/` | 创建和管理「Actor」（每个 Actor 里挂一个 Agent），提供本地实现 `LocalActorRuntime`。 |
| **事件流** | `Streaming/` | 内存版事件流与订阅，用于把运行中的事件推送给前端或下游。 |
| **路由** | `Routing/` | 维护 Agent 树的父子关系，按「方向」把事件发给当前节点、父节点或子节点。 |
| **持久化** | `Persistence/` | 状态存储、事件存储、Agent 配置（Manifest）的默认内存实现；可换成数据库等。 |
| **依赖注入** | `DependencyInjection/` | 一行配置注册整个运行时（`AddAevatarRuntime()`），供宿主程序使用。 |

---

## 目录结构一览

```
Runtime/
├── Actor/               # 单个 Actor、事件发布、运行时入口
├── Streaming/           # 内存流与订阅（如 SSE 推送）
├── Routing/             # 事件路由与层级存储
├── Persistence/         # 状态、事件、Manifest 的内存实现与去重
├── Context/             # 运行上下文
├── Observability/       # 可观测性（如指标）
└── DependencyInjection/ # 运行时注册入口
```

---

## 事件如何被「路由」

Runtime 维护一棵 **Agent 树**（父/子关系）。每个事件带一个**方向**，路由逻辑按方向决定谁收到：

| 方向 | 含义 |
|------|------|
| **Self** | 只给当前这个 Agent，不往上也不往下传。 |
| **Up** | 往父 Agent 传（例如子节点完成一步后上报给工作流）。 |
| **Down** | 往所有子 Agent 传（例如工作流把任务分给多个角色）。 |
| **Both** | 同时往父和子传（按需使用）。 |

路由会做**环路检测**（通过事件上的元数据），避免同一条消息在树里转圈。

---

## 持久化与存储（默认是内存）

当前默认实现都是**内存**的，适合开发、演示和单机部署：

- **状态存储**：Agent 的状态快照（如对话计数、工作流进度）。
- **事件存储**：若开启 Event Sourcing，用于保存事件流。
- **Manifest**：Agent 的配置与已挂载的模块列表。
- **路由层级**：父子关系。

生产环境可替换为数据库或其它持久化实现，接口由 Aevatar 抽象层定义。

---

## 作为使用者 / 集成方

- **只打算用 Aevatar 跑工作流、对话**：直接使用 **Aevatar.Hosts.Api** 的 HTTP 接口即可，无需关心 Runtime 内部。
- **想理解「一次 Chat 请求背后发生了什么」**：请求进入 Api → 由 Runtime 创建或复用 Workflow Agent → 事件在工作流与角色 Agent 之间按路由传递 → 结果通过 Streaming 推回。

---

## 作为开发者（如何接入 Runtime）

在宿主程序中通过「依赖注入」注册整个运行时，然后向容器请求 `IActorRuntime` 即可使用：

```csharp
var services = new ServiceCollection();
services.AddAevatarRuntime();
var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
```

之后由 Runtime 负责创建 Actor、投递事件、与 Stream/存储交互。  
（若你未使用 .NET，只需知道：Runtime 通过标准配置入口挂接到宿主，无需改 Aevatar 源码即可替换实现或扩展。）

---

## 依赖说明（面向开发者）

Runtime 依赖 Aevatar.Foundation.Core（Agent 基类与事件管道）、以及 .NET 的依赖注入与缓存、可观测性库。具体依赖见项目文件；部署时与 Aevatar.Hosts.Api 一起使用即可。
