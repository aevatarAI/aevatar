# Aevatar.Workflow.Presentation.AGUIAdapter

工作流运行时 envelope 到 workflow run-event stream 的适配层。将内部 `EventEnvelope` 转换为领域中立的 `WorkflowRunEventEnvelope`，写入 run event sink 供上层消费。

语义边界：

- 这里输入的是 runtime `EventEnvelope`，不是 Event Sourcing 的持久化领域事件记录。
- AGUIAdapter 处理的是运行时消息投影，不直接读取 Actor state，也不直接消费 EventStore。

## 目录结构

```
Aevatar.Workflow.Presentation.AGUIAdapter/
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs          # AddWorkflowExecutionAGUIAdapter()
├── EventEnvelopeToWorkflowRunEventMapper.cs    # EventEnvelope -> WorkflowRunEventEnvelope（handler chain）
└── WorkflowExecutionRunEventProjector.cs       # Projection 分支：写入 run event sink
```

## 映射链路

```
EventEnvelope
  -> IEventEnvelopeToWorkflowRunEventMapper (handler chain，一对多)
     -> WorkflowRunEventEnvelope[]
        -> IEventSink<WorkflowRunEventEnvelope>.PushAsync
```

## Handler Chain

`EventEnvelopeToWorkflowRunEventMapper` 持有一组 `IWorkflowRunEventEnvelopeMappingHandler`，按 `Order` 排序，依次尝试映射。每个 handler 专注一种事件类型：

| Handler | Order | 处理事件 | 输出 run-event |
|---------|-------|----------|---------------|
| `WorkflowRunExecutionStartedEnvelopeMappingHandler` | -10 | `WorkflowRunExecutionStartedEvent` | runtime bookkeeping，不输出 |
| `StartWorkflowRunEventEnvelopeMappingHandler` | 0 | `StartWorkflowEvent` | `RunStarted` |
| `StepRequestRunEventEnvelopeMappingHandler` | 10 | `StepRequestEvent` | `StepStarted` + `Custom` |
| `StepCompletedRunEventEnvelopeMappingHandler` | 20 | `StepCompletedEvent` | `StepFinished` + `Custom` |
| `AITextStreamRunEventEnvelopeMappingHandler` | 30 | `TextMessageStart/Content/End`、`ChatResponse`、`MediaContentEvent`、`WorkflowLlmStreamChunkEvent.DeltaContent` | `TextMessageStart/Content/End` 或 `Custom` |
| `AIReasoningRunEventEnvelopeMappingHandler` | 35 | `ReasoningContentEvent`、`WorkflowLlmStreamChunkEvent.DeltaReasoningContent` | `Custom` |
| `WorkflowCompletedRunEventEnvelopeMappingHandler` | 40 | `WorkflowCompletedEvent` | `RunFinished` 或 `RunError` |
| `WorkflowStoppedRunEventEnvelopeMappingHandler` | 45 | `WorkflowStoppedEvent` | `RunFinished` 或 `RunError` |
| `ToolCallRunEventEnvelopeMappingHandler` | 50 | `ToolCallEvent`/`ToolResultEvent` | `ToolCallStart/End` |
| `WorkflowSuspendedRunEventEnvelopeMappingHandler` | 60 | `WorkflowSuspendedEvent` | `Custom` |
| `WorkflowWaitingSignalRunEventEnvelopeMappingHandler` | 70 | `WorkflowWaitingSignalEvent` | `Custom` |
| `WorkflowSignalBufferedRunEventEnvelopeMappingHandler` | 80 | `WorkflowSignalBufferedEvent` | `Custom` |

## WorkflowExecutionRunEventProjector

作为 Projection Pipeline 的一个分支，继承 `ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, WorkflowRunEventEnvelope>`。职责：

1. 收到 `EventEnvelope` 后调用 mapper 转换为 `WorkflowRunEventEnvelope`
2. 使用 projection session command id 作为优先路由键，缺失时回退到 `EventEnvelope.Propagation.CorrelationId`
3. 通过 `ProjectionSessionEventHub<WorkflowRunEventEnvelope>` 写入 run event stream

容错策略：
- `EventSinkBackpressureException`：丢弃当前事件，保持 sink 连接
- `EventSinkCompletedException` / `InvalidOperationException`：断开 sink，停止后续推送

## OCP 扩展

新增 workflow run-event 映射：

1. 实现 `IWorkflowRunEventEnvelopeMappingHandler`，设定 `Order`
2. DI 注册：`services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowRunEventEnvelopeMappingHandler, MyHandler>())`

无需修改核心 mapper 或 projector。

## DI 入口

```csharp
services.AddWorkflowExecutionAGUIAdapter();
```

注册内容：
- `IEventEnvelopeToWorkflowRunEventMapper`（组合 mapper）
- 默认 workflow run-event handler（`WorkflowRunExecutionStarted`/`StartWorkflow`/`StepRequest`/`StepCompleted`/`AITextStream`/`AIReasoning`/`WorkflowCompleted`/`WorkflowStopped`/`ToolCall`/`WorkflowSuspended`/`WorkflowWaitingSignal`/`WorkflowSignalBuffered`）

## 分层边界

- 依赖 `Aevatar.Workflow.Projection`（投影上下文）与 `Aevatar.Workflow.Application.Abstractions`（run-event 契约）
- 不承载 Host endpoint 逻辑
- 不包含应用层用例编排
- 不直接依赖 `Aevatar.Workflow.Application`

## 依赖

- `Aevatar.AI.Abstractions`
- `Aevatar.CQRS.Projection.Abstractions`
- `Aevatar.Workflow.Projection`
- `Aevatar.Foundation.Abstractions`
- `Aevatar.Workflow.Core`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
