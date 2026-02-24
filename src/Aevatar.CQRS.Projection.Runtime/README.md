# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Provider Runtime 组装层。

## 职责

- 收敛 Provider 注册查询（`IProjectionDocumentStoreProviderRegistry`）。
- 执行 Provider 选择策略（`IProjectionDocumentStoreProviderSelector`）。
- 按 `IDocumentReadModel/IGraphReadModel` 能力推导选择需求（`IProjectionStoreSelectionPlanner`）。
- 统一创建 Store 并输出结构化创建日志（`IProjectionDocumentStoreFactory`）。
- 提供 `IProjectionMaterializationRouter<TReadModel, TKey>` 与 `ProjectionGraphMaterializer<TReadModel>` 双写路由能力。
- 所有上述契约统一来自 `Aevatar.CQRS.Projection.Runtime.Abstractions`。

## DI 入口

- `services.AddProjectionReadModelRuntime()`

## 设计约束

- 不承载业务 ReadModel 类型，不引用 Workflow/AI 等业务模块。
- 仅依赖抽象与 DI，具体 Provider 由上层模块按需注册。
