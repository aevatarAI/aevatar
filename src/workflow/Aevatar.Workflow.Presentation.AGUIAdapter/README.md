# Aevatar.Workflow.Presentation.AGUIAdapter

工作流事件到 AGUI 协议的适配层。将内部 `EventEnvelope` 转换为 AG-UI 协议事件，再映射为领域中立的 `WorkflowRunEvent`，写入 run event sink 供上层消费。

## 目录结构

```
Aevatar.Workflow.Presentation.AGUIAdapter/
├── DependencyInjection/
│   └── ServiceCollectionExtensions.cs          # AddWorkflowExecutionAGUIAdapter()
├── EventEnvelopeToAGUIEventMapper.cs           # EventEnvelope -> AGUIEvent（handler chain）
├── AGUIEventToWorkflowRunEventMapper.cs        # AGUIEvent -> WorkflowRunEvent
└── WorkflowExecutionAGUIEventProjector.cs      # Projection 分支：写入 run event sink
```

## 映射链路

```
EventEnvelope
  -> IEventEnvelopeToAGUIEventMapper (handler chain，一对多)
     -> AGUIEvent[]
        -> AGUIEventToWorkflowRunEventMapper (一对一)
           -> WorkflowRunEvent
              -> IEventSink<WorkflowRunEvent>.PushAsync
```

## Handler Chain

`EventEnvelopeToAGUIEventMapper` 持有一组 `IAGUIEventEnvelopeMappingHandler`，按 `Order` 排序，依次尝试映射。每个 handler 专注一种事件类型：

| Handler | Order | 处理事件 | 输出 AGUI 事件 |
|---------|-------|----------|---------------|
| `StartWorkflowAGUIEventEnvelopeMappingHandler` | 0 | `StartWorkflowEvent` | `RunStartedEvent` |
| `StepRequestAGUIEventEnvelopeMappingHandler` | 10 | `StepRequestEvent` | `StepStartedEvent` + `CustomEvent` |
| `StepCompletedAGUIEventEnvelopeMappingHandler` | 20 | `StepCompletedEvent` | `StepFinishedEvent` |
| `AITextStreamAGUIEventEnvelopeMappingHandler` | 30 | `TextMessageStart/Content/End/ChatResponse` | `TextMessageStart/Content/EndEvent` |
| `WorkflowCompletedAGUIEventEnvelopeMappingHandler` | 40 | `WorkflowCompletedEvent` | `RunFinishedEvent` 或 `RunErrorEvent` |
| `ToolCallAGUIEventEnvelopeMappingHandler` | 50 | `ToolCallEvent`/`ToolResultEvent` | `ToolCallStart/EndEvent` |

## WorkflowExecutionAGUIEventProjector

作为 Projection Pipeline 的一个分支，实现 `IProjectionProjector`。职责：

1. 收到 `EventEnvelope` 后调用 mapper 转换为 AGUI 事件
2. 再经 `AGUIEventToWorkflowRunEventMapper` 转为 `WorkflowRunEvent`
3. 写入 run event sink

容错策略：
- `EventSinkBackpressureException`：丢弃当前事件，保持 sink 连接
- `EventSinkCompletedException` / `InvalidOperationException`：断开 sink，停止后续推送

## OCP 扩展

新增 AGUI 事件映射：

1. 实现 `IAGUIEventEnvelopeMappingHandler`，设定 `Order`
2. DI 注册：`services.TryAddEnumerable(ServiceDescriptor.Singleton<IAGUIEventEnvelopeMappingHandler, MyHandler>())`

无需修改核心 mapper 或 projector。

## DI 入口

```csharp
services.AddWorkflowExecutionAGUIAdapter();
```

注册内容：
- `IEventEnvelopeToAGUIEventMapper`（组合 mapper）
- 6 个默认 handler（`StartWorkflow`/`StepRequest`/`StepCompleted`/`AITextStream`/`WorkflowCompleted`/`ToolCall`）

## 分层边界

- 依赖 `Aevatar.Presentation.AGUI`（AGUI 协议定义）与 `Aevatar.Workflow.Projection`（投影上下文）
- 不承载 Host endpoint 逻辑
- 不包含应用层用例编排
- 不直接依赖 `Aevatar.Workflow.Application`

## 依赖

- `Aevatar.AI.Abstractions`
- `Aevatar.CQRS.Projection.Abstractions`
- `Aevatar.Workflow.Projection`
- `Aevatar.Foundation.Abstractions`
- `Aevatar.Presentation.AGUI`
- `Aevatar.Workflow.Core`
- `Microsoft.Extensions.DependencyInjection.Abstractions`
