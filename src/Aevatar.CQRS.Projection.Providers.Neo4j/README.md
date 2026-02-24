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
