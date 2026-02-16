# Aevatar.Demos.CaseProjection.Abstractions

Case Projection 领域契约项目。

包含：

- 事件定义（`case_projection_messages.proto`）
- Projection 上下文与会话模型
- 读模型定义
- 领域接口（`ICaseProjectionService`、`ICaseProjectionProjector`、`ICaseProjectionEventReducer` 等）

该项目只放抽象，不包含 DI、存储和编排实现。
