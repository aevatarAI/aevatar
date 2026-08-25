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
5. Graph Writer 由调用方显式传入权威 `projectionKind`，并从 `IProjectionReadModel.StateVersion`
   复制权威版本到 `ProjectionOwnedGraph`；不得从 scope、ownerId 或 properties 推断。
6. Graph Writer 用单调时钟单独记录 graph construction；Provider write 由具体 Provider 自己计时，
   两段日志通过同一个 `projectionKind / stateVersion` 对齐。
