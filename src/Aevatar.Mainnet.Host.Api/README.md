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

2. 以 Distributed 环境启动：

```bash
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
- `Projection:Graph:Providers:Neo4j:Enabled=false`
- `Projection:Graph:Providers:InMemory:Enabled=false`

在上述配置下，Event Sourcing 的 `IEventStore` 会自动使用 `GarnetEventStore`（连接串复用 `ActorRuntime:OrleansGarnetConnectionString`）。
图投影关闭时，workflow 与 scripting 的图写入按 no-op 成功完成，图查询返回空结果或 unavailable；document read model、workflow 执行与 scripting 执行不依赖 Neo4j。

如需重新启用 Neo4j，将配置改为：

```json
"Projection": {
  "Graph": {
    "Providers": {
      "Neo4j": {
        "Enabled": true,
        "Uri": "bolt://localhost:7687",
        "Username": "neo4j"
      },
      "InMemory": {
        "Enabled": false
      }
    }
  }
}
```

同时通过 `AEVATAR_Projection__Graph__Providers__Neo4j__Password` 注入密码。环境变量优先于 `appsettings.Distributed.json`；部署侧需要移除或设为 `false` 的开关包括 `AEVATAR_Projection__Graph__Providers__Neo4j__Enabled` 和兼容的 bare key `Projection__Graph__Providers__Neo4j__Enabled`。bare key 的优先级更高，不能遗留为 `true`。

Neo4j 现在是显式 opt-in：仅配置 `Uri`、用户名或密码不会启用它，必须同时设置 `Enabled=true`。关闭期间不会保存图事实；重新启用后，已完成且不再产生事件的历史 workflow 不会自动回补。当前仓库没有自动全量 graph backfill，若需要恢复历史图，必须先执行受控的全量 reprojection，再开放图查询流量。

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
- 它不是完整的 distributed / production profile；若需要 durable document projection，应使用 `Distributed` 环境并启动 Kafka、Garnet、Elasticsearch。只有显式启用 durable graph projection 时才需要 Neo4j。

## 多机集群测试（Docker）

分布式部署请直接按宿主配置拉起 Mainnet 与必需依赖服务（Kafka、Garnet、Elasticsearch）。Neo4j 仅在显式启用 graph projection 时需要；仓库不再内置集群脚本。

## 端点

`Aevatar.Mainnet.Host.Api` 现在是 `aevatar app` 的唯一后端 API 面。当前用户面 contract 已经收敛为 `scope-first`，默认认为一个 `scope` 对应一个对外 service binding；内核仍保留 `service` 级别接口，作为未来扩展到多 service 的基础。

`/ai` 是与 `/admin` 平行、由 Mainnet Host 自己拥有的独立 AI 应用。Host 将 `AI/ai.html`
作为 embedded resource 编译并注入 OIDC 配置，只在 `GET|HEAD /ai` 返回。页面使用
`/ai#/overview`、`/ai#/agents`、`/ai#/models`、`/ai#/activity` hash routes，不依赖或构建
`apps/aevatar-console-web`，也不接管 `/login`、`/auth/callback`、`/chat`、`/settings` 或
`/scopes/**`。`/admin` 的 route、asset 和原有功能保持不变。

未登录时 `ai.html` 渲染 Aevatar AI 自己的登录画面，并通过共享 OIDC/PKCE 基础设施和
`/auto/callback` 完成 NyxID 登录。所有 `/api/ai/*` 都要求认证，只从验证过的 principal
解析唯一授权分区；浏览器不能通过 route、query 或 body 自报该 identity，页面也不展示或拼接它。
当前 facade 提供：

- `GET /api/ai/context`
- `GET /api/ai/overview`
- `GET /api/ai/agents`
- `POST /api/ai/agents`
- `GET /api/ai/agents/editor-options`
- `GET /api/ai/agents/{profileSlug}`
- `PUT /api/ai/agents/{profileSlug}/draft`
- `POST /api/ai/agents/{profileSlug}:validate`
- `POST /api/ai/agents/{profileSlug}:publish`
- `GET|PUT|DELETE /api/ai/agents/default/{agentKind}`
- `GET /api/ai/models`
- `GET|PUT /api/ai/models/personal-default`
- `GET|PUT|DELETE /api/ai/models/catalog`
- `GET /api/ai/models/catalog/candidates`
- `GET /api/ai/models/catalog/candidates/{userServiceId}/models`
- `GET /api/ai/activity`
- `GET /api/ai/activity/conversations`
- `GET /api/ai/activity/runs`
- `GET /api/ai/activity/runs/{runId}`

这些 query 和 command facade 复用 Agent Profile、UserConfig、LLM model catalog、Conversation 和
Workflow Observatory 的既有 Application authority；各来源独立暴露可用性、版本与刷新时间，不会在
Host 或浏览器内伪造统一版本、统一排序或第二份事实状态。Overview、Agents、Models 和 Activity 已接入
真实 API；Chat、Channels 与 Capabilities 在形成独立契约和可用页面前不进入导航。完整边界见
[`docs/canon/ai-workspace.md`](../../docs/canon/ai-workspace.md)。

Backend Admin 的 Studio 从 `/admin#/studio` 进入。Admin shell 通过 Mainnet-owned
`/admin/studio` 页面路由装载 canonical Studio 静态资源，并在嵌入时移除 Studio 自带顶栏；
`/workflow/studio` 仅保留为独立 Studio surface，不再是 Admin 导航的目标。

Backend Admin 的模型目录从 `/admin#/models` 进入。当前 scope 与平台默认分别使用以下
API：

- `GET|PUT|DELETE /api/scopes/{scopeId}/llm-model-catalog`
- `GET /api/scopes/{scopeId}/llm-model-catalog/candidates`
- `GET /api/scopes/{scopeId}/llm-model-catalog/candidates/{userServiceId}/models`
- `GET|PUT /api/admin/llm-model-catalog`
- `GET /api/admin/llm-model-catalog/candidates`
- `GET /api/admin/llm-model-catalog/candidates/{catalogServiceId}/models`

`LLMModelCatalogPolicyGAgent` 是每个 platform/scope policy 的唯一写侧权威；提交命令只返回
`202 Accepted`，客户端必须等待 current-state projection 的后续 GET 同时返回更高
`stateVersion` 和等于本次 `mutationId` 的 `lastMutationId` 才能确认物化。版本前进但 mutation
不同表示本次修改已被并发更新取代，客户端必须加载最新配置并提示重试。平台策略始终是 `custom_replace`，并用精确
`catalogServiceId` 保存可移植的默认来源；scope 可以 `inherit_platform`，也可以用精确
`userServiceId` 保存完整 `custom_replace`。显式空的 `custom_replace` 是有效空目录，不会
回退平台默认。每个来源必须保存非空 `explicit_models` 列表，不存在 wildcard/all-models
模式。`serviceSlugSnapshot` 必须是 policy 内唯一的 NyxID canonical slug；它只充当公开模型
命名空间，不替代 exact service identity 或授权判断。scope `custom_replace` 对平台默认做完整
覆盖，不执行来源合并；`DELETE /api/scopes/{scopeId}/llm-model-catalog` 会提交
`inherit_platform`，使该 scope 重新继承管理员配置，而不是复制一份平台来源。

两个 `.../candidates/{identity}/models` API 只在配置时按需访问上游 `/models`。scope API 会
重新读取 NyxID 权威 inventory，以 exact `userServiceId` 确认当前可调用服务及其 canonical
slug，再调用
`/api/v1/proxy/s/{serviceSlug}/models?_nyxid_via={userServiceId}`；platform API 会以 exact
`catalogServiceId` 确认当前可选 catalog service，再调用
`/api/v1/proxy/{catalogServiceId}/models`。前端不提供可信 URL 或 slug。响应返回
`sourceIdentity`、`serviceSlug`、排序去重后的 `modelIds` 与可选 `defaultModelId`；用户或管理员
选择后仍需通过 policy `PUT` 保存为 `explicit_models`，fetch 本身不修改配置。

Responses / Messages 直连接口也挂在主机上，外部推荐经 NyxID proxy 访问：

- `GET /v1/models`
- `POST /v1/responses`
- `POST /v1/responses/{responseId}/cancel`
- `POST /v1/messages`
- `POST /v1/chat/completions`

说明：

- `/v1/models` 要求 bearer，只用于 caller-scope resolver 得到 `scopeId`；discovery service 只接收 `scopeId`，不接收 bearer，也不执行 HTTP 请求。它只读取 effective policy current-state projection，运行时不读取 human-only 的 `/api/v1/keys`、`/api/v1/services` 或 `/api/v1/user-services`，也不调用上游 `/models`。Admin candidate inventory 只是配置辅助，不是运行时事实。
- scope `custom_replace`（包括显式空来源）是完整替换且永不回退；scope policy 缺失或 `inherit_platform` 时使用平台 projection，所需平台 projection 缺失或不可用时返回 `503 model_catalog_unavailable`。缺失或非法 caller authentication 返回 `401`；显式空有效策略返回 `200 OK` 与 `data: []`。不存在按来源失败、部分成功或上游认证状态分类。
- `/v1/models` 把每个显式 model 确定性映射为 `<serviceSlugSnapshot>/<upstreamModelId>` 并按完整 `id` 做 ordinal 排序；`created=0`，`owned_by` 与 `group` 等于 slug，optional rich metadata 为 null 并从 JSON 省略。
- `/v1/models` 返回的 qualified model ID 在 `POST /v1/chat/completions` 与 `POST /v1/responses` 中共用同一份 effective policy 和 typed route resolution；具备 NyxID proxy 权限且上游可用时，同一个 ID 可在两条入口使用。
- qualified invocation 必须用 slug 与 upstream model ID 对同一份 effective policy 做 exact match。平台来源生成携带 exact `catalogServiceId` 的 typed target；scope 来源生成携带 exact `userServiceId` 与 canonical slug snapshot 的 typed target。slug 不是 authoritative identity。未知 slug/model 返回 `404 model_not_found`，routing projection 不可用返回 `503 model_route_unavailable`；裸 model 名只保留为旧调用方兼容路径。
- qualified invocation 走 NyxID REST proxy plane，调用 bearer 必须具备 `proxy` 或 `proxy:*` capability；只有 `llm:proxy` 的旧 token 只能覆盖 gateway，不能调用配置后的 `<serviceSlugSnapshot>/<upstreamModelId>`。
- `/v1/responses` 是 OpenAI Responses 兼容主入口；`previous_response_id` 会通过 response session read model 校验同一调用者、同一 ingress origin 下的上一条 response。`function_call_output` 会按上一条 response 的 forwarded tool call 记录用 `call_id` 对账。
- `stream=true` 时返回 Responses 风格 SSE：`response.created`、`response.output_item.added`、`response.output_text.delta`、`response.output_text.done`、`response.output_item.done`、`response.completed`；失败时输出 `response.failed` / `error`。
- `Authorization: Bearer <token>` 只在请求上下文中透传，不会落盘；持久化的 response session 只记录 NyxID `/me` 解析出的 caller scope 与 opaque `response.id`。
- forward tool call 在输出给客户端前会先落 response session actor，记录 `call_id`、`tool_name`、`schema_hash`、arguments、状态与过期时间。客户端续传 tool result 时可携带 `schema_hash`，不匹配会返回明确 4xx。
- `/v1/responses`、`/v1/messages`、`/v1/chat/completions` 在 ownership classification 前共用 `IResponsesOwnedToolCatalogPlanner`。每个新 run 固定 published profile snapshot、intent-selected exact catalog 与 proof/digest；普通 no-match 注入 0 个 Aevatar-owned tools，不把 `workspace.default` 直接变成 model surface。客户端 declared tools 只有在名称不属于 frozen owned catalog 时才 forward；canonical `web_fetch` / `web_search` 被授权时，Responses ingress 才把客户端 `WebFetch` / `WebSearch` 别名替换为服务端工具。source collision、invalid schema、预算超限或 executor proof mismatch 均 fail closed。
- substitute 工具状态归 `ResponsesAgentToolStateGAgent` 拥有：`TodoWrite` 只通过显式 `responses.state` / coding profile 写入 agent-scoped todo state；`WebFetch` / `WebSearch` 是 canonical web intent 的 ingress alias，并记录 trace 与简单 cache 命中状态。这些状态通过 ProjectionPipeline 物化为 current-state read model。普通 skill intent 只选择 `ornn_search_skills` + `use_skill`，publish/update 只允许 `skill.authoring` profile。旧 `Task` trace 契约暂留为 dead surface，当前 Mainnet 不注册 `Task` / `task` substitute。
- cancel 端点会复用同一 bearer token scope resolution；可见性通过后，session actor 会把 response 标记为 `cancelled` 并将 pending forwarded tool call 标为 `cancelled`。已过期或已取消的 `previous_response_id` 不能 resume。
- `/v1/messages` 是 Anthropic Messages 兼容门面。它每次请求注册一个新的 `LlmSession`，不支持 `previous_response_id`，`max_tokens` 必填，共享 profile snapshot、exact catalog planner、proof 校验与 forwarded classification；`top_p`、`top_k`、`stop_sequences` 和 forced `tool_choice` 会被拒绝，image content v1 会被丢弃并记录 warning。
- `/v1/chat/completions` 是 OpenAI Chat Completions 兼容窄门面。它每次请求注册一个新的 `LlmSession`，复用 NyxID caller scope、模型 route preference、相同 exact catalog/proof 与同一条流式 LLM 主链；支持 text messages、基础 `tool_calls`、`stream`、`temperature`、`max_tokens`、`response_format`，但不提供 Responses `previous_response_id` continuation。

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
