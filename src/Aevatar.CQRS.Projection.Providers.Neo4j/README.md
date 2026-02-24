# Aevatar.CQRS.Projection.Providers.Neo4j

通用 Neo4j Provider（仅 Graph 能力）。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IProjectionGraphStore>` 与上层模块解耦集成（Graph）。
- 能力声明：Graph schema validation。
- 基于官方 `Neo4j.Driver` 实现连接与会话管理。
- 写入路径输出结构化日志：`provider/scope/nodeId-or-edgeId/elapsedMs/result/errorType`。

## DI 注册

使用扩展方法：

- `AddNeo4jGraphStoreRegistration(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:Graph:Providers:Neo4j:*` 配置。
- `scopeFactory`：graph scope 提供器。
- `providerName`：默认 `Neo4j`（与 `ProjectionProviderNames.Neo4j` 一致）。
