# Aevatar.CQRS.Projection.Providers.InMemory

通用 InMemory ReadModel Provider。

- 不依赖业务域模型。
- 支持按 keySelector 注册任意 `IProjectionReadModelStore<TReadModel, TKey>`。
- 默认能力：非索引型（`SupportsIndexing=false`）。

## DI 注册

使用扩展方法：

- `AddInMemoryReadModelStoreRegistration<TReadModel, TKey>(...)`

关键参数：

- `keySelector/keyFormatter`：ReadModel 主键映射。
- `listSortSelector`：`ListAsync` 排序字段（可选）。
- `listTakeMax`：`ListAsync` 硬上限。
- `providerName`：默认 `InMemory`（与 `ProjectionReadModelProviderNames.InMemory` 一致）。
