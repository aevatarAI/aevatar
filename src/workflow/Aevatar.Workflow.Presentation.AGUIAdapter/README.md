# Aevatar.Workflow.Presentation.AGUIAdapter

`Aevatar.Workflow.Presentation.AGUIAdapter` 负责 Workflow 事件与 AGUI 协议之间的适配。

## 职责

- `EventEnvelope -> AGUIEvent`
  - 通过 `IEventEnvelopeToAGUIEventMapper` + `IAGUIEventEnvelopeMappingHandler` 组合映射。
- `AGUIEvent -> WorkflowRunEvent`
  - 由 `AGUIEventToWorkflowRunEventMapper` 统一转换为领域中立输出事件。
- `WorkflowExecutionAGUIEventProjector`
  - 作为 Projection 分支，把 AGUI 输出写入 run event sink。

## OCP 扩展方式

新增 AGUI 映射时：

1. 新增一个 `IAGUIEventEnvelopeMappingHandler` 实现。
2. 在 DI 中注册该 handler。

无需修改核心 mapper/项目器。

## DI 入口

- `AddWorkflowExecutionAGUIAdapter()`
  - 注册组合 mapper 与默认 handlers。

## 边界

- 依赖 `Aevatar.Presentation.AGUI` 与 `Aevatar.Workflow.Projection`。
- 不承载 Host endpoint 逻辑，不包含应用层编排。
