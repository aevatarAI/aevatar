# Aevatar.CQRS.Projection.Stores.Abstractions

`Aevatar.CQRS.Projection.Stores.Abstractions` 仅包含 Projection 存储契约与 ReadModel 结构契约，不包含任何运行时编排或 Provider 选择逻辑。

## 契约清单

- ReadModel 基础：`IProjectionReadModel`
- Document Reader：`IProjectionDocumentReader<TReadModel, TKey>`
- Document Writer：`IProjectionDocumentWriter<TReadModel>`
- Document Query：`ProjectionDocumentQuery`、`ProjectionDocumentFilter`、`ProjectionDocumentSort`、`ProjectionDocumentQueryResult<TReadModel>`
- Graph Store：`IProjectionGraphStore`
- Document 索引元数据：`DocumentIndexMetadata`、`IProjectionDocumentMetadataProvider<TReadModel>`
- Graph 数据结构：`ProjectionGraphNode`、`ProjectionGraphEdge`、`ProjectionOwnedGraph`、`ProjectionGraphQuery`、`ProjectionGraphSubgraph`

## 设计边界

1. Document 与 Graph 是平行的两类存储契约。
2. 不包含 Router/Fanout/Factory/ProviderName 选择逻辑。
3. 不包含业务域实现、DI 装配和具体存储实现。
4. `ProjectionOwnedGraph.ProjectionKind / StateVersion` 是写入 provenance：前者来自 materialization
   context，后者来自权威 read model；Provider 只能透传和观测，不得自行推导。
