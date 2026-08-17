# Aevatar.CQRS.Projection.Providers.Neo4j

Neo4j Graph Provider。

## 能力

- `Neo4jProjectionGraphStore`
- 邻居查询 / 子图查询
- owner 维度节点与边查询（用于精确清理）

## DI

- `AddNeo4jGraphProjectionStore(...)`

## 配置

- `Projection:Graph:Providers:Neo4j:*`
- 至少配置 `Uri`
- `AutoCreateSchema` 默认为 `true`。首次访问会创建 `(scope, nodeId)` 唯一约束、
  `(scope, projectionOwnerId)` 节点与关系 RANGE 索引，并等待两个 owner 索引 ONLINE
  后再执行读写。
- `AutoCreateSchema=false` 时 provider 不创建或等待任何 schema；部署方必须在接收流量前
  手动预建上述唯一约束和两个 owner RANGE 索引，并确认索引为 ONLINE。

owner graph replacement 在删除旧关系后通过单行聚合屏障继续执行，确保节点、关系写入和
陈旧节点清理的执行次数不随旧关系数量增长。owner 索引使用以下稳定名称：

- `projection_graph_node_scope_owner_id_<node-label>`
- `projection_graph_relationship_scope_owner_id_<relationship-type>`

## Observability

五个 public write operation 各发出一条 terminal log，并由 Provider 本地 Meter
`Aevatar.CQRS.Projection.Providers.Neo4j` 记录 duration/total。metric tag 仅允许
`provider / operation / result`；`projectionKind / stateVersion / scope / ownerId /
nodeId / edgeId / nodeCount / edgeCount` 只进入结构化日志。

`replace_owner_graph` 从 `ProjectionOwnedGraph` 读取权威 `projectionKind / stateVersion`。
直接 CRUD API 没有该上下文时记录 `null`，不得从 scope、ownerId 或 id 字面量推断。
日志和 metric listener 的失败不能改变写入结果，也不能替换原始异常。
