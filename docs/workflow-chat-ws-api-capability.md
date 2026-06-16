# Workflow Chat / WebSocket API Capability

本文是 Workflow chat HTTP/SSE 与 WebSocket 能力的单一说明入口。Host 只做协议适配和依赖组合；业务输入会被规范化为 `WorkflowChatRunRequest`，再进入 CQRS command skeleton。

## Transport Surface

| Endpoint | Transport | Purpose |
|---|---|---|
| `POST /api/chat` | HTTP + SSE response | 启动 workflow chat run，并持续返回 run event frames。 |
| `GET /api/ws/chat` | WebSocket | 通过 `chat.command` frame 启动同等 workflow chat run，并返回 WebSocket frames。 |

两条入口最终都复用 `ChatRunRequestNormalizer -> WorkflowChatRunRequest -> CQRS command skeleton`。Host 不直接编排 workflow run，也不把协议输入变成第二套执行链路。

## JSON Chat Input

`POST /api/chat` 的 `application/json` body 是 `ChatInput`：

```json
{
  "prompt": "describe the release plan",
  "workflow": "direct",
  "sessionId": "session-1",
  "scopeId": "scope-1"
}
```

常用 source 字段：

| Field | Meaning |
|---|---|
| `workflow` | 已注册 workflow 名称查找。 |
| `workflowYamls` | inline YAML bundle，首项为入口。 |
| `source.definitionActor.actorId` | 显式复用 workflow definition actor。 |
| `source.inlineBundle` | typed inline YAML bundle。 |

当 `workflow` 与 `workflowYamls` 同时出现时，固定使用 inline YAML bundle 路径；`workflow` 只作为 entry name 语义参与规范化。

## Multipart File Producer

`POST /api/chat` 也支持 `multipart/form-data`，用于 Host/API 边界上传一个文件并启动同一条 chat run 主链路。

必需字段：

| Field | Requirement |
|---|---|
| `file` | 必须且只能出现一个文件 part；字段名必须是 `file`，可通过 `WorkflowFormFileIngress:FileFieldName` 调整。 |

可选字段：

| Field | Meaning |
|---|---|
| `payload` | 可选 `ChatInput` JSON；不得包含 `inputParts[].inlineFile`、`inputParts[].fileRef` 或 `inputParts[].dataBase64`。 |
| `prompt` | 覆盖或补充 payload 中的 prompt。 |
| `workflow` | 覆盖或补充 payload 中的 workflow name。 |
| `sessionId` | 覆盖或补充 payload 中的 session id。 |
| `scopeId` | 覆盖或补充 payload 中的 workflow scope id，同时传给 file ingress owner scope。 |
| `workflowYaml` | legacy single inline YAML field。 |
| `workflowYamls` | inline YAML bundle；同名 form field 可重复。 |

默认校验：

| Option | Default |
|---|---|
| `WorkflowMultipartFileIngress:MaxFileBytes` | `10485760` |
| `WorkflowMultipartFileIngress:AllowedMediaTypes` | `image/png`, `image/jpeg`, `image/webp`, `audio/mpeg`, `audio/wav`, `audio/wave`, `audio/x-wav`, `video/mp4` |

成功路径：

1. Host 先校验 caller credential；无效 bearer 不读取文件、不写 artifact store。
2. Host 校验 multipart shape、文件数量、字段名、大小、media type。
3. Host 调用 `IWorkflowFileIngressPort.IngestAsync(...)`，`SourceKind=FormUpload`。
4. parser 把返回的 typed `WorkflowFileRef` 构造成既有 `ChatInputContentPart.fileRef`。
5. `ChatRunRequestNormalizer` 继续生成 `WorkflowChatRunRequest`，后续 CQRS command skeleton 不区分 JSON producer 与 form producer。

actor-facing command、state、readmodel、stream frame 与日志都不得携带上传文件 bytes/base64；它们只携带 `WorkflowFileRef` 或由它派生出的 URI/metadata。

## Error Semantics

| Code | HTTP status | Meaning |
|---|---:|---|
| `INVALID_CALLER_CREDENTIAL` | 400 | Authorization bearer 格式无效。 |
| `INVALID_CHAT_INPUT` | 400 | JSON body 或 multipart payload 不是合法 `ChatInput`。 |
| `INVALID_FILE_INPUT` | 400 | 文件缺失、多个文件、字段名不匹配、media type 不允许、大小超限或 payload 试图携带 actor-facing file payload。 |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | 请求不是 JSON，也不是 `multipart/form-data`。 |
| `PROMPT_REQUIRED` | 400 | normalizer 无法从 prompt 或 input parts 得到有效输入。 |
| `WORKFLOW_NOT_FOUND` | 404 | workflow 名称未命中。 |
| `WORKFLOW_BINDING_MISMATCH` | 409 | 目标 actor workflow binding 与请求不一致。 |

## WebSocket Contract

`GET /api/ws/chat` 接收首个 `chat.command` frame：

```json
{
  "type": "chat.command",
  "requestId": "req-1",
  "payload": {
    "prompt": "hello",
    "workflow": "direct"
  }
}
```

WebSocket 不接收 `multipart/form-data`。文件输入应先经 HTTP artifact ingress producer 生成 typed file ref，或由客户端使用已有 `fileRef` descriptor。
