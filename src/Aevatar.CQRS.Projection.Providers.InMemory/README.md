# Aevatar.CQRS.Projection.Providers.InMemory

通用 InMemory Provider（支持 Document/Graph 两类能力）。

- 不依赖业务域模型。
- 支持按 keySelector 注册任意 `IDocumentProjectionStore<TReadModel, TKey>`（Document）。
- 支持图存储注册（Graph）。
- 仅用于开发和测试语义，不作为生产事实源。

## DI 注册

- `AddInMemoryDocumentStoreRegistration<TReadModel, TKey>(...)`
- `AddInMemoryGraphStoreRegistration()`

关键参数：

- `keySelector/keyFormatter`：ReadModel 主键映射。
- `listSortSelector`：`ListAsync` 排序字段（可选）。
- `listTakeMax`：`ListAsync` 硬上限。
