# Aevatar.CQRS.Projection.Providers.Elasticsearch

Elasticsearch Document Provider。

## 能力

- `ElasticsearchProjectionDocumentStore<TReadModel, TKey>`
- OCC 更新：`seq_no/primary_term`
- 基于 `DocumentIndexMetadata` 的索引初始化（`Mappings/Settings/Aliases`）

## DI

- `AddElasticsearchDocumentProjectionStore<TReadModel, TKey>(...)`

## 配置

- `Projection:Document:Providers:Elasticsearch:*`
- 至少配置 `Endpoints`
