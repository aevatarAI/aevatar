# Aevatar.Mainnet.Host.Api Distributed Architecture (Orleans + TM + Kafka + Garnet)

## 目标

将 `src/Aevatar.Mainnet.Host.Api` 从单机默认运行模式扩展为可配置的分布式运行模式：

- Actor Runtime: `Orleans`
- Stream Backend: `MassTransitAdapter`
- Transport: `Kafka`
- Grain Persistence: `Garnet` (configurable)

默认仍保留 InMemory 模式；仅当 `ActorRuntime:Provider=Orleans` 时启用分布式 Silo。

## 入口与装配

- 程序入口：`src/Aevatar.Mainnet.Host.Api/Program.cs`
- 分布式入口扩展：`src/Aevatar.Mainnet.Host.Api/Hosting/MainnetDistributedHostBuilderExtensions.cs`
- 分布式配置模板：`src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json`

`Program.cs` 装配顺序：

1. `AddAevatarDefaultHost(...)`（Bootstrap + Runtime Provider 选择）
2. `AddMainnetDistributedOrleansHost()`（按配置启用 Orleans Silo）
3. `AddWorkflowCapabilityWithAIDefaults()`
4. `AddWorkflowMakerExtensions()`

## 关键配置

### ActorRuntime

- `ActorRuntime:Provider=Orleans`
- `ActorRuntime:OrleansStreamBackend=MassTransitAdapter`
- `ActorRuntime:OrleansPersistenceBackend` (`InMemory` / `Garnet`)
- `ActorRuntime:OrleansGarnetConnectionString`（当持久化后端为 `Garnet` 时必填）
- `ActorRuntime:MassTransitTransportBackend=Kafka`
- `ActorRuntime:MassTransitKafkaBootstrapServers`
- `ActorRuntime:MassTransitKafkaTopicName`
- `ActorRuntime:MassTransitKafkaConsumerGroup`

### Orleans

- `Orleans:ClusteringMode` (`Localhost` / `Development`)
- `Orleans:ClusterId`
- `Orleans:ServiceId`
- `Orleans:SiloHost`
- `Orleans:PrimarySiloEndpoint`
- `Orleans:SiloPort`
- `Orleans:GatewayPort`
- `Orleans:QueueCount`
- `Orleans:QueueCacheSize`

## 运行拓扑

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    A["Client / API Caller"] --> B["Aevatar.Mainnet.Host.Api"]
    B --> C["IActorRuntime (Orleans)"]
    C --> D["RuntimeActorGrain / Domain Actors"]
    D --> E["Orleans Stream Provider"]
    E --> F["MassTransit Queue Adapter"]
    F --> G["Kafka Topic"]
    G --> F
    F --> E
    E --> D
```

## 语义说明

- 写路径保持 `Command -> Event`，事件通过 Orleans Stream + Kafka 扩散。
- Stream Forward/Topology 的权威状态仍在 Orleans Grain（`IStreamTopologyGrain`），非中间层进程内事实态。
- 该版本不改业务层编排逻辑，仅替换 runtime 与传输实现。
- Orleans grain state 与 Stream `PubSubStore` 持久化可按配置切换到 Garnet。
- `Localhost` 模式使用 `UseLocalhostClustering`，适合本机多进程开发。
- `Development` 模式使用 `UseDevelopmentClustering + ConfigureEndpoints`，可通过主节点实现多机测试集群。
- 生产跨主机集群建议替换为持久化 Membership Provider。

## 启动示例

```bash
docker compose up -d kafka garnet
ASPNETCORE_ENVIRONMENT=Distributed dotnet run --project src/Aevatar.Mainnet.Host.Api
```

## 多机测试集群（Docker）

仓库提供 `docker-compose.mainnet-cluster.yml` + `tools/cluster/*.sh`（Kafka + Garnet + 3 个 Mainnet 节点）：

```bash
bash tools/cluster/start-mainnet-cluster.sh
```

节点入口：

- `http://localhost:19081`
- `http://localhost:19082`
- `http://localhost:19083`

停止：

```bash
bash tools/cluster/stop-mainnet-cluster.sh
```

## 验证

```bash
dotnet build src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
bash tools/ci/orleans_garnet_persistence_smoke.sh
bash tools/ci/distributed_3node_smoke.sh
```

`tools/ci/distributed_3node_smoke.sh` 除了节点健康检查外，还会执行跨 3 节点的 `/api/workflows` 与 `/api/agents` 一致化集成测试。
