# Aevatar.CQRS.Projection.Providers.Elasticsearch

通用 Elasticsearch Document ReadModel Provider。

- 不依赖任何业务域 read model。
- 通过 `IProjectionStoreRegistration<IDocumentProjectionStore<TReadModel, TKey>>` 与上层模块解耦集成。
- `MutateAsync` 基于 `seq_no/primary_term` 执行 OCC（冲突可重试，超限失败）。
- `AutoCreateIndex=false` 时可通过 `MissingIndexBehavior` 控制索引缺失行为（默认抛错）。
- `ListSortField` 为空时默认按 `CreatedAt desc -> _id desc` 排序。
- 索引初始化支持 `DocumentIndexMetadata`：`Mappings`、`Settings`、`Aliases`（结构化对象）。

## DI 注册

- `AddElasticsearchDocumentStoreRegistration<TReadModel, TKey>(...)`

关键参数：

- `optionsFactory`：绑定 `Projection:Document:Providers:Elasticsearch:*` 配置。
- `metadataFactory`：通常由 `IProjectionDocumentMetadataResolver` 解析 `IProjectionDocumentMetadataProvider<TReadModel>`。
- `keySelector/keyFormatter`：ReadModel 主键映射。
