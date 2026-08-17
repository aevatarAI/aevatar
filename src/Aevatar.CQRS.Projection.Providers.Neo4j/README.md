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

## Observability

五个 public write operation 各发出一条 terminal log，并由 Provider 本地 Meter
`Aevatar.CQRS.Projection.Providers.Neo4j` 记录 duration/total。metric tag 仅允许
`provider / operation / result`；`projectionKind / stateVersion / scope / ownerId /
nodeId / edgeId / nodeCount / edgeCount` 只进入结构化日志。

`replace_owner_graph` 从 `ProjectionOwnedGraph` 读取权威 `projectionKind / stateVersion`。
直接 CRUD API 没有该上下文时记录 `null`，不得从 scope、ownerId 或 id 字面量推断。
日志和 metric listener 的失败不能改变写入结果，也不能替换原始异常。
