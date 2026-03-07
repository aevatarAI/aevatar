# Aevatar.Workflow.Application.Abstractions

工作流应用层稳定契约。`Host`、`Infrastructure`、`Presentation` 层均只依赖此项目，不直接依赖 `Application` 实现。

## 目录结构

```
Aevatar.Workflow.Application.Abstractions/
├── Runs/
│   ├── IWorkflowChatRunApplicationService.cs  # run 用例主入口
│   ├── WorkflowChatRunModels.cs               # Request/Result/OutputFrame 等契约
│   └── WorkflowRunEventContracts.cs           # WorkflowRunEvent 体系 + Sink + Channel
├── Queries/
│   ├── IWorkflowExecutionQueryApplicationService.cs  # 查询门面
│   └── WorkflowExecutionQueryModels.cs               # Summary/Report/StepTrace 等查询模型
├── Workflows/
│   └── IWorkflowDefinitionCatalog.cs         # definition catalog + lookup contract
├── Reporting/
│   └── IWorkflowExecutionReportArtifactSink.cs # 报告工件落盘端口
└── Projections/
    └── IWorkflowExecutionProjectionPort.cs     # 投影生命周期端口 + ProjectionSession
```

## 关键契约

### Run 用例

| 类型 | 说明 |
|------|------|
| `IWorkflowChatRunApplicationService` | `ExecuteAsync` 单入口，接收 request + emit 回调 |
| `WorkflowChatRunRequest` | 请求：prompt、workflow、workflowYamls、agentId |
| `WorkflowChatRunExecutionResult` | 结果：包含 start error、started info、finalize result |
| `WorkflowOutputFrame` | 输出帧：type + payload，用于 SSE 流 |
| `WorkflowChatRunStartError` | 启动错误枚举 |

### Run 事件体系

| 类型 | 说明 |
|------|------|
| `WorkflowRunEvent` | 基类，所有 run 输出事件继承 |
| `WorkflowRunStartedEvent` | run 开始 |
| `WorkflowRunFinishedEvent` | run 正常结束 |
| `WorkflowRunErrorEvent` | run 异常结束 |
| `WorkflowRunStepStartedEvent` | 步骤开始 |
| `WorkflowRunStepFinishedEvent` | 步骤完成 |
| `WorkflowRunTextMessageStartEvent` | LLM 文本流开始 |
| `WorkflowRunTextMessageContentEvent` | LLM 文本流片段 |
| `WorkflowRunTextMessageEndEvent` | LLM 文本流结束 |
| `WorkflowRunToolCallStartEvent` | 工具调用开始 |
| `WorkflowRunToolCallEndEvent` | 工具调用结束 |
| `IEventSink<WorkflowRunEvent>` | 事件推送端口（push/complete/dispose） |
| `EventChannel<WorkflowRunEvent>` | Channel-based 实现，支持背压 |

### 查询模型

| 类型 | 说明 |
|------|------|
| `IWorkflowExecutionQueryApplicationService` | 查询门面 |
| `WorkflowRunSummary` | run 摘要（列表用） |
| `WorkflowRunReport` | run 完整报告 |
| `WorkflowRunStepTrace` | 步骤执行轨迹 |
| `WorkflowRunRoleReply` | 角色回复记录 |
| `WorkflowRunTimelineEvent` | 时间线事件 |
| `WorkflowRunTopologyEdge` | Agent 拓扑边 |

### 投影端口

| 类型 | 说明 |
|------|------|
| `IWorkflowExecutionProjectionPort` | 投影生命周期：Start/WaitForCompletion/Complete + 查询 |
| `WorkflowProjectionSession` | run 级 projection session |
| `WorkflowProjectionCompletionStatus` | 投影完成状态枚举 |

## 设计目标

- **稳定端口优先**：Host 与 Infrastructure 只依赖本项目的接口与模型
- **依赖反转**：避免基础设施直接依赖应用层实现程序集
- **可替换实现**：应用层、落盘策略、workflow definition catalog 都可独立替换

## 依赖

本项目无 ProjectReference，仅使用 .NET 标准类型。
