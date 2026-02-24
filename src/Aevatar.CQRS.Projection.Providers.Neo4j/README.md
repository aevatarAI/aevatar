# Aevatar.CQRS.Projection.Providers.Neo4j

通用 Neo4j Provider（仅 Graph 能力）。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IProjectionGraphStore>` 与上层模块解耦集成（Graph）。
- 基于官方 `Neo4j.Driver` 实现连接与会话管理。
- 支持 schema 约束初始化、邻居查询、子图遍历。

## DI 注册

- `AddNeo4jGraphStoreRegistration(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:Graph:Providers:Neo4j:*` 配置。
- `scopeFactory`：graph scope 提供器。
