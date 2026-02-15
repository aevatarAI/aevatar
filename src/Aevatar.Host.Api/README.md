# Aevatar.Host.Api

`Aevatar.Host.Api` 是协议接入层（SSE/WebSocket/HTTP Query），不承载 CQRS 内核实现。

## 职责

- 暴露 `POST /api/chat`（SSE）与 `GET /api/ws/chat`（WebSocket）
- 创建/复用 `WorkflowGAgent`
- 调用 `IWorkflowExecutionRunOrchestrator` 启动与收尾投影 run
- 提供 `GET /api/runs` / `GET /api/runs/{runId}` 查询（按配置开关）

## 运行语义契约

- 同一 `Actor` 的多个 `run` 不做事件隔离：订阅端可看到该 Actor 的全量事件流。
- 单次请求仅以当前 `runId` 的终止事件（`RUN_FINISHED`/`RUN_ERROR`）作为收尾条件。
- `RUN_STARTED` 统一由 `StartWorkflowEvent` 投影产出，`threadId` 统一使用发布事件的 `ActorId`。
- projection completion 采用显式状态：`Completed` / `TimedOut` / `Failed` / `Stopped` / `NotFound` / `Disabled`。

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
  - Chat/WS 路由与协议入口
- `Endpoints/ChatQueryEndpoints.cs`
  - `agents/workflows/runs` 查询端点映射
- `Endpoints/ChatRunExecution.cs`
  - chat run 准备、执行、投影收尾
- `Endpoints/ChatWebSocketProtocol.cs`
  - WebSocket 收发协议封装
- `Orchestration/WorkflowExecutionRunOrchestrator.cs`
  - run 生命周期编排（start/wait-status/complete/rollback/topology）
- `Orchestration/WorkflowExecutionTopologyResolver.cs`
  - 拓扑解析策略（默认 runtime snapshot，可替换）
- `Reporting/WorkflowExecutionReportWriter.cs`
  - 可选报告输出（json/html，best-effort，不影响 projection finalize）

## 默认装配

```csharp
builder.Services.AddWorkflowExecutionProjectionCQRS(...);
builder.Services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
builder.Services.AddSingleton<IWorkflowExecutionRunOrchestrator, WorkflowExecutionRunOrchestrator>();
```

即：API 只负责协议与组合，CQRS 运行时由 WorkflowExecution 模块处理，AGUI 映射由 Adapter 层处理。
