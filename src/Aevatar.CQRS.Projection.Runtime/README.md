# Aevatar.CQRS.Projection.Runtime

通用 Projection Runtime 组装层。

## 职责

- 统一分发：`ProjectionStoreDispatcher<TReadModel, TKey>`
- Document Binding：`ProjectionDocumentStoreBinding<TReadModel, TKey>`
- Graph Binding：`ProjectionGraphStoreBinding<TReadModel, TKey>`
- Document Metadata 解析：`ProjectionDocumentMetadataResolver`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

默认注册：

- `IProjectionStoreDispatcher<,>` -> `ProjectionStoreDispatcher<,>`
- `IProjectionQueryableStoreBinding<,>` -> `ProjectionDocumentStoreBinding<,>`
- `IProjectionStoreBinding<,>`(默认) -> `ProjectionDocumentStoreBinding<,>`
- `IProjectionStoreBinding<,>`(默认) -> `ProjectionGraphStoreBinding<,>`
- `IProjectionDocumentMetadataResolver` -> `ProjectionDocumentMetadataResolver`

## 语义

1. Runtime 负责“一对多 store 分发”，不做 ProviderName 路由。
2. Document 与 Graph 保持平行；Runtime 默认同时装配两类 binding，按配置自动激活。
3. queryable binding 为可选（0..1）；存在时提供 `Get/List/Mutate`，写入始终分发到所有已配置 binding。
4. binding 未激活时由 `IProjectionStoreBindingAvailability.AvailabilityReason` 输出统一跳过原因日志。
