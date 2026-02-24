# Aevatar.CQRS.Projection.Runtime.Abstractions

`Aevatar.CQRS.Projection.Runtime.Abstractions` 承载 Projection Runtime 的编排契约，不承载任何具体 Provider 实现。

## 目录结构

- `Abstractions/Core`：Store 注册契约（`IProjectionStoreRegistration<TStore>`）
- `Abstractions/ReadModels`：Document metadata resolver
- `Abstractions/Selection`：Materialization 路由与 graph materializer 契约

## 关键契约

- Store 注册：`IProjectionStoreRegistration<TStore>`
  - `ProviderName`
- Materialization：`IProjectionMaterializationRouter<TReadModel, TKey>`、`IProjectionGraphMaterializer<TReadModel>`
- Metadata：`IProjectionDocumentMetadataResolver`

查询语义：

- Fan-out Runtime 以注册顺序选择查询源（第一个注册的 provider 作为 query store，其余作为写入副本）。

## 约束

1. 不包含 ProviderName 选择与 RuntimeOptions。
2. 不包含能力协商模型（Capabilities/Requirements/CapabilityValidator）。
3. 仅依赖 `Aevatar.CQRS.Projection.Stores.Abstractions`。
