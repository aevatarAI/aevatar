# Aevatar.Foundation.Runtime.Implementations.Orleans

`Aevatar.Foundation.Runtime.Implementations.Orleans` 提供 `IActorRuntime` 的 Orleans 并行实现，保持 Foundation 分层不变：

- `Aevatar.Foundation.Abstractions`：抽象契约（`IActorRuntime/IActor`）。
- `Aevatar.Foundation.Runtime.Implementations.Orleans`：Orleans 基础设施实现（Grain + Runtime 适配）。
- `Aevatar.Foundation.Runtime.Hosting`：通过 provider 进行装配选择。

## 核心组成

- `Actors/OrleansActorRuntime`：`IActorRuntime` 的 Orleans 实现。
- `Actors/OrleansActor`：客户端侧 `IActor` 代理。
- `Grains/RuntimeActorGrain`：实际承载 `IAgent` 的 Orleans Grain。
- `DependencyInjection/ServiceCollectionExtensions`：DI 注册入口。

## 当前语义边界

- Orleans 模式下 `IActor.Agent` 返回的是远程代理（`IAgent`），不保证可向下转型为具体 `GAgent` 实现。
- 依赖 `actor.Agent is SomeConcreteAgent` 的调用路径仍建议使用默认 `InMemory` provider。

## 使用方式

在宿主层先完成 Orleans `IGrainFactory` 注册，再调用：

```csharp
services.AddAevatarFoundationRuntimeOrleans();
```

或在 Silo：

```csharp
siloBuilder.AddAevatarFoundationRuntimeOrleans();
```

默认 provider 仍为 `InMemory`，Orleans 作为并行可选实现。
