# Aevatar.AI.Projection

`Aevatar.AI.Projection` 提供 AI 通用事件的读侧 reducer 抽象，避免在业务投影里重复解析同类事件。

## 核心模式

- `AIProjectionReadModelBase`
  - 作为 AI 层 ReadModel 基类，可被业务层 ReadModel 继承
  - 声明 `IHasProjectionTimeline` / `IHasProjectionRoleReplies` 能力契约
  - 不内置集合字段，避免给业务 ReadModel 增加冗余存储
- `IAIProjectionContext`
  - 约束 AI 默认 applier 需要的最小上下文（当前仅 `RootActorId`）
- `ProjectionEventApplierReducerBase<TReadModel, TContext, TEvent>`
  - 统一处理 `TypeUrl` 精确匹配与 protobuf 反序列化
  - 将字段映射下沉到 `IProjectionEventApplier<,,>` 实现
- 内置 reducer：
  - `TextMessageStartProjectionReducer`
  - `TextMessageContentProjectionReducer`
  - `TextMessageEndProjectionReducer`
  - `ToolCallProjectionReducer`
  - `ToolResultProjectionReducer`
- 内置默认 applier：
  - `AITextMessageStartProjectionApplier`
  - `AITextMessageContentProjectionApplier`
  - `AITextMessageEndProjectionApplier`
  - `AIToolCallProjectionApplier`
  - `AIToolResultProjectionApplier`

## DI 接入

推荐按“AI 分层”一次性注册默认 applier + reducer：

```csharp
services.AddAIDefaultProjectionLayer<TReadModel, TContext>();
```

也支持按事件类型选择性注册 reducer：

```csharp
services.AddAITextMessageEndProjectionReducer<TReadModel, TContext>();
```

如需仅全量 reducer，可显式调用：

```csharp
services.AddAllAIProjectionEventReducers<TReadModel, TContext>();
```

业务能力既可直接使用默认 applier，也可替换为自定义 `IProjectionEventApplier<,,>`。

## 设计约束

- 仅包含通用 AI 事件读侧逻辑。
- 不依赖具体业务读模型实现项目。
- 不包含 Host/API 协议适配代码。
