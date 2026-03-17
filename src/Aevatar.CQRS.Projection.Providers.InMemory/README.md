# Aevatar.CQRS.Projection.Providers.InMemory

通用 InMemory Provider，提供 Document 与 Graph 两类平行实现。

## 能力

- Document：`InMemoryProjectionDocumentStore<TReadModel, TKey>`
- Graph：`InMemoryProjectionGraphStore`
- Document Query：`GetAsync(key)` 与 `QueryAsync(query)`

## DI

- `AddInMemoryDocumentProjectionStore<TReadModel, TKey>(...)`
- `AddInMemoryGraphProjectionStore()`

## 说明

- 仅用于开发/测试语义。
- `QueryAsync` 语义尽量对齐生产 provider，避免测试/生产分叉。
- 不作为生产事实源。
