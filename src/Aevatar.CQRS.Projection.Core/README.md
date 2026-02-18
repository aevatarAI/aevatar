# Aevatar.CQRS.Projection.Core

`Aevatar.CQRS.Projection.Core` 是与业务无关的 CQRS 投影内核实现。

## 项目边界

- `Aevatar.CQRS.Projection.Abstractions`
  - 仅定义通用契约：`IProjection*`、`IActorStreamSubscriptionHub<TMessage>`。
- `Aevatar.CQRS.Projection.Core`
  - 通用运行时实现：
    - `ProjectionCoordinator<TContext, TTopology>`
    - `ProjectionSubscriptionRegistry<TContext, TCompletion>`
    - `ProjectionLifecycleService<TContext, TCompletion>`
    - `ActorStreamSubscriptionHub<TMessage>`
    - `ProjectionAssemblyRegistration`
    - `GuidProjectionRunIdGenerator` / `SystemProjectionClock`
- 领域扩展项目
  - 在子系统内承载具体 context/read model/reducer/projector/service/DI。

## 设计原则

1. 内核只处理通用编排，不绑定任何业务模型。
2. 业务投影通过 `IProjectionEventReducer<,>` 与 `IProjectionProjector<,>` 扩展。
3. 订阅按 `actorId` 复用底层 stream，再分发到 run 级 context。

## 运行链路（通用）

1. `ProjectionLifecycleService.StartAsync` -> `Coordinator.InitializeAsync` + `SubscriptionRegistry.RegisterAsync`
2. actor stream 到达 `EventEnvelope` 后，`SubscriptionRegistry` 分发给 `Coordinator.ProjectAsync`
3. `Coordinator` 按顺序调用多个 projector
4. `ProjectionLifecycleService.CompleteAsync` -> `SubscriptionRegistry.UnregisterAsync` + `Coordinator.CompleteAsync`

## 扩展点

- 外部程序集自动注册：`ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(...)`
- 典型扩展：
  - 新 reducer：`IProjectionEventReducer<TReadModel, TContext>`
  - 新 projector：`IProjectionProjector<TContext, TTopology>`
