---
title: "Chat API 能力说明（Mainnet 与 Workflow）"
status: active
owner: eanzhao
---

# Chat API 能力说明（Mainnet 与 Workflow）

> 单一事实源（Single Source of Truth）：Mainnet `/api/chat` 的组合与 Workflow Chat 能力说明以本文为准；NyxID Assistant 的 actor/task 细节见 `docs/canon/nyxid-chat-api.md`。
> Host 侧入口文档：`src/workflow/Aevatar.Workflow.Host.Api/README.md`、`src/workflow/Aevatar.Workflow.Host.Api/CHAT_API_CAPABILITIES.md`。

Transcript、execution state、prompt context 与 user memory 的跨能力语义以
[conversation-context-and-memory.md](conversation-context-and-memory.md) 为准。

本文档面向框架使用者，说明当前 `POST /api/chat` 与 `GET /api/ws/chat` 可以做什么，尤其是：

- 根据 `prompt` 自动判断是否要生成 workflow
- 在 `human_approval` 节点等待人工确认
- 支持多轮“反馈 -> 重新生成 -> 再审批”
- 审批通过后自动执行（`auto`）或只定稿不执行（`auto_review`）

## Mainnet 统一 `/api/chat` facade

Mainnet 只映射一个 `POST /api/chat`。Aevatar-owned Chat 的唯一执行主干是
`NyxIdChatConversationGAgent -> NyxIdChatTurnGAgent`；Host 只把八类 v4 command 映射到既有
application/actor boundary，不拥有 task、turn、等待、审批、重试或终态事实。`type` 是 command
discriminator，不能用“存在或不存在 `type`”选择另一套 Chat runtime；缺少 `type` 只会进入
冻结的 external compatibility adapter，不定义第二个 Chat 产品模型。

| Request | Mainnet owner | Result |
|---|---|---|
| Existing multipart or JSON object without `type` | External Workflow compatibility adapter | 保留 Phase 1 既有 wire 行为，但冻结能力面，不作为 Aevatar-owned Chat runtime 演进。最终删除由 #3319 跟踪。 |
| JSON with `type=text` | NyxID Assistant | 创建或复用现有 NyxIdChat conversation actor，并返回 AGUI SSE。 |
| JSON with one of the other seven recognized Assistant types | NyxID Assistant | 复用现有 input、action、approval 或 task-control application port。 |
| JSON with malformed or unknown explicit `type` | none | `400 INVALID_CHAT_INPUT`; never falls through to Workflow. |
| Other content type or malformed/non-object JSON | none | `400 INVALID_CHAT_INPUT`; fail closed. |

The eight closed Assistant discriminators are `text`, `input.resolve`, `action.continue`,
`approval.resolve`, `task.stop`, `task.steer`, `step.retry`, and `step.skip`. Assistant JSON is
strict: unknown fields are rejected, `scopeId` is not a request field, and scope is derived only
from one unambiguous authenticated `scope_id` or `workflow.scope_id` claim. A recognized `type`
always selects the typed command contract; Assistant DTO validation then rejects fields that are
not part of that command.

Mainnet also exposes the authenticated Assistant resource family:

| Endpoint | Meaning |
|---|---|
| `GET /api/chat/conversations?pageSize={n}&cursor={cursor}` | List the caller's NyxIdChat transcript index; the response carries `nextCursor`. |
| `GET /api/chat/conversations/{conversationId}` | Read the durable transcript, transcript `stateVersion`, and projection status. |
| `GET /api/chat/conversations/{conversationId}/state?afterStateVersion={v}&turnId={turnId}` | Read the conditional actor current-state replica. |
| `DELETE /api/chat/conversations/{conversationId}` | Submit the existing authoritative retirement/deletion workflow. |

The transcript endpoint returns every committed terminal turn while the conversation is active. An acknowledged conversation whose transcript actor read model has not materialized yet returns `200` with `messages: []`, `stateVersion: 0`, and `projectionStatus: "pending"`; once materialized, it returns `projectionStatus: "current"` and the authoritative transcript version. `404` remains reserved for a conversation that was never accepted, belongs to another scope, was abandoned before acceptance, or was explicitly deleted. The current contract has no per-turn TTL or silent rolling eviction: the 251st and later turns remain appendable, and only explicit whole-conversation deletion removes query availability. LLM continuation context is independently bounded to the latest 24 nonblank messages; that prompt selection never prunes the durable transcript.

### NyxID Chat 核心与业务扩展边界

Aevatar-owned NyxID Chat 只拥有可跨业务复用的执行机制：typed input、actor-owned condition、
exact guarded tool、operation admission、typed receipt、read-back、retry，以及 `TaskPlan` / current-state
projection。核心可以验证这些协议是否被正确推进，但不能解释具体业务的输入规范化、去重、评分、
业务字段、目标记录或完成策略。

具体业务策略必须由已加载 skill、workflow/domain actor，或 provider-owned typed contract 提供。
如果某个业务事实需要稳定查询，它必须由自己的 authoritative actor 发布强类型状态并物化到专属
read model；不得把业务 evidence、artifact 或控制字段塞入通用 NyxID Chat bag，也不得在 Chat actor、
共享 projection、默认 prompt/overlay、Web tool provider 或前端 Chat contract 中硬编码固定业务路线。

`tools/ci/nyxid_chat_semantics_guard.sh` 对这些生产边界执行静态门禁，防止具体业务类型、字段、
工具名、策略文案和固定 workflow route 重新进入通用 Chat 主链。

Standalone Workflow Host behavior is unchanged: its own `POST /api/chat` remains Workflow
JSON/multipart, and `GET /api/ws/chat` remains the Workflow WebSocket surface. Those routes are
explicit Workflow Host capabilities, not a second Mainnet Chat product. New NyxID clients use only
the typed HTTP facade and `/api/chat/conversations/**`; scoped NyxIdChat routes and the frozen
workflow-shaped leg are compatibility adapters, not evolving contracts.

## Chat Activity audit surface

Mainnet exposes `GET /api/audit/chat-activity` as a sanitized Audit Trail view of tool calls
from both `POST /api/chat` branches and committed NyxID browser-action facts. It stores and
returns only typed tool/action identities, safe outcome/correlation fields, and typed chat
provenance such as surface, conversation, turn, task, step, and action-request IDs.

It does not store or return prompts, assistant text, reasoning, input parts, attachments, tool
arguments/results, action parameters/resources, raw subjects, or credentials. It does not fetch
conversation history or transcripts. An authenticated user is fixed to their own scope and every
HMAC identity retained for the 30-day window. An Aevatar platform admin may explicitly request
`scope=__all__`; admin reads remain personal by default.

Tool capture reuses the existing tool-execution audit middleware. Browser-action capture consumes
committed actor facts through the existing unified Projection Pipeline. Neither path creates a
ChatLog store, Chat Activity actor/read model, or second projection rail. The default 30-day TTL
applies only to Audit Trail artifacts with typed chat provenance.

## 1. 端点与职责

| Endpoint | 协议 | 作用 |
|---|---|---|
| `POST /api/chat` | HTTP + SSE | Mainnet typed command 进入 Assistant actor 主干；既有 form/no-type 请求进入冻结的 external Workflow compatibility adapter；Workflow Host 直接发起 Workflow run |
| `GET /api/ws/chat` | WebSocket | 与 `/api/chat` 同能力，使用 WS 封装 |
| `POST /api/workflow-webhooks/{routeKey}` | HTTP JSON | 认证外部 webhook，并按 Host 或 scope-owned exact binding 启动新 run |
| `PUT /api/scopes/{scopeId}/workflow-webhooks/{routeKey}` | HTTP JSON | 为当前 scope 注册或更新 exact Definition/revision webhook binding |
| `GET /api/scopes/{scopeId}/workflow-webhooks` | HTTP JSON | 列出当前 scope 的 webhook bindings（secret 只返回 set/unset） |
| `DELETE /api/scopes/{scopeId}/workflow-webhooks/{routeKey}` | HTTP JSON | 原子删除仍归当前 scope 所有的 binding |
| `POST /api/workflows/resume` | HTTP JSON | 恢复 `human_input/human_approval` 挂起步骤 |
| `POST /api/workflows/signal` | HTTP JSON | 向等待信号的步骤发送 signal |

说明：以下 Workflow 输入与 producer 章节描述 Standalone Workflow Host 以及 Mainnet 的冻结
external compatibility adapter，不是 Mainnet 普通 Chat 的第二执行路径。对于 Standalone Workflow
Host 请求，`/api/chat` 与 `/api/ws/chat` 走同一套执行链路，差别只有传输协议。

口径补充：

- API 输入会先规范化为应用命令模型，再走 CQRS 标准命令骨架：`target resolve -> command context -> envelope -> dispatch port -> accepted receipt`。
- Workflow capability 只提供 workflow 特有的目标解析、payload 映射与观察映射；命令生命周期契约属于 CQRS Core，而不是 workflow 私有协议。
- 命令最终会被包装成 `EventEnvelope`；目标 Actor 的获取/创建由 `IActorRuntime` 负责，envelope 投递由 `IActorDispatchPort` 完成。
- 这里的 `EventEnvelope` 是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。
- 命令主链路不额外经过 ingress queue/stream；stream 保留给 actor envelope 的投影、实时输出与读侧观察。
- `command.ack` / `accepted=true` 对外只应被解释为“系统接受了该次交互并返回追踪句柄”，不应被解释为领域事件已提交或 ReadModel 已可见。
- Workflow command accepted 后，interaction layer 要求在 30 秒内收到首个 projection-backed 业务 frame。该 deadline 只约束“accepted 到首次可观察”，不是整个 workflow 的执行超时；若超时，SSE 必须发送 `RUN_ERROR(code=RUN_OBSERVATION_TIMEOUT)` 并关闭，不能只保留 heartbeat。
- Webhook ingress 是 start-run 入口，不是 `wait_signal` continuation；外部 JSON 只在 Host/Adapter 边界解析，进入应用层后只保留 typed `WorkflowExternalIngressContext` 与 `WorkflowChatRunRequest`。

## 2. 输入模型（chat）

```json
{
  "prompt": "用户输入，必填",
  "workflow": "可选：已注册 workflow 名称（内建 + 文件加载）",
  "source": {
    "kind": "definition_actor",
    "definitionActor": { "actorId": "可选：显式 Workflow Actor 地址" }
  },
  "workflowYamls": ["可选：inline YAML bundle（数组）"]
}
```

选择优先级：

1. `workflowYamls`（inline bundle，首项为入口 workflow）
2. `workflow`（已注册 workflow 名称 lookup）
3. 当 source/workflow/workflowYamls 都为空时，外部 API 边界默认路由到 `auto`
4. 复用已绑定 workflow 的 Actor 时，使用 typed `source.definitionActor.actorId`

契约约束：

- `workflow` 只表示“按名称查找已注册 workflow”（内建 + 文件加载）。
- `workflowYamls` 只表示“inline YAML bundle”，不承担名称查找语义。
- `actorId` 只由 typed source 子消息承载：`source.definitionActor.actorId` 或 `source.inlineBundle.actorId`。
- 若同时传 `workflow` 与 `workflowYamls`，以 `workflowYamls` 为准。
- `direct/auto/auto_review` 可显式传入，按注册表解析，不要求存在同名文件。

### HTTP 请求 producer

Standalone Workflow Host 所拥有的 `POST /api/chat` 请求支持两种 Host/API 边界 producer；
两者最终都会被规范化为同一个 `WorkflowChatRunRequest`，并进入同一条 CQRS command skeleton。
Host 不直接编排 workflow run，也不因为表单上传创建第二套执行链路。Mainnet 仅为 Phase 1
schema/behavior compatibility 保留既有 workflow-shaped 输入，并在命名明确的 external adapter
`ExternalWorkflowChatCompatibilityAdapter` 中复用该 Workflow handler；Aevatar-owned Studio
不使用此路径。

#### JSON Chat Input

`application/json` body is `HttpChatInput`:

```json
{
  "prompt": "describe the release plan",
  "workflow": "direct",
  "sessionId": "session-1"
}
```

`scopeId` is resolved from the authenticated principal and must not be provided in the request body.

常用 source 字段：

| Field | Meaning |
|---|---|
| `workflow` | 已注册 workflow 名称查找。 |
| `workflowYamls` | inline YAML bundle，首项为入口。 |
| `source.definitionActor.actorId` | 显式复用 workflow definition actor。 |
| `source.inlineBundle` | typed inline YAML bundle。 |

#### Multipart File Producer

`multipart/form-data` 用于在 Host/API 边界上传文件并启动同一条 chat run 主链路。multipart shape、字段名、大小、媒体类型、raw `payload` JSON 与 pending file bytes 由 workflow infrastructure 的共享 `WorkflowMultipartFileInputParser` 处理；artifact ingress 与 typed `WorkflowFileRef` 注入只在确认目标语义后发生。

必需字段：

| Field | Requirement |
|---|---|
| `file` | 必须至少出现一个文件 part；字段名必须是 `file`，可通过 `WorkflowFormFileIngress:FileFieldName` 调整。同一字段可重复，按 form 顺序追加 input parts。 |

可选字段：

| Field | Meaning |
|---|---|
| `payload` | Optional `HttpChatInput` JSON; must not contain `inputParts[].inlineFile`, `inputParts[].fileRef`, or `inputParts[].dataBase64`. |
| `prompt` | 覆盖或补充 payload 中的 prompt。 |
| `workflow` | 覆盖或补充 payload 中的 workflow name。 |
| `sessionId` | 覆盖或补充 payload 中的 session id。 |
| `workflowYaml` | legacy single inline YAML field。 |
| `workflowYamls` | inline YAML bundle；同名 form field 可重复。 |

默认校验：

| Option | Default |
|---|---|
| `WorkflowMultipartFileIngress:MaxFileBytes` | `10485760` |
| `WorkflowMultipartFileIngress:AllowedMediaTypes` | `image/png`, `image/jpeg`, `image/webp`, `audio/mpeg`, `audio/wav`, `audio/wave`, `audio/x-wav`, `video/mp4`, `application/pdf`, `application/vnd.openxmlformats-officedocument.wordprocessingml.document`, `text/csv`, `text/plain`, `text/markdown`, `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet` |

成功路径：

1. Host 先校验 caller credential；无效 bearer 不读取文件、不写 artifact store。
2. Host 通过共享 parser 校验 multipart shape、字段名、大小、media type，并得到 raw payload JSON 与 pending files。
3. `/api/chat` 兼容 facade 在 payload 校验通过后调用 `IWorkflowFileIngressPort.IngestAsync(...)`，`SourceKind=FormUpload`。
4. 共享 parser 的 mapping helper 把返回的 typed `WorkflowFileRef` 构造成既有 `ChatInputContentPart.fileRef`；`image/*`、`audio/*`、`video/*` 分别映射为 `type=image/audio/video`，其余已允许的 document/text/spreadsheet 类型统一映射为 `type=file`。
5. `ChatRunRequestNormalizer` 继续生成 `WorkflowChatRunRequest`，后续 CQRS command skeleton 不区分 JSON producer 与 form producer。

actor-facing command、state、readmodel、stream frame 与日志都不得携带上传文件 bytes/base64；它们只携带 `WorkflowFileRef` 或由它派生出的 URI/metadata。

常见 Host/API 边界错误：

| Code | HTTP status | Meaning |
|---|---:|---|
| `INVALID_CALLER_CREDENTIAL` | 400 | Authorization bearer 格式无效。 |
| `INVALID_CHAT_INPUT` | 400 | JSON body 或 multipart payload 不是合法 `ChatInput`。 |
| `INVALID_FILE_INPUT` | 400 | 文件缺失、字段名不匹配、media type 不允许、大小超限或 payload 试图携带 actor-facing file payload。 |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | 请求不是 JSON，也不是 `multipart/form-data`。 |
| `PROMPT_REQUIRED` | 400 | normalizer 无法从 prompt 或 input parts 得到有效输入。 |
| `WORKFLOW_NOT_FOUND` | 404 | workflow 名称未命中。 |
| `WORKFLOW_BINDING_MISMATCH` | 409 | 目标 actor workflow binding 与请求不一致。 |

WebSocket 不接收 `multipart/form-data`。文件输入应先经 HTTP artifact ingress producer 生成 typed file ref，或由客户端使用已有 `fileRef` descriptor。

Scope service stream 入口（如 `/api/scopes/{scopeId}/invoke/chat:stream`、member/team stream）也可以接收 `multipart/form-data`，但 Host 只做 Content-Type 分派、path `scopeId` 权威性、DTO 反序列化、service kind gating 和错误映射。共享 parser 先返回 raw payload JSON、`HasFiles` 与 pending files；只有 service invocation 目标解析并确认是 workflow service 后，Host 才使用 path `scopeId` 调用 `IWorkflowFileIngressPort.IngestAsync(...)` 并追加 typed file refs。static / scripting 目标收到 multipart 文件时 fail closed，且不得通过公开 JSON `inputParts`、headers 或 metadata 承载上传文件语义。

### 多模态文件输入

`inputParts` 支持两类文件载体：

1. `inlineFile`：只用于小型 inline bytes。`inlineFile.sizeBytes` 是可选校验字段，服务端只用它和 decoded base64 长度比对；它不是客户端声明的 workflow 文件事实。Host API 会把 decoded bytes 写入 workflow file ingress store，并把 command input part 替换为 typed `WorkflowFileRef`，因此 actor-facing request 不长期携带 inline base64。
2. `fileRef`：用于已经由外部 ingress、connected service 或后续 artifact store 产生的稳定文件引用。API 会归一化为 typed `WorkflowFileRef` 并写入 command envelope，同时保留旧的 `uri/name/mediaType` 镜像字段供现有消费者兼容。`type` 可为 `image`、`audio`、`video` 或 `file`；`file` 表示通用文件/文档引用，不再拆成多个 actor-facing 行为枚举。

```json
{
  "inputParts": [
    {
      "type": "file",
      "fileRef": {
        "fileId": "file-1",
        "artifactId": "artifact-1",
        "sourceKind": "connected_service_resource",
        "sourceMessageId": "om_1",
        "sourceResourceKey": "file_key_1",
        "fileName": "invoice.pdf",
        "mediaType": "application/pdf",
        "sha256": "redacted",
        "createdAtUnixMs": 1710000000000,
        "expiresAtUnixMs": 1710003600000
      }
    }
  ]
}
```

`fileRef` 约束：

- `fileId` 或 `artifactId` 至少有一个必须存在；旧 `uri` 会被映射为 `artifactId`。
- `sourceKind` 可省略；显式传入时必须是 `chat_input`、`form_upload`、`connected_service_resource`、`external_resource`、`generated` 或 `unspecified`。
- 时间戳必须为非负 Unix milliseconds；同时存在 `createdAtUnixMs` 与 `expiresAtUnixMs` 时，过期时间不得早于创建时间。
- public `fileRef` 不接受 `sizeBytes`。文件大小事实只能由 ingress/artifact descriptor 或 decoded bytes 产生，不能由客户端在 reusable file ref 上声明。
- 当前文件链路支持 chat/API inline bytes 暂存、Lark resource 下载、command-level `fileRef` 替换、descriptor-only projection readmodel 物化、单一公开 `document_extract` 文档抽取，以及 workflow-only 文件提交。
- `document_extract` 只能读取 workflow artifact store 中的文件引用；显式 arguments `fileRef` 优先，未传 `fileRef` 时只允许从当前 step 的 typed input file refs 中选择唯一一项，0 个或多个输入文件都 fail closed。`extraction_kind` 可省略或设为 `text`，此时返回既有 descriptor + bounded extracted text JSON shape；支持 UTF-8 text/json/markdown/csv、PDF text、DOCX text，以及 `image/png` / `image/jpeg` 图片文字抽取。图片路径默认最多读取 5 MiB，只通过已配置且支持 image input 的 `ILLMProvider.ChatStreamAsync` 聚合文本 delta；未配置或不支持时返回 `image_provider_unavailable`，图片超限返回 `image_too_large`，provider 异常返回 `image_extraction_failed`。`image/webp` 当前仍不属于 `document_extract` 支持类型。结果不返回 bytes/base64。
- `document_extract` 也支持 `extraction_kind=schema_bound_json`，但仍是同一个公开 tool，不新增第二个 OCR/tool surface。该模式必须提供 `schema_contract`，形如 `{ "name": "invoice_summary", "description": "...", "schema": { ... } }`；v1 schema guard 只允许收敛 JSON Schema 子集（`type/properties/required/additionalProperties/items/enum/description/title/$schema`），并对 provider 结果做 fail-closed 校验。成功输出为 canonical JSON envelope：`extraction_kind=schema_bound_json`、`media_type`、sanitized `file` descriptor、`schema_name`、`schema_hash`、`structured_result`；不会回显 provider raw body、base64/data URI、prompt 或 extracted text。缺少 provider 返回 `schema_bound_provider_unavailable`，provider 异常返回 `schema_bound_extraction_failed`，provider JSON 无效或不符合 schema 返回 `schema_bound_validation_failed`。
- `schema_bound_json` v1 的结构化结果仍只通过既有 `WorkflowToolCallCompletedEvent.result_json` / `StepCompletedEvent.output` string 通道传播，不修改 `workflow_execution_messages.proto`。一旦 schema-bound output 成为 actor state、domain event payload、readmodel/projection transport、SDK DTO/response、跨模块 command/query contract，或 workflow engine 开始解释 `structured_result` 内业务字段，必须新增 `.proto` typed contract，并把 string output 只视为兼容序列化。
- `workflow_file_submit` 是 workflow-only、policy-bound 的 NyxID multipart upload primitive。tool 参数只表达候选上传请求：`file_ref`、`slug`、`path`、`method`、`file_field_name`、字符串 `form` 字段、`output.kind`、`output.selector` 与可收窄的 `max_file_bytes`；执行前必须由 `IWorkflowFileMultipartUploadPolicyResolver` 解析为 Host/provider-owned safety policy decision，无 policy、policy 不可用或被拒绝都 fail closed。Mainnet resolver 不维护静态 destination allowlist，也不决定用户能上传到哪个 NyxID service；它只限制通用安全上限（当前允许 `POST/PUT/PATCH` 这类上传/替换/局部更新方法，并使用 Mainnet 文件大小上限，用户 `max_file_bytes` 只能进一步收窄）。Workflow runtime 继续负责候选 destination 的通用校验：相对 `path`、非空 `slug/file_field_name`、安全 `output.selector`、禁止 `target/headers/body/raw_body/bytes/base64/data_uri` 等；resolved policy 中的 `slug/path/file_field_name/form/output.kind/output.selector` 从 candidate 透传，不包含 destination-level media type allowlist。Workflow runtime 只依赖 `IWorkflowFileArtifactReadPort`、`IWorkflowFileMultipartUploadPolicyResolver` 与 `IWorkflowFileMultipartUploadPort`，不直接依赖 `NyxIdApiClient`、connected-service submit target registry 或 Lark upload adapter。public `file_ref` 只携带 identity/ownership，文件名、媒体类型、大小与 hash 只能来自 artifact descriptor；结果固定为 `success/error/detail/output_code/output_kind/http_status/provider_code/destination/file`，不生成 `file_token`、`file_code` 等 provider alias，也不回显 provider raw body、bytes/base64/data URI。文件内容只在 artifact read port 与 NyxID multipart upload port 边界流式传递，不进入 actor state/readmodel/prompt/result JSON。
- workflow file artifact backend 由 Host 组合显式选择。`FileSystem` backend 是本地/测试默认；生产环境必须使用 `WorkflowFileArtifacts:Backend=External`，并由部署显式注册 ingress/read/ownership/cleanup 四个 artifact ports。缺少任一端口时启动 fail closed，不能静默回落到 filesystem。
- artifact descriptor manifest 是文件可读性的提交记录；workflow run 归属仍是 actor-owned fact。Host 只通过后台 `IWorkflowFileArtifactCleanupPort` 触发 provider-owned cleanup；provider 基于 durable descriptor/index state 清理过期 descriptor-committed artifacts 与未完成 staged content，不使用进程内 run/artifact registry。

## 3. 自动编排能力（按 prompt 决策）

框架内建了 `direct`、`auto`、`auto_review` 三个 workflow（内部能力）。

### `direct`

- 直接 `llm_call` 输出答案。

### `auto`

典型链路：

1. `classify`：判断“直接回答”还是“输出 YAML”
2. 非 YAML：直接回答用户问题并结束本次 run（不进入 YAML 校验/审批）
3. YAML：先经过 `workflow_yaml_validate` 校验
4. 校验失败：走 `refine_yaml` 继续修正
5. 校验成功：进入 `human_approval`
6. 人工拒绝：走 `refine_yaml`，再次审批（可多轮）
7. 人工通过：`dynamic_workflow` 执行定稿 YAML

结论：`auto` 支持“根据 prompt 自动写 workflow + 强制校验 + 人工审批 + 通过即执行”。

### `auto_review`

和 `auto` 相同地支持“自动生成 + 强制校验 + 多轮审批优化”，但审批通过后：

- 不自动执行，只输出最终 YAML（适合手动触发最终 run）。

## 4. Human Approval / Human Input 如何继续

### Webhook start-run

`POST /api/workflow-webhooks/{routeKey}` 用于外部系统认证后启动新的 workflow run。`routeKey` 可以匹配 Host 配置的静态 binding，也可以匹配 scope member 通过管理 API 注册的动态 binding。二者共享 canonical route namespace；大小写/首尾空白统一规范化，同名冲突 fail closed，动态 binding 不能遮蔽静态 binding。

运行语义：

- 动态 binding 只接受当前 scope 的 committed Definition actor，并在注册时固定 revision；每次 ingress 在 HMAC 成功后、replay admission 前重新核对 actor kind、scope、workflow name、revision、definition payload/version 与 capability admission digest，发生 drift 返回 `409`，不启动 run。
- Host 有界读取 raw JSON，先执行 HMAC 校验，再按 binding 从已签名 body 解析 delivery id 与 prompt。delivery id header 若配置只能与 body 值相同，不能作为未签名的 replay identity。
- JSON prompt template 使用结构化 JSON 替换，字符串引号、反斜杠与换行保持原值；缺失 path、未知 placeholder、非法 template、超限 body/template/output/delivery id 均 fail closed。`@run_date` 按 binding 的 IANA/系统时区计算，默认 UTC。
- `CommandIdSeed` 与 `CorrelationIdSeed` 是对 canonical `route/source/delivery` 长度前缀 tuple 做 SHA-256 后得到的稳定 opaque seed，避免分隔符歧义。
- `WorkflowChatRequestEvent.external_ingress` 承载 typed route/source/delivery/fingerprint/auth 信息；这些稳定语义不得塞进 `Metadata`。
- 动态 binding 以 Protobuf 持久化；HMAC secret 加密落盘，GET/list 只返回 set/unset，不回显 secret。
- 默认 binding 只启动 run。显式 `enableUnattendedEffects=true` 时，管理请求必须来自同 scope 的 direct-human NyxID access credential，目标必须是 exact versioned Durable Definition；服务端把 caller binding authority 与 eligible authored-request write call-sites 密封、加密保存。binding 不接受或持久化 user bearer token。
- unattended authorization 在 ingress、run-start 和每个 tool call 三层与 authoritative definition/plan/authority 重验，仅为 exact non-destructive `nyxid_proxy` write 生成 process-local permit；不得传播给 LLM、fork、subworkflow 或 dynamic replacement。它只满足 Aevatar 本地 approval gate，下游 NyxID/provider policy 仍可独立拒绝或要求批准。
- 防重放由 `IWorkflowWebhookReplayStore` 承载，生产实现必须是 durable/distributed first-writer-wins store；显式 in-memory 实现只允许本地或测试使用。
- 启用 webhook ingress 但没有 replay store 时，Host fail closed 返回 `503 WEBHOOK_REPLAY_STORE_UNAVAILABLE`。
- 成功响应是 `202 Accepted`，只表示命令已被接受并可追踪；不承诺 run 已提交、执行完成或 readmodel 已刷新。
- 当前 admission record 在 dispatch 接受后用于压制重复投递，但尚未与 workflow terminal state 建立 lease/complete 状态机；因此不得宣称 crash-safe exactly-once。webhook HMAC 只证明事件来源；只有显式 opt-in 且 exact Durable authorization 全部通过时，才具备上述受限写权限。

`POST /api/workflows/signal` 仍只用于已有 run 的 `wait_signal` continuation，必须携带已知 `actorId + runId + signalName`，不能作为新 run webhook trigger 使用。

当 run 到 `human_input` 或 `human_approval`，运行时 envelope 投影流会发出 `HUMAN_INPUT_REQUEST`，包含：

- `runId`
- `stepId`
- `prompt`
- `suspensionType`
- `metadata`

客户端拿到这些字段后，调用恢复接口：

```json
POST /api/workflows/resume
{
  "actorId": "Workflow:xxx",
  "runId": "run-xxx",
  "stepId": "show_for_approval",
  "approved": false,
  "userInput": "这里是优化建议",
  "commandId": "建议传，便于串联同一轮交互"
}
```

如果某些流程在等待外部信号，再调用：

```json
POST /api/workflows/signal
{
  "actorId": "Workflow:xxx",
  "runId": "run-xxx",
  "signalName": "continue",
  "payload": "任意字符串",
  "commandId": "建议传"
}
```

实践建议：显式传递 `actorId + runId (+ stepId)`，不要依赖服务端内存映射。

## 5. 输出事件（SSE/WS）

统一输出 `WorkflowRunEventEnvelope` proto；JSON 仅作为 SSE/WS external wire adapter 表达。核心事件类型包括：

- `RUN_STARTED` / `RUN_FINISHED` / `RUN_ERROR`
- `STEP_STARTED` / `STEP_FINISHED`
- `TEXT_MESSAGE_START` / `TEXT_MESSAGE_CONTENT` / `TEXT_MESSAGE_END`
- `HUMAN_INPUT_REQUEST`
- `TOOL_CALL_START` / `TOOL_CALL_END`
- `STATE_SNAPSHOT`
- `CUSTOM`

常见 `CUSTOM` 事件：

- `aevatar.run.context`：回传 `actorId/workflowName/commandId`
- `aevatar.step.request`、`aevatar.step.completed`
- `aevatar.llm.reasoning`：LLM 思考过程增量
- `aevatar.media.chunk`：媒体分片，payload 为 `MediaContentEvent`
- `aevatar.workflow.waiting_signal`

SSE `: keepalive` 只维持传输连接，不表示 run 有业务进展，也不会延长 accepted observation deadline。任何已经 accepted 但在 deadline 内没有首个 projection-backed frame 的 run，都以 `RUN_OBSERVATION_TIMEOUT` 终止当前 stream；客户端可继续使用 `actorId + commandId` 查询该 run 后续状态。

Workflow role 的用户可见输出只投影 actor 已提交的
`RoleChatSessionProgressedEvent` 与 completion 内的 typed terminal tail。
text/reasoning/media/tool/usage/authorization 均通过同一 Projection Pipeline
生成 run-event；不存在 workflow 专用 transient chunk 旁路。Role terminal
progress 不关闭 workflow stream，`RUN_FINISHED/RUN_ERROR` 的唯一权威仍是
workflow 根 actor 的 committed terminal event。

## 6. WebSocket 请求/回包协议

连接 `GET /api/ws/chat` 后，发送：

```json
{
  "type": "chat.command",
  "requestId": "client-req-1",
  "payload": {
    "inputParts": [
      { "type": "text", "text": "帮我分析这段录音和截图" },
      { "type": "audio", "uri": "https://example.com/call.wav", "mediaType": "audio/wav" },
      { "type": "image", "uri": "https://example.com/screenshot.png", "mediaType": "image/png" }
    ]
  }
}
```

服务端回包类型：

- `command.ack`：返回 `commandId/actorId/workflow`
- `agui.event`：逐帧业务事件（payload 即 `WorkflowRunEventEnvelope` 的 JSON wire 表达）
- `command.error`：输入或启动阶段错误

`command.ack` 使用约束：

1. 客户端应把 `actorId + commandId` 视为后续观察句柄，其中 `commandId` 负责追踪，`actorId` 负责定位。
2. `command.ack` 是 CQRS dispatch pipeline 生成的 accepted receipt，只表示当前命令已经通过 runtime 成功 dispatch 到目标 actor 语义边界。
3. 最终结果仍以 `agui.event` 流与 `/api/actors/*` 查询为准。

## 7. 常见使用模式

### 模式 A：直接对话

```json
{ "prompt": "解释一下 event sourcing" }
```

### 模式 B：自动生成 + 审批 + 自动执行

```json
{ "prompt": "设计一个内容生产流水线，包含并行校对与质量门禁", "workflow": "auto" }
```

### 模式 C：自动生成 + 审批 + 只定稿

```json
{ "prompt": "设计一个多语言本地化流程，先不要执行", "workflow": "auto_review" }
```

### 模式 D：inline 多工作流 bundle（支持 workflow_call 子流程）

```json
{
  "prompt": "执行 inline 流程",
  "workflowYamls": [
    "name: root\nroles: ...\nsteps: ...",
    "name: child_a\nroles: ...\nsteps: ...",
    "name: child_b\nroles: ...\nsteps: ..."
  ]
}
```

---

参考实现：

- `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/*`
- `src/workflow/Aevatar.Workflow.Application/Runs/*`
- `src/workflow/Aevatar.Workflow.Application/Workflows/WorkflowDefinitionCatalog.cs`
