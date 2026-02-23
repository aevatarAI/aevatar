# MAF-Inspired Framework Improvements

基于 Microsoft Agent Framework (MAF) 调研，对 Aevatar 框架进行 6 项改进，涵盖中间件管线、可观测性、人机交互、工具审批、编排模式和表达式语言。

## 变更概览

| 改进项 | 优先级 | 新增文件 | 修改文件 | 涉及层 |
|--------|--------|----------|----------|--------|
| Agent Middleware Pipeline | HIGH | 4 | 3 | AI.Abstractions / AI.Core |
| OpenTelemetry GenAI Conventions | HIGH | 4 | 1 | AI.Core / Foundation.Runtime |
| Human-in-the-Loop | MEDIUM | 3 | 5 | Workflow.Core / Projection / AGUI |
| Tool Approval Mechanism | MEDIUM | 1 | 1 | AI.Abstractions |
| Orchestration Patterns | LOW | 5 | 0 | Foundation.Core |
| Expression Language | LOW | 1 | 1 | Workflow.Core |

---

## 1. Agent Middleware Pipeline

### 问题
原有 Hook 系统 (`IAIGAgentExecutionHook`) 是 best-effort 执行，无法修改或拦截 Agent 输入输出。MAF 提供三层中间件（Agent Run / Function Calling / IChatClient），支持安全过滤、限流、内容审查等横切关注点。

### 方案
新增三层 Middleware 接口，与 Hook 系统共存（Hook 做观测，Middleware 做拦截）：

- `IAgentRunMiddleware` — 包裹整个 Agent Chat 执行，可修改用户输入、短路返回
- `IToolCallMiddleware` — 包裹每次 Tool 执行，可验证参数、覆盖结果、阻止调用
- `ILLMCallMiddleware` — 包裹每次 LLM 调用，可修改 messages、注入 system prompt、变换响应

中间件通过 DI 注册，遵循 ASP.NET Core 中间件模式（`next()` 递归链）。

补充约定：
- `ChatAsync` 与 `ChatStreamAsync` 均会进入 `IAgentRunMiddleware` 链。
- 流式路径中，真实的 `provider.ChatStreamAsync(...)` 会在 `ILLMCallMiddleware` 链的 `next()` 内执行，确保观测/限流/缓存等横切逻辑对流式调用同样生效。

### 新增文件
- `src/Aevatar.AI.Abstractions/Middleware/IAgentRunMiddleware.cs`
- `src/Aevatar.AI.Abstractions/Middleware/IToolCallMiddleware.cs`
- `src/Aevatar.AI.Abstractions/Middleware/ILLMCallMiddleware.cs`
- `src/Aevatar.AI.Core/Middleware/MiddlewarePipeline.cs`

### 修改文件
- `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs` — 集成 Tool + LLM 中间件
- `src/Aevatar.AI.Core/Chat/ChatRuntime.cs` — 集成 Agent Run + LLM 中间件
- `src/Aevatar.AI.Core/AIGAgentBase.cs` — 从 DI 收集中间件并注入 Runtime

### 使用示例

```csharp
// 注册安全过滤中间件
services.AddSingleton<IAgentRunMiddleware, ContentSafetyMiddleware>();
services.AddSingleton<IToolCallMiddleware, ToolAuditMiddleware>();
services.AddSingleton<ILLMCallMiddleware, PromptInjectionGuardMiddleware>();
```

---

## 2. OpenTelemetry GenAI Semantic Conventions

### 问题
原有 ActivitySource 仅在 LocalActor 层记录事件处理 span，缺少 LLM 调用和 Tool 执行的标准化 span 和 metrics。MAF 遵循 OpenTelemetry GenAI Semantic Conventions 发射 `invoke_agent` / `chat` / `execute_tool` 标准 span。

### 方案
以内置 Middleware 形式实现可观测性（`GenAIObservabilityMiddleware`），同时实现三个 Middleware 接口：

**Span 类型**（GenAI 语义规范）：
- `invoke_agent <name>` — 每次 Agent Run，包含 `gen_ai.agent.id` / `gen_ai.agent.name` / `gen_ai.provider.name`
- `chat <model>` — 每次 LLM 调用，包含 `gen_ai.request.model` / `gen_ai.provider.name` / `gen_ai.usage.*_tokens`
- `execute_tool <name>` — 每次 Tool 调用，包含 `gen_ai.tool.name` / `gen_ai.tool.call_id`（span kind 为 `INTERNAL`）

**Metrics**（直方图）：
- `gen_ai.client.token.usage` — Token 消耗
- `gen_ai.client.operation.duration` — LLM 调用耗时
- `aevatar.tool.invocation.duration` — Tool 调用耗时

**敏感数据控制**：通过 `GenAIActivitySource.EnableSensitiveData` 控制是否在 span 中包含 prompt/response 内容。

### 新增文件
- `src/Aevatar.AI.Core/Observability/GenAIActivitySource.cs`
- `src/Aevatar.AI.Core/Observability/GenAIObservabilityMiddleware.cs`
- `src/Aevatar.Foundation.Runtime/Observability/AevatarObservabilityOptions.cs`
- `src/Aevatar.Foundation.Runtime/Observability/GenAIMetrics.cs`

### 修改文件
- `src/Aevatar.Foundation.Runtime/Observability/AevatarActivitySource.cs` — 增加 GenAI span 工厂方法

### 使用示例

```csharp
// 启用全链路 GenAI 可观测性
var mw = new GenAIObservabilityMiddleware();
services.AddSingleton<IAgentRunMiddleware>(mw);
services.AddSingleton<IToolCallMiddleware>(mw);
services.AddSingleton<ILLMCallMiddleware>(mw);
```

---

## 3. Human-in-the-Loop Workflow Steps

### 问题
MAF 提供 `Question` / `Confirmation` / `WaitForInput` / `RequestExternalInput` 四种 Human-in-the-Loop 操作。Aevatar 工作流引擎没有暂停执行等待人工输入的机制。

### 方案
新增两个工作流步骤模块和暂停/恢复事件：

- `HumanApprovalModule` — 处理 `type: human_approval`，暂停工作流等待 yes/no 审批
- `HumanInputModule` — 处理 `type: human_input`，暂停工作流等待自由文本输入

通过 `WorkflowSuspendedEvent` / `WorkflowResumedEvent` 实现暂停恢复，AGUI 层映射为 `HUMAN_INPUT_REQUEST` 事件供前端渲染。

### YAML DSL 扩展

```yaml
steps:
  - id: approve_report
    type: human_approval
    parameters:
      prompt: "审批生成的报告？"
      timeout: "3600"
      on_reject: fail

  - id: get_context
    type: human_input
    parameters:
      prompt: "请提供补充信息"
      variable: user_context
      timeout: "1800"
      on_timeout: fail
```

参数约定（最小集）：
- `human_approval`：`prompt` / `timeout`（秒）/ `on_reject`（默认 `fail`）
- `human_input`：`prompt` / `variable` / `timeout`（秒）/ `on_timeout`（默认 `fail`）

### 新增文件
- `src/workflow/Aevatar.Workflow.Core/Modules/HumanApprovalModule.cs`
- `src/workflow/Aevatar.Workflow.Core/Modules/HumanInputModule.cs`
- `src/workflow/Aevatar.Workflow.Projection/Reducers/WorkflowSuspendedEventReducer.cs`

### 修改文件
- `src/workflow/Aevatar.Workflow.Core/cognitive_messages.proto` — 新增 `WorkflowSuspendedEvent` / `WorkflowResumedEvent`
- `src/Aevatar.Presentation.AGUI/AGUIEvents.cs` — 新增 `HumanInputRequestEvent` / `HumanInputResponseEvent`
- `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/EventEnvelopeToAGUIEventMapper.cs` — 新增映射 Handler
- `src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/DependencyInjection/ServiceCollectionExtensions.cs` — 注册
- `src/workflow/Aevatar.Workflow.Core/ServiceCollectionExtensions.cs` — 默认注册 `human_input` / `human_approval` 步骤模块

---

## 4. Tool Approval Mechanism

### 问题
MAF 的 `@tool(approval_mode="always_require")` 可阻止 Agent 自主执行敏感工具。Aevatar 的工具系统无审批机制。

### 方案
在 `IAgentTool` 接口增加 `ApprovalMode` 属性（默认 `NeverRequire`，向后兼容）：

```csharp
public enum ToolApprovalMode
{
    NeverRequire = 0,   // 立即执行（默认）
    AlwaysRequire = 1,  // 始终需要审批
    Auto = 2,           // 由 Middleware 决定
}
```

与 Middleware Pipeline 自然组合：`IToolCallMiddleware` 可检查 `context.Tool.ApprovalMode` 实现审批逻辑。

### 新增文件
- `src/Aevatar.AI.Abstractions/ToolProviders/ToolApprovalMode.cs`

### 修改文件
- `src/Aevatar.AI.Abstractions/ToolProviders/IAgentTool.cs` — 增加 `ApprovalMode` 默认接口属性

---

## 5. Reusable Multi-Agent Orchestration Patterns

### 问题
MAF 将 5 种编排模式打包为可复用组件。Aevatar 通过 Workflow 模块实现类似能力，但与 YAML 引擎紧耦合，无法在无 YAML 场景下程序化使用。

### 方案
抽取 4 种编排模式为 `Aevatar.Foundation.Core.Orchestration` 命名空间下的一等公民：

| 模式 | 类名 | 行为 |
|------|------|------|
| Sequential | `SequentialOrchestration` | 链式：A → B → C |
| Concurrent | `ConcurrentOrchestration` | 并行扇出 + 合并 |
| Vote | `VoteOrchestration` | 并行执行 + 投票选最佳 |
| Handoff | `HandoffOrchestration` | 动态 Agent 控制转移 |

所有模式基于 `IOrchestration<TInput, TOutput>` 接口，接受 `Func<string, string, CancellationToken, Task<string>>` 作为 Agent 执行委托，与具体 Agent 实现解耦。

### 新增文件
- `src/Aevatar.Foundation.Core/Orchestration/IOrchestration.cs`
- `src/Aevatar.Foundation.Core/Orchestration/SequentialOrchestration.cs`
- `src/Aevatar.Foundation.Core/Orchestration/ConcurrentOrchestration.cs`
- `src/Aevatar.Foundation.Core/Orchestration/VoteOrchestration.cs`
- `src/Aevatar.Foundation.Core/Orchestration/HandoffOrchestration.cs`

---

## 6. Declarative Workflow Expression Language

### 问题
MAF 的声明式工作流使用 PowerFx 表达式语言。Aevatar YAML DSL 有变量替换但缺少运行时表达式求值。

### 方案
新增轻量级 `${...}` 表达式求值器，支持：

| 语法 | 功能 | 示例 |
|------|------|------|
| `${name}` | 变量引用 | `${variables.user}` |
| `${if(...)}` | 条件 | `${if(variables.age, 'adult', 'minor')}` |
| `${concat(...)}` | 拼接 | `${concat('Hello, ', variables.name)}` |
| `${isBlank(...)}` | 空值检测 | `${isBlank(variables.input)}` |
| `${length(...)}` | 长度 | `${length(variables.text)}` |
| `${and/or/not(...)}` | 布尔逻辑 | `${and(variables.a, variables.b)}` |
| `${upper/lower/trim(...)}` | 字符串函数 | `${upper(variables.name)}` |

支持嵌套函数调用和引号字面量，参数分隔正确处理括号嵌套。

### 新增文件
- `src/workflow/Aevatar.Workflow.Core/Expressions/WorkflowExpressionEvaluator.cs`

### 修改文件
- `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs` — 在派发 `StepRequestEvent` 前对步骤参数执行 `${...}` 求值

### 运行时变量与求值时机
- **求值时机**：`WorkflowLoopModule.DispatchStep(...)` 发布 `StepRequestEvent` 前，对 `step.parameters` 的 value 做一次模板替换（`${...}`）。
- **变量来源**：每个 `run_id` 维护独立变量字典，默认包含：
  - `input`：当前步骤的输入（上一步输出）
  - `<stepId>`：已完成步骤的输出（同名 key），可用 `${first_step}` 或 `${variables.first_step}` 引用
---

## 验证

```bash
dotnet build aevatar.slnx --nologo    # 0 错误
dotnet test aevatar.slnx --nologo     # 所有新增测试通过
```
