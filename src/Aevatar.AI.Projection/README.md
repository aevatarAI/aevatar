# Aevatar.AI.Projection

`Aevatar.AI.Projection` 提供 AI 通用事件的读侧 reducer 抽象，避免在业务投影里重复解析同类事件。

## 核心模式

- `ProjectionEventApplierReducerBase<TReadModel, TContext, TEvent>`
  - 统一处理 `TypeUrl` 精确匹配与 protobuf 反序列化
  - 将字段映射下沉到 `IProjectionEventApplier<,,>` 实现
- 内置 reducer：
  - `TextMessageStartProjectionReducer`
  - `TextMessageContentProjectionReducer`
  - `TextMessageEndProjectionReducer`
  - `ToolCallProjectionReducer`
  - `ToolResultProjectionReducer`

## DI 接入

支持按事件类型选择性注册 reducer（推荐）：

```csharp
services.AddAITextMessageEndProjectionReducer<TReadModel, TContext>();
```

如需全量注册，可显式调用：

```csharp
services.AddAllAIProjectionEventReducers<TReadModel, TContext>();
```

业务能力只需实现对应事件的 `IProjectionEventApplier<,,>`，无需复制 reducer。

## 设计约束

- 仅包含通用 AI 事件读侧逻辑。
- 不依赖具体业务读模型实现项目。
- 不包含 Host/API 协议适配代码。
