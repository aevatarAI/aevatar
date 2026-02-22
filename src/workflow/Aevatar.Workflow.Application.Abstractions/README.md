# Aevatar.Workflow.Application.Abstractions

Workflow 应用层抽象契约，供 Host/Infrastructure 依赖。

## 包含内容

- `Runs/`
  - `IWorkflowRunCommandService`
  - `WorkflowChatRunRequest`、`WorkflowChatRunStarted`、`WorkflowChatRunExecutionResult`、`WorkflowOutputFrame`
- `Queries/`
  - `IWorkflowExecutionQueryApplicationService`
  - `WorkflowActorSnapshot`、`WorkflowActorTimelineItem`、`WorkflowRunReport`
- `Workflows/`
  - `IWorkflowDefinitionRegistry`
- `Reporting/`
  - `IWorkflowExecutionReportArtifactSink`

## 设计目标

- 稳定端口优先：Host 与 Infrastructure 只依赖本项目。
- 依赖反转：避免基础设施直接依赖应用层实现程序集。
- 可替换实现：应用层、落盘策略、workflow 来源都可独立替换。
