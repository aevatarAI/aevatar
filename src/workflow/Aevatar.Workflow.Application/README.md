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

### WorkflowChatRunApplicationService

`IWorkflowChatRunApplicationService` 唯一实现。`ExecuteAsync` 是对外单入口，内部编排：

```
ExecuteAsync(request, emitAsync)
  1. CreateRunContextAsync
     -> WorkflowRunActorResolver: 解析 workflow YAML、创建/复用 WorkflowGAgent
     -> WorkflowExecutionRunOrchestrator.StartAsync: 启动投影 run
  2. ProcessEnvelopeAsync (后台)
     -> WorkflowRunRequestExecutor: 投递 ChatRequestEvent 到 actor
  3. WorkflowRunOutputStreamer.StreamAsync
     -> 读 WorkflowRunEventChannel -> 映射 WorkflowOutputFrame -> emitAsync 回调
  4. FinalizeAsync
     -> WorkflowExecutionRunOrchestrator: 等待投影收敛、获取拓扑、生成报告
  5. PersistReportBestEffortAsync
     -> IWorkflowExecutionReportArtifactSink: 落盘（best-effort）
  finally:
     -> 异常时 AbortCoreAsync: rollback + dispose
```

### WorkflowExecutionRunOrchestrator

投影 run 生命周期：
- `StartAsync`：通过 `IWorkflowExecutionProjectionPort` 创建 projection session
- `FinalizeAsync`：等待投影完成信号（含 grace timeout 重试）、读取拓扑、拼装报告
- `RollbackAsync`：异常时清理 projection session

### WorkflowRunOutputStreamer

从 `IWorkflowRunEventSink` 读取事件流，经 `WorkflowOutputFrameMapper` 映射为 `WorkflowOutputFrame`，遇到 `RUN_FINISHED` 或 `RUN_ERROR` 终止事件时停止。

### WorkflowExecutionQueryApplicationService

查询门面，经 `IWorkflowExecutionProjectionPort` 提供：
- `ListAgentsAsync`：列出已创建的 workflow actor
- `ListWorkflowsAsync`：列出已注册 workflow 定义
- `ListRunsAsync` / `GetRunAsync`：查询 run 历史与详情

### WorkflowDefinitionRegistry

内存注册表，维护 `workflow 名称 -> YAML` 映射。支持内置 `direct` workflow（单步 LLM 问答）。由 `Infrastructure` 层的文件加载器在启动时填充。

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
