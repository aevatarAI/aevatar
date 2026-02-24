# Aevatar.CQRS.Projection.Providers.Neo4j

通用 Neo4j Graph ReadModel Provider。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>` 与上层模块解耦集成。
- 能力声明：`Graph` 索引、schema validation。
- 基于官方 `Neo4j.Driver` 实现连接与会话管理。
- 写入路径输出结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。

## DI 注册

使用扩展方法：

- `AddNeo4jReadModelStoreRegistration<TReadModel, TKey>(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:ReadModel:Providers:Neo4j:*` 配置。
- `scope`：图存储作用域（等价于 document provider 的 indexScope）。
- `keySelector/keyFormatter`：ReadModel 主键映射。
- `providerName`：默认 `Neo4j`（与 `ProjectionReadModelProviderNames.Neo4j` 一致）。
