# Aevatar.CQRS.Projection.Runtime.Abstractions

`Aevatar.CQRS.Projection.Runtime.Abstractions` 承载 Projection Runtime 的编排契约，不承载任何具体 Provider 实现。

## 目录结构

- `Abstractions/Core`：Provider 注册契约（`IProjectionStoreRegistration<TStore>`）
- `Abstractions/Documents`：Document runtime options、selection options、startup validator 契约
- `Abstractions/ReadModels`：Document store registry/factory/selector、metadata resolver 契约
- `Abstractions/Graphs`：Graph runtime options、selection options、startup validator、store registry/factory/selector 契约
- `Abstractions/Selection`：Materialization 路由与 graph materializer 契约

## 关键契约

- Provider 注册：`IProjectionStoreRegistration<TStore>`
- Document 运行时：`IProjectionDocumentRuntimeOptions`、`ProjectionDocumentSelectionOptions`
- Graph 运行时：`IProjectionGraphRuntimeOptions`、`ProjectionGraphSelectionOptions`
- Store factory：`IProjectionDocumentStoreFactory`、`IProjectionGraphStoreFactory`
- Startup validation：`IProjectionDocumentStartupValidator`、`IProjectionGraphStartupValidator`
- Materialization：`IProjectionMaterializationRouter<TReadModel, TKey>`、`IProjectionGraphMaterializer<TReadModel>`

## 约束

1. 不包含能力协商模型（Capabilities/Requirements/CapabilityValidator）。
2. Provider 选择语义为显式 providerName + fail-fast，不做自动降级。
3. 仅依赖 `Aevatar.CQRS.Projection.Stores.Abstractions`。
