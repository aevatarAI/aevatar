# Aevatar.Workflow.Application

工作流应用层：承载 run 用例编排（启动/执行/流式输出/收敛/回滚）与查询门面，不做协议适配与基础设施细节。

## 目录结构

```
Aevatar.Workflow.Application/
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs    # AddWorkflowApplication()
├── Runs/                                 # run 用例编排
│   ├── WorkflowChatRunApplicationService.cs  # 主入口：ExecuteAsync
│   ├── WorkflowRunActorResolver.cs           # 解析/创建 workflow actor
│   ├── WorkflowChatRequestEnvelopeFactory.cs # 构造 ChatRequestEvent 信封
│   ├── WorkflowRunRequestExecutor.cs         # 投递请求事件 + 异常补偿
│   ├── WorkflowRunOutputStreamer.cs           # 读取 run 事件 -> WorkflowOutputFrame
│   ├── WorkflowOutputFrameMapper.cs          # WorkflowRunEvent -> WorkflowOutputFrame
│   ├── WorkflowRunContext.cs                 # run 内部上下文
│   ├── IWorkflowRunActorResolver.cs
│   ├── IWorkflowRunRequestExecutor.cs
│   ├── IWorkflowRunOutputStreamer.cs
│   └── IWorkflowChatRequestEnvelopeFactory.cs
├── Orchestration/                        # 投影 run 生命周期
│   ├── WorkflowExecutionRunOrchestrator.cs       # start/finalize/rollback
│   ├── WorkflowExecutionTopologyResolver.cs      # 读取 actor 拓扑
│   ├── WorkflowRunOrchestrationOptions.cs        # 编排选项
│   ├── IWorkflowExecutionRunOrchestrator.cs
│   └── IWorkflowExecutionTopologyResolver.cs
├── Queries/
│   └── WorkflowExecutionQueryApplicationService.cs # agents/workflows/runs 查询门面
├── Workflows/
│   ├── WorkflowDefinitionRegistry.cs     # 名称 -> YAML 内存注册表
│   └── WorkflowDefinitionRegistryOptions.cs
└── Reporting/
    └── NoopWorkflowExecutionReportArtifactSink.cs  # 默认空实现
```

## 核心服务

- `WorkflowChatRunApplicationService`
  - `ExecuteAsync` 单入口：参数校验 + 获取 run context + 委托执行引擎。
- `WorkflowRunContextFactory`
  - 负责 actor 解析、command context 构造、projection lease 初始化与 live sink attach。
- `WorkflowRunExecutionEngine`
  - 负责请求执行、输出泵送、终态收敛与最终资源回收触发。
- `WorkflowRunCompletionPolicy`
  - 负责输出帧终态判定（`RUN_FINISHED` / `RUN_ERROR`）。
- `WorkflowRunResourceFinalizer`
  - 负责 `detach/release/complete/dispose` 兜底清理。
- `WorkflowRunActorResolver`
  - 无 `actorId` 时创建并绑定 workflow actor。
  - 有 `actorId` 时仅复用既有 actor，不负责切换 workflow。
- `WorkflowRunRequestExecutor`
  - 投递请求事件并处理异常补偿。
- `WorkflowRunOutputStreamer`
  - 读取 run 事件并映射 `WorkflowOutputFrame`。
- `WorkflowExecutionQueryApplicationService`
  - `agents/workflows/runs` 查询门面（经 `IWorkflowExecutionProjectionQueryPort` 读取读侧模型）。
  - `ListAgentsAsync` 仅返回 `WorkflowGAgent`，不扫描暴露非 Workflow actor。
- `WorkflowDefinitionRegistry`
  - 维护 workflow 名称到 YAML 的内存注册表。

## 分层约束

- 本层不依赖 Presentation 协议实现（AGUI/SSE/WS）
- 本层不包含文件系统扫描逻辑
- 报告落盘通过 `IWorkflowExecutionReportArtifactSink` 端口交给 Infrastructure
- 默认注册 `NoopWorkflowExecutionReportArtifactSink`，Infrastructure 可 Replace 为真实实现

## DI 入口

```csharp
services.AddWorkflowApplication(
    configureRegistry: opt => opt.RegisterBuiltInDirectWorkflow = true,
    configureOrchestration: opt => opt.RunProjectionFinalizeGraceTimeoutMs = 3000);
```

注册内容：
- `IWorkflowChatRunApplicationService`
- `IWorkflowExecutionQueryApplicationService`
- `IWorkflowDefinitionRegistry`
- `IWorkflowExecutionRunOrchestrator` + `IWorkflowExecutionTopologyResolver`
- `IWorkflowRunActorResolver`、`IWorkflowRunRequestExecutor`、`IWorkflowRunOutputStreamer`
- `IWorkflowChatRequestEnvelopeFactory`
- `IWorkflowExecutionReportArtifactSink`（Noop 默认）

## 依赖

- `Aevatar.Workflow.Application.Abstractions`
- `Aevatar.Workflow.Core`
- `Aevatar.AI.Abstractions`、`Aevatar.Foundation.Abstractions`
- `Google.Protobuf`
- `Microsoft.Extensions.DependencyInjection.Abstractions`、`Microsoft.Extensions.Logging.Abstractions`
