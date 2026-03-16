# Projection Context 重构分析（已落地）

## 一句话结论

`Context` 现在已经被收敛成两种窄语义：

- `IProjectionMaterializationContext`
- `IProjectionSessionContext`

它不再是 feature 可以随意扩展的 bag，也不再承载业务字段。

## Context 现在到底是什么

### 1. durable materialization context

定义在 [IProjectionSessionContext.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionSessionContext.cs)：

- `RootActorId`
- `ProjectionKind`

它表示：

> 某个 actor-scoped durable materialization scope 的最小运行时身份。

### 2. session observation context

同文件中的 `IProjectionSessionContext` 在 materialization context 上增加：

- `SessionId`

它表示：

> 某个 externally observable projection session 的最小运行时身份。

## Context 不再是什么

它不再是：

- 业务事实
- query 输入
- committed state 快照
- live sink 句柄
- workflowName / input / startedAt / proposal payload bag

## 配套 start request

### session

- [ProjectionSessionStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionSessionStartRequest.cs)
  - `RootActorId`
  - `ProjectionKind`
  - `SessionId`

### durable materialization

- [ProjectionMaterializationStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionMaterializationStartRequest.cs)
  - `RootActorId`
  - `ProjectionKind`

这一步把 durable path 从 session identity 中彻底解耦了。

## 为什么之前会混乱

旧设计的主要问题是：

- materialization 与 session 共用 `ProjectionSessionStartRequest`
- durable path 被迫伪造 `SessionId = actorId`
- feature port 容易把 `workflowName/input/command bag` 顺手塞进 activation API

这会导致：

- runtime control plane 被业务字段污染
- session 语义误扩散到 durable path
- `Context` 看起来像什么都能装的 feature bag

## 当前文件落点

### 核心接口

- [IProjectionSessionContext.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/IProjectionSessionContext.cs)
- [ProjectionSessionStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionSessionStartRequest.cs)
- [ProjectionMaterializationStartRequest.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Core/ProjectionMaterializationStartRequest.cs)

### session activation

- [ContextProjectionActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionActivationService.cs)
- [ContextProjectionReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionReleaseService.cs)

### materialization activation

- [ContextProjectionMaterializationActivationService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationActivationService.cs)
- [ContextProjectionMaterializationReleaseService.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/Orchestration/ContextProjectionMaterializationReleaseService.cs)

## 各子系统结果

### workflow

- session context：
  - [WorkflowExecutionProjectionContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionContext.cs)
- materialization context：
  - [WorkflowExecutionMaterializationContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionMaterializationContext.cs)
  - [WorkflowBindingProjectionContext.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowBindingProjectionContext.cs)

workflow session activation 现在只保留 `rootActorId + commandId`，不再接受 `workflowName/input`。

### scripting

- session context：
  - [ScriptExecutionProjectionContext.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Orchestration/ScriptExecutionProjectionContext.cs)
  - [ScriptEvolutionSessionProjectionContext.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Orchestration/ScriptEvolutionSessionProjectionContext.cs)
- materialization context：
  - [ScriptExecutionMaterializationContext.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Orchestration/ScriptExecutionMaterializationContext.cs)
  - [ScriptAuthorityProjectionContext.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Orchestration/ScriptAuthorityProjectionContext.cs)
  - [ScriptEvolutionMaterializationContext.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Orchestration/ScriptEvolutionMaterializationContext.cs)

### platform

platform 当前全部是 materialization context，不再复用 session request：

- [ServiceCatalogProjectionContext.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Contexts/ServiceCatalogProjectionContext.cs)
- [ServiceConfigurationProjectionContext.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Governance.Projection/Contexts/ServiceConfigurationProjectionContext.cs)

## 强制规则

- projector/materializer 不得依赖 runtime handle
- feature 不得再向 context 增加业务查询字段
- session 语义只允许通过 `SessionId` 扩展
- durable path 只允许 `RootActorId + ProjectionKind`
