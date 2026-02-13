# Aevatar.Runtime

`Aevatar.Runtime` 提供本地运行时实现，负责把 `Aevatar.Core` 的 Agent 组织成可运行的 Actor 系统。

## 职责

- 提供 `IActorRuntime` 的本地实现 `LocalActorRuntime`
- 提供 `LocalActor` 与 `LocalActorPublisher`
- 提供内存 Stream、路由和默认持久化实现
- 提供运行时依赖注入入口 `AddAevatarRuntime()`

## 主要组件

```
Runtime/
├── Actor/
│   ├── LocalActor.cs
│   ├── LocalActorPublisher.cs
│   └── LocalActorRuntime.cs
├── Streaming/
│   ├── InMemoryStream.cs
│   ├── InMemoryStreamProvider.cs
│   └── StreamSubscription.cs
├── Routing/
│   ├── EventRouter.cs
│   └── InMemoryRouterStore.cs
├── Persistence/
│   ├── InMemoryStateStore.cs
│   ├── InMemoryEventStore.cs
│   ├── InMemoryManifestStore.cs
│   └── MemoryCacheDeduplicator.cs
├── Context/
├── Observability/
└── DependencyInjection/ServiceCollectionExtensions.cs
```

## Routing（新增说明）

`Routing` 现在不仅有路由器，还有可持久化的层级存储抽象：

- `EventRouter`
  - 维护当前 Actor 的父子关系（`ParentId` / `ChildrenIds`）
  - 通过 `RouteAsync(...)` 按 `EventDirection` 路由到 `Self / Up / Down / Both`
  - 使用 `EventEnvelope.metadata["__publishers"]` 做环路检测，避免重复传播
- `IRouterHierarchyStore`
  - 路由层级持久化接口（`Load/Save/Delete`）
- `InMemoryRouterStore`
  - `IRouterHierarchyStore` 的内存实现，适用于开发和测试

### 方向语义

- `Self`：仅本 Actor 处理，不转发
- `Up`：向父 Actor 转发
- `Down`：向所有子 Actor 转发
- `Both`：同时向父和子转发

## 快速使用

```csharp
var services = new ServiceCollection();
services.AddAevatarRuntime();
var sp = services.BuildServiceProvider();
var runtime = sp.GetRequiredService<IActorRuntime>();
```

## 依赖

- `Aevatar.Core`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Caching.Memory`
- `OpenTelemetry`
