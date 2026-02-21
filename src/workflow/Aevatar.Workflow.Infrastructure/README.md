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
  - 对重复 workflow 名称执行 fail-fast（抛异常，阻止静默覆盖）。
- `Workflows/WorkflowDefinitionBootstrapHostedService`
  - 宿主启动时自动加载 workflow 文件源。
- `CapabilityApi/*`
  - Workflow 能力 API 定义（`/api/chat`、`/api/ws/chat`、`/api/actors/*` 等）与协议适配实现。

## DI 扩展

- `AddWorkflowInfrastructure(...)`
  - 注册报告工件 sink。
- `AddWorkflowDefinitionFileSource(...)`
  - 注册 workflow 文件源与启动加载 HostedService。
- `AddWorkflowCapability(IServiceCollection, IConfiguration)`
  - 能力一键组合（Application + Projection + AGUIAdapter + Infrastructure + workflow 文件源）。
- `AddWorkflowCapability(WebApplicationBuilder)`
  - Host 侧一行接入 Workflow 能力（服务注册 + 能力端点声明）。
- `Aevatar.Workflow.Extensions.Hosting.AddWorkflowCapabilityWithAIDefaults(WebApplicationBuilder)`
  - 在 Host 入口统一装配 Workflow capability + AI features + AI projection extension（推荐用于生产组合入口）。
- `MapWorkflowCapabilityEndpoints(...)`
  - 将 Workflow 能力 API 端点挂载到 Host（默认由 `UseAevatarDefaultHost()` 自动调用能力映射链路）。

## 配置

- `WorkflowExecutionReportArtifacts:OutputDirectory`
  - 报告输出目录，默认 `artifacts/workflow-executions`。
- `WorkflowDefinitionFileSource:WorkflowDirectories`
  - workflow 定义扫描目录列表。
  - 未知 YAML 字段与重复 workflow 名称会在启动期触发失败，避免运行时暴露配置问题。

## API 语义约束

- 一个 workflow 对应一个 actor，actor 绑定 workflow 后不可切换。
- 当请求携带 `actorId` 时，表示在该 actor 上继续运行；`workflow` 必须为空或与已绑定 workflow 一致。
- 若要运行另一个 workflow，必须创建新 actor（不复用旧 `actorId`）。

## 分层说明

本项目是 workflow 能力的组合层，按宿主场景装配 `Application/Projection/Adapter/Infrastructure`。
