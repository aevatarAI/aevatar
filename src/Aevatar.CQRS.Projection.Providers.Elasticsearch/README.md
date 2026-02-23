# Aevatar.CQRS.Projection.Providers.Elasticsearch

通用 Elasticsearch Document ReadModel Provider。

- 不依赖任何业务域 read model。
- 通过 `IProjectionReadModelStoreRegistration<TReadModel, TKey>` 与上层模块解耦集成。
- 能力声明：`Document` 索引、alias、schema validation。
- 写入路径输出结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。

## DI 注册

使用扩展方法：

- `AddElasticsearchReadModelStoreRegistration<TReadModel, TKey>(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:ReadModel:Providers:Elasticsearch:*` 配置。
- `indexScope`：按业务语义隔离索引（会与 `IndexPrefix` 组合）。
- `keySelector/keyFormatter`：ReadModel 主键映射。
- `providerName`：默认 `Elasticsearch`（与 `ProjectionReadModelProviderNames.Elasticsearch` 一致）。
