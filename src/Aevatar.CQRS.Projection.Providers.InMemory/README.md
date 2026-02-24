# Aevatar.CQRS.Projection.Providers.InMemory

通用 InMemory Provider（支持 Document/Graph 两类能力）。

- 不依赖业务域模型。
- 支持按 keySelector 注册任意 `IDocumentProjectionStore<TReadModel, TKey>`（Document）。
- 支持关系图存储注册（Graph）。
- 默认能力：Document 索引 / Graph 索引（仅用于开发和测试语义）。
- 写入路径输出结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。

## DI 注册

使用扩展方法：

- `AddInMemoryDocumentStoreRegistration<TReadModel, TKey>(...)`
- `AddInMemoryGraphStoreRegistration(...)`

关键参数：

- `keySelector/keyFormatter`：ReadModel 主键映射。
- `listSortSelector`：`ListAsync` 排序字段（可选）。
- `listTakeMax`：`ListAsync` 硬上限。
- `providerName`：默认 `InMemory`（与 `ProjectionProviderNames.InMemory` 一致）。
