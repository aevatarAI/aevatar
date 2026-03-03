# Aevatar.Workflow.Host.Api

`Aevatar.Workflow.Host.Api` 是协议层宿主，只做 HTTP/SSE/WebSocket 适配与依赖组合。

能力文档入口：

- Host 快速入口：`CHAT_API_CAPABILITIES.md`
- 框架完整说明（单一事实源）：`docs/workflow-chat-ws-api-capability.md`

## 职责边界

- 暴露端点：
  - `POST /api/chat`（SSE）
  - `POST /api/openclaw/hooks/agent`（OpenClaw Hook Bridge）
  - `POST /hooks/agent`（OpenClaw Hook Bridge 短路径）
  - `GET /api/ws/chat`（WebSocket）
  - `GET /api/agents`、`GET /api/workflows`、`GET /api/actors/{actorId}`、`GET /api/actors/{actorId}/timeline`
  - `chat` payload 支持 `prompt` + `agentId` 复用已绑定 Actor，也支持 `workflow`（file-backed 名称 lookup）或 `workflowYamls`（inline YAML bundle）；当 `workflow/workflowYamls` 同时为空时，外部 API 默认走 `auto`
- 调用应用层：
  - `ICommandExecutionService<WorkflowChatRunRequest,...>`
  - `IWorkflowExecutionQueryApplicationService`
- 不承载 workflow/cqrs 业务编排。

## `/api/chat` 入参速查

| 场景 | 示例 |
|------|------|
| 按名称加载 file-backed workflow（新建 Actor） | `{ "prompt": "...", "workflow": "publish_pipeline" }` |
| `workflow/workflowYamls` 都不传（新建 Actor） | `{ "prompt": "..." }` |
| 复用已绑定 workflow 的 Actor | `{ "prompt": "...", "agentId": "actor-123" }` |
| inline 提交 workflow YAML bundle（新建 Actor） | `{ "prompt": "...", "workflowYamls": ["name: root\\nroles: ...\\nsteps: ..."] }` |
| 指定 Actor + inline YAML bundle | `{ "prompt": "...", "agentId": "actor-123", "workflowYamls": ["..."] }` |
| `workflow` + `workflowYamls` 同传 | 固定以 `workflowYamls` 路径为准，`workflow` 被忽略 |

常见错误码：

- `INVALID_WORKFLOW_YAML`：`workflowYamls` 任一 YAML 解析或校验失败（400）
- `WORKFLOW_BINDING_MISMATCH`：目标 actor 已绑定其它 workflow（409）
- `WORKFLOW_NOT_FOUND`：`workflow` 未命中 file-backed 名称（404）
- `AGENT_WORKFLOW_NOT_CONFIGURED`：传了 `agentId`，但 actor 未绑定且未提供 `workflowYamls`（409）

异常回退语义：

- 应用层仅对白名单 workflow + 白名单异常类型启用一次性 `direct` 回退。
- inline `workflowYamls` 与显式 `direct` 请求默认不触发自动回退，避免隐藏真实错误。

## Endpoint 定义归属

- `Workflow` 能力 API 定义位于 `Aevatar.Workflow.Infrastructure/CapabilityApi/*`。
- Host 通过 `builder.AddWorkflowCapabilityWithAIDefaults()` 统一装配 Workflow capability + AI features + AI projection extension，端点由默认 Host 自动挂载。
- Host 项目不再保留重复 endpoint 实现。

## 运行语义

- 运行时通过 `workflow-run:{actorId}:{commandId}` 事件流订阅输出，避免同 Actor 并发 run 串流。
- 单次请求在终止事件（`RUN_FINISHED`/`RUN_ERROR`）后收尾。
- 客户端可通过 `actorId` 查询对应 ReadModel 视图（`/api/actors/*`）。

## 组合方式

`Program.cs` 默认注册：

- `builder.AddAevatarDefaultHost(...)`
- `builder.AddWorkflowCapabilityWithAIDefaults()`
- `app.UseAevatarDefaultHost()`（默认自动执行 `MapAevatarCapabilities()`）

Host 只做“协议 + 组合”，核心用例在 `workflow/*` 能力实现层。

## 能力文档维护策略

- `docs/workflow-chat-ws-api-capability.md`：完整说明（权威版本）
- `CHAT_API_CAPABILITIES.md`：Host 入口摘要
- 本 README：Host 宿主职责与接入说明
