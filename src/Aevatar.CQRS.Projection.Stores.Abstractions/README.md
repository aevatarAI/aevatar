# Aevatar.CQRS.Projection.Stores.Abstractions

`Aevatar.CQRS.Projection.Stores.Abstractions` 只包含投影存储、能力声明、provider 选择与启动校验相关抽象。

## 目录结构

- `Abstractions/Core`：provider 元数据与通用注册契约（`IProjectionStoreRegistration<TStore>`）
- `Abstractions/ReadModels`：ReadModel store/provider 能力、选择与校验抽象
- `Abstractions/Relations`：Relation store/provider 与图关系模型抽象
- `Abstractions/Selection`：跨 readmodel/relation 的统一选择计划与启动校验契约

## 包含内容

- 读模型存储：`IProjectionReadModelStore<,>`
- 关系存储：`IProjectionRelationStore`
- ReadModel 能力模型：`IProjectionReadModel`、`IDocumentReadModel`、`IGraphReadModel`
- 双写能力抽象：`IDocumentProjectionStore<,>`、`IGraphProjectionStore<>`、`IProjectionMaterializationRouter<,>`
- 文档索引元数据抽象：`DocumentIndexMetadata`、`IReadModelDocumentMetadataProvider<TReadModel>`、`IProjectionDocumentMetadataResolver`
- Provider 能力建模：`ProjectionReadModelProviderCapabilities`、`IProjectionStoreProviderMetadata`
- 能力校验：`ProjectionReadModelRequirements`、`ProjectionReadModelCapabilityValidator`
- Provider 注册与选择：`IProjectionStoreRegistration<TStore>`、`DelegateProjectionStoreRegistration<TStore>`
- Provider Runtime 契约：`IProjectionReadModelProviderRegistry`、`IProjectionReadModelProviderSelector`、`IProjectionReadModelStoreFactory`
- 选择编排：`IProjectionStoreSelectionPlanner`、`IProjectionStoreStartupValidator`

## 约束

1. 不包含投影主链路编排接口（这些在 `Aevatar.CQRS.Projection.Core.Abstractions`）。
2. 不包含业务模型、DI 装配与具体 provider 实现。
3. capability 声明必须与 provider 真实实现一致，不允许“声明支持但未实现”。
