# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Runtime 组装层。

## 职责

- Store 创建与 provider 选择（内聚在 factory）：`IProjectionDocumentStoreFactory`、`IProjectionGraphStoreFactory`
- Store 创建日志与 fail-fast 错误：`ProjectionProviderSelectionException`
- Materialization 路由：`IProjectionMaterializationRouter<TReadModel, TKey>`、`ProjectionGraphMaterializer<TReadModel>`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

## 设计约束

1. 不承载业务 ReadModel 类型。
2. 不实现能力协商，不依赖 Capabilities/Requirements 模型。
3. Provider 选择规则：`providerName` 精确匹配；无注册、多注册无明确 provider、provider 不存在都立即失败。
4. 仅依赖抽象契约与 DI；具体 Provider 由上层注册。
