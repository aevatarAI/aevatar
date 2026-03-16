# Aevatar.CQRS.Projection.Runtime

通用 Projection Runtime 组装层。

## 职责

- 统一写分发：`ProjectionStoreDispatcher<TReadModel>`
- Document Sink：`ProjectionDocumentStoreBinding<TReadModel>`
- Graph Sink：`ProjectionGraphStoreBinding<TReadModel>`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

默认注册：

- `IProjectionWriteDispatcher<TReadModel>` -> `ProjectionStoreDispatcher<TReadModel>`
- `IProjectionWriteSink<TReadModel>` -> `ProjectionDocumentStoreBinding<TReadModel>`
- `IProjectionWriteSink<TReadModel>` -> `ProjectionGraphStoreBinding<TReadModel>`

## 语义

1. Runtime 负责“一对多 store 分发”，不做 ProviderName 路由。
2. Document 与 Graph 保持平行；Runtime 默认同时装配两类 binding，按配置自动激活。
3. Runtime 不提供 read-side query；读取由 `IProjectionDocumentReader` 和 `IProjectionGraphStore` 直接承担。
4. binding 未激活时由 `IProjectionWriteSink.DisabledReason` 输出统一跳过原因日志。
