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
| `GET /v1/models` | OpenAI models list | 已上线 | 返回当前 scope effective policy 中显式配置的模型 |
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

`GET /v1/models` 先解析 caller scope，再只读该 scope 的 effective model catalog policy
current-state projection。scope 可以完整替换平台默认，也可以继承平台默认；显式空替换不会回退。
每个配置来源必须列出至少一个精确模型 ID，Aevatar 返回的 id 统一带服务前缀：

```text
<service-slug>/<model-id>
```

例如：

```text
chrono-llm/gpt-5.5
llm-anthropic/claude-haiku-4-5
```

每个 model entry 稳定包含：

- `group` / `owned_by`：服务 slug
- `created`：固定为 `0`

当前 policy 不存储 `context_length`、`max_output_tokens`、`display_name` 或 `description`，
因此这些扩展字段从 JSON 省略。列模型不会调用 NyxID `/api/v1/keys`、`/api/v1/services`
或任一上游 `/models`，也不会按 URL、display name 或 `llm` 子串推断服务能力。

发起 `/v1/responses` 或 `/v1/messages` 时，客户端应该优先使用 `/v1/models` 返回的完整 id。Aevatar 会解析 `<service-slug>/<model>`：

1. qualified model 必须在 effective policy 中精确命中同一 slug 和模型 ID，否则返回 `404 model_not_found`。
2. scope 自定义来源把 exact `userServiceId + serviceSlugSnapshot` 写入 typed `LLMRouteTarget`；平台默认来源写入 exact `catalogServiceId + serviceSlugSnapshot`。
3. NyxID provider 分别调用 `/api/v1/proxy/s/{slug}?_nyxid_via={userServiceId}` 或 `/api/v1/proxy/{catalogServiceId}`；NyxID 在 proxy 边界最终校验 caller、组织权限和 API-key `allowed_service_ids`。
4. policy/read model 暂不可读时返回 `503 model_route_unavailable`，不会回退到字符串猜测。

这两种 qualified route 都属于 NyxID REST proxy plane，因此调用 bearer 必须具备 `proxy`
或 `proxy:*` capability。只有 `llm:proxy` 的旧 token 仅覆盖 LLM gateway，不足以调用
`<service-slug>/<model>`；调用方必须重新签发包含 REST proxy capability 的 token。

裸模型名仍保留 gateway 或 owner UserConfig fallback，供旧调用方兼容；新客户端不要依赖裸名自动路由。
Admin 的 `/admin#/models` 页面使用 `/api/v1/keys`（scope）和 `/api/v1/services`
（平台管理员）作为配置候选，但候选 inventory 不是运行时事实。保存配置不会创建 NyxID binding
或授予权限，所以一个已列出的模型仍可能在调用时被 NyxID 拒绝。

## 4. Responses 主入口

`POST /v1/responses` 是当前推荐的主入口。它支持：

- JSON 与 SSE streaming 两种返回形态
- OpenAI Responses 风格的 `input`
- `tools` 声明
- `function_call_output`
- `previous_response_id`
- `max_output_tokens`、`temperature`
- response session 注册、查询可见性校验、24 小时 TTL

默认非 streaming create 是 completed-only：Aevatar 会先把 run command dispatch 到 response session actor，再通过 observation/projection 链等待 terminal event。成功时 HTTP 200 只返回 `status:"completed"` 的 response JSON，`output` 包含 completed message 和已完成的 function-call output item，`usage` 来自 terminal observation。

非 streaming create 不返回 `status:"in_progress"` accepted 空壳。观察超时返回 HTTP 504 error envelope，`code:"response_timeout"`；terminal failure 返回非 2xx error envelope，默认 HTTP 500 或 terminal failure 指定状态；terminal cancellation 返回 HTTP 409，`code:"run_cancelled"`；请求取消或客户端中断沿用 HTTP 408，`code:"request_timeout"`。这些失败不会返回 failed response JSON。真正的 `background:true` 和 `GET /v1/responses/{id}` retrieve 属于后续独立协议与 readmodel，不属于当前入口。

`previous_response_id` 的约束是诚实的：只能继续同一 caller scope 可见、未过期、未失败、未取消的 session。带 `function_call_output` 且显式带 `previous_response_id` 时，Aevatar 会按上一轮记录的 forwarded tool call 对账。

为了兼容 Anthropic 到 OpenAI 的转换器，如果请求里有 `function_call_output`，但没有 `previous_response_id`，Aevatar 不会直接报错，而是把这些 tool result 折进 prompt，形如：

```text
[tool_result call_id=...] ...
```

这只是兼容无状态客户端的历史上下文折叠，不等于正式 continuation。

### 4.1 Run 记录与 streaming 完成语义

Responses、Messages 和 Chat Completions 三条直连入口都把模型调用收敛到 `LlmSessionGAgent` 的 typed run 记录链路。HTTP handler 只 dispatch run command 并观察 terminal projection；真正的 provider stream 不在 Host 内同步消费，也不占用 session actor command turn，当前由 off-grain hosted run executor（`LlmRunExecutionWorker`，普通线程池任务，不占用任何 Orleans grain turn）连续消费。

执行边界如下：

1. `LlmRunRequested` 进入 session actor 后先记录 `LlmRunStartedEvent`，ACK 只表示 run 已被 actor 接受。
2. session actor 持久化 `LlmRunExecutionReadyEvent` 后，在自身 turn 内通过 `ILlmRunExecutionScheduler` 把 run 非阻塞入队到有界 `ILlmRunExecutionQueue`（满则抛 `LlmRunExecutionQueueFullException` → actor 落 `execution_dispatch_failed` 终态，不阻塞 actor turn）；host 注册的 `LlmRunExecutionWorker`（`BackgroundService`）取出后通过 `ILlmRunExecutionService` / `ILlmRunExecutor` 调用 `ILlmRunCore`，在任何 grain turn 之外连续消费 NyxID/provider 的 live `ChatStreamAsync`。run 的 crash/abandon 兜底由 session actor 自持久 run-timeout finalizer（分钟级，与 24h session TTL 解耦）负责。
3. 执行侧 sink 不直接写 session 状态，而是通过 `IActorDispatchPort` 把 chunk/tool/terminal 结果作为 typed `Record*` recorder commands 发回 session actor；session actor handler 再提交 `LlmStreamChunkObserved`、`LlmToolCallObserved`、`LlmSessionForwardedToolCallEmittedEvent`、`LlmRunCompleted`、`LlmRunFailed`、`LlmRunCancelled`。
4. session actor 是唯一权威状态源。它使用 `responseId + runId + sequence` 做幂等接受，重复 chunk、晚到 recorder command、重复 terminal dispatch、terminal 后失败重试都不会改写已提交 terminal 状态，也不依赖执行侧或进程内 sequence counter。
5. provider stream 没有 terminal chunk 时，取消或观察超时必须落成 `LlmRunCancelled` / terminal failure 这类 typed fact；不能依赖 Host 临时拼 response，也不能用 query-time replay 修补。
6. self-continuation 只适合持久化 actor 下一拍要处理的稳定事实，不适合保存 HTTP stream 枚举器。live provider stream 一旦离开当前 async consumption frame，就没有可重放的远端连接状态；因此不能设计为 session actor self-message 小步恢复，也不能交给无权威状态的 `Task.Run` callback 推进。若未来继续拆分执行器，执行器必须保持 run-scoped execution boundary，并通过 typed recorder commands 让 session actor 拥有 durable sequence/watermark、cancellation 和 terminal facts。

这组约束保证 streaming 与非 streaming create 都从同一个 committed terminal fact 得到结果：成功映射为 completed response，provider failure 映射为非 2xx error envelope，取消映射为 `run_cancelled`。

## 5. 直连工具行为

`/v1/responses`、`/v1/messages`、`/v1/chat/completions` 在 ownership classification 前共用
`IResponsesOwnedToolCatalogPlanner`。planner 固定 server-owned published profile snapshot，按本轮 intent 从 route ceiling
物化小型 exact catalog；普通 no-match 是 restricted empty，不会把 `workspace.default` 直接注入模型。
`IResponsesDirectToolPlanService` 只解析 route 的 typed tool-set ceiling 和 tool-choice hint，不是 always-on provider fallback。
三条入口随后用同一个 `IResponsesToolClassificationService` 把工具分成三类：

- Aevatar substitute tools：由服务端接管执行并记录状态。
- Aevatar exact tools：由本轮 frozen catalog 选择并由服务端执行。
- forwarded tools：保留给客户端或上游模型继续处理。

Workflow external-capability authoring 使用独立的
`workflow.external-capability-authoring` tool set，只包含 discovery、readiness 与
explicit-request preview 三个只读工具。它不进入 `workspace.default` 或普通
`nyxid.chat.default`；内置 Studio workflow 必须显式选择它，NyxID Chat 则只在 typed
`WORKFLOW_AUTHORING` 回合意图下从 Agent Profile route ceiling 物化该集合。完整 binding
source 不会随这个窄集合进入任何 surface。

前两类是 server-owned tools，最终参数在 caller-owned trusted prefill/hook 完成后冻结，并统一进入 `IAgentToolExecutionPort`。同一 proof/digest 约束 model schema 与 off-grain executor 的重新物化；端口内只做一次 safety classification，再执行 credential policy、actor-owned grant、start-once admission ledger 和 `WAITING_APPROVAL/RUNNING/TERMINAL` audit observation。只有 ledger 返回 `Started` 可以进入唯一 raw terminal `AdmittedAgentToolExecutor`。audit append status 不授予执行，terminal 已调用后的 audit failure 保留实际结果且不可重试。统一 catalog 契约见 [agent-turn-tool-catalog.md](agent-turn-tool-catalog.md)。

Responses ingress 边界的 substitute compatibility 包括：

| 工具名 | 说明 |
|---|---|
| `TodoWrite` | 仅当 profile 显式选择 `responses.state` / coding 能力时持久化 agent-scoped todo state |
| `WebFetch` / `web_fetch` | 通过 Aevatar 抓取 URL，记录 trace/cache |
| `WebSearch` / `web_search` | 通过 Aevatar 执行 web search，记录 trace/cache |

`WebFetch` / `WebSearch` 只是在客户端显式声明别名、且 frozen catalog 已授权 canonical
`web_fetch` / `web_search` 时做边界替换；内部 catalog 始终只有 snake_case canonical schema。
`ResponsesAevatarToolProvider` 不再作为 `workspace.default` additive source，作为内部 `responses.state` source 时也只暴露
`TodoWrite`。旧 `Task` / `task` trace 契约暂留为 dead surface，当前 Mainnet 不注册 fake Task substitute。

Skill 能力按 profile/intent 分层：

| 工具名 | 说明 |
|---|---|
| `use_skill` | 按名称加载本地或 Ornn 远程 skill，并把 skill 指令返回给模型执行 |
| `ornn_search_skills` | 通过 NyxID proxy 搜索调用者在 Ornn 上可见的 skill |
| `ornn_publish_skill` / `ornn_update_skill` | 仅 `skill.authoring` profile 可选择的私有 skill 写能力 |
| `list_external_workflow_capabilities` | 列出当前调用者可见的 exact workflow external-capability descriptors |
| `inspect_external_workflow_capability_readiness` | 对一个 exact typed selector 做只读 readiness 检查 |
| `preview_workflow_explicit_requests` | 只读预览 authored request 在 bind 前需要确认的 typed grants |

这些工具使用当前 `/v1/*` 请求的 bearer token，经 NyxID proxy 访问调用者可见的 Ornn capability，而不是服务端全局 skill 库。普通 skill intent 只允许 `ornn_search_skills` + `use_skill`；publish/update 必须显式进入 `skill.authoring` profile。`ornn_publish_skill` v1 只发布 private skill，并先做 workflow/script/package-format 校验。受限 NyxID API key 的 `--allowed-services` 需要覆盖 `aevatar`、目标 LLM service 与 Ornn API service（默认 slug `ornn-api`）。

chat-route policy 指定 profile、`tool_set_ref` 或 `tool_choice_hint` 时，三条直连入口都会使用同一个 plan；tool set 只是 ceiling，最终只能注入 frozen profile/intent catalog 中的 exact tools。不要为 `/v1/messages` 或 `/v1/chat/completions` 另建工具白名单。

直连工具的失败语义按边界分层处理。客户端声明但不属于 frozen Aevatar-owned catalog 的 forwarded tools 仍由客户端执行，不进入 `IAgentToolExecutionPort`。属于 catalog 的名称会写入 `owned_tool_names` 并成为 deny-forward 边界；同名 alias/canonical 重复、source collision、invalid schema、schema/connected-operation 安全预算超限或执行端 proof mismatch 都 typed fail closed，不调用模型或工具，也不跳过失败 source 后继续。Tool count 只作为优化目标，合法 exact catalog 超目标仍完整进入模型。Aevatar 本地 direct tool 在已经通过准入后的业务执行失败会转换成稳定 JSON tool output；`RUNNING Duplicate/Conflict` 不重放，terminal audit failure 保留真实 result 且不可重试。错误 JSON 不透出 token、请求头或内部路径；调用方取消仍取消整次 run。

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
- 与 `/v1/responses` 共享 profile snapshot、exact catalog planner、proof 校验和 forwarded classification；只有当前 intent 选择 skill/web 等能力时才注入对应 owned tools，chat-route ceiling / tool-choice hint 同样生效。

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
- 与 `/v1/responses` 共享 profile snapshot、exact catalog planner、proof 校验和 forwarded classification；只有当前 intent 选择 skill/web 等能力时才注入对应 owned tools，chat-route ceiling / tool-choice hint 同样生效。

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

## 10. Aevatar Service 外部暴露记录

Aevatar service catalog 的 `externalExposure` 是 service definition 拥有的 typed fact，用来记录该 service 已经作为外部 NyxID downstream service 暴露时的稳定信息：

- `nyxidSlug`：NyxID 侧用于 `/api/v1/proxy/s/{slug}/...` 的 slug。
- `registeredAt`：该外部暴露记录写入 Aevatar service definition 的时间。

这个事实不属于 service binding。Binding 只表达本 service 依赖的下游资源；`externalExposure` 表达本 service 自身对外暴露后的可发现信息。Aevatar 不会因为该字段自动向 NyxID 注册服务，也不会把该字段接入 dispatcher 路由。写入仍走 service definition 的窄 external exposure 更新入口，读取走 service catalog readmodel 与现有 service/scope service API 响应。

因此 API key 至少需要 `proxy` scope，或更宽的 `proxy:*` scope；`llm:proxy` 不能替代这项
REST proxy capability。生产上可以用 `--allowed-services` 收紧到 Aevatar 与目标 LLM 服务；
调试时可以先用 `--allow-all-services` 验证链路。

`--allowed-services` 必须填 `nyxid service list --output json` 里的 UserService id，不是 catalog id。填错时常见错误是 `api_key_scope_forbidden_legacy`。

## 11. 代码锚点

主机端入口：

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesEndpoints.cs`
- `src/Aevatar.Mainnet.Host.Api/Messages/MessagesEndpoints.cs`
- `src/Aevatar.Mainnet.Host.Api/ChatCompletions/ChatCompletionsEndpoints.cs`

模型目录与路由：

- `src/Aevatar.Mainnet.Host.Api/ModelCatalog/LLMModelCatalogEndpoints.cs`
- `src/Aevatar.Studio.Application/Studio/Services/LLMModelRouteApplicationService.cs`
- `src/Aevatar.Studio.Projection/QueryPorts/ProjectionLLMModelCatalogPolicyQueryPort.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesRouteResolver.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesApiModels.cs`

调用者身份与工具：

- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesCallerScope.cs`
- `src/Aevatar.Mainnet.Host.Api/Responses/ResponsesAevatarToolProvider.cs`
- `src/platform/Aevatar.GAgentService.Application/Responses/ResponsesCompletionApplicationService.cs`
