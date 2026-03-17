# Aevatar.Workflow.Abstractions

`Aevatar.Workflow.Abstractions` 提供 Workflow 执行协议的消息抽象（protobuf 事件契约）。

职责边界：

1. 只承载事件抽象（如 `StartWorkflowEvent`、`StepRequestEvent`、`StepCompletedEvent`、`WorkflowCompletedEvent`）。
2. 不包含 Workflow 执行编排、Actor 生命周期、Projection 运行时逻辑。
3. 供 `Workflow.Core`、`Maker.Core` 及测试/演示按抽象依赖复用。
