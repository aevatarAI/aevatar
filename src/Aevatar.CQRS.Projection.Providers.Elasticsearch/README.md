# Aevatar.CQRS.Projection.Providers.Elasticsearch

Elasticsearch Document Provider。

## 能力

- `ElasticsearchProjectionDocumentStore<TReadModel, TKey>`
- `GetAsync(key)` 精确读取
- `QueryAsync(query)` 结构化 document 查询
- 基于 `DocumentIndexMetadata` 的索引初始化（`Mappings/Settings/Aliases`）

## DI

- `AddElasticsearchDocumentProjectionStore<TReadModel, TKey>(...)`

## 配置

- `Projection:Document:Providers:Elasticsearch:*`
- 至少配置 `Endpoints`
- 默认查询排序由 `DefaultSortField` 控制，未配置时回退到 `CreatedAt desc`，并追加 `Id.keyword desc` / `id.keyword desc` 作为稳定 tie-break
