# Aevatar.Hosts.Api

`Aevatar.Hosts.Api` 是面向应用侧的极简 HTTP 层，提供“只关心 chat”的统一入口。

## 职责

- 暴露 `/api/chat`（SSE）与 `/api/ws/chat`（WebSocket）对话入口
- 创建或复用 `WorkflowGAgent`
- 启动同一条 WorkflowExecution 投影链路，并把 Actor 事件投影为 AG-UI 实时事件
- 提供 Agent 列表和工作流列表查询端点
- 可选提供 CQRS 读侧查询端点（run read model）

## 关键端点

- `POST /api/chat`：触发工作流执行并返回 SSE
- `GET /api/ws/chat`：WebSocket 命令通道（发送 `chat.command`，服务端异步推送 AG-UI 事件与 `query.result`）
- `GET /api/agents`：列出活跃 Agent
- `GET /api/workflows`：列出可用 workflow 名称
- `GET /api/runs`：列出最近 run 投影摘要（启用 projection 时）
- `GET /api/runs/{runId}`：查询单次 run 的完整读模型（启用 projection 时）

## 核心组件

- `Endpoints/ChatEndpoints.cs`：API 入口与请求处理
- `WorkflowExecutionProjectionService`：CQRS 投影门面（run 生命周期 + query）
- `WorkflowExecutionAGUIEventProjector`：作为 WorkflowExecution 投影链路中的 live-output projector，把同一 `EventEnvelope` 流映射到 AGUI 事件
- `WorkflowExecutionProjectionContextAGUIExtensions`：在 run context 上挂载/读取请求级 `IAGUIEventSink`
- `Projection/EventEnvelopeToAGUIEventMapper.cs`：纯映射函数（`EventEnvelope` -> `AGUIEvent`）
- `IActorStreamSubscriptionHub<EventEnvelope>`（来自 `Aevatar.CQRS.Projections`）：按 actor 统一复用底层 stream 订阅并分发到会话回调
- `Aevatar.CQRS.Projections.Abstractions`：CQRS 契约与读模型定义（供查询与 reporting 复用）
- `Aevatar.CQRS.Projections`：读模型存储、projector、coordinator、event reducer 与 DI 组合
- `Workflows/WorkflowRegistry.cs`：workflow YAML 注册与发现
- `Program.cs`：Runtime、Cognitive 模块、CORS 与端点装配
- `Endpoints/ChatEndpoints.cs` 中 `StartProjectionRunAsync/FinalizeProjectionRunAsync`：SSE 与 WebSocket 共用同一套投影编排步骤（启动/等待完成/收尾）

详细设计见 `src/Aevatar.CQRS.Projections/README.md`。

`WorkflowExecutionProjection` 配置节可控制是否启用投影、查询端点和报告落盘。

## 默认装配

`Program.cs` 默认装配以下链路：

```csharp
builder.Services.AddWorkflowExecutionProjectionCQRS(...);
builder.Services.AddWorkflowExecutionProjectionProjector<WorkflowExecutionAGUIEventProjector>();
```

即：读模型 projector（CQRS 项目内置）与 AGUI projector（API 宿主扩展）在同一 coordinator 下并行执行。

## 依赖

- `Aevatar.Foundation.Runtime`
- `Aevatar.Workflows.Core`
- `Aevatar.Presentation.AGUI`
- `Aevatar.AI.LLMProviders.MEAI`
