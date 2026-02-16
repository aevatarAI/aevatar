# Aevatar.Workflow.Infrastructure

工作流基础设施层。提供文件系统加载、报告工件落盘、宿主启动装配等 IO 相关实现。

## 目录结构

```
Aevatar.Workflow.Infrastructure/
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs            # AddWorkflowInfrastructure() / AddWorkflowDefinitionFileSource()
├── Workflows/
│   ├── WorkflowDefinitionFileLoader.cs           # 扫描目录、加载 YAML、注册 workflow
│   ├── WorkflowDefinitionBootstrapHostedService.cs # IHostedService：启动时自动加载
│   └── WorkflowDefinitionFileSourceOptions.cs    # 配置：扫描目录列表
└── Reporting/
    ├── FileSystemWorkflowExecutionReportArtifactSink.cs # IWorkflowExecutionReportArtifactSink 文件系统实现
    ├── WorkflowExecutionReportWriter.cs                 # JSON/HTML 报告生成
    └── WorkflowExecutionReportArtifactOptions.cs        # 配置：输出目录
```

## 核心组件

### WorkflowDefinitionFileLoader

从配置的目录列表中扫描 `*.yaml` / `*.yml` 文件，读取内容并注册到 `IWorkflowDefinitionRegistry`。文件名（去掉扩展名）作为 workflow 名称。

### WorkflowDefinitionBootstrapHostedService

`IHostedService` 实现。在宿主启动时调用 `WorkflowDefinitionFileLoader` 自动加载所有 workflow 定义。适用于需要开箱即用的部署场景。

### FileSystemWorkflowExecutionReportArtifactSink

`IWorkflowExecutionReportArtifactSink` 实现。接收 `WorkflowRunReport`，调用 `WorkflowExecutionReportWriter` 生成：
- **JSON 文件**：完整的结构化报告
- **HTML 文件**：带样式的可视化报告（步骤轨迹、时间线、角色回复、拓扑）

输出路径格式：`{OutputDirectory}/{workflowName}_{runId}_{timestamp}.{json|html}`

### WorkflowExecutionReportWriter

静态工具类，负责：
- `WriteJsonAsync`：将 `WorkflowRunReport` 序列化为 JSON
- `WriteHtmlAsync`：生成带内联 CSS 的 HTML 报告页面

## 配置

### 报告工件

`WorkflowExecutionReportArtifacts` section：

| 选项 | 默认 | 说明 |
|------|------|------|
| `OutputDirectory` | `artifacts/workflow-executions` | 报告输出目录 |

### Workflow 文件源

`WorkflowDefinitionFileSource` section：

| 选项 | 默认 | 说明 |
|------|------|------|
| `WorkflowDirectories` | `[]` | workflow YAML 扫描目录列表 |

## DI 入口

```csharp
// 注册报告落盘（Replace Application 层的 Noop 默认）
services.AddWorkflowInfrastructure(opt =>
{
    opt.OutputDirectory = "artifacts/workflow-executions";
});

// 注册 workflow 文件源 + 启动自动加载
services.AddWorkflowDefinitionFileSource(opt =>
{
    opt.WorkflowDirectories.Add("workflows/");
    opt.WorkflowDirectories.Add(Path.Combine(homeDir, ".aevatar/workflows"));
});
```

`AddWorkflowInfrastructure` 使用 `Replace` 将 Application 层注册的 `NoopWorkflowExecutionReportArtifactSink` 替换为 `FileSystemWorkflowExecutionReportArtifactSink`。

## 分层边界

- 依赖 `Aevatar.Workflow.Application.Abstractions`（端口接口）
- 依赖 `Aevatar.Configuration`（配置模型）
- 不依赖 `Aevatar.Workflow.Application` 实现
- 不依赖 `Aevatar.Workflow.Core`
- 只做 IO 与启动装配，不承载业务逻辑

## 依赖

- `Aevatar.Workflow.Application.Abstractions`
- `Aevatar.Configuration`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
- `Microsoft.Extensions.Hosting.Abstractions`
- `Microsoft.Extensions.Logging.Abstractions`
- `Microsoft.Extensions.Options`
