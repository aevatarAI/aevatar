# Aevatar.Workflow.Host.Api

`Aevatar.Workflow.Host.Api` 是协议层宿主，只做 HTTP/SSE/WebSocket 适配与依赖组合。

## 职责边界

- 暴露端点：
  - `POST /api/chat`（SSE）
  - `GET /api/ws/chat`（WebSocket）
  - `GET /api/agents`、`GET /api/workflows`、`GET /api/actors/{actorId}`、`GET /api/actors/{actorId}/timeline`
- 调用应用层：
  - `ICommandExecutionService<WorkflowChatRunRequest,...>`
  - `IWorkflowExecutionQueryApplicationService`
- 不承载 workflow/cqrs 业务编排。

## Endpoint 定义归属

- `Workflow` 能力 API 定义位于 `Aevatar.Workflow.Infrastructure/CapabilityApi/*`。
- Host 仅通过 `app.MapWorkflowCapabilityEndpoints()` 挂载能力端点。
- Host 项目不再保留重复 endpoint 实现。

## 运行语义

- 运行时按 `commandId` 过滤 live sink，避免同 Actor 并发 run 串流。
- 单次请求在终止事件（`RUN_FINISHED`/`RUN_ERROR`）后收尾。
- 客户端可通过 `actorId` 查询对应 ReadModel 视图（`/api/actors/*`）。

## 组合方式

`Program.cs` 默认注册：

- `UseAevatarCqrsRuntime(...)`
- `AddAevatarCqrsRuntime(...)`
- `AddWorkflowCapability(...)`

Host 只做“协议 + 组合”，核心用例在 `workflow/*` 能力实现层。
