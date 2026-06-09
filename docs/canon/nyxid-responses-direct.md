---
title: "NyxID Responses 直连"
status: active
owner: eanzhao
---

# NyxID Responses 直连

本文说明外部客户端如何通过 NyxID proxy 直连 Aevatar 的 OpenAI Responses、Anthropic Messages 与 OpenAI Chat Completions 兼容接口。

这里的“直连”不是绕过 NyxID 直接访问 Aevatar。客户端只认 NyxID proxy，Aevatar 只接收 NyxID 转发过来的 bearer 身份，再用同一把调用者凭据经 NyxID 访问下游 LLM 服务。

## 1. 范围与状态

当前对外入口有五个：

| 入口 | 协议形态 | 状态 | 说明 |
|---|---|---|---|
| `GET /v1/models` | OpenAI models list | 已上线 | 聚合调用者在 NyxID 上可达的 LLM 服务模型 |
| `POST /v1/responses` | OpenAI Responses 兼容 | 已上线 | 主入口，支持 streaming、工具声明、`previous_response_id` continuation |
| `POST /v1/responses/{id}/cancel` | OpenAI Responses cancel | 已上线 | 取消可见的 response session |
| `POST /v1/messages` | Anthropic Messages facade | 已上线 | 给 Messages-only 客户端使用，共享直连工具分类 |
| `POST /v1/chat/completions` | OpenAI Chat Completions facade | 已上线 | 给 Chat Completions-only 客户端使用，共享直连工具分类 |

`/api/scopes/{scopeId}/streaming-proxy/...` 仍由 Mainnet Host 保留给既有客户端，但已软废弃。该 route 会返回：

- `Deprecation: true`
- `Sunset: Wed, 25 Nov 2026 00:00:00 GMT`
- `Link: </v1/responses>; rel="successor-version"; title="Migrate direct model streaming to /v1/responses; StreamingProxy room fan-out has no one-to-one replacement"`

迁移口径必须诚实：直接模型对话、streaming 与工具调用迁到 `/v1/responses`；只会 Anthropic Messages 的客户端迁到 `/v1/messages`。StreamingProxy 的 room CRUD、participant join/post 与 room fan-out 是不同产品语义，`/v1/responses` 不是一对一替代。如果客户端依赖 room/fan-out，必须先明确新的 room 产品契约，不能把旧 StreamingProxy 当作新的通用 streaming 主入口继续扩展。

推荐外部客户端统一走：

```text
https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1/...
```

不要把客户端配置成 Aevatar Host 的真实域名。NyxID proxy 负责校验 API key、服务授权、凭据注入和审计；绕过它会导致 Aevatar 无法解析调用者身份。

## 2. 鉴权与 Caller Scope

所有 `/v1/*` 入口都要求：

```http
Authorization: Bearer nyx_...
```

Aevatar endpoint 本身标记为 anonymous，不是因为免鉴权，而是因为 NyxID API key 是 opaque token，不是 Host 的 JWT。handler 会手动取出 bearer token，并通过 NyxID 当前用户接口解析调用者。

如果 NyxID proxy 同时转发 `X-NyxID-Identity-Token`，Aevatar 会先在 Host 内用 NyxID JWKS 校验该 RS256 identity assertion。校验通过时，caller identity 直接取 token 的 `sub`，不会再调用 NyxID `/me`。该 header 一旦存在但签名、issuer、audience、有效期、`sub`、`jti` 或配置的 service id 校验失败，请求 fail closed，不回退 `/me`。只有 `X-NyxID-Identity-Token` 缺失时，才保留 bearer `/me` fallback。

`X-NyxID-Delegation-Token` 只是传给下游 NyxID/LLM/工具调用的 delegated credential，永远不作为 caller identity 输入；delegation-only 请求仍按 bearer `/me` fallback 解析调用者。

解析结果会落到 Responses caller scope：

- `scopeId`：NyxID user id
- `ownerSubject`：NyxID user id
- `origin kind`：`ApiKey`

后续 response session 可见性、工具状态、模型路由、下游 LLM 调用都基于这个 caller scope。`commandId/correlationId` 只用于追踪，不承担用户身份语义。

## 3. 模型清单与路由

`GET /v1/models` 会从 NyxID LLM service catalog 取到调用者可见的服务，再 fan-out 到每个服务的 `/models` 平面。Aevatar 返回给客户端的模型 id 统一带服务前缀：

```text
<service-slug>/<model-id>
```

例如：

```text
chrono-llm/gpt-5.5
llm-anthropic/claude-haiku-4-5
```

每个 model entry 还会尽量带上：

- `route_value`：NyxID 真实 route，例如 `/api/v1/proxy/s/chrono-llm`
- `group` / `owned_by`：服务 slug
- `context_length`
- `max_output_tokens`
- `display_name`
- `description`

发起 `/v1/responses` 或 `/v1/messages` 时，客户端应该优先使用 `/v1/models` 返回的完整 id。Aevatar 会解析 `<service-slug>/<model>`：

1. 如果 prefix 像 NyxID service slug，并能在调用者 catalog 中解析到 route，Aevatar 会把 route 写入 `NyxIdRoutePreference`，再把裸 model 传给下游 provider。
2. 如果 prefix 解析不到，或模型名本来就是裸模型名，Aevatar 走默认 gateway fallback，让 NyxID gateway 自己按 model 名处理。

裸模型名仍然保留是为了兼容旧调用方；新客户端不要依赖裸名自动路由。

## 4. Responses 主入口

`POST /v1/responses` 是当前推荐的主入口。它支持：

- JSON 与 SSE streaming 两种返回形态
- OpenAI Responses 风格的 `input`
- `tools` 声明
- `function_call_output`
- `previous_response_id`
- `max_output_tokens`、`temperature`
- response session 注册、查询可见性校验、24 小时 TTL

`previous_response_id` 的约束是诚实的：只能继续同一 caller scope 可见、未过期、未失败、未取消的 session。带 `function_call_output` 且显式带 `previous_response_id` 时，Aevatar 会按上一轮记录的 forwarded tool call 对账。

为了兼容 Anthropic 到 OpenAI 的转换器，如果请求里有 `function_call_output`，但没有 `previous_response_id`，Aevatar 不会直接报错，而是把这些 tool result 折进 prompt，形如：

```text
[tool_result call_id=...] ...
```

这只是兼容无状态客户端的历史上下文折叠，不等于正式 continuation。

## 5. 直连工具行为

`/v1/responses`、`/v1/messages`、`/v1/chat/completions` 使用同一套 `IResponsesDirectToolPlanService` + `IResponsesToolClassificationService` 抽象组装 tool sources 并做工具分类。三条 create 入口都会合并同一批全局 `IResponsesToolProvider`，并按 chat-route `ForwardToModel.ToolSetRef` 追加同一个 route tool set。Mainnet 的 direct LLM ingress 默认补 `workspace.default`；`lark.self_notify`、`voice.realtime` 也必须组合 `workspace.default`，所以 NyxID/Aevatar workspace tools 是默认可见能力，不依赖调用方显式配置 route tool set。最终工具分成三类：

- Aevatar substitute tools：由服务端接管执行并记录状态。
- Aevatar additive tools：由服务端额外注入，供模型主动调用。
- forwarded tools：保留给客户端或上游模型继续处理。

当前 substitute tools 包括：

| 工具名 | 说明 |
|---|---|
| `TodoWrite` | 持久化 agent-scoped todo state |
| `WebFetch` / `web_fetch` | 通过 Aevatar 抓取 URL，记录 trace/cache |
| `WebSearch` / `web_search` | 通过 Aevatar 执行 web search，记录 trace/cache |

旧 `Task` / `task` trace 契约暂留为 dead surface，当前 Mainnet 不再注册 fake Task substitute。需要执行 GAgent、team 或 workflow 时使用下方 workspace additive tools。

当前 additive tools 包括：

| 工具名 | 说明 |
|---|---|
| `use_skill` | 按名称加载本地或 Ornn 远程 skill，并把 skill 指令返回给模型执行 |
| `ornn_search_skills` | 通过 NyxID proxy 搜索调用者在 Ornn 上可见的 skill |
| `ornn_publish_skill` | 组装并校验私有 Ornn skill ZIP 后通过 NyxID proxy 发布 |

`use_skill`、`ornn_search_skills` 和 `ornn_publish_skill` 使用当前 `/v1/*` 请求的 bearer token，经 NyxID proxy 访问 Ornn API。也就是说，它们看到和写入的是这个调用者在 NyxID / Ornn 权限下可见的 skill，而不是 Aevatar 服务端的全局技能库。`ornn_publish_skill` v1 只发布 private skill，先在 Aevatar 内完成 workflow/script/package-format 校验，校验失败不会上传。使用受限 NyxID API key 时，`--allowed-services` 需要同时覆盖 `aevatar`、目标 LLM service，以及 Ornn API service（默认 slug 为 `ornn-api`，可通过 `Aevatar:Ornn:NyxIdSlug` 覆盖）。

chat-route policy 指定 `tool_set_ref` 或 `tool_choice_hint` 时，三条直连入口都会使用同一个 direct tool plan：同一个 tool set 会被注入，同一个 trusted prefilled arguments 合并规则会生效。不要为 `/v1/messages` 或 `/v1/chat/completions` 另建工具白名单。

直连工具的失败语义按边界分层处理。客户端声明但未被 Aevatar substitute 的 forwarded tools 仍然由客户端执行；这类工具不会在 Aevatar 内被降级。Aevatar 本地 direct tools 执行失败时，actor 会把失败转成合法 JSON tool output，并继续把结果送回模型，避免一个可选本地工具异常终止整次 response。该 JSON 只暴露稳定错误码、工具名和异常类型，不透出 token、请求头或内部路径。调用方取消仍然取消整次 run，不会被转成 tool output。chat-route `tool_set_ref` 解析错误仍然 fail closed 返回配置错误；但已经解析出的 tool source、全局 provider、skills/Ornn discovery 如果单个 source 失败，会记录 warning 并跳过该 source，其他可用工具继续进入本次计划。

## 6. 显式 Skill 触发

直连入口支持同一套显式 skill 触发语法：

```text
::skill-name optional arguments
```

触发只能出现在输入开头，或后续某一行的行首；句中普通文本例如 `please run ::skill` 不会触发。`skill-name` 会归一化为小写 route token，后面的文本原样作为 `command_arguments` 进入 skill recovery context。三条 `/v1/responses`、`/v1/messages`、`/v1/chat/completions` 入口只在 Application 层解析一次：`CommandName` 只给 chat-route policy 做命令匹配，arguments 和 discovery 只进入 `AgentSkillRecoveryContext`。

裸 `::` 表示请求 skill discovery。它不会伪造 route command 或 skill 名称，chat-route `CommandName` 为空，skill recovery 会触发 `ornn_search_skills`。`:: ` 这类只有触发符和空白的输入按普通文本处理。

Channel relay 入口也使用同一 parser。默认平台别名为：

| 平台 | 触发符 |
|---|---|
| CLI / direct `/v1/*` / direct `nyxid-chat` | `::` |
| Lark / Web channel | `::`、`/` |
| 其它 channel | `::` |

`/name` 只是在 Lark/Web channel 上保留的兼容别名，且本地注册 slash command（如 `/init`、`/model`）仍优先走本地确定性处理。canonical `::name` 不会被这些本地 slash command 吞掉；例如 `::model args` 表示名为 `model` 的 skill 触发，而不是 `/model` 本地模型配置命令。

命名触发会优先调用 `use_skill` 加载同名 skill；如果加载失败或后续出现 blocker，再按 recovery 规则使用 `ornn_search_skills` 查找更合适的 skill。`use_skill` 与 `ornn_search_skills` 仍然使用当前调用者的 NyxID/Ornn 可见性，和普通工具调用一致。

## 7. Messages 门面

`POST /v1/messages` 已经上线，但它不是 `/v1/responses` 的完整替代品。它的定位是让只会说 Anthropic Messages 的客户端，例如 Claude Code，能通过同一条 NyxID proxy 链路访问 Aevatar。

当前行为：

- 每个请求注册一个新的 `LlmSession`。
- 不支持 `previous_response_id`，也不在 Messages 表面维护 session continuation。
- `max_tokens` 必填。
- 支持 text、`tool_use`、`tool_result`、`thinking` 的基础映射。
- 支持 streaming，并输出 Anthropic Messages 事件形态。
- 与 `/v1/responses` 共享直连 tool-source plan 和工具分类，会注入 `use_skill`、`ornn_search_skills` 等 additive tools，也会用 Aevatar substitute tools 替换同名客户端 declared tools；chat-route 指定的 tool set / tool choice hint 同样生效。

当前限制：

- `top_p`、`top_k`、`stop_sequences` 会被拒绝。
- forced `tool_choice` 不支持；`tool_choice` 只能用于禁用工具。
- image content v1 会被丢弃并记录 warning。
- 它是无状态协议门面，不承载 background task、response session continuation 或完整 Responses 工具可观察性。

需要完整异步编排和 continuation 时，用 `/v1/responses`。

## 8. Chat Completions 门面

`POST /v1/chat/completions` 是 OpenAI Chat Completions 兼容窄门面，适合只支持 `OPENAI_BASE_URL` + `/chat/completions` 的客户端通过 NyxID proxy 直连 Aevatar。

当前行为：

- 每个请求注册一个新的 `LlmSession`。
- 复用 `/v1/responses` / `/v1/messages` 的 caller scope、模型路由和 NyxID bearer 透传。
- 支持 text `messages`、基础 `tool_calls` / `tool` message、`stream`、`temperature`、`max_tokens` / `max_completion_tokens`、`response_format`。
- 支持 OpenAI SSE chunk 形态，流结束输出 `data: [DONE]`。
- 与 `/v1/responses` 共享直连 tool-source plan 和工具分类，会注入 `use_skill`、`ornn_search_skills` 等 additive tools，也会用 Aevatar substitute tools 替换同名客户端 declared tools；chat-route 指定的 tool set / tool choice hint 同样生效。

当前限制：

- `n` 只支持 `1`。
- forced `tool_choice` 不支持；`tool_choice` 只能用于 `auto` 或 `none`。
- GAgent/team chat-route target 只能用 `ForwardToModel.ToolSetRef + ToolChoiceHint` 表达；`ForwardToTeam` / `ForwardToGAgent` wire action 已删除，该 tool-first 形态在三条直连入口同样生效。
- 不承载 Responses 的 `previous_response_id` continuation、background task 或完整工具可观察性。

需要完整异步编排和 continuation 时，用 `/v1/responses`。

## 9. API Key 要求

NyxID slug proxy 路由是 REST proxy plane：

```text
/api/v1/proxy/s/{slug}/{path}
```

因此 API key 至少需要 `proxy` scope，或更宽的 proxy scope。生产上可以用 `--allowed-services` 收紧到 Aevatar 与目标 LLM 服务；调试时可以先用 `--allow-all-services` 验证链路。

`--allowed-services` 必须填 `nyxid service list --output json` 里的 UserService id，不是 catalog id。填错时常见错误是 `api_key_scope_forbidden_legacy`。

## 10. 代码锚点

主机端入口：

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesEndpoints.cs`
- `src/Aevatar.Mainnet.Host.Api/Messages/MessagesEndpoints.cs`
- `src/Aevatar.Mainnet.Host.Api/ChatCompletions/ChatCompletionsEndpoints.cs`

模型聚合与路由：

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesModelsAggregator.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesRouteResolver.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesApiModels.cs`

调用者身份与工具：

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCallerScope.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesAevatarToolProvider.cs`
- `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCompletionApplicationService.cs`
