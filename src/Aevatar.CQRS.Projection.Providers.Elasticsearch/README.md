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
- mapping 契约变更不兼容旧 Elasticsearch index 时，index 初始化仍按当前契约创建**新** index；provider 不在读路径在线修复、重建或 mutate 旧 index
- `AutoCreateIndex=true` 只会在缺失 index 时按当前契约创建新 index；如果需要保留数据，应通过 projection 重放或外部重建流程恢复数据

## 精确匹配字段路径解析

- 精确匹配（`term` / `terms`）过滤的 `keyword` / `text` 字段路径解析基于目标 index 的**实时** `_mapping`（`GET <index>/_mapping`），而非代码侧 augmented metadata
- 原因：augmented metadata 是代码意图；2026-05-18 之前创建的 index 上 `*_id` 等字符串字段可能仍是 dynamic `text` + `.keyword` multi-field。二者背离会让 `term` 查询命中 analyzed `text` 字段并对 identifier 形态的值返回 0 命中（见 issue #743、ADR-0025）
- 读取 `_mapping` 只读取 index schema，不做 mapping mutation / reindex / 文档回填 / event replay；成功的探测结果按 index 缓存
- `_mapping` 探测失败（index 缺失、ES 不可达、超时、响应不可解析）时回退到 declared / augmented metadata，即 #743 之前的解析行为
- 决策记录见 `docs/adr/0025-elasticsearch-exact-match-resolution-reads-index-truth.md`

参考：

- [_id field](https://www.elastic.co/docs/reference/elasticsearch/mapping-reference/mapping-id-field)
- [Paginate search results](https://www.elastic.co/docs/reference/elasticsearch/rest-apis/paginate-search-results)
