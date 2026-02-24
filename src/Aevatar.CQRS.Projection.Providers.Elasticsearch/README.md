# Aevatar.CQRS.Projection.Providers.Elasticsearch

通用 Elasticsearch Document ReadModel Provider。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IProjectionReadModelStore<TReadModel, TKey>>` 与上层模块解耦集成。
- 能力声明：`Document` 索引（不声明 alias/schema validation 能力）。
- 写入路径输出结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。
- `MutateAsync` 基于 `seq_no/primary_term` 执行 OCC（冲突可重试，超限失败）。
- `AutoCreateIndex=false` 时可通过 `MissingIndexBehavior` 控制索引缺失行为（默认抛错）。
- `ListSortField` 为空时默认按 `CreatedAt desc -> _id desc` 排序，优先按创建时间倒序并保证稳定性。

## DI 注册

使用扩展方法：

- `AddElasticsearchReadModelStoreRegistration<TReadModel, TKey>(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:ReadModel:Providers:Elasticsearch:*` 配置。
- `indexScope`：按业务语义隔离索引（会与 `IndexPrefix` 组合）。
- `keySelector/keyFormatter`：ReadModel 主键映射。
- `providerName`：默认 `Elasticsearch`（与 `ProjectionReadModelProviderNames.Elasticsearch` 一致）。
