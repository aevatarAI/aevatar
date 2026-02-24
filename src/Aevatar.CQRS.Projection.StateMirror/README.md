# Aevatar.CQRS.Projection.StateMirror

通用 `State -> ReadModel` 镜像组件。

- 默认实现：`JsonStateMirrorProjection<TState, TReadModel>`。
- 支持字段忽略：`StateMirrorProjectionOptions.IgnoredFields`。
- 支持字段重命名：`StateMirrorProjectionOptions.RenamedFields`。
- 可作为 `Default` 模式的基础设施组件复用。
