# Aevatar.Presentation.AGUI.Adapter.WorkflowExecution

`Aevatar.Presentation.AGUI.Adapter.WorkflowExecution` 是 AGUI 与 WorkflowExecution 之间的适配层。

## 职责

- 将 `EventEnvelope` 映射为 `AGUIEvent`（`EventEnvelopeToAGUIEventMapper`）
- 提供 WorkflowExecution 的 AGUI projector（`WorkflowExecutionAGUIEventProjector`）
- 提供 run context 的 AGUI sink 挂载扩展（`WorkflowExecutionProjectionContextAGUIExtensions`）

## 边界

- 依赖 `Aevatar.Presentation.AGUI` 协议层
- 依赖 `Aevatar.CQRS.Projection.WorkflowExecution` 领域投影上下文
- 不承载 Host endpoint 与流程编排逻辑

## 目标

把 AGUI 展示协议与 Workflow 领域耦合点收敛在适配层，避免 Host 与领域核心重复耦合实现。
