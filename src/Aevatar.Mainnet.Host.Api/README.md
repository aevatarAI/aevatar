# Aevatar.Mainnet.Host.Api

`Aevatar.Mainnet.Host.Api` 是主网宿主。

## 默认能力装配

- `builder.AddAevatarDefaultHost(...)`
- `builder.AddMainnetDistributedOrleansHost()`（当 `ActorRuntime:Provider=Orleans` 时启用 Orleans Silo）
- `builder.AddWorkflowCapabilityWithAIDefaults()`
- `app.UseAevatarDefaultHost()`（自动挂载能力端点）

## 分布式模式（Orleans + TM + Kafka）

1. 启动 Kafka（仓库根目录）：

```bash
docker compose up -d kafka
```

2. 以 Distributed 环境启动：

```bash
# MassTransit 9 需要 License（可通过 MT_LICENSE 或 MT_LICENSE_PATH 提供）
# export MT_LICENSE="<your-base64-license>"
ASPNETCORE_ENVIRONMENT=Distributed dotnet run --project src/Aevatar.Mainnet.Host.Api
```

3. `src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json` 默认启用：

- `ActorRuntime:Provider=Orleans`
- `ActorRuntime:OrleansStreamBackend=MassTransitAdapter`
- `ActorRuntime:MassTransitTransportBackend=Kafka`
- `Orleans:ClusteringMode=Localhost`

`Orleans:ClusteringMode` 支持：

- `Localhost`：本机多进程开发模式（默认）。
- `Development`：多机测试模式（主节点 + 从节点），通过 `Orleans:PrimarySiloEndpoint` 加入集群。

可通过 `AEVATAR_` 前缀环境变量覆盖，例如：

```bash
export AEVATAR_ActorRuntime__MassTransitKafkaBootstrapServers=localhost:9092
export AEVATAR_Orleans__SiloPort=11111
export AEVATAR_Orleans__GatewayPort=30000
```

## 多机集群测试（Docker）

仓库提供了 `docker-compose.mainnet-cluster.yml`（3 节点 Mainnet + Kafka）。

```bash
export MT_LICENSE="<your-base64-license>"
bash tools/cluster/start-mainnet-cluster.sh
```

停止集群：

```bash
bash tools/cluster/stop-mainnet-cluster.sh
```

## 端点

- `POST /api/chat`
- `GET /api/ws/chat`
- `GET /api/agents`
- `GET /api/workflows`
- `GET /api/actors/{actorId}`
- `GET /api/actors/{actorId}/timeline`
