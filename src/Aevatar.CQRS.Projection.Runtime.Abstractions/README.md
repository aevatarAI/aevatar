# Aevatar.CQRS.Projection.Runtime.Abstractions

`Aevatar.CQRS.Projection.Runtime.Abstractions` 承载 Projection Runtime 的编排契约，不承载任何具体 Provider 实现。

## 目录结构

- `Abstractions/Core`：Store 注册契约（`IProjectionStoreRegistration<TStore>`）
- `Abstractions/ReadModels`：Document metadata resolver
- `Abstractions/Selection`：Materialization 路由与 graph materializer 契约

## 关键契约

- Store 注册：`IProjectionStoreRegistration<TStore>`
  - `ProviderName`
  - `IsPrimaryQueryStore`（多 provider 场景下唯一主查询存储）
- Materialization：`IProjectionMaterializationRouter<TReadModel, TKey>`、`IProjectionGraphMaterializer<TReadModel>`
- Metadata：`IProjectionDocumentMetadataResolver`

## 约束

1. 不包含 ProviderName 选择与 RuntimeOptions。
2. 不包含能力协商模型（Capabilities/Requirements/CapabilityValidator）。
3. 仅依赖 `Aevatar.CQRS.Projection.Stores.Abstractions`。
