# Aevatar.CQRS.Projection.Abstractions

`Aevatar.CQRS.Projection.Abstractions` 只包含 CQRS 投影通用抽象。

## 包含内容

- 生命周期与编排：`IProjectionLifecycleService<,>`、`IProjectionCoordinator<,>`、`IProjectionDispatcher<>`、`IProjectionSubscriptionRegistry<>`
- 扩展抽象：`IProjectionProjector<,>`、`IProjectionEventReducer<,>`
- 失败回传：`IProjectionDispatchFailureReporter<>`
- 读模型存储：`IProjectionReadModelStore<,>`
- 运行时策略：`IProjectionRuntimeOptions`、`IProjectionClock`
- 流订阅复用：`IActorStreamSubscriptionHub<TMessage>`
- 投影上下文：`IProjectionContext`

## 约束

1. 不放业务 read model、业务 context、业务 service。
2. 不放 DI 装配、endpoint 或具体 store 实现。
3. 任何子系统都应依赖这些泛型抽象，而不是再造别名接口层。
4. `IProjectionEventReducer<,>` / `IProjectionEventApplier<,,>` 通过 `bool` 返回值表达是否发生读模型变更。
