---
title: "Workflow Chat API 能力说明（框架层）"
status: active
owner: eanzhao
---

# Workflow Chat API 能力说明（框架层）

> 单一事实源（Single Source of Truth）：`/api/chat`、`/api/ws/chat` 相关能力说明以本文为准。  
> Host 侧入口文档：`src/workflow/Aevatar.Workflow.Host.Api/README.md`、`src/workflow/Aevatar.Workflow.Host.Api/CHAT_API_CAPABILITIES.md`。

本文档面向框架使用者，说明当前 `POST /api/chat` 与 `GET /api/ws/chat` 可以做什么，尤其是：

- 根据 `prompt` 自动判断是否要生成 workflow
- 在 `human_approval` 节点等待人工确认
- 支持多轮“反馈 -> 重新生成 -> 再审批”
- 审批通过后自动执行（`auto`）或只定稿不执行（`auto_review`）

## 1. 端点与职责

| Endpoint | 协议 | 作用 |
|---|---|---|
| `POST /api/chat` | HTTP + SSE | 发起一次 run，并持续接收运行时 envelope 投影流 |
| `GET /api/ws/chat` | WebSocket | 与 `/api/chat` 同能力，使用 WS 封装 |
| `POST /api/workflow-webhooks/{routeKey}` | HTTP JSON | 认证外部 webhook，并按 Host binding 启动新 run |
| `POST /api/workflows/resume` | HTTP JSON | 恢复 `human_input/human_approval` 挂起步骤 |
| `POST /api/workflows/signal` | HTTP JSON | 向等待信号的步骤发送 signal |

说明：`/api/chat` 与 `/api/ws/chat` 走同一套执行链路，差别只有传输协议。

口径补充：

- API 输入会先规范化为应用命令模型，再走 CQRS 标准命令骨架：`target resolve -> command context -> envelope -> dispatch port -> accepted receipt`。
- Workflow capability 只提供 workflow 特有的目标解析、payload 映射与观察映射；命令生命周期契约属于 CQRS Core，而不是 workflow 私有协议。
- 命令最终会被包装成 `EventEnvelope`；目标 Actor 的获取/创建由 `IActorRuntime` 负责，envelope 投递由 `IActorDispatchPort` 完成。
- 这里的 `EventEnvelope` 是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。
- 命令主链路不额外经过 ingress queue/stream；stream 保留给 actor envelope 的投影、实时输出与读侧观察。
- `command.ack` / `accepted=true` 对外只应被解释为“系统接受了该次交互并返回追踪句柄”，不应被解释为领域事件已提交或 ReadModel 已可见。
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

### 多模态文件输入

`inputParts` 支持两类文件载体：

1. `inlineFile`：只用于小型 inline bytes。`inlineFile.sizeBytes` 是可选校验字段，服务端只用它和 decoded base64 长度比对；它不是客户端声明的 workflow 文件事实。Host API 会把 decoded bytes 写入 workflow file ingress store，并把 command input part 替换为 typed `WorkflowFileRef`，因此 actor-facing request 不长期携带 inline base64。
2. `fileRef`：用于已经由外部 ingress、connected service 或后续 artifact store 产生的稳定文件引用。API 会归一化为 typed `WorkflowFileRef` 并写入 command envelope，同时保留旧的 `uri/name/mediaType` 镜像字段供现有消费者兼容。

```json
{
  "inputParts": [
    {
      "type": "image",
      "fileRef": {
        "fileId": "file-1",
        "artifactId": "artifact-1",
        "sourceKind": "connected_service_resource",
        "sourceMessageId": "om_1",
        "sourceResourceKey": "image_key_1",
        "fileName": "invoice.png",
        "mediaType": "image/png",
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
- 当前切片完成 chat/API inline bytes 的 file ingress 暂存、Lark resource 下载、command-level `fileRef` 替换，以及 descriptor-only projection readmodel 物化；`document_extract` 与外部文件提交仍属于文件链路后续实现。

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

`POST /api/workflow-webhooks/{routeKey}` 用于外部系统认证后启动新的 workflow run。`routeKey` 只匹配 Host 配置里的 binding；workflow 名称、scope、delivery id 来源、prompt 映射与 HMAC header 都由 `WorkflowWebhookIngress` options 承载，不在生产代码硬编码具体 workflow。

运行语义：

- Host 读取 raw JSON、执行 HMAC 校验、按 binding 映射 delivery id 与 prompt，然后构造 typed `WorkflowChatRunRequest`。
- `CommandIdSeed` 与 `CorrelationIdSeed` 使用稳定格式 `webhook:{routeKey}:{sourceId}:{deliveryId}`。
- `WorkflowChatRequestEvent.external_ingress` 承载 typed route/source/delivery/fingerprint/auth 信息；这些稳定语义不得塞进 `Metadata`。
- 防重放由 `IWorkflowWebhookReplayStore` 承载，生产实现必须是 durable/distributed first-writer-wins store；显式 in-memory 实现只允许本地或测试使用。
- 启用 webhook ingress 但没有 replay store 时，Host fail closed 返回 `503 WEBHOOK_REPLAY_STORE_UNAVAILABLE`。
- 成功响应是 `202 Accepted`，只表示命令已被接受并可追踪；不承诺 run 已提交、执行完成或 readmodel 已刷新。

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
