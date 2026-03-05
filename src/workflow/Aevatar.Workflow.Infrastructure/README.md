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

- `AddWorkflowInfrastructure(...)`
  - 注册报告工件 sink。
- `AddWorkflowDefinitionFileSource(...)`
  - 注册 workflow 文件源与启动加载 HostedService。
- `AddWorkflowCapability(IServiceCollection, IConfiguration)`
  - 能力一键组合（Application + Projection + AGUIAdapter + Infrastructure + workflow 文件源）。
  - 不负责具体 ReadModel Provider 注册（Provider 组合下沉到 Host/Extensions 层）。
- `AddWorkflowCapability(WebApplicationBuilder)`
  - Host 侧一行接入 Workflow 能力（服务注册 + 能力端点声明）。
- `Aevatar.Workflow.Extensions.Hosting.AddWorkflowCapabilityWithAIDefaults(WebApplicationBuilder)`
  - 在 Host 入口统一装配 Workflow capability + AI features + AI projection extension（推荐用于生产组合入口）。
  - 默认同时注册 Workflow 读模型 Provider（InMemory/Elasticsearch/Neo4j）。
- `MapWorkflowCapabilityEndpoints(...)`
  - 将 Workflow 能力 API 端点挂载到 Host（默认由 `UseAevatarDefaultHost()` 自动调用能力映射链路）。

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
| `DuplicatePolicy` | `Throw` | 重名处理策略：`Throw` / `Skip` / `Override` |

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
    opt.DuplicatePolicy = WorkflowDefinitionDuplicatePolicy.Override;
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
