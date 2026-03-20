# Aevatar.Mainnet.Host.Api

`Aevatar.Mainnet.Host.Api` 是主网宿主。

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

`Aevatar.Mainnet.Host.Api` 现在是 `aevatar app` 的唯一后端 API 面。workflow authoring、user workflow publish/run、resume/signal 都应接到 mainnet，不再额外依赖独立的 workflow host。

- `POST /api/chat`
- `GET /api/ws/chat`
- `GET /api/agents`
- `GET /api/workflows`
- `GET /api/actors/{actorId}`
- `GET /api/actors/{actorId}/timeline`
- `GET /api/scopes/{scopeId}/workflows`
- `GET /api/scopes/{scopeId}/workflows/{workflowId}`
- `PUT /api/scopes/{scopeId}/workflows/{workflowId}`
- `POST /api/scopes/{scopeId}/workflows/{workflowId}/runs:stream`
- `POST /api/scopes/{scopeId}/workflow-runs:stream`（兼容旧 actorId 调用）

`/api/scopes/{scopeId}/workflows/{workflowId}/runs:stream` 支持请求体字段 `eventFormat`：

- `workflow`：返回现有 workflow frame SSE。
- `agui`：返回 AGUI 原始事件 SSE，供 app 前端展示 workflow execution 过程。
