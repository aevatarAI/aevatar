# Aevatar.AI.Core

`Aevatar.AI.Core` 是 Aevatar 的 AI 能力核心层，提供 AI Agent 基类、LLM 抽象、Tool Calling 和 Hook 管线。

## 职责

- 提供 AI Agent 基类 `AIGAgentBase<TState>`
- 定义统一 LLM 抽象：`ILLMProvider`、`ILLMProviderFactory`
- 管理工具系统：`IAgentTool`、`ToolManager`、`ToolCallLoop`
- 管理会话历史与请求构建：`ChatHistory`、`ChatRuntime`
- 提供 AI 事件与消息协议（`ai_messages.proto`）
- 提供角色型 Agent：`RoleGAgent` 及 YAML 配置工厂

## 核心类型

- `AIGAgentBase<TState>`：组合 ChatRuntime + ToolManager + Hooks
- `RoleGAgent`：面向对话场景的默认 AI Agent 实现
- `RoleGAgentFactory`：从 YAML 配置角色 Prompt/Provider/模块
- `Routing/*`：AI 层事件路由规则与模块过滤包装
- `Hooks/*`：AI 层 Hook 管线与内置 Hook
- `LLM/*`：跨 Provider 的请求/响应模型

## AI Routing（EventRoutes）

`Aevatar.AI.Core` 里有一套“模块级路由”，用于控制**哪些事件可以进入哪些 EventModule**。  
这套路由配置在 `RoleGAgentFactory` 中生效，不是 Runtime 的 Actor 层级路由。

### 相关类型

- `EventRoute`：路由规则模型与解析器
- `RoutedEventModule`：对 `IEventModule` 的过滤包装器
- `IEventRouteEvaluator`：从 `EventEnvelope` 提取匹配字段（默认支持 `event.type`）

### 生效机制

1. `RoleGAgentFactory` 根据 `event_modules` 创建模块
2. 解析 `extensions.event_routes`
3. 对非 `IRouteBypassModule` 的模块，用 `RoutedEventModule` 包装
4. 运行时匹配通过的事件才进入目标模块

### 支持的匹配条件

- `event.type == "ChatRequestEvent"`
- `event.step_type == "llm_call"`（需要 evaluator 提供 step_type 解析）

### YAML 示例

```yaml
name: researcher
extensions:
  event_modules: "llm_handler,tool_handler"
  event_routes: |
    - when: event.type == "ChatRequestEvent"
      to: llm_handler
    - when: event.step_type == "tool_call"
      to: tool_handler
```

## 设计特点

- 通过接口隔离具体 LLM 供应商实现
- Tool Calling 循环与对话历史独立组件化
- 支持 Hook 双通道（Foundation 事件钩子 + AI 调用钩子）

## 依赖

- `Aevatar.Foundation.Core`
- `Microsoft.Extensions.AI`
- `Google.Protobuf` / `Grpc.Tools`
- `YamlDotNet`
- `Microsoft.Extensions.*.Abstractions`
