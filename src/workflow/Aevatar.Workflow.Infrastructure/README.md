# Aevatar.Workflow.Infrastructure

`Aevatar.Workflow.Infrastructure` 承载 Workflow 用例的基础设施实现，不包含业务编排逻辑。

## 当前内容

- `Reporting/FileSystemWorkflowExecutionReportArtifactSink`
  - 实现 `IWorkflowExecutionReportArtifactSink`。
  - 负责把 `WorkflowExecutionReport` 写入 JSON/HTML 工件。
- `Reporting/WorkflowExecutionReportWriter`
  - 报告序列化与 HTML 渲染工具。
- `DependencyInjection/ServiceCollectionExtensions`
  - `AddWorkflowInfrastructure(...)`：注册基础设施实现。

## 配置

`WorkflowExecutionReportArtifacts:OutputDirectory`

- 未配置时默认输出到 `artifacts/workflow-executions`。
