# Aevatar.CQRS.Projection.Providers.Neo4j

通用 Neo4j Provider（支持 Document/Graph 两类能力）。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>` 与上层模块解耦集成（Document）。
- 通过 `IProjectionStoreRegistration<IProjectionGraphStore>` 与上层模块解耦集成（Graph）。
- 能力声明：`Document/Graph` 索引、schema validation。
- 基于官方 `Neo4j.Driver` 实现连接与会话管理。
- 写入路径输出结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。

## DI 注册

使用扩展方法：

- `AddNeo4jDocumentStoreRegistration<TReadModel, TKey>(...)`
- `AddNeo4jGraphStoreRegistration(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:Document:Providers:Neo4j:*` 或 `Projection:Graph:Providers:Neo4j:*` 配置。
- `scopeFactory`：文档 scope 或 graph scope 提供器。
- `keySelector/keyFormatter`：ReadModel 主键映射。
- `providerName`：默认 `Neo4j`（与 `ProjectionProviderNames.Neo4j` 一致）。
