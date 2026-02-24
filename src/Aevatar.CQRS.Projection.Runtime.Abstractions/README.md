# Aevatar.CQRS.Projection.Runtime.Abstractions

`Aevatar.CQRS.Projection.Runtime.Abstractions` 定义 Runtime 层的最小编排契约。

## 契约清单

- `IProjectionStoreDispatcher<TReadModel, TKey>`
- `IProjectionStoreBinding<TReadModel, TKey>`
- `IProjectionQueryableStoreBinding<TReadModel, TKey>`
- `IProjectionDocumentMetadataResolver`
- `ProjectionGraphManagedPropertyKeys`

## 模型说明

1. 一个 ReadModel 可绑定多个 Store（例如 Document + Graph）。
2. 仅允许一个 `IProjectionQueryableStoreBinding` 作为查询/读取来源。
3. 其余 binding 作为写入目标参与分发。

## 边界

- 不包含具体 Provider 实现。
- 不包含业务域 ReadModel。
- 仅依赖 `Aevatar.CQRS.Projection.Stores.Abstractions`。
