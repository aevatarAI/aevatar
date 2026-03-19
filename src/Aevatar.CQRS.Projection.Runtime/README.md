# Aevatar.CQRS.Projection.Runtime

通用 Projection Runtime 组装层。

## 职责

- 统一写分发：`ProjectionStoreDispatcher<TReadModel>`
- Document Sink：`ProjectionDocumentStoreBinding<TReadModel>`
- Graph Writer：`ProjectionGraphWriter<TReadModel>`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

默认注册：

- `IProjectionWriteDispatcher<TReadModel>` -> `ProjectionStoreDispatcher<TReadModel>`
- `IProjectionWriteSink<TReadModel>` -> `ProjectionDocumentStoreBinding<TReadModel>`
- `IProjectionGraphWriter<TReadModel>` -> `ProjectionGraphWriter<TReadModel>`

## 语义

1. Runtime 负责“一对多 store 分发”，不做 ProviderName 路由。
2. Document 与 Graph 分责：dispatcher 只负责 document/readmodel 覆盖写，graph 通过 owner-level replace 单独提交。
3. Runtime 不提供 read-side query；读取由 `IProjectionDocumentReader` 和 `IProjectionGraphStore` 直接承担。
4. binding 未激活时由 `IProjectionWriteSink.DisabledReason` 输出统一跳过原因日志。
