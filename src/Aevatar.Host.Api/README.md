# Aevatar.Host.Api

`Aevatar.Host.Api` 是协议接入层（SSE/WebSocket/HTTP Query），不承载 CQRS 内核实现。

## 职责

- 暴露 `POST /api/chat`（SSE）与 `GET /api/ws/chat`（WebSocket）
- 创建/复用 `WorkflowGAgent`
- 调用 `IWorkflowExecutionRunOrchestrator` 启动与收尾投影 run
- 提供 `GET /api/runs` / `GET /api/runs/{runId}` 查询（按配置开关）

## 依赖关系

- `Aevatar.Workflow.Projection`
  - WorkflowExecution 读侧模型与投影服务
- `Aevatar.CQRS.Projection.Core`
  - 由 WorkflowExecution 间接依赖的通用内核
- `Aevatar.Presentation.AGUI`
  - AGUI 实时事件通道与映射
- `Aevatar.Workflow.Presentation.AGUIAdapter`
  - WorkflowExecution 到 AGUI 的适配器（mapper + projector）

## 关键组件

- `Endpoints/ChatEndpoints.cs`
  - 请求校验、协议响应（SSE/WS）、端点映射
- `Orchestration/WorkflowExecutionRunOrchestrator.cs`
  - run 生命周期编排（start/wait/complete/rollback/topology）
- `Reporting/WorkflowExecutionReportWriter.cs`
  - 可选报告输出（json/html）

## 默认装配

```csharp
builder.Services.AddWorkflowExecutionProjectionCQRS(...);
builder.Services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
builder.Services.AddSingleton<IWorkflowExecutionRunOrchestrator, WorkflowExecutionRunOrchestrator>();
```

即：API 只负责协议与组合，CQRS 运行时由 WorkflowExecution 模块处理，AGUI 映射由 Adapter 层处理。
