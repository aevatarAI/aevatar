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
- 如果索引由外部预建，必须匹配当前 provider mapping 契约，包括 `ProjectionDocumentId` 的 `keyword` 映射以及 descriptor 派生出的稳定 `keyword` / `date` 字段；不匹配视为配置错误并应直接修正或重建索引

## 自动索引映射

- 新建索引时，provider 会基于 read model 的 protobuf descriptor 补齐低风险稳定字段映射：root-level `google.protobuf.Timestamp` 映射为 `date`，root-level 稳定字符串标识字段（如 `id`、`actor_id`、`last_event_id`、`*_id`、`*_key`、`*_hash`、`*_status`、`*_kind`、`*_type`、`*_type_url`）映射为 `keyword`
- `DocumentIndexMetadata` 中显式声明的 mapping 优先，provider 不覆盖自定义 `text`、analyzer、object、nested 或其他业务 mapping
- `google.protobuf.Any`、`google.protobuf.Struct`、map、repeated message 与 repeated scalar 字段默认保持开放，不由通用 helper 递归展开
- schema-drift 权威源只有 alias + augmented mapping fingerprint：alias 必须指向 `{alias}-v{fingerprint}` 物理索引
- alias 指向单一旧 fingerprint physical index 时，provider lifecycle 会创建 expected physical index、从旧 physical `_reindex`、确认没有 failures / timeout 后，用一次 `_aliases` 原子 remove old / add new
- alias 多 backing、source 缺失、不兼容 mapping、reindex failure / timeout 或 partial copy 时继续 fail closed，不会切 alias，也不会继续读写 read model document
- `GetAsync`、`QueryAsync` 与 consistency probe 只做 alias fingerprint 诊断；它们不会读取 live ES mapping 作为第二真相，也不会在 query-time 做 mapping repair、双读 fallback 或 reindex
- `AutoCreateIndex=true` 会在缺失 index、legacy bare index 包装、写侧 first-touch 或 provider-local startup initializer 中复用同一个 lifecycle ensure；read-first 静态 alias 的迁移由 startup initializer 触发，动态 index scope 仍由写侧 first-touch 触发

参考：

- [_id field](https://www.elastic.co/docs/reference/elasticsearch/mapping-reference/mapping-id-field)
- [Paginate search results](https://www.elastic.co/docs/reference/elasticsearch/rest-apis/paginate-search-results)
