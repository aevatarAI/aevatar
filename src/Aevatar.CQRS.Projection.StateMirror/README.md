# Aevatar.CQRS.Projection.StateMirror

通用 `State -> ReadModel` 镜像组件。

- 默认实现：`JsonStateMirrorProjection<TState, TReadModel>`。
- 支持字段忽略：`StateMirrorProjectionOptions.IgnoredFields`。
- 支持字段重命名：`StateMirrorProjectionOptions.RenamedFields`。
- 一站式执行器：`IStateMirrorReadModelProjector<TState,TReadModel,TKey>`。

## 目标架构

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  S["State(TState)"] --> M["IStateMirrorProjection"]
  M --> RM["ReadModel(TReadModel)"]
  RM --> P["IStateMirrorReadModelProjector"]
  P --> D["IProjectionStoreDispatcher"]
  D --> DS["DocumentStore Binding"]
  D --> GS["GraphStore Binding"]
```

## DI 入口

- `services.AddJsonStateMirrorProjection<TState, TReadModel>(configure?)`
  仅注册 `State -> ReadModel` 映射器。
- `services.AddJsonStateMirrorReadModelProjector<TState, TReadModel>(configure?)`
  注册映射器和默认 `string` 键的一站式执行器。
- `services.AddJsonStateMirrorReadModelProjector<TState, TReadModel, TKey>(configure?)`
  注册映射器和自定义键类型执行器。

## 边界约束

- `StateMirror` 仅用于 `State -> ReadModel` 的结构镜像与字段重命名/忽略，不承载业务规则。
- 若映射过程涉及业务语义计算、状态机衍生字段、跨事件聚合，应使用业务专用 mapper/reducer，而不是 `StateMirror`。
- 可并存策略：默认采用 `StateMirror` 快速落地，出现业务语义时切换到显式业务 mapper。

## 示例

```csharp
services.AddProjectionReadModelRuntime();
services.AddJsonStateMirrorReadModelProjector<MyState, MyReadModel>(options =>
{
    options.RenamedFields[nameof(MyState.ActorId)] = nameof(MyReadModel.Id);
});
```
