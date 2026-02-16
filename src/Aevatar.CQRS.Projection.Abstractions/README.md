# Aevatar.CQRS.Projection.Abstractions

`Aevatar.CQRS.Projection.Abstractions` 只包含 CQRS 投影通用抽象。

## 包含内容

- 生命周期与编排：`IProjectionLifecycleService<,>`、`IProjectionCoordinator<,>`、`IProjectionSubscriptionRegistry<>`
- 扩展抽象：`IProjectionProjector<,>`、`IProjectionEventReducer<,>`
- 读模型存储：`IProjectionReadModelStore<,>`
- 运行时策略：`IProjectionRuntimeOptions`、`IProjectionRunIdGenerator`、`IProjectionClock`
- 流订阅复用：`IActorStreamSubscriptionHub<TMessage>`
- run 上下文：`IProjectionRunContext`、`IProjectionCompletionDetector<>`

## 约束

1. 不放业务 read model、业务 context、业务 service。
2. 不放 DI 装配、endpoint 或具体 store 实现。
3. 任何领域（WorkflowExecution、CaseDemo 等）都应依赖这些泛型抽象，而不是再造别名接口层。
