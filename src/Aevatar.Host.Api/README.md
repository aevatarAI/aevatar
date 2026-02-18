# Aevatar.Host.Api

`Aevatar.Host.Api` 是协议层宿主，只做 HTTP/SSE/WebSocket 适配与依赖组合。

## 职责边界

- 暴露端点：
  - `POST /api/commands`（Accepted + commandId）
  - `POST /api/chat`（SSE）
  - `GET /api/ws/chat`（WebSocket）
  - `GET /api/agents`、`GET /api/workflows`、`GET /api/actors/{actorId}`、`GET /api/actors/{actorId}/timeline`
- 调用应用层：
  - `ICommandExecutionService<WorkflowChatRunRequest,...>`
  - `IWorkflowExecutionQueryApplicationService`
- 不承载 workflow/cqrs 业务编排。

## Endpoint 结构

- `ChatEndpoints.cs`：仅路由与入口调用。
- `ChatSseResponseWriter.cs`：SSE 启动与帧写出。
- `ChatWebSocketCommandParser.cs`：WS 命令解析与校验。
- `ChatWebSocketRunCoordinator.cs`：WS 命令执行协调。
- `ChatRunStartErrorMapper.cs`：run 启动错误到 HTTP/WS 错误码映射。
- `ChatQueryEndpoints.cs`：Query 端点。

## 运行语义

- 默认按 `Actor` 共享事件流（同 Actor 多 run 不隔离）。
- 单次请求在当前 `runId` 的终止事件（`RUN_FINISHED`/`RUN_ERROR`）后收尾。
- `runId/sessionId` 均由服务端内部生成，客户端无需传入。

## 组合方式

`Program.cs` 默认注册：

- `AddCqrsCore()`
- `AddWorkflowSubsystem(...)`

Host 只做“协议 + 组合”，核心用例在 `workflow/*` 子系统。
