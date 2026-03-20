# 2026-03-20 Studio Workflows Production Config Audit

## 范围

只覆盖 `studio + workflows` 上线所需的配置面，不包含 `actors / runs / observability / settings` 的额外产品范围判断。

## 结论

当前仓库不是“完全写死无法上线”，而是存在三类配置问题：

1. **已经支持配置，但默认值明显偏本地开发**  
   这类值上线时必须显式提供，不能继续依赖默认值。
2. **现在仍然硬编码在代码里，应该抽成 env/config**  
   这类值是正式上线前的代码改造项。
3. **可以保留为产品常量**  
   例如品牌名、窗口名、埋点来源标识，不需要 env 化。

## 已经支持 env/config 的配置项

### 前端构建时变量

前端当前已经通过 `Umi define` 注入以下变量：

- `NYXID_BASE_URL`
- `NYXID_CLIENT_ID`
- `NYXID_REDIRECT_URI`
- `NYXID_SCOPE`

参考：

- [config.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/config.ts#L140)
- [auth/config.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/auth/config.ts#L128)

### 本地开发代理目标

这些变量仅用于开发代理，不用于生产 build 后运行：

- `AEVATAR_API_TARGET`
- `AEVATAR_CONFIGURATION_API_TARGET`
- `AEVATAR_STUDIO_API_TARGET`

参考：

- [proxy.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/proxy.ts#L12)
- [proxy.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/proxy.ts#L48)

### Studio sidecar 的可配置项

以下配置当前已经能通过 ASP.NET Core 配置系统或 `~/.aevatar/config.json` 提供：

- `Cli__App__ApiBaseUrl`
- `Cli__App__ScopeId`
- `AEVATAR_SCOPE_ID`
- `Cli__App__NyxId__Enabled`
- `Cli__App__NyxId__Authority`
- `Cli__App__NyxId__ClientId`
- `Cli__App__NyxId__ClientSecret`
- `Cli__App__NyxId__Scope`
- `Cli__App__NyxId__CallbackPath`
- `Cli__App__Connectors__ChronoStorage__Enabled`
- `Cli__App__Connectors__ChronoStorage__UseNyxProxy`
- `Cli__App__Connectors__ChronoStorage__NyxProxyBaseUrl`
- `Cli__App__Connectors__ChronoStorage__NyxProxyServiceSlug`
- `Cli__App__Connectors__ChronoStorage__BaseUrl`
- `Cli__App__Connectors__ChronoStorage__Bucket`
- `Cli__App__Connectors__ChronoStorage__Prefix`
- `Cli__App__Connectors__ChronoStorage__RolesPrefix`
- `Cli__App__Connectors__ChronoStorage__MasterKey`
- `Cli__App__Connectors__ChronoStorage__StaticBearerToken`
- `Studio__Storage__RootDirectory`
- `Studio__Storage__DefaultRuntimeBaseUrl`
- `Studio__Storage__ForceLocalRuntime`
- `AEVATAR_HOME`
- `AEVATAR_SECRETS_PATH`

参考：

- [CliAppConfigStore.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/CliAppConfigStore.cs#L13)
- [AppScopeResolver.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/AppScopeResolver.cs#L79)
- [NyxIdAppAuthentication.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/NyxIdAppAuthentication.cs#L20)
- [ServiceCollectionExtensions.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs#L17)
- [ConnectorCatalogStorageOptions.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/Storage/ConnectorCatalogStorageOptions.cs#L5)
- [StudioStorageOptions.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/Storage/StudioStorageOptions.cs#L3)
- [FileAevatarSettingsStore.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/Storage/FileAevatarSettingsStore.cs#L13)

### Mainnet / distributed runtime 配置

正式 runtime 相关的 Garnet、Kafka、Orleans、Elasticsearch、Neo4j 已经支持通过配置覆盖：

- `AEVATAR_ActorRuntime__OrleansGarnetConnectionString`
- `AEVATAR_ActorRuntime__MassTransitKafkaBootstrapServers`
- `AEVATAR_ActorRuntime__MassTransitKafkaTopicName`
- `AEVATAR_ActorRuntime__MassTransitKafkaConsumerGroup`
- `AEVATAR_Orleans__ClusteringMode`
- `AEVATAR_Orleans__ClusterId`
- `AEVATAR_Orleans__ServiceId`
- `AEVATAR_Orleans__SiloHost`
- `AEVATAR_Orleans__PrimarySiloEndpoint`
- `AEVATAR_Orleans__SiloPort`
- `AEVATAR_Orleans__GatewayPort`
- `AEVATAR_Projection__Document__Providers__Elasticsearch__Endpoints__0`
- `AEVATAR_Projection__Graph__Providers__Neo4j__Uri`
- `AEVATAR_Projection__Graph__Providers__Neo4j__Username`
- `AEVATAR_Projection__Graph__Providers__Neo4j__Password`

参考：

- [appsettings.Distributed.json](/Users/potter/Desktop/sbt_project/aevatar/src/Aevatar.Mainnet.Host.Api/appsettings.Distributed.json#L1)
- [MainnetDistributedHostBuilderExtensions.cs](/Users/potter/Desktop/sbt_project/aevatar/src/Aevatar.Mainnet.Host.Api/Hosting/MainnetDistributedHostBuilderExtensions.cs#L203)

## 必须改代码再抽离的硬编码

### P0: sidecar 监听地址写死 localhost

`aevatar app` 当前直接把监听地址拼成 `http://localhost:{port}`，只改端口不能满足生产监听、容器监听或内网 LB 接入。

参考：

- [AppToolHost.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/AppToolHost.cs#L36)
- [AppCommandHandler.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/AppCommandHandler.cs#L15)

建议：

- 新增 `Cli__App__ListenUrls`，或直接优先尊重 `ASPNETCORE_URLS`
- `AppCommandHandler` 与 `ChatCommandHandler` 的本地探活 URL 也要一起调整

### P0: config tool 监听地址写死 localhost

如果生产需要配置服务，这里同样只能监听本机。

参考：

- [ConfigToolHost.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Config/ConfigToolHost.cs#L39)

建议：

- 新增 `ConfigTool__ListenUrls` 或直接使用 `ASPNETCORE_URLS`

### P0: NyxID 默认 authority / clientId 仍 baked in

前端和 sidecar 各自内置了一套默认 NyxID 配置，且两边默认值还不一致。这会导致“没配环境变量时看起来能跑，但实际接错身份租户”。

参考：

- [auth/config.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/shared/auth/config.ts#L10)
- [NyxIdAppAuthentication.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Hosting/NyxIdAppAuthentication.cs#L24)

建议：

- 删除默认 `baseUrl/clientId`
- 若缺失配置则 fail closed，并在 UI/host 启动日志中明确报错

### P0: 默认 runtimeBaseUrl 在多处重复硬编码

`http://127.0.0.1:5100` 目前散落在 sidecar 存储层、应用层和前端旧版 Studio 前端中，多点重复意味着上线时很容易只改一处。

参考：

- [StudioStorageOptions.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/Storage/StudioStorageOptions.cs#L8)
- [FileStudioWorkspaceStore.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Infrastructure/Storage/FileStudioWorkspaceStore.cs#L33)
- [WorkspaceService.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Application/Services/WorkspaceService.cs#L192)
- [SettingsService.cs](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Studio/Application/Services/SettingsService.cs#L165)
- [App.tsx](/Users/potter/Desktop/sbt_project/aevatar/tools/Aevatar.Tools.Cli/Frontend/src/App.tsx#L229)

建议：

- 统一只从 `Studio__Storage__DefaultRuntimeBaseUrl` 读取
- 删除应用层重复 fallback

### P1: console 前端 publicPath 写死 `/`

如果生产挂载在子路径，例如 `/console/`，当前 build 不支持。

参考：

- [config.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/config.ts#L16)

建议：

- 新增 `AEVATAR_CONSOLE_PUBLIC_PATH`

### P1: console 生产 API 基址没有 runtime config 面

当前 production build 后并没有可配置的 `console api base url` / `studio api base url`；现状只能依赖同域反向代理把 `/api/*` 路由切到正确后端。

参考：

- [proxy.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/proxy.ts#L3)
- [proxy.ts](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/config/proxy.ts#L62)

建议：

- 保持同域反向代理，并在部署文档中明确
- 或增加运行时配置注入，例如 `window.__AEVATAR_RUNTIME_CONFIG__`

## 可以保留为产品常量的值

以下值当前不需要 env 化：

- 品牌标题 `Aevatar Console`
- 页面文案
- `aevatar-console-web` 作为 AI 生成元数据中的 `source`
- `aevatar-console-execution-logs` 作为弹窗窗口名

参考：

- [login/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/login/index.tsx#L119)
- [studio/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/index.tsx#L1950)
- [studio/index.tsx](/Users/potter/Desktop/sbt_project/aevatar/apps/aevatar-console-web/src/pages/studio/index.tsx#L1792)

## 推荐的生产环境变量基线

### 前端 build

```bash
NYXID_BASE_URL=https://nyx.example.com
NYXID_CLIENT_ID=<public-client-id>
NYXID_REDIRECT_URI=https://console.example.com/auth/callback
NYXID_SCOPE="openid profile email"
```

### Studio sidecar

以下变量里，`ASPNETCORE_URLS` 属于建议补齐的目标配置面；按当前代码，`aevatar app` 仍会把监听地址固定为 `http://localhost:{port}`，需要先完成上面的 P0 改造。

```bash
ASPNETCORE_URLS=http://0.0.0.0:6690
Cli__App__ApiBaseUrl=https://workflow.example.com
Cli__App__ScopeId=<optional-fixed-scope-id>
Cli__App__NyxId__Enabled=true
Cli__App__NyxId__Authority=https://nyx-api.example.com
Cli__App__NyxId__ClientId=<confidential-or-public-client-id>
Cli__App__NyxId__ClientSecret=<secret-if-required>
Cli__App__NyxId__Scope="openid profile email"
Cli__App__NyxId__CallbackPath=/auth/callback
Studio__Storage__RootDirectory=/var/lib/aevatar/studio
Studio__Storage__DefaultRuntimeBaseUrl=https://workflow.example.com
Cli__App__Connectors__ChronoStorage__Enabled=true
Cli__App__Connectors__ChronoStorage__UseNyxProxy=true
Cli__App__Connectors__ChronoStorage__NyxProxyBaseUrl=https://nyx-api.example.com
Cli__App__Connectors__ChronoStorage__NyxProxyServiceSlug=chrono-storage-service
Cli__App__Connectors__ChronoStorage__Bucket=studio-catalogs
Cli__App__Connectors__ChronoStorage__Prefix=aevatar/connectors/v1
Cli__App__Connectors__ChronoStorage__RolesPrefix=aevatar/roles/v1
Cli__App__Connectors__ChronoStorage__MasterKey=<secret>
AEVATAR_HOME=/var/lib/aevatar/home
AEVATAR_SECRETS_PATH=/var/lib/aevatar/home/secrets.json
```

### Mainnet / distributed runtime

```bash
AEVATAR_ActorRuntime__Provider=Orleans
AEVATAR_ActorRuntime__OrleansPersistenceBackend=Garnet
AEVATAR_ActorRuntime__OrleansGarnetConnectionString=garnet:6379
AEVATAR_ActorRuntime__MassTransitKafkaBootstrapServers=kafka:9092
AEVATAR_ActorRuntime__MassTransitKafkaTopicName=aevatar-mainnet-agent-events
AEVATAR_ActorRuntime__MassTransitKafkaConsumerGroup=aevatar-mainnet-host-api
AEVATAR_Orleans__ClusteringMode=Development
AEVATAR_Orleans__ClusterId=aevatar-mainnet-cluster
AEVATAR_Orleans__ServiceId=aevatar-mainnet-host-api
AEVATAR_Orleans__SiloHost=<pod-ip-or-node-ip>
AEVATAR_Orleans__SiloPort=11111
AEVATAR_Orleans__GatewayPort=30000
AEVATAR_Projection__Document__Providers__Elasticsearch__Endpoints__0=http://elasticsearch:9200
AEVATAR_Projection__Graph__Providers__Neo4j__Uri=bolt://neo4j:7687
AEVATAR_Projection__Graph__Providers__Neo4j__Username=neo4j
AEVATAR_Projection__Graph__Providers__Neo4j__Password=<secret>
```

## 推荐改造顺序

1. 先改 `aevatar app` 和 `config tool` 的监听地址配置面。
2. 再删掉前端与 sidecar 的 NyxID 默认 `clientId/baseUrl`。
3. 收敛 `runtimeBaseUrl` 默认值到单一配置源。
4. 补一个 frontend runtime config 或明确固定同域反向代理方案。
5. 最后再清理文档和 example env，避免部署文档继续指向本地默认值。
