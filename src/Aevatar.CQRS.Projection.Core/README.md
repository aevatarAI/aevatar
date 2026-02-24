# Aevatar.CQRS.Projection.Core

`Aevatar.CQRS.Projection.Core` 是与业务无关的 CQRS 投影内核实现。

## 项目边界

- `Aevatar.CQRS.Projection.Core.Abstractions`
  - 仅定义通用契约：`IProjection*`、`IActorStreamSubscriptionHub<TMessage>`。
- `Aevatar.CQRS.Projection.Core`
  - 通用运行时实现：
    - `ProjectionCoordinator<TContext, TTopology>`
    - `ProjectionDispatcher<TContext, TTopology>`
    - `ProjectionSubscriptionRegistry<TContext>`
    - `ProjectionLifecycleService<TContext, TCompletion>`
    - `ProjectionLifecyclePortServiceBase<TLeaseContract, TRuntimeLease, TSink, TEvent>`
    - `ProjectionQueryPortServiceBase<TSnapshot, TTimelineItem, TRelationItem, TRelationSubgraph>`
    - `ActorStreamSubscriptionHub<TMessage>`
    - `ProjectionAssemblyRegistration`
    - `SystemProjectionClock`
- 领域扩展项目
  - 在子系统内承载具体 context/read model/reducer/projector/service/DI。

## 设计原则

1. 内核只处理通用编排，不绑定任何业务模型。
2. 业务投影通过 `IProjectionEventReducer<,>` 与 `IProjectionProjector<,>` 扩展。
3. 订阅按 `actorId` 复用底层 stream，再分发到投影上下文。
4. 同一事件分发到多个 projector 时采用全分支尝试，最终按 projector 顺序聚合失败信息统一上报。

## 运行链路（通用）

1. `ProjectionLifecycleService.StartAsync` -> `Coordinator.InitializeAsync` + `SubscriptionRegistry.RegisterAsync`
2. actor stream 到达 `EventEnvelope` 后，`SubscriptionRegistry` 分发给 `ProjectionDispatcher.DispatchAsync`
3. `ProjectionDispatcher` 调用 `Coordinator.ProjectAsync`
4. `Coordinator` 按服务注册顺序调用多个 projector
5. `ProjectionLifecycleService.CompleteAsync` -> `SubscriptionRegistry.UnregisterAsync` + `Coordinator.CompleteAsync`

## 扩展点

- 外部程序集自动注册：`ProjectionAssemblyRegistration.RegisterProjectionExtensionsFromAssembly(...)`
- 典型扩展：
  - 新 reducer：`IProjectionEventReducer<TReadModel, TContext>`
  - 新 projector：`IProjectionProjector<TContext, TTopology>`
