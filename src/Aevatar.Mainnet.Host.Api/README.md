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
- `Orleans:ClusteringMode=Localhost`

在上述配置下，Event Sourcing 的 `IEventStore` 会自动使用 `GarnetEventStore`（连接串复用 `ActorRuntime:OrleansGarnetConnectionString`）。
`Projection:Graph:Providers:Neo4j:Password` 不再在仓库内提供默认明文值，需通过环境变量注入。

`Orleans:ClusteringMode` 支持：

- `Localhost`：本机多进程开发模式（默认）。
- `Development`：多机测试模式（主节点 + 从节点），通过 `Orleans:PrimarySiloEndpoint` 加入集群。

可通过 `AEVATAR_` 前缀环境变量覆盖，例如：

```bash
export AEVATAR_ActorRuntime__KafkaBootstrapServers=localhost:9092
export AEVATAR_ActorRuntime__OrleansPersistenceBackend=Garnet
export AEVATAR_ActorRuntime__OrleansGarnetConnectionString=localhost:6379
export AEVATAR_Orleans__SiloPort=11111
export AEVATAR_Orleans__GatewayPort=30000
```

## NyxID spec catalog token

`nyxid_search_capabilities` 与 `nyxid_proxy_execute` 依赖
`NyxIdSpecCatalog` 从 NyxID 拉取 OpenAPI spec。NyxID 的
`/api/v1/docs/openapi.json` 要求真实用户 token；未配置 token 时 catalog
会保持为空，specialized NyxID tools 仍可用，但 generic capability discovery
不可用。

生产部署必须通过 Secret / 环境变量注入：

```bash
export AEVATAR_Aevatar__NyxId__SpecFetchToken="<real-user-nyxid-api-key>"
```

如果部署平台直接使用 .NET 裸环境变量，也可以注入等价的
`Aevatar__NyxId__SpecFetchToken`。Mainnet host 会把它绑定到
`Aevatar:NyxId:SpecFetchToken`。

缺少 token、token 被 NyxID 拒绝或 spec 成功返回但 catalog 为空时，
`/health/ready` 会返回 not-ready，并在 `components` 中出现 `nyxid-catalog`：

```json
{
  "name": "nyxid-catalog",
  "status": "unhealthy",
  "message": "NyxID spec fetch token is missing; generic capability discovery is unavailable."
}
```

若 token 已配置但启动时 NyxID / 网络短暂不可用，readiness 会保持 ready，
并在同一组件的 `lastRefreshError` / `lastRefreshFailureKind` 里暴露临时失败；
后台 refresh 成功后会更新 operation count。

部署后冒烟：

```bash
curl -fsS http://127.0.0.1:5080/health/ready | jq '.components[] | select(.name == "nyxid-catalog")'
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
GAgentService__Demo__Enabled=false \
Projection__Document__Providers__Elasticsearch__Enabled=false \
Projection__Document__Providers__InMemory__Enabled=true \
Projection__Graph__Providers__Neo4j__Enabled=false \
Projection__Graph__Providers__InMemory__Enabled=true \
Projection__Policies__Environment=Development \
Projection__Policies__DenyInMemoryDocumentReadStore=false \
Projection__Policies__DenyInMemoryGraphFactStore=false \
ActorRuntime__OrleansStreamBackend=InMemory \
ActorRuntime__OrleansPersistenceBackend=InMemory \
dotnet run --project src/Aevatar.Mainnet.Host.Api --no-build
```

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

仓库提供的集群启动脚本会拉起 3 节点 Mainnet + Kafka + Garnet + Elasticsearch + Neo4j。

```bash
export NEO4J_PASSWORD="<set-a-password>"
bash tools/cluster/start-mainnet-cluster.sh
```

停止集群：

```bash
bash tools/cluster/stop-mainnet-cluster.sh
```

Orleans + Garnet 持久化集成测试：

```bash
bash tools/ci/orleans_garnet_persistence_smoke.sh
```

三节点集群一致化测试（包含节点健康检查 + 跨节点 `/api/workflows`、`/api/agents` 一致性断言）：

```bash
bash tools/ci/distributed_3node_smoke.sh
```

三节点 Orleans scripting 集群测试（Kafka + Garnet + Elasticsearch + Neo4j）：

```bash
bash tools/ci/orleans_3node_real_env_smoke.sh
```

## 端点

`Aevatar.Mainnet.Host.Api` 现在是 `aevatar app` 的唯一后端 API 面。当前用户面 contract 已经收敛为 `scope-first`，默认认为一个 `scope` 对应一个对外 service binding；内核仍保留 `service` 级别接口，作为未来扩展到多 service 的基础。

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
