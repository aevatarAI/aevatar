# Aevatar.Host.Api

`Aevatar.Host.Api` 是协议接入层（SSE/WebSocket/HTTP Query），不承载工作流编排与 CQRS 内核实现。

## 职责

- 暴露 `POST /api/chat`（SSE）与 `GET /api/ws/chat`（WebSocket）
- 调用 `IWorkflowChatRunApplicationService.ExecuteAsync` 执行 chat run
- 调用 `IWorkflowExecutionQueryApplicationService` 提供 `GET /api/runs` / `GET /api/runs/{runId}` 查询（按配置开关）

## 运行语义契约

- 同一 `Actor` 的多个 `run` 不做事件隔离：订阅端可看到该 Actor 的全量事件流。
- 单次请求仅以当前 `runId` 的终止事件（`RUN_FINISHED`/`RUN_ERROR`）作为收尾条件。
- `RUN_STARTED` 统一由 `StartWorkflowEvent` 投影产出，`threadId` 统一使用发布事件的 `ActorId`。
- projection completion 采用显式状态：`Completed` / `TimedOut` / `Failed` / `Stopped` / `NotFound` / `Disabled`。

## 依赖关系

- `Aevatar.Workflow.Application.Abstractions`
  - 应用层契约（run 用例、query DTO、工作流定义注册）
- `Aevatar.Workflow.Application`
  - 应用层实现（run 编排 + query 服务）
- `Aevatar.Workflow.Projection`
  - 由 Workflow 应用层间接依赖的读侧实现
- `Aevatar.Presentation.AGUI`
  - AGUI 实时事件通道与映射
- `Aevatar.Workflow.Presentation.AGUIAdapter`
  - WorkflowExecution 到 AGUI 的适配器（mapper + projector）

## 关键组件

- `Endpoints/ChatEndpoints.cs`
  - Chat/WS 路由与协议入口
- `Endpoints/ChatQueryEndpoints.cs`
  - `agents/workflows/runs` 查询端点映射
- `Endpoints/ChatWebSocketProtocol.cs`
  - WebSocket 收发协议封装

## 默认装配

```csharp
builder.Services.AddWorkflowExecutionProjectionCQRS(...);
builder.Services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
builder.Services.AddWorkflowApplication(...);
```

即：API 只负责协议与组合；run 编排、拓扑策略、报告写出下沉到 `workflow` 应用层。
