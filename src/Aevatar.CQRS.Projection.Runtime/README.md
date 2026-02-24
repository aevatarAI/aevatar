# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Provider Runtime 组装层。

## 职责

- 收敛 Provider 注册查询（`IProjectionReadModelProviderRegistry`）。
- 执行 Provider 选择策略（`IProjectionReadModelProviderSelector`）。
- 按 `IDocumentReadModel/IGraphReadModel` 能力推导选择需求（`IProjectionStoreSelectionPlanner`）。
- 统一创建 Store 并输出结构化创建日志（`IProjectionReadModelStoreFactory`）。
- 提供 `IProjectionMaterializationRouter<TReadModel, TKey>` 与 `ProjectionGraphStoreAdapter<TReadModel>` 双写路由能力。

## DI 入口

- `services.AddProjectionReadModelRuntime()`

## 设计约束

- 不承载业务 ReadModel 类型，不引用 Workflow/AI 等业务模块。
- 仅依赖抽象与 DI，具体 Provider 由上层模块按需注册。
