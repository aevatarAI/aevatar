# Aevatar.CQRS.Projection.Core

`Aevatar.CQRS.Projection.Core` 是 actorized projection runtime 内核。当前框架只保留两条主链：

- `Durable Materialization`
- `Session Observation`

两条链路都以 `scope actor` 为唯一运行态事实源，host 侧只保留薄适配层。

## 核心抽象

### durable materialization

- [IProjectionMaterializer.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializer.cs)
- [IProjectionMaterializerKinds.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializerKinds.cs)
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

## 当前运行时

- scope identity：
  - [ProjectionRuntimeScopeKey.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionRuntimeScopeKey.cs)
  - [ProjectionRuntimeMode.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionRuntimeMode.cs)
- scope actor runtime：
  - [ProjectionScopeGAgentBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs)
  - [ProjectionMaterializationScopeGAgentBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionMaterializationScopeGAgentBase.cs)
  - [ProjectionSessionScopeGAgentBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSessionScopeGAgentBase.cs)
  - [ProjectionScopeActorRuntime.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeActorRuntime.cs)
- host 侧薄适配：
  - [ProjectionMaterializationScopeActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionMaterializationScopeActivationService.cs)
  - [ProjectionMaterializationScopeReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionMaterializationScopeReleaseService.cs)
  - [ProjectionSessionScopeActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSessionScopeActivationService.cs)
  - [ProjectionSessionScopeReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSessionScopeReleaseService.cs)
  - [EventSinkProjectionLifecyclePortBase.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs)
- session stream：
  - [ProjectionSessionEventHub.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Streaming/ProjectionSessionEventHub.cs)

## 关键约束

- scope actor 持有 projection 的存在性、处理水位、失败状态和 release 状态
- host 侧不保留 `actorId/sessionId/scopeId -> runtime` 长期注册表
- durable 只消费 committed observation
- durable materializer 必须显式区分：
  - `ICurrentStateProjectionMaterializer<TContext>`：actor-scoped current-state replica，不能依赖回读旧文档
  - `IProjectionArtifactMaterializer<TContext>`：derived durable artifact，不再伪装成 canonical readmodel
- durable materialization turn 不承载业务 continuation；需要由 committed fact 推进业务协议时，必须在业务模块内建模独立 observer/continuation actor，并通过标准 dispatch port 派发 command
- session 只负责发布 session event stream，不再把 live sink 当作生命周期事实
