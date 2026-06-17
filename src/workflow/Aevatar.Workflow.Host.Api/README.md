# Aevatar.Workflow.Host.Api

`Aevatar.Workflow.Host.Api` 是协议层宿主，只做 HTTP/SSE/WebSocket 适配与依赖组合。

能力文档入口：

- Host 快速入口：`CHAT_API_CAPABILITIES.md`
- 框架完整说明（单一事实源）：`docs/canon/chat-api.md`

## 职责边界

- 暴露端点：
  - `POST /api/chat`（SSE）
  - `GET /api/ws/chat`（WebSocket）
  - `POST /api/workflow-webhooks/{routeKey}`（认证 webhook start-run）
  - `GET /api/agents`、`GET /api/workflows`、`GET /api/actors/{actorId}`、`GET /api/actors/{actorId}/timeline`
  - `chat` payload 支持 `prompt`、`workflow`（注册表名称 lookup，含内建与文件工作流）、`workflowYamls`（inline YAML bundle）或 typed `source.definitionActor.actorId`；仅在新建 Actor 且未提供 source/workflow/workflowYamls 时，外部 API 默认走 `auto`
- 调用应用层：
  - `IWorkflowChatRunInteractionPort`（`/api/chat` SSE 与 WebSocket 实时交互入口）
  - `ICommandDispatchService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>`
  - `IWorkflowExecutionQueryApplicationService`
- 不承载 workflow/cqrs 业务编排。

## `/api/chat` 入参速查

`POST /api/chat` 支持两种 producer：

- `application/json`：直接提交 `ChatInput` JSON。
- `multipart/form-data`：提交一个名为 `file` 的文件，并可用 `payload` 字段携带 `ChatInput` JSON，或用 `prompt`、`workflow`、`sessionId`、`scopeId`、`workflowYaml`、`workflowYamls` 表单字段覆盖对应输入。

| 场景 | 示例 |
|------|------|
| 按名称加载已注册 workflow（新建 Actor） | `{ "prompt": "...", "workflow": "publish_pipeline" }` |
| `workflow/workflowYamls` 都不传（新建 Actor） | `{ "prompt": "..." }` |
| 复用已绑定 workflow 的 Actor | `{ "prompt": "...", "source": { "kind": "definition_actor", "definitionActor": { "actorId": "actor-123" } } }` |
| 显式选择内建 workflow | `{ "prompt": "...", "workflow": "auto_review" }` |
| inline 提交 workflow YAML bundle（新建 Actor） | `{ "prompt": "...", "workflowYamls": ["name: root\\nroles: ...\\nsteps: ..."] }` |
| 指定 Actor + inline YAML bundle | `{ "prompt": "...", "source": { "kind": "inline_yaml_bundle", "inlineBundle": { "actorId": "actor-123", "yamlDocuments": [{ "yaml": "..." }] } } }` |
| `workflow` + `workflowYamls` 同传 | 固定以 `workflowYamls` 路径为准，`workflow` 被忽略 |
| 表单上传文件并启动 run | `multipart/form-data`：`file=@cat.png;type=image/png`、`prompt=describe this`、`workflow=direct` |
| 表单上传文档并启动 run | `multipart/form-data`：`file=@invoice.pdf;type=application/pdf`、`prompt=summarize this`、`workflow=direct`；允许的非媒体文件统一映射为 `inputParts[].type = "file"` |

常见错误码：

- `INVALID_WORKFLOW_YAML`：`workflowYamls` 任一 YAML 解析或校验失败（400）
- `WORKFLOW_BINDING_MISMATCH`：目标 actor 已绑定其它 workflow（409）
- `WORKFLOW_NOT_FOUND`：`workflow` 未命中注册表名称（404）
- `AGENT_WORKFLOW_NOT_CONFIGURED`：typed source 指定的 actor 未绑定且未提供 inline YAML（409）

异常回退语义：

- 应用层仅对白名单 workflow + 白名单异常类型启用一次性 `direct` 回退。
- inline `workflowYamls` 与显式 `direct` 请求默认不触发自动回退，避免隐藏真实错误。

## Endpoint 定义归属

- `Workflow` 能力 API 定义位于 `Aevatar.Workflow.Infrastructure/CapabilityApi/*`。
- Host 通过 `builder.AddAevatarPlatform()` 统一装配 Workflow capability、Scripting capability、AI features 与 Workflow AI projection extension，端点由默认 Host 自动挂载。
- Host 项目不再保留重复 endpoint 实现。

## 运行语义

- API 输入先被规范化为 `WorkflowChatRunRequest` 等应用命令模型，再走 CQRS 标准命令骨架：`target resolve -> command context -> envelope -> dispatch port -> accepted receipt`。
- Workflow Host 通过 `IWorkflowChatRunInteractionPort` 启动实时交互；该业务端口内部使用默认 CQRS interaction service 作为非 fallback plumbing，并负责本次 realtime projection scope activation 与 cleanup ownership。
- Workflow Host 只消费这条 CQRS 骨架，不自定义通用 command lifecycle；workflow 领域只负责目标解析、payload 映射与读侧观察映射。
- `resume/signal` 也复用同一条骨架，Host 只依赖对应的 `ICommandDispatchService<...>`，不再直接注入 `IActorRuntime/IActorDispatchPort`。
- Webhook ingress 只在 Host/Adapter 处理 raw JSON、HMAC 与 binding mapping；应用层接收 typed `WorkflowExternalIngressContext`，防重放依赖 `IWorkflowWebhookReplayStore`，生产启用但缺少 durable store 时 fail closed。
- Workflow file tools 只通过 workflow-owned tool source 暴露：`workflow_connected_service_resource_fetch` 按 provider/operation/resource_kind allowlist 读取 connected-service 二进制资源，并且只通过 `IWorkflowFileIngressPort` 以 `SourceKind=ConnectedServiceResource` 写入 artifact store；结果只返回 sanitized `WorkflowFileRef`，不回显 bytes/base64。Connected-service 包只注册窄 adapter，例如 Lark 的 `lark/message_resource_download/image|file`。
- `document_extract` 读取 artifact store 中的 typed file ref 并返回 bounded text；支持 UTF-8/PDF/DOCX 文档文本，以及最多 5 MiB 的 `image/png` / `image/jpeg` 图片文字抽取。图片抽取需要已配置且支持 image input 的 LLM provider；缺失或不支持时返回 `image_provider_unavailable`，`image/webp` 仍 fail closed。
- `workflow_file_submit` 由 Workflow Infrastructure 的唯一 runtime 负责参数解析、caller bearer 校验、目标策略绑定、artifact 描述/打开与结果净化。Lark 只注册 `lark_drive_media`、`lark_approval_file` submit adapter，并保持 `file_token`/`file_code` typed 输出；generic connected-service submit 目标只能来自 Host 拥有的 `WorkflowConnectedServiceFileSubmit:Targets` allowlist 配置，workflow 参数不得覆盖 service/path/method/header/body/file-field 等 endpoint policy，结果不回显 bytes/base64/provider raw body。Host 启动时会校验配置的 endpoint policy，格式错误时 fail fast；真实非 Lark 目标值应来自部署配置、ConfigMap 或 secret，不来自 `.refactor-loop/host.env`。
- `multipart/form-data` 文件上传只在 Host/API 边界读取文件 bytes；Host 先校验 caller credential、表单 shape、文件大小与媒体类型，再通过 `IWorkflowFileIngressPort` 写入 artifact store。默认允许 image/audio/video 与 PDF、DOCX、CSV、plain text、markdown、XLSX；非 image/audio/video 的允许类型统一作为 `file` input part。后续 `WorkflowChatRunRequest` 只携带 typed `WorkflowFileRef` input part，`SourceKind=FormUpload`，不把 bytes/base64 带入 actor-facing command、state、readmodel 或日志。
- 命令最终会被包装成 `EventEnvelope`；目标 Actor 的获取/创建由 `IActorRuntime` 负责，envelope 投递由 `IActorDispatchPort` 完成，CQRS 侧由 `ActorCommandTargetDispatcher` 承接 target dispatch。
- 这里的 `EventEnvelope` 是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。
- 命令主链路不额外经过 ingress queue/stream；stream 仅用于 actor envelope 的投影与实时输出。
- `actorId + commandId` 是客户端后续观察 run 输出与读模型查询的会话句柄，其中 `commandId` 负责追踪，`actorId` 负责定位；任何 `ack/accepted` 语义都只应理解为“请求已被系统接受并可追踪”。
- 运行时通过 `workflow-run:{actorId}:{commandId}` 会话流订阅输出，避免同 Actor 并发 run 串流。
- 单次请求在终止事件（`RUN_FINISHED`/`RUN_ERROR`）后收尾。
- 客户端可通过 `actorId` 查询对应 ReadModel 视图（`/api/actors/*`）。

## 组合方式

`Program.cs` 默认注册：

- `builder.AddAevatarDefaultHost(...)`
- `builder.AddAevatarPlatform()`
- `app.UseAevatarDefaultHost()`（默认自动执行 `MapAevatarCapabilities()`）

Host 只做“协议 + 组合”，核心用例在 `workflow/*` 能力实现层。

## 能力文档维护策略

- `docs/canon/chat-api.md`：完整说明（权威版本）
- `CHAT_API_CAPABILITIES.md`：Host 入口摘要
- 本 README：Host 宿主职责与接入说明
