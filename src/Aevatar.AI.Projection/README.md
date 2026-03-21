# Aevatar.AI.Projection

`Aevatar.AI.Projection` 提供 AI 通用事件的 durable artifact applier 组件，避免在业务物化链路里重复解析同类事件。

## 核心模式

- `IProjectionMaterializationContext`
  - AI 默认 applier 只依赖 actor-scoped durable materialization 上下文
  - 不依赖 session/live sink 语义
- 内置默认 applier：
  - `AITextMessageStartProjectionApplier`
  - `AITextMessageContentProjectionApplier`
  - `AITextMessageEndProjectionApplier`
  - `AIToolCallProjectionApplier`
  - `AIToolResultProjectionApplier`

## DI 接入

推荐按 durable artifact 物化链路一次性注册默认 applier：

```csharp
services.AddAIDefaultProjectionAppliers<TReadModel, TContext>();
```

业务能力既可直接使用默认 applier，也可替换为自定义 `IProjectionEventApplier<,,>`。

## 设计约束

- 仅包含通用 AI 事件 durable artifact 逻辑。
- 不依赖具体业务读模型实现项目。
- 不包含 Host/API 协议适配代码。
