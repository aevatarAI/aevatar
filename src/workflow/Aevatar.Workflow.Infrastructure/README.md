# Aevatar.Workflow.Infrastructure

`Aevatar.Workflow.Infrastructure` 提供 Workflow 应用层端口的基础设施实现（文件系统、工件落盘、启动装配）。

## 当前实现

- `Reporting/FileSystemWorkflowExecutionReportArtifactSink`
  - 实现 `IWorkflowExecutionReportArtifactSink`。
  - 将 `WorkflowRunReport` 输出为 JSON/HTML 工件。
- `Reporting/WorkflowExecutionReportWriter`
  - 报告序列化与 HTML 渲染。
- `Workflows/WorkflowDefinitionFileLoader`
  - 从目录加载 `*.yaml/*.yml` 并注册到 `IWorkflowDefinitionRegistry`。
- `Workflows/WorkflowDefinitionBootstrapHostedService`
  - 宿主启动时自动加载 workflow 文件源。
- `CapabilityApi/*`
  - Workflow 能力 API 定义（`/api/chat`、`/api/ws/chat`、`/api/actors/*` 等）与协议适配实现。

## DI 扩展

- `AddWorkflowInfrastructure(...)`
  - 注册报告工件 sink。
- `AddWorkflowDefinitionFileSource(...)`
  - 注册 workflow 文件源与启动加载 HostedService。
- `AddWorkflowCapability(...)`
  - 能力一键组合（Application + Projection + AGUIAdapter + Infrastructure + workflow 文件源）。
- `MapWorkflowCapabilityEndpoints(...)`
  - 将 Workflow 能力 API 端点挂载到 Host。

## 配置

- `WorkflowExecutionReportArtifacts:OutputDirectory`
  - 报告输出目录，默认 `artifacts/workflow-executions`。
- `WorkflowDefinitionFileSource:WorkflowDirectories`
  - workflow 定义扫描目录列表。

## 分层说明

本项目是 workflow 能力的组合层，按宿主场景装配 `Application/Projection/Adapter/Infrastructure`。
