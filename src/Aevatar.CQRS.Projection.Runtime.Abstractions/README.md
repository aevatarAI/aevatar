# Aevatar.CQRS.Projection.Runtime.Abstractions

`Aevatar.CQRS.Projection.Runtime.Abstractions` 承载 Projection Runtime 的策略与编排契约，不承载具体实现。

## 目录结构

- `Abstractions/Core`：Provider 注册契约（`IProjectionStoreRegistration<TStore>`）
- `Abstractions/ReadModels`：Document provider 选择、能力模型、runtime options、metadata resolver 契约
- `Abstractions/Graphs`：Graph provider 选择与 factory 契约
- `Abstractions/Selection`：统一选择计划、启动校验与 materialization 路由契约

## 关键契约

- Provider 注册与能力：`IProjectionStoreRegistration<TStore>`、`ProjectionProviderCapabilities`
- 选择规划：`IProjectionStoreSelectionPlanner`、`ProjectionStoreSelectionPlan`
- 运行时选择参数：`ProjectionStoreSelectionOptions`、`ProjectionStoreRequirements`、`IProjectionStoreSelectionRuntimeOptions`
- Store factory：`IProjectionDocumentStoreFactory`、`IProjectionGraphStoreFactory`
- Materialization：`IProjectionMaterializationRouter<TReadModel, TKey>`、`IProjectionGraphMaterializer<TReadModel>`

## 约束

1. 仅定义运行时编排协议，不包含具体 provider/store 实现。
2. 仅依赖抽象层（`Stores.Abstractions`），不依赖业务模块。
