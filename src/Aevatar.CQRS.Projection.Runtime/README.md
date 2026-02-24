# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Runtime 组装层。

## 职责

- Document/Graph Provider 注册查询：`IProjectionDocumentStoreProviderRegistry`、`IProjectionGraphStoreProviderRegistry`
- Document/Graph Provider 显式选择：`IProjectionDocumentStoreProviderSelector`、`IProjectionGraphStoreProviderSelector`
- Store 创建与创建日志：`IProjectionDocumentStoreFactory`、`IProjectionGraphStoreFactory`
- 启动期 fail-fast 校验：`IProjectionDocumentStartupValidator`、`IProjectionGraphStartupValidator`
- Materialization 路由：`IProjectionMaterializationRouter<TReadModel, TKey>`、`ProjectionGraphMaterializer<TReadModel>`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

## 设计约束

1. 不承载业务 ReadModel 类型。
2. 不实现能力协商，不依赖 Capabilities/Requirements 模型。
3. 仅依赖抽象契约与 DI；具体 Provider 由上层注册。
