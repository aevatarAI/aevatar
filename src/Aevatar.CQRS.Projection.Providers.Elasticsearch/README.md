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
- 默认查询排序由 `DefaultSortField` 控制，未配置时回退到 `CreatedAt desc, ProjectionDocumentId desc`

## 分页排序约束

- `QueryAsync` 的 `search_after` 分页必须带稳定且唯一的 tie-breaker；这里统一使用 provider 保留字段 `ProjectionDocumentId`，它会在写入时复制 document key，并在自动建索引时固定映射为 `keyword`
- 不要把默认 tie-breaker 改回 `_id`：Elastic 官方文档明确说明 `_id` 不能用于 sorting，若确实要按 id 排序，应该复制到另一个启用 `doc_values` 的字段
- 也不要把这里改成 `_doc`：`_doc`/扫描顺序适合底层迭代，不是当前 read-model 查询的稳定业务分页键；当前查询路径需要一个显式、唯一、可复用 cursor 的排序字段
- `ProjectionDocumentId` 是当前 provider 的硬约束，不提供 `_id`/`_doc` fallback，也不为旧索引或旧文档做兼容兜底
- 如果索引由外部预建，必须保留 `ProjectionDocumentId` 的 `keyword` 映射，否则视为配置错误并应直接修正索引定义

参考：

- [_id field](https://www.elastic.co/docs/reference/elasticsearch/mapping-reference/mapping-id-field)
- [Paginate search results](https://www.elastic.co/docs/reference/elasticsearch/rest-apis/paginate-search-results)
