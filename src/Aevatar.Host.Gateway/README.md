# Aevatar.Host.Gateway

`Aevatar.Host.Gateway` 是集成装配宿主，负责把配置、AI Provider、MCP、Skills 与 Runtime 组装为可运行服务。

## 职责

- 加载 `~/.aevatar` 配置
- 注册 Aevatar Runtime 与 Cognitive 模块
- 可选装配 LLM Provider（MEAI / LLMTornado）
- 可选装配 MCP Tools 与 Skills
- 提供最小健康检查端点（`GET /`）

## 主要文件

- `Program.cs`：组合式 DI 注册入口

## 与 Host.Api 的关系

- `Aevatar.Workflow.Host.Api` 是工作流 Chat 协议入口（SSE/WS/CQRS 查询）。
- `Aevatar.Host.Gateway` 不再暴露并行的 Chat 协议端点，避免协议语义漂移。

## 依赖

- `Aevatar.Foundation.Runtime`
- `Aevatar.Workflow.Core`
- `Aevatar.Configuration`
- `Aevatar.AI.LLMProviders.MEAI`
- `Aevatar.AI.LLMProviders.Tornado`
- `Aevatar.AI.ToolProviders.MCP`
- `Aevatar.AI.ToolProviders.Skills`
