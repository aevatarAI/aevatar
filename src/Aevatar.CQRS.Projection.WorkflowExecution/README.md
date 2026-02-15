# Aevatar.CQRS.Projection.WorkflowExecution

`Aevatar.CQRS.Projection.WorkflowExecution` 是 Workflow 领域的 CQRS 读侧扩展层。

## 职责边界

- 领域契约与上下文：`IWorkflowExecutionProjectionService`、`IWorkflowExecutionProjectionContextFactory`、`WorkflowExecutionProjectionContext`
- 领域投影实现：reducers、projectors、read model store
- 领域 DI 组合：`AddWorkflowExecutionProjectionCQRS(...)`

本项目依赖：

- `Aevatar.CQRS.Projection.Abstractions`（通用抽象）
- `Aevatar.CQRS.Projection.Core`（通用生命周期/订阅/协调实现）

## 统一运行链路

1. `StartAsync` 创建 projection run 上下文并注册 actor stream 订阅
2. 每条 `EventEnvelope` 进入统一 coordinator，一对多调用已注册 projector
3. `WorkflowExecutionReadModelProjector` 驱动 reducers 生成并更新 read model
4. `CompleteAsync` 解除订阅并完成 run 生命周期

AGUI 输出与 CQRS 读模型共享同一链路，只是在 projector 分支不同；
AGUI 分支实现位于 `Aevatar.Presentation.AGUI.Adapter.WorkflowExecution`，本项目仅保留 WorkflowExecution 领域读侧实现。

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

## 与 API 的关系

`Aevatar.Host.Api` 通过 `IWorkflowExecutionRunOrchestrator` 调用本项目，不直接编排投影内核细节。API 仅负责协议适配（SSE/WebSocket/HTTP Query）。
