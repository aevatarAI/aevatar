# Aevatar.Workflow.Presentation.AGUIAdapter

`Aevatar.Workflow.Presentation.AGUIAdapter` 是 AGUI 与 WorkflowExecution 之间的适配层。

## 职责

- 将 `EventEnvelope` 映射为 `AGUIEvent`（`EventEnvelopeToAGUIEventMapper`）
- 将 `AGUIEvent` 映射为领域中立 `WorkflowRunEvent`（`AGUIEventToWorkflowRunEventMapper`）
- 提供 WorkflowExecution 的 AGUI projector（`WorkflowExecutionAGUIEventProjector`）

## 边界

- 依赖 `Aevatar.Presentation.AGUI` 协议层
- 依赖 `Aevatar.Workflow.Projection` 领域投影上下文
- 不承载 Host endpoint 与流程编排逻辑

## 目标

把 AGUI 展示协议与 Workflow 领域耦合点收敛在适配层，避免 Host 与领域核心重复耦合实现。
