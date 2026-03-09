# Aevatar.Foundation.Runtime.Implementations.Local

`Aevatar.Foundation.Runtime.Implementations.Local` 提供 `IActorRuntime` 与 `IActorDispatchPort` 的本地进程内实现，负责：

- `LocalActorRuntime`：Actor 创建/销毁/按需物化（local activation index 驱动）。
- `LocalActorDispatchPort`：向目标 Actor mailbox 定向投递 `EventEnvelope`。
- `LocalActor`：单 Actor mailbox 串行执行与订阅管理。
- `LocalActorPublisher`：按 `EventDirection` 做本地路由与转发。
- `LocalActorTypeProbe`：本地运行时类型探测。
- `AddAevatarRuntime(...)`：一键装配本地运行时 + 内存流 + 默认持久化与事件溯源组件。

该项目依赖 `Aevatar.Foundation.Runtime` 中的通用运行时组件（Routing/Streaming/Persistence/Observability），并与 Orleans 实现保持对称。
