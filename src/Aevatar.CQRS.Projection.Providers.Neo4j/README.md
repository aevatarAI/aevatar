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
- `AutoCreateSchema` 默认为 `true`。首次访问会创建下表全部 10 个 schema object，并等待
  其中 4 个热路径索引（node owner、relationship owner、relationship edgeId、edge-identity
  owner）ONLINE 后再执行读写。
- `AutoCreateSchema=false` 时 provider 不创建或等待任何 schema；部署方必须在接收流量前
  手动预建下表**全部 10 个** schema object，并确认 4 个被等待的索引为 ONLINE。

schema object 清单（`<node-label>` 为配置的 `NodeLabel`，`<rel-type>` 为 `EdgeType`；
versioned (v2) label 为 `<node-label>OwnerState` / `<node-label>OwnerEvent` /
`<node-label>EdgeIdentity`；名称经 `NormalizeSchemaName` 小写化）：

| # | 对象 | Cypher | 名称 | 等待 ONLINE |
|---|---|---|---|---|
| 1 | node 唯一约束 | `CREATE CONSTRAINT … FOR (n:<node-label>) REQUIRE (n.scope, n.nodeId) IS UNIQUE` | `projection_graph_node_scope_id_<node-label>` | 约束 |
| 2 | node owner 索引 | `CREATE RANGE INDEX … FOR (n:<node-label>) ON (n.scope, n.projectionOwnerId)` | `projection_graph_node_scope_owner_id_<node-label>` | ✓ |
| 3 | relationship owner 索引 | `CREATE RANGE INDEX … FOR ()-[r:<rel-type>]-() ON (r.scope, r.projectionOwnerId)` | `projection_graph_relationship_scope_owner_id_<rel-type>` | ✓ |
| 4 | relationship edgeId 索引 | `CREATE RANGE INDEX … FOR ()-[r:<rel-type>]-() ON (r.scope, r.edgeId)` | `projection_graph_relationship_scope_edge_id_<rel-type>` | ✓ |
| 5 | owner-state 唯一约束 (v2) | `… FOR (n:<node-label>OwnerState) REQUIRE (n.physicalNamespace, n.projectionOwnerId) IS UNIQUE` | `projection_graph_v2_owner_<node-label>ownerstate` | 约束 |
| 6 | owner-event 唯一约束 (v2) | `… FOR (n:<node-label>OwnerEvent) REQUIRE (n.physicalNamespace, n.projectionOwnerId, n.eventId) IS UNIQUE` | `projection_graph_v2_event_<node-label>ownerevent` | 约束 |
| 7 | edge-identity 唯一约束 (v2) | `… FOR (n:<node-label>EdgeIdentity) REQUIRE (n.physicalNamespace, n.edgeId) IS UNIQUE` | `projection_graph_v2_edge_<node-label>edgeidentity` | 约束 |
| 8 | pending-from 索引 (v2) | `CREATE RANGE INDEX … FOR (n:<node-label>EdgeIdentity) ON (n.physicalNamespace, n.projectionOwnerId, n.status, n.fromNodeId)` | `projection_graph_v2_pending_from_<node-label>edgeidentity` | — |
| 9 | pending-to 索引 (v2) | `CREATE RANGE INDEX … FOR (n:<node-label>EdgeIdentity) ON (n.physicalNamespace, n.projectionOwnerId, n.status, n.toNodeId)` | `projection_graph_v2_pending_to_<node-label>edgeidentity` | — |
| 10 | edge-identity owner 索引 (v2) | `CREATE RANGE INDEX … FOR (n:<node-label>EdgeIdentity) ON (n.physicalNamespace, n.projectionOwnerId)` | `projection_graph_v2_edge_identity_owner_<node-label>edgeidentity` | ✓ |

权威来源是 `Neo4jProjectionGraphStore.Infrastructure.cs` 的 `EnsureSchemaAsync` 与
`Neo4jProjectionGraphStoreCypherSupport` / `Neo4jProjectionGraphStoreVersionedCypherSupport`
中的 builder；修改任一 builder 必须同步本表。

owner graph replacement 在删除旧关系后通过单行聚合屏障继续执行，确保节点、关系写入和
陈旧节点清理的执行次数不随旧关系数量增长。

## Observability

五个 public write operation 各发出一条 terminal log，并由 Provider 本地 Meter
`Aevatar.CQRS.Projection.Providers.Neo4j` 记录 duration/total。metric tag 仅允许
`provider / operation / result`；`projectionKind / stateVersion / scope / ownerId /
nodeId / edgeId / nodeCount / edgeCount` 只进入结构化日志。

`replace_owner_graph` 从 `ProjectionOwnedGraph` 读取权威 `projectionKind / stateVersion`。
直接 CRUD API 没有该上下文时记录 `null`，不得从 scope、ownerId 或 id 字面量推断。
日志和 metric listener 的失败不能改变写入结果，也不能替换原始异常。
