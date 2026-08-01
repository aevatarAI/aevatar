# Aevatar.Mainnet.Host.Api

`Aevatar.Mainnet.Host.Api` 是主网宿主。

本地直接执行 `dotnet run --project src/Aevatar.Mainnet.Host.Api` 时，默认监听 `http://127.0.0.1:5080`。
如果显式传入 `ASPNETCORE_URLS` 或 `--urls`，宿主仍然优先使用外部配置。

## 默认能力装配

- `builder.AddAevatarDefaultHost(...)`
- `builder.AddMainnetDistributedOrleansHost()`（当 `ActorRuntime:Provider=Orleans` 时启用 Orleans Silo）
- `builder.AddAevatarPlatform(options => { options.EnableMakerExtensions = true; })`
- `app.UseAevatarDefaultHost()`（自动挂载能力端点）

## 分布式模式（Orleans + KafkaProvider）

1. 启动 Kafka 与 Garnet（仓库根目录）：

```bash
docker compose up -d kafka garnet
```

2. 注入 Neo4j 密码并以 Distributed 环境启动：

```bash
export NEO4J_PASSWORD="<set-a-password>"
export AEVATAR_Projection__Graph__Providers__Neo4j__Password="${NEO4J_PASSWORD}"
ASPNETCORE_ENVIRONMENT=Distributed dotnet run --project src/Aevatar.Mainnet.Host.Api
```

3. `src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json` 默认启用：

- `ActorRuntime:Provider=Orleans`
- `ActorRuntime:OrleansStreamBackend=KafkaProvider`
- `ActorRuntime:OrleansPersistenceBackend=Garnet`
- `ActorRuntime:KafkaReceiverBufferCapacity=1024`
- `ActorRuntime:KafkaReceiverBufferHighWatermark=768`
- `ActorRuntime:KafkaReceiverBufferLowWatermark=512`
- `Orleans:ClusteringMode=Garnet`

在上述配置下，Event Sourcing 的 `IEventStore` 会自动使用 `GarnetEventStore`（连接串复用 `ActorRuntime:OrleansGarnetConnectionString`）。
`Projection:Graph:Providers:Neo4j:Password` 不再在仓库内提供默认明文值，需通过环境变量注入。

`Orleans:ClusteringMode` 支持：

- `Garnet`：共享 membership 模式（Distributed 配置默认，生产必须）。membership 表与
  reminder 表、grain state 共用同一 Garnet 实例与 `ServiceId`，滚动发布期间新旧 silo
  组成同一集群、分摊 reminder ring，避免双触发与 grain-state etag 冲突。
  `Orleans:SiloHost` 留空时自动通告第一个非回环网卡地址（k8s 内即 Pod IP）。
- `Localhost`：本机单进程开发模式（代码默认值），membership 仅在进程内，禁止用于多副本部署。
- `Development`：多机测试模式（主节点 + 从节点），通过 `Orleans:PrimarySiloEndpoint` 加入集群；
  membership 由主节点内存持有，主节点重启即丢失，不可用于生产。

可通过 `AEVATAR_` 前缀环境变量覆盖，例如：

```bash
export AEVATAR_ActorRuntime__KafkaBootstrapServers=localhost:9092
export AEVATAR_ActorRuntime__KafkaReceiverBufferCapacity=1024
export AEVATAR_ActorRuntime__KafkaReceiverBufferHighWatermark=768
export AEVATAR_ActorRuntime__KafkaReceiverBufferLowWatermark=512
export AEVATAR_ActorRuntime__OrleansPersistenceBackend=Garnet
export AEVATAR_ActorRuntime__OrleansGarnetConnectionString=localhost:6379
export AEVATAR_Orleans__SiloPort=11111
export AEVATAR_Orleans__GatewayPort=30000
```

## 本地持久化开发模式（Orleans + Garnet）

如果只是想快速起一个本地开发后端，并且希望避免“写侧还在、读侧已丢失”的不对称状态，优先使用脚本默认的 `local` 模式。脚本会优先使用 `~/.dotnet/dotnet`，避免系统 `dotnet` 与仓库 `global.json` 的 SDK 版本不匹配：

```bash
bash src/Aevatar.Mainnet.Host.Api/boot.sh
```

该模式默认显式启用：

- `AEVATAR_ActorRuntime__Provider=InMemory`
- `Projection:Document:Providers:InMemory:Enabled=true`
- `Projection:Graph:Providers:InMemory:Enabled=true`
- `GAgentService:Demo:Enabled=false`

说明：

- 这是最一致的单机开发模式：read/write 都是本地临时态。
- 后端重启后，actor state 与 projection/read model 会一起清空，不会出现“service definition 还在，但 services/read model 已空”的错位。
- 如需本地不带 token 调试 scope / studio / playground API，必须使用 `ASPNETCORE_ENVIRONMENT=Development` 并显式设置 `Aevatar__Authentication__Enabled=false`。该关闭开关只在 `Development` 环境生效；`PersistentLocal`、`Distributed` 等非 Development 环境会强制保持认证开启。

最小无认证冒烟启动示例：

```bash
ASPNETCORE_ENVIRONMENT=Development \
Aevatar__Authentication__Enabled=false \
Audit__ActorIdentityHasher__ActiveKeyId=local-development-key \
Audit__ActorIdentityHasher__Keys__0__KeyId=local-development-key \
Audit__ActorIdentityHasher__Keys__0__Key=local-development-audit-identity-key \
ChannelIdentity__OAuthClient__Bootstrap__Enabled=false \
GAgentService__Demo__Enabled=false \
Projection__Document__Providers__Elasticsearch__Enabled=false \
Projection__Document__Providers__InMemory__Enabled=true \
Projection__Graph__Providers__Neo4j__Enabled=false \
Projection__Graph__Providers__InMemory__Enabled=true \
Projection__Policies__Environment=Development \
Projection__Policies__DenyInMemoryDocumentReadStore=false \
Projection__Policies__DenyInMemoryGraphFactStore=false \
ActorRuntime__Provider=InMemory \
ActorRuntime__SecretStoreBackend=InMemory \
dotnet run --project src/Aevatar.Mainnet.Host.Api --no-build
```

上述审计 key 只用于本机临时开发数据，不得用于共享或生产环境。日常本地启动优先使用
`bash src/Aevatar.Mainnet.Host.Api/boot.sh`，脚本会注入同一组 Development-only 默认值。

如果只是想避免本地 scope workflow / actor state 因后端重启而完全丢失，而当前机器又没有 Kafka / Elasticsearch / Neo4j，可以使用仓库内置的 `PersistentLocal` 环境：

```bash
ASPNETCORE_ENVIRONMENT=PersistentLocal dotnet run --project src/Aevatar.Mainnet.Host.Api
```

该模式默认启用：

- `ActorRuntime:Provider=Orleans`
- `ActorRuntime:OrleansStreamBackend=InMemory`
- `ActorRuntime:OrleansPersistenceBackend=Garnet`
- `Projection:Document:Providers:InMemory:Enabled=true`
- `Projection:Graph:Providers:InMemory:Enabled=true`

前提：

- 本机 `localhost:6379` 可用（Redis / Garnet 兼容连接）

说明：

- 该模式的目标是保住本地 actor 持久态与 workflow 存储回补能力，适合单机开发验证。
- 由于 document / graph projection 仍是 `InMemory`，后端重启后 read model 会清空；如果 write-side 仍保留，可能出现本地 Console 看不到团队卡、但重复绑定提示“already exists”的现象。
- 它不是完整的 distributed / production profile；若需要 durable document / graph projection，仍应使用 `Distributed` 环境并启动 Kafka、Elasticsearch、Neo4j。

## 多机集群测试（Docker）

分布式部署请直接按宿主配置拉起 Mainnet 与依赖服务（Kafka、Garnet、Elasticsearch、Neo4j）。仓库不再内置集群脚本。

## 端点

`Aevatar.Mainnet.Host.Api` 现在是 `aevatar app` 的唯一后端 API 面。当前用户面 contract 已经收敛为 `scope-first`，默认认为一个 `scope` 对应一个对外 service binding；内核仍保留 `service` 级别接口，作为未来扩展到多 service 的基础。

Responses / Messages 直连接口也挂在主机上，外部推荐经 NyxID proxy 访问：

- `GET /v1/models`
- `POST /v1/responses`
- `POST /v1/responses/{responseId}/cancel`
- `POST /v1/messages`
- `POST /v1/chat/completions`

说明：

- `/v1/models` 会聚合当前调用者在 NyxID 上可达的 LLM service，并返回 `<service-slug>/<model>` 形态的模型 id。创建请求会把 service slug 解析成 NyxID route preference，裸 model 名仍作为旧调用方兼容路径。
- `/v1/responses` 是 OpenAI Responses 兼容主入口；`previous_response_id` 会通过 response session read model 校验同一调用者、同一 ingress origin 下的上一条 response。`function_call_output` 会按上一条 response 的 forwarded tool call 记录用 `call_id` 对账。
- `stream=true` 时返回 Responses 风格 SSE：`response.created`、`response.output_item.added`、`response.output_text.delta`、`response.output_text.done`、`response.output_item.done`、`response.completed`；失败时输出 `response.failed` / `error`。
- `Authorization: Bearer <token>` 只在请求上下文中透传，不会落盘；持久化的 response session 只记录 NyxID `/me` 解析出的 caller scope 与 opaque `response.id`。
- forward tool call 在输出给客户端前会先落 response session actor，记录 `call_id`、`tool_name`、`schema_hash`、arguments、状态与过期时间。客户端续传 tool result 时可携带 `schema_hash`，不匹配会返回明确 4xx。
- `/v1/responses`、`/v1/messages`、`/v1/chat/completions` 共用同一套 `IResponsesDirectToolPlanService` + `IResponsesToolClassificationService` 抽象。三条入口都会合并全局 `IResponsesToolProvider`，并按 chat-route `ForwardToModel.ToolSetRef` 追加同一个 route tool set；Mainnet 默认补 `workspace.default`，`lark.self_notify` 也组合同一批 workspace tools。`TodoWrite`、`WebFetch`、`WebSearch` 属于 substitute 类，会替换同名客户端 declared tools；`use_skill`、`ornn_search_skills`、`ornn_publish_skill` 属于 additive 类，会在三条直连接口注入，并使用当前 caller bearer 经 NyxID proxy 访问调用者可见的 Ornn skills。客户端 declared tool 只有在名称不属于 Aevatar-owned substitute/additive discovery 时才会 forward；同名 additive collision 也会写入 `owned_tool_names` 并由运行时拒绝 forward。
- substitute 工具状态归 `ResponsesAgentToolStateGAgent` 拥有：`TodoWrite` 写入 agent-scoped todo state，`WebFetch` / `WebSearch` 记录 trace 与简单 cache 命中状态；这些状态通过 ProjectionPipeline 物化为 current-state read model，可供后续会话查询。旧 `Task` trace 契约暂留为 dead surface，当前 Mainnet 不再注册 `Task` / `task` substitute。
- cancel 端点会复用同一 bearer token scope resolution；可见性通过后，session actor 会把 response 标记为 `cancelled` 并将 pending forwarded tool call 标为 `cancelled`。已过期或已取消的 `previous_response_id` 不能 resume。
- `/v1/messages` 是 Anthropic Messages 兼容门面。它每次请求注册一个新的 `LlmSession`，不支持 `previous_response_id`，`max_tokens` 必填，共享直连 tool-source plan、工具分类与 Ornn skill bridge；`top_p`、`top_k`、`stop_sequences` 和 forced `tool_choice` 会被拒绝，image content v1 会被丢弃并记录 warning。
- `/v1/chat/completions` 是 OpenAI Chat Completions 兼容门面。它每次请求注册一个新的 `LlmSession`，复用 NyxID caller scope、模型 route preference、共享直连 tool-source plan、工具分类与同一条流式 LLM 主链；支持 text messages、基础 `tool_calls`、`stream`、`temperature`、`max_tokens`、`response_format`，但不提供 Responses `previous_response_id` continuation。

当前推荐使用的 scope-first 入口：

- `POST /api/scopes/{scopeId}/workflow/draft-run`
- `PUT /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/binding`
- `GET /api/scopes/{scopeId}/revisions`
- `GET /api/scopes/{scopeId}/revisions/{revisionId}`
- `POST /api/scopes/{scopeId}/binding/revisions/{revisionId}:activate`
- `POST /api/scopes/{scopeId}/binding/revisions/{revisionId}:retire`
- `POST /api/scopes/{scopeId}/invoke/chat:stream`
- `GET /api/scopes/{scopeId}/runs`
- `GET /api/scopes/{scopeId}/runs/{runId}`
- `GET /api/scopes/{scopeId}/runs/{runId}/audit`
- `POST /api/scopes/{scopeId}/runs/{runId}:resume`
- `POST /api/scopes/{scopeId}/runs/{runId}:signal`
- `POST /api/scopes/{scopeId}/runs/{runId}:stop`

`draft-run` 与 `binding` 使用 `workflowYamls` 作为 workflow bundle：

- `workflowYamls[0]` 是主 workflow
- `workflowYamls[1..]` 是 sub workflow
- `workflow_call` 默认在这组 YAML 内解析

scope-first 正式运行面现在补齐了两类治理能力：

- formal run 的历史 / 详情 / 审计：通过 `GET /runs`、`GET /runs/{runId}`、`GET /runs/{runId}/audit` 暴露 scope 级正式 run 查询面，继续配合 `resume|signal|stop` 做恢复与控制。
- revision / version 治理：通过 `GET /revisions`、`GET /revisions/{revisionId}`、`activate`、`retire` 暴露正式 revision catalog；read side 会返回 `CatalogStateVersion` 与 `CatalogLastEventId`，revision 项也会返回 workflow / script / static gagent 的 typed implementation 摘要。

`invoke` 请求现在允许显式携带 `revisionId`，用于绕过 default serving alias，直接命中指定 active revision。

内部与扩展面仍保留 service-level 入口：

- `POST /api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}:stream`
- `POST /api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}`
- `GET /api/scopes/{scopeId}/services/{serviceId}/revisions`
- `GET /api/scopes/{scopeId}/services/{serviceId}/revisions/{revisionId}`
- `POST /api/scopes/{scopeId}/services/{serviceId}/revisions/{revisionId}:retire`
- `GET /api/scopes/{scopeId}/services/{serviceId}/runs`
- `GET /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}`
- `GET /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}/audit`
- `POST /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}:resume`
- `POST /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}:signal`
- `POST /api/scopes/{scopeId}/services/{serviceId}/runs/{runId}:stop`
- `POST /api/scopes/{scopeId}/services/{serviceId}/bindings`
- `PUT /api/scopes/{scopeId}/services/{serviceId}/bindings/{bindingId}`
- `POST /api/scopes/{scopeId}/services/{serviceId}/bindings/{bindingId}:retire`
- `GET /api/scopes/{scopeId}/services/{serviceId}/bindings`

scope workflow 的 catalog/read 面目前仍然保留：

- `GET /api/scopes/{scopeId}/workflows`
- `GET /api/scopes/{scopeId}/workflows/{workflowId}`
- `PUT /api/scopes/{scopeId}/workflows/{workflowId}`

旧的 `/api/chat`、`/api/ws/chat`、`/api/workflows/resume|signal|stop` 不再是 `aevatar app` 的正式运行时 contract。
