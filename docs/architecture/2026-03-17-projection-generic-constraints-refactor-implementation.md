# Projection 泛型约束重构实施文档（已落地）

## 一句话结论

泛型问题已经按“先删无语义参数，再给保留参数加硬约束”的顺序收口。

当前 projection core 保留的泛型只有三类：

- `TContext : IProjectionSessionContext`
- `TContext : IProjectionMaterializationContext`
- `TRuntimeLease : IProjectionRuntimeLease + IProjectionContextRuntimeLease<TContext>`

## 已删除

- `InitializeAsync(...)`
- `CompleteAsync(...)`
- `TTopology`
- `TCompletion`
- 依赖这些空生命周期的 coordinator/lifecycle 语义

## 当前根接口

### session projector

[IProjectionProjector.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionProjector.cs)

```csharp
public interface IProjectionProjector<in TContext>
    where TContext : IProjectionSessionContext
{
    ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
```

### durable materializer

[IProjectionMaterializer.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializer.cs)

```csharp
public interface IProjectionMaterializer<in TContext>
    where TContext : IProjectionMaterializationContext
{
    ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
```

## 当前运行时泛型约束

### session runtime

- [ProjectionLifecycleService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionLifecycleService.cs)
- [ContextProjectionActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionActivationService.cs)
- [ContextProjectionReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionReleaseService.cs)

核心约束：

- `TContext : class, IProjectionSessionContext`
- `TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>`

### durable runtime

- [ProjectionMaterializationLifecycleService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionMaterializationLifecycleService.cs)
- [ContextProjectionMaterializationActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationActivationService.cs)
- [ContextProjectionMaterializationReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationReleaseService.cs)

核心约束：

- `TContext : class, IProjectionMaterializationContext`
- `TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>`

## 新增的语义分离

### start request 分离

- [ProjectionSessionStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionSessionStartRequest.cs)
- [ProjectionMaterializationStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionMaterializationStartRequest.cs)

### activation/release 分离

- [IProjectionSessionActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionSessionActivationService.cs)
- [IProjectionSessionReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionSessionReleaseService.cs)
- [IProjectionMaterializationActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionMaterializationActivationService.cs)
- [IProjectionMaterializationReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionMaterializationReleaseService.cs)

这一步让类型系统直接表达：

- session path 有 `SessionId`
- durable path 没有 `SessionId`

## 当前原则

- 不再引入无独立语义的泛型参数
- 不再用 `class` 或 `object` 作为唯一约束
- 不再为少数 feature 的遗留行为在根接口里保留 `Initialize/Complete`
- feature 若需要额外语义，优先增加强类型 request/context/lease，而不是再扩一个弱泛型
