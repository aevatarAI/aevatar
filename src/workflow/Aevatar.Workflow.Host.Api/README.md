# Aevatar.Workflow.Host.Api

`Aevatar.Workflow.Host.Api` 是协议层宿主，只做 HTTP/SSE/WebSocket 适配与依赖组合。

详细能力文档见：`CHAT_API_CAPABILITIES.md`（含 `/api/chat`、`/api/ws/chat`、`human approval`、`resume/signal`、`auto/auto_review` 流程说明）。

## 职责边界

- 暴露端点：
  - `POST /api/chat`（SSE）
  - `GET /api/ws/chat`（WebSocket）
  - `GET /api/agents`、`GET /api/workflows`、`GET /api/actors/{actorId}`、`GET /api/actors/{actorId}/timeline`
  - `chat` payload 支持 `prompt` + `agentId` 复用已绑定 Actor，也支持 `workflow`（按名称）或 `workflowYaml`（inline YAML）；当 `workflow/workflowYaml` 同时为空时默认走 `direct`（可由运行选项切换为 `auto`）
- 调用应用层：
  - `ICommandExecutionService<WorkflowChatRunRequest,...>`
  - `IWorkflowExecutionQueryApplicationService`
- 不承载 workflow/cqrs 业务编排。

## `/api/chat` 入参速查

| 场景 | 示例 |
|------|------|
| 按名称加载 workflow（新建 Actor） | `{ "prompt": "...", "workflow": "direct" }` |
| `workflow/workflowYaml` 都不传（新建 Actor） | `{ "prompt": "..." }` |
| 复用已绑定 workflow 的 Actor | `{ "prompt": "...", "agentId": "actor-123" }` |
| inline 提交 workflow YAML（新建 Actor） | `{ "prompt": "...", "workflowYaml": "name: demo\\nroles: ...\\nsteps: ..." }` |
| 指定 Actor + inline YAML | `{ "prompt": "...", "agentId": "actor-123", "workflowYaml": "..." }` |
| `workflow` + `workflowYaml` 同传 | 要求 `workflow == workflowYaml.name`，否则返回 `WORKFLOW_NAME_MISMATCH`（400） |

常见错误码：

- `INVALID_WORKFLOW_YAML`：`workflowYaml` 解析或校验失败（400）
- `WORKFLOW_NAME_MISMATCH`：`workflow` 与 YAML 内 `name` 不一致（400）
- `WORKFLOW_BINDING_MISMATCH`：目标 actor 已绑定其它 workflow（409）
- `AGENT_WORKFLOW_NOT_CONFIGURED`：传了 `agentId`，但 actor 未绑定且未提供 `workflowYaml`（409）

异常回退语义：

- 应用层仅对白名单 workflow + 白名单异常类型启用一次性 `direct` 回退。
- inline `workflowYaml` 与显式 `direct` 请求默认不触发自动回退，避免隐藏真实错误。

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
