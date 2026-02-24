# Aevatar.CQRS.Projection.Runtime

通用 ReadModel Runtime 组装层。

## 职责

- Store Fan-out 组合：
  - `ProjectionDocumentStoreFanout<TReadModel, TKey>`
  - `ProjectionGraphStoreFanout`
- Materialization 路由：`IProjectionMaterializationRouter<TReadModel, TKey>`、`ProjectionGraphMaterializer<TReadModel>`
- Document metadata 解析：`IProjectionDocumentMetadataResolver`

## DI 入口

- `services.AddProjectionReadModelRuntime()`

默认注册：

- `IDocumentProjectionStore<,>` -> `ProjectionDocumentStoreFanout<,>`
- `IProjectionGraphStore` -> `ProjectionGraphStoreFanout`
- `IProjectionGraphMaterializer<>`
- `IProjectionMaterializationRouter<,>`
- `IProjectionDocumentMetadataResolver`

## 设计约束

1. 不承载业务 ReadModel 类型。
2. 不做 providerName 单选，不存在运行时降级逻辑；多 provider 时必须显式且唯一 `IsPrimaryQueryStore=true`。
3. Document 与 Graph 完全解耦，分别按注册列表一对多分发（写 fan-out，读走 primary）。
4. 仅依赖抽象契约与 DI；具体 Provider 由上层注册。
