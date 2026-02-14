# Aevatar.Hosts.Api

`Aevatar.Hosts.Api` 是面向应用侧的极简 HTTP 层，提供“只关心 chat”的统一入口。

## 职责

- 暴露 `/api/chat` 对话入口（SSE）
- 创建或复用 `WorkflowGAgent`
- 把 Actor 事件投影为 AG-UI 事件并流式返回
- 提供 Agent 列表和工作流列表查询端点
- 可选提供 CQRS 投影读侧查询端点（run read model）

## 关键端点

- `POST /api/chat`：触发工作流执行并返回 SSE
- `GET /api/ws/chat`：WebSocket 命令通道（发送 `chat.command`，服务端异步推送 AG-UI 事件与 `query.result`）
- `GET /api/agents`：列出活跃 Agent
- `GET /api/workflows`：列出可用 workflow 名称
- `GET /api/runs`：列出最近 run 投影摘要（启用 projection 时）
- `GET /api/runs/{runId}`：查询单次 run 的完整读模型（启用 projection 时）

## 核心组件

- `Endpoints/ChatEndpoints.cs`：API 入口与请求处理
- `Projection/AgUiProjector.cs`：`EventEnvelope` -> `AgUiEvent` 投影
- `Aevatar.Cqrs.Projections.Abstractions`：CQRS 契约与读模型定义（供查询与 reporting 复用）
- `Aevatar.Cqrs.Projections`：读模型存储、projector、coordinator、event reducer 与 DI 组合
- `Workflows/WorkflowRegistry.cs`：workflow YAML 注册与发现
- `Program.cs`：Runtime、Cognitive 模块、CORS 与端点装配

详细设计见 `docs/CQRS_PROJECTION_ARCHITECTURE.md`。

`ChatProjection` 配置节可控制是否启用投影、查询端点和报告落盘。

## 依赖

- `Aevatar.Foundation.Runtime`
- `Aevatar.Workflows.Core`
- `Aevatar.Presentation.AGUI`
- `Aevatar.AI.LLMProviders.MEAI`
