# Aevatar.AI.Core

`Aevatar.AI.Core` 是 Aevatar 的 AI 运行内核，负责 Role/LLM/Tool 的 typed 执行链。

## 职责

- 提供 `AIGAgentBase<TState>`
- 提供 `RoleGAgent`
- 管理 LLM 请求构建、聊天历史、Tool loop
- 暴露 AI 事件协议（`ai_messages.proto`）
- 提供 AI 层 typed middleware / hook 扩展点

## 核心类型

- `AIGAgentBase<TState>`：AI Agent 基类
- `RoleGAgent`：标准对话角色 Agent
- `RoleGAgentFactory`：把 YAML/配置归一化为 `InitializeRoleAgentEvent`
- `ChatRuntime`：聊天历史与请求构建
- `ToolManager` / `ToolCallLoop`：工具调用管理

## Role 初始化语义

`RoleGAgent` 只保留强类型初始化链路。

`InitializeRoleAgentEvent` 当前承载：

- `role_name`
- `provider_name`
- `model`
- `system_prompt`
- `temperature`
- `max_tokens`
- `max_tool_rounds`
- `max_history_messages`
- `stream_buffer_capacity`

已删除旧的 role 内部字符串路由配置。

所以 Role YAML 现在表达的是“角色执行参数”，不是“内部业务管线配置”。

## 扩展方式

AI 扩展不再借用 Foundation 的旧事件模块机制，而是走 AI 自己的 typed seam：

- `IAgentRunMiddleware`
- `ILLMCallMiddleware`
- `IToolCallMiddleware`
- `IAIGAgentExecutionHook`

这些扩展点适合处理：

- 调用前后观测
- tracing / logging / metrics
- request/response 变换
- tool / LLM 调用治理

如果能力需要跨事件、跨 reactivation 持有业务事实，就不应塞进 Role 内部 middleware，而应升级为 actor + state。

## 设计原则

- Role 负责 AI 执行语义，不负责 workflow 业务编排
- 业务流程控制应由 workflow 显式步骤表达
- 横切扩展走 middleware / hook
- 持久业务事实走 actor state

一句话：

`Aevatar.AI.Core` 提供的是 typed AI execution pipeline，不再提供可热插拔的 role 内部事件模块体系。
