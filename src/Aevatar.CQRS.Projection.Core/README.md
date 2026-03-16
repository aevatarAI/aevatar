# Aevatar.CQRS.Projection.Core

`Aevatar.CQRS.Projection.Core` 是与业务无关的 projection runtime 内核。当前架构已经拆成两条主链：

- `Durable Materialization`
- `Session Observation`

## 核心接口

### durable materialization

- [IProjectionMaterializer.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializer.cs)
- [IProjectionMaterializationContext.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionMaterializationContext.cs)
- [ProjectionMaterializationStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionMaterializationStartRequest.cs)
- [IProjectionMaterializationActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionMaterializationActivationService.cs)
- [IProjectionMaterializationReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionMaterializationReleaseService.cs)

### session observation

- [IProjectionProjector.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionProjector.cs)
- [IProjectionSessionContext.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionSessionContext.cs)
- [ProjectionSessionStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionSessionStartRequest.cs)
- [IProjectionSessionActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionSessionActivationService.cs)
- [IProjectionSessionReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionSessionReleaseService.cs)

## 运行时实现

- durable：
  - [ProjectionMaterializationLifecycleService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionMaterializationLifecycleService.cs)
  - [ContextProjectionMaterializationActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationActivationService.cs)
  - [ContextProjectionMaterializationReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationReleaseService.cs)
  - [MaterializationProjectionPortBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/MaterializationProjectionPortBase.cs)

- session：
  - [ProjectionLifecycleService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionLifecycleService.cs)
  - [ContextProjectionActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionActivationService.cs)
  - [ContextProjectionReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionReleaseService.cs)
  - [EventSinkProjectionLifecyclePortBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs)
  - [ProjectionSessionEventHub.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs)

## 关键约束

- 根接口不再有 `InitializeAsync/CompleteAsync`
- durable path 不再复用 `SessionId`
- `Context` 只表达 runtime scope，不表达业务事实
- query 语义不在 core 提供基类
