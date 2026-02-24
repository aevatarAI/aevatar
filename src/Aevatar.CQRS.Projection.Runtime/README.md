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
- `IProjectionDocumentMetadataResolver` -> `ProjectionDocumentMetadataResolver`

## 语义

1. Runtime 负责“一对多 store 分发”，不做 ProviderName 路由。
2. Document 与 Graph 保持平行；Graph 通过额外 binding 接入。
3. 查询统一走唯一 queryable binding；写入同时分发到所有 binding。
