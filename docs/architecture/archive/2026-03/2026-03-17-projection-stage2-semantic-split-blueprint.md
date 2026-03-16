# Projection 第二阶段重构蓝图：Current-State / Artifact 语义拆分

## 目标

第一阶段已经完成 framework runtime actorization：`durable materialization` 和 `session observation` 都回到 `scope actor` 事实源。

第二阶段不再加 runtime，而是做语义瘦身：

- `readmodel` 只表示 `actor-scoped current-state replica`
- `artifact` 明确表示 `derived durable artifact`
- 能从 committed state root 或完整 durable fact 覆盖重建的 materializer，进入 `current-state`
- 依赖旧文档补丁、增量历史拼接、列表累积、报表/时间线/图镜像的 materializer，降级为 `artifact`
- 直接删掉误导性的命名和入口，不保留兼容层

## 本阶段设计

### 1. 框架抽象显式分流

新增两类 durable materializer：

- [IProjectionMaterializerKinds.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializerKinds.cs)
  - `ICurrentStateProjectionMaterializer<TContext>`
  - `IProjectionArtifactMaterializer<TContext>`
- [ProjectionMaterializerRegistration.cs](/Users/auric/aevatar/src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializerRegistration.cs)
  - `AddCurrentStateProjectionMaterializer<TContext, TMaterializer>()`
  - `AddProjectionArtifactMaterializer<TContext, TMaterializer>()`

原则：

- `current-state` 不能依赖回读旧文档做 reducer
- `artifact` 可以是 durable 的，但不再冒充 canonical current-state query model
- runtime 仍统一分发 `IProjectionMaterializer<TContext>`，语义分类由类型系统和注册方式承载

### 2. Workflow 先说实话

Workflow durable materialization 现在拆成：

- current-state
  - [WorkflowExecutionCurrentStateProjector.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionCurrentStateProjector.cs)
- artifacts
  - [WorkflowRunInsightReportArtifactProjector.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowRunInsightReportArtifactProjector.cs)
  - [WorkflowRunTimelineArtifactProjector.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowRunTimelineArtifactProjector.cs)
  - [WorkflowRunGraphArtifactProjector.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowRunGraphArtifactProjector.cs)
  - [WorkflowActorBindingProjector.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowActorBindingProjector.cs)
- artifact shared support
  - [WorkflowExecutionArtifactMaterializationSupport.cs](/Users/auric/aevatar/src/workflow/Aevatar.Workflow.Projection/Projectors/WorkflowExecutionArtifactMaterializationSupport.cs)

已删除的误导命名：

- `WorkflowRunInsightReportDocumentProjector`
- `WorkflowRunTimelineReadModelProjector`
- `WorkflowRunGraphMirrorProjector`
- `WorkflowExecutionArtifactProjectionSupport`

### 3. Platform 第一批 current-state 覆盖写

直接改成覆盖写、去掉旧文档依赖：

- [ServiceServingSetProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceServingSetProjector.cs)
- [ServiceTrafficViewProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceTrafficViewProjector.cs)

这两条现在属于 `current-state`，因为单个 committed fact 已经携带完整 serving/traffic 视图。

仍属于 `artifact` 的平台投影：

- [ServiceCatalogProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceCatalogProjector.cs)
- [ServiceDeploymentCatalogProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceDeploymentCatalogProjector.cs)
- [ServiceRolloutProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceRolloutProjector.cs)
- [ServiceRevisionCatalogProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Projection/Projectors/ServiceRevisionCatalogProjector.cs)
- [ServiceConfigurationProjector.cs](/Users/auric/aevatar/src/platform/Aevatar.GAgentService.Governance.Projection/Projectors/ServiceConfigurationProjector.cs)

原因：

- 仍依赖旧文档 patch 或跨事件增量积累
- 还不是“从 authority current state 覆盖复制”的 honest current-state replica

### 4. Scripting 全部收口到 current-state

Scripting 现有 durable materializer 都是 committed full-fact overwrite：

- [ScriptReadModelProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptReadModelProjector.cs)
- [ScriptNativeDocumentProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptNativeDocumentProjector.cs)
- [ScriptNativeGraphProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptNativeGraphProjector.cs)
- [ScriptDefinitionSnapshotProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptDefinitionSnapshotProjector.cs)
- [ScriptCatalogEntryProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptCatalogEntryProjector.cs)
- [ScriptEvolutionReadModelProjector.cs](/Users/auric/aevatar/src/Aevatar.Scripting.Projection/Projectors/ScriptEvolutionReadModelProjector.cs)

## 已完成任务

- 增加 current-state / artifact 强类型抽象
- 所有 projection DI 改用新注册助手
- Workflow artifact projector 改名并迁移到 artifact 分类
- Platform serving / traffic 改成覆盖写 current-state
- Platform governance / catalog / rollout / revision projector 诚实降级为 artifact
- Scripting durable materializer 全部归入 current-state
- 同步 README / 架构文档 / 注册测试

## 下一批必须继续做的事

### A. 把剩余 artifact 从“readmodel 命名”里彻底清出去

优先级最高：

- 继续清理仍保留 `readmodel` 口径的 artifact 查询和文档说明
- 继续收缩 Workflow query surface，让 canonical query 只强调 current-state；report/timeline/graph 作为显式 artifact 查询

### B. 让平台 catalog/configuration 真正拥有 authority current-state 输入

如果这些对象是稳定业务事实：

- 应让 owning actor 在 committed observation 中暴露完整 current-state mirror
- 然后把现在的 patch projector 改成覆盖写 current-state

如果这些对象本质是列表汇总、活动集、生命周期清单：

- 就保留 artifact 身份
- 或者上提为新的 aggregate actor

### C. 重做 graph / report / timeline 的长期归属

当前 Workflow report/timeline/graph 仍是 durable artifacts，不是 canonical current-state replica。

后续两条路二选一：

- 继续作为 artifact，并在命名、查询、文档中保持诚实
- 如果它们被视为稳定业务事实，则应上提为 dedicated aggregate actor，由该 actor 拥有权威状态，再单独 materialize

## 成功判定

满足以下条件才算第二阶段彻底完成：

- 当前态查询入口只依赖 `ICurrentStateProjectionMaterializer`
- artifact 查询入口不再伪装成 canonical current-state
- 新增 projector 必须在注册时显式声明自己是 `current-state` 还是 `artifact`
- live docs 与代码命名一致，不再提旧 projector 名
- query/read path 不引入 replay、priming 或旧文档 patch 兜底
