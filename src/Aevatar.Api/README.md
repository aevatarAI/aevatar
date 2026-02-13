# Aevatar.Api

`Aevatar.Api` 是面向应用侧的极简 HTTP 层，提供“只关心 chat”的统一入口。

## 职责

- 暴露 `/api/chat` 对话入口（SSE）
- 创建或复用 `WorkflowGAgent`
- 把 Actor 事件投影为 AG-UI 事件并流式返回
- 提供 Agent 列表和工作流列表查询端点

## 关键端点

- `POST /api/chat`：触发工作流执行并返回 SSE
- `GET /api/agents`：列出活跃 Agent
- `GET /api/workflows`：列出可用 workflow 名称

## 核心组件

- `Endpoints/ChatEndpoints.cs`：API 入口与请求处理
- `Projection/AgUiProjector.cs`：`EventEnvelope` -> `AgUiEvent` 投影
- `Workflows/WorkflowRegistry.cs`：workflow YAML 注册与发现
- `Program.cs`：Runtime、Cognitive 模块、CORS 与端点装配

## 依赖

- `Aevatar.Runtime`
- `Aevatar.Cognitive`
- `Aevatar.AGUI`
- `Aevatar.AI.MEAI`
