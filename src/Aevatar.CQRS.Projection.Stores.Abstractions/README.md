# Aevatar.CQRS.Projection.Stores.Abstractions

`Aevatar.CQRS.Projection.Stores.Abstractions` 只包含投影存储能力契约与读模型结构契约。

## 目录结构

- `Abstractions/ReadModels`：读模型能力与文档存储契约
- `Abstractions/Graphs`：图存储契约与图查询模型

## 包含内容

- 读模型存储：`IDocumentProjectionStore<,>`
- 图存储：`IProjectionGraphStore`
- ReadModel 能力模型：`IProjectionReadModel`、`IDocumentReadModel`（marker）、`IGraphReadModel`
- 文档索引元数据声明：`DocumentIndexMetadata`、`IProjectionDocumentMetadataProvider<TReadModel>`；`DocumentIndexMetadata` 使用结构化对象字段（`Mappings/Settings/Aliases`）表达索引元数据。
- 图结构描述：`GraphNodeDescriptor`、`GraphEdgeDescriptor`

## 约束

1. 不包含 Provider 选择、Factory、Runtime options、Materialization Router 等运行时编排契约。
2. 不包含投影主链路编排接口（这些在 `Aevatar.CQRS.Projection.Core.Abstractions`）。
3. 不包含业务模型、DI 装配与具体 provider 实现。
