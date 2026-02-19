# 标识符关系（EventEnvelope.Id / RunId / MessageId / ActorId）

本文说明当前实现中几个常见标识符的职责边界与流转关系。

## 一句话结论

| 标识符 | 作用域 | 谁生成 | 主要用途 |
|---|---|---|---|
| `EventEnvelope.Id` | 单条事件 | 事件发布方 | 去重、追踪单条消息 |
| `RunId` | 一次 workflow 执行 | `WorkflowChatRunApplicationService` | 关联同一次运行的多条事件与读模型 |
| `MessageId` | 一次 AI 消息链路（或步骤消息链路） | 服务端内部 | 给 AI 消息链路分段，不等于 run |
| `ActorId` | 一个 Actor 实例 | Runtime | 订阅粒度、线程/会话归属（`threadId`） |

## 生成点（代码）

- `RunId` 生成：`src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs`
  - `GenerateRunId()` 生成 runId，并用于创建 `WorkflowExecutionGAgent`（`executionActorId = runId`）
- `RunId` 注入投影会话：`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs`
  - `StartAsync(..., runId)` 接收应用层传入的 runId
- `RunId` 注入请求：
  - `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRequestEnvelopeFactory.cs`
  - 写入 `ChatRequestEvent.Metadata["run_id"]`
- `WorkflowExecutionGAgent`：
  - `src/workflow/Aevatar.Workflow.Core/WorkflowExecutionGAgent.cs`
  - `BindWorkflowAgentId(...)` 显式绑定长期 `WorkflowGAgent`
  - `ConfigureWorkflow(...)` 注入运行期 workflow 快照
- `MessageId`：
  - 请求入口会生成内部会话：`chat-{guid}`
  - 步骤级会话常用 `runId:stepId`（`:attempt` 预留，见 `src/Aevatar.AI.Abstractions/ChatMessageKeys.cs`）

## 流程图（执行链路）

```mermaid
sequenceDiagram
    participant Client
    participant Api as HostApi
    participant App as WorkflowApp
    participant Workflow as WorkflowGAgent
    participant Execution as WorkflowExecutionGAgent
    participant Projection as WorkflowExecutionProjectionService

    Client->>Api: POST /api/chat
    Api->>App: ExecuteAsync(prompt, workflow, agentId)
    App->>Workflow: ResolveOrCreate(workflow agent)
    App->>App: Generate RunId
    App->>Execution: Create(actorId = RunId)
    App->>Execution: BindWorkflowAgentId(workflowAgentId)
    App->>Execution: ConfigureWorkflow(workflowYaml, workflowName)
    App->>Workflow: LinkAsync(workflowAgentId, executionActorId)
    App->>Projection: StartAsync(rootActorId = RunId, runId = RunId)
    App->>Execution: ChatRequestEvent(metadata.run_id = RunId)
    Execution->>Execution: 发布 StartWorkflowEvent(runId)
```

## 流程图（投影识别 run）

```mermaid
flowchart LR
    A["Actor Stream(EventEnvelope)"] --> B["ProjectionSubscriptionRegistry 按 ActorId 分发"]
    B --> C["WorkflowExecutionReadModelProjector"]
    C --> D["RunIdResolver[] 解析 runId"]
    D --> E["EnableRunEventIsolation=false: actor_shared"]
    D --> F["EnableRunEventIsolation=true: 仅当前 run"]
```

## 为什么 `RunId` 不能等于 `EventEnvelope.Id`

- 一次 run 会产生多条 `EventEnvelope`（开始、步骤请求、步骤完成、流式消息、结束）。
- `EventEnvelope.Id` 必须保持“单事件唯一”；如果复用为 run，会丢失事件级去重与追踪能力。
- 因此：`RunId` 是“会话/执行级”，`EventEnvelope.Id` 是“消息级”。

## 客户端契约（当前）

- 客户端只传：`prompt`、`workflow`、`agentId?`。
- `RunId` 与内部 `MessageId` 都由服务端生成和管理。
- 查询读模型用 `runId`（`GET /api/runs/{runId}`）；实时流输出统一 `threadId=executionActorId=runId`。
