# Aevatar.Workflow.Projection

`Aevatar.Workflow.Projection` 是 Workflow 领域的 CQRS 读侧扩展层。

## 职责边界

- 应用层投影端口实现：`IWorkflowExecutionProjectionPort`（实现类 `WorkflowExecutionProjectionService`）
- 领域上下文：`IWorkflowExecutionProjectionContextFactory`、`WorkflowExecutionProjectionContext`
- 实时输出契约：`WorkflowRunEvent`、`IWorkflowRunEventSink`、`WorkflowRunEventChannel`（定义于 `Aevatar.Workflow.Application.Abstractions`）
- 领域投影实现：reducers、projectors、read model store
- 领域 DI 组合：`AddWorkflowExecutionProjectionCQRS(...)`

本项目依赖：

- `Aevatar.CQRS.Projection.Abstractions`（通用抽象）
- `Aevatar.CQRS.Projection.Core`（通用生命周期/订阅/协调实现）

## 统一运行链路

1. `EnsureActorProjectionAsync` 创建 actor 共享 projection 上下文并注册 actor stream 订阅
2. 每条 `EventEnvelope` 进入统一 coordinator，一对多调用已注册 projector
3. `WorkflowExecutionReadModelProjector` 驱动 reducers 生成并更新 read model
4. AGUI 分支与读模型分支共享同一输入事件流，不再维护独立 run 编排链路

AGUI 输出与 CQRS 读模型共享同一链路，只是在 projector 分支不同。
应用层通过 `AttachLiveSinkAsync/DetachLiveSinkAsync` 挂载实时输出通道；
AGUI 分支实现位于 `Aevatar.Workflow.Presentation.AGUIAdapter`。

## 扩展方式

- 新增 reducer：
  - 实现 `IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>`
  - 在 DI 中注册
- 新增 projector：
  - 实现 `IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>`
  - 在 DI 中注册
- 替换存储：
  - 实现 `IProjectionReadModelStore<WorkflowExecutionReport, string>`
  - 使用自定义实现替换默认内存存储
- 扩展 run 输出协议：
  - 保持 `WorkflowRunEvent` 不变，新增 presentation adapter 进行协议映射
  - 不改 Application 用例编排代码

## 与 API 的关系

`Aevatar.Host.Api` 通过 `Aevatar.Workflow.Application` 调用本项目，不直接编排投影内核细节。API 仅负责协议适配（SSE/WebSocket/HTTP Query）。
