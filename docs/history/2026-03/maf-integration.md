# Microsoft Agent Framework (MAF) 集成方案

## 1. 背景

Microsoft Agent Framework (MAF) 是 Semantic Kernel + AutoGen 的统一继任者，当前版本 `dotnet-1.0.0-rc4`（2026-03-11），API 面已声明稳定，GA 目标 Q1 2026 末。

- 仓库：`github.com/microsoft/agent-framework`
- 文档：`learn.microsoft.com/en-us/agent-framework/overview/`
- 核心包：`Microsoft.Agents.AI`、`Microsoft.Agents.AI.Workflows`、`Microsoft.Agents.AI.Hosting.AspNetCore`

### 1.1 架构本质差异

| 维度 | MAF | Aevatar |
|------|-----|---------|
| 状态模型 | Session + Checkpoint（快照） | Actor + Event Sourcing |
| 并发模型 | Request/Response | 单线程 Actor（Orleans） |
| 持久化 | 序列化快照 | Event Store + Projection |
| 编排 | Graph-based Workflow（Executor + Edge） | Actor-based Workflow（YAML + Module） |
| 读写分离 | 无（直读 session/checkpoint） | CQRS + Projection Pipeline |
| 分布式 | Durable Task Framework | Orleans Grain |

**结论：二者底层范式不重合，但上层能力大量互补。集成策略是"在边界适配，不替换内核"。**

### 1.2 集成原则

1. **边界适配，不替换内核**：MAF 能力在 Host/Adapter 边界接入，内部仍走 Actor + Event Sourcing。
2. **协议优先**：优先采纳 MAF 定义的开放协议（A2A、AG-UI、MCP），而非框架实现细节。
3. **渐进引入**：按 Phase 分步，每步独立可验证，不引入大爆炸依赖。
4. **不引入双轨状态**：禁止同时维护 MAF Session 和 Actor State 两套状态管理。

---

## 2. Phase 1 — Microsoft.Extensions.AI 对齐（低成本高收益）

### 2.1 现状

Aevatar 已有 `Aevatar.AI.LLMProviders.MEAI` 项目，基于 `Microsoft.Extensions.AI` 的 `IChatClient` 抽象。

### 2.2 行动项

| 项目 | 动作 | 预期收益 |
|------|------|----------|
| 升级 MEAI 版本 | 跟进 `Microsoft.Extensions.AI` 最新稳定版（当前 v10.4.x） | 获取最新 `IChatClient` middleware 组合、tool schema 自动生成、telemetry 标准化 |
| `AIFunction` schema 生成 | 在 `IAgentTool` 实现中复用 `AIFunctionFactory.Create()` 的 JSON Schema 生成 | 消除手写 `ParametersSchema` 的维护成本 |
| OTel Gen AI 语义约定 | 在 `ChatRuntime` 的 LLM 调用链路加入标准 `ActivitySource` span | 与 Application Insights / Aspire Dashboard 等生态对齐 |

### 2.3 约束

- MEAI 是**抽象层**，不引入 MAF agent runtime 依赖。
- `IChatClient` middleware pipeline 可在 `Aevatar.AI.Core` 内组合使用，但 agent 生命周期仍由 GAgent 管理。

### 2.4 影响范围

```
src/Aevatar.AI.LLMProviders.MEAI/   — 版本升级 + API 适配
src/Aevatar.AI.Core/                 — OTel span 注入
src/Aevatar.AI.Abstractions/         — IAgentTool schema 生成优化（可选）
```

---

## 3. Phase 2 — A2A Protocol 适配层（标准化互操作）

### 3.1 价值

A2A（Agent-to-Agent Protocol v0.3.3-preview）是跨进程/跨框架的 agent 通信标准。实现后，Aevatar GAgent 可与 MAF agent、LangChain agent、任何 A2A 兼容系统互操作。

### 3.2 架构设计

```
                          ┌─────────────────────┐
   External MAF Agent ──► │  A2A HTTP Endpoint   │ ◄── External A2A Agent
                          │  (ASP.NET Controller)│
                          └──────────┬──────────┘
                                     │ A2A Protocol ↔ EventEnvelope 转换
                          ┌──────────▼──────────┐
                          │  A2AAdapterService   │
                          │  (Application Layer) │
                          └──────────┬──────────┘
                                     │ IActorDispatchPort
                          ┌──────────▼──────────┐
                          │   GAgent (Actor)     │
                          └─────────────────────┘
```

### 3.3 关键组件

| 组件 | 层 | 职责 |
|------|-----|------|
| `A2AController` | Host | HTTP endpoint，接收/返回 A2A 消息 |
| `A2AAdapterService` | Application | A2A Task ↔ EventEnvelope 双向转换；target 解析 |
| `A2AAgentCard` | Host | 暴露 agent 能力描述（name, skills, tools）供发现 |
| `IA2ATaskStore` | Infrastructure | A2A task 状态跟踪（映射到 actor command receipt） |

### 3.4 协议映射

| A2A 概念 | Aevatar 映射 |
|----------|-------------|
| Task | Command dispatch + receipt observation |
| Task.send | `IActorDispatchPort.DispatchAsync()` |
| Task.status | ReadModel 查询 / projection observation |
| Task.artifacts | `CommittedStateEventPublished` → projection 物化 |
| AgentCard | GAgent metadata（`GetDescriptionAsync()` + tool schema） |

### 3.5 约束

- A2A 是**外部协议**，adapter 在 Host 边界做协议转换；进入 Application 层后恢复为 `EventEnvelope`。
- A2A task 状态不作为权威源；权威源仍是 actor state + event store。
- Streaming（SSE push）映射到现有 projection observation 链路。

### 3.6 影响范围

```
新增：
  src/Aevatar.Interop.A2A.Abstractions/   — A2A 协议类型 + adapter 抽象
  src/Aevatar.Interop.A2A.Hosting/         — ASP.NET Controller + AgentCard
  src/Aevatar.Interop.A2A.Application/     — 转换逻辑 + task 跟踪

现有（无侵入）：
  src/Aevatar.Foundation.Abstractions/     — 可能扩展 GAgent metadata 接口
```

---

## 4. Phase 2 — AG-UI 协议对齐

### 4.1 现状

Aevatar 已有 `Aevatar.Presentation.AGUI` 项目和 `agui_events.proto`，说明已在关注 AG-UI 方向。

### 4.2 行动项

| 项目 | 动作 |
|------|------|
| 协议对齐 | 对照 MAF AG-UI 实现，确认 `agui_events.proto` 覆盖 AG-UI 标准事件类型（text, tool_call, state_update, lifecycle 等） |
| 前端 SDK 兼容 | 确保 SSE/WebSocket 输出格式兼容 AG-UI 前端 SDK（`@anthropic-ai/ag-ui` 或 MAF 官方 JS client） |
| Streaming 映射 | AG-UI 事件流 → 现有 projection observation SSE 出口统一 |

### 4.3 约束

- AG-UI 是**展示层协议**，不影响领域模型和事件语义。
- 保持 `agui_events.proto` 作为仓库内的强类型契约，AG-UI 标准事件类型用 proto field 表达，不退化为通用 bag。

---

## 5. Phase 3 — Agent-as-Tool 适配器（能力扩展）

### 5.1 价值

MAF 的 `.AsAIFunction()` 将任意 agent 转换为 tool，实现层级化 agent 委托。Aevatar 可实现等价机制，让 GAgent 被其他 agent 当作 tool 调用。

### 5.2 设计

```csharp
/// <summary>
/// 将 GAgent 适配为 IAgentTool，使其可被其他 AI agent 当作 tool 调用。
/// </summary>
public class GAgentToolAdapter : IAgentTool
{
    // GAgent 通过 IActorDispatchPort 交互，不直接持有 actor 引用
    private readonly ActorId _targetActorId;
    private readonly IActorDispatchPort _dispatchPort;
    private readonly IProjectionObservationPort _observationPort;

    public string Name { get; }           // 从 GAgent metadata 派生
    public string Description { get; }    // 从 GetDescriptionAsync() 派生
    public string ParametersSchema { get; } // 从 GAgent 接受的 command schema 派生

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        // 1. 反序列化参数 → 构建 command EventEnvelope
        // 2. DispatchAsync → 投递到目标 GAgent
        // 3. 通过 observation 等待 completion event
        // 4. 提取结果 → 序列化为 string 返回
    }
}
```

### 5.3 反向适配：MAF Agent → Aevatar Tool

```csharp
/// <summary>
/// 将外部 MAF AIAgent 适配为 IAgentTool，使 Aevatar GAgent 可调用外部 MAF agent。
/// </summary>
public class MAFAgentToolAdapter : IAgentTool
{
    private readonly IAIAgent _mafAgent;

    public async Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var session = new AgentSession();
        var result = await _mafAgent.RunAsync(argumentsJson, session, ct);
        return result?.ToString() ?? string.Empty;
    }
}
```

### 5.4 注册方式

```csharp
// 作为 IAgentToolSource 发现
public class GAgentToolSource : IAgentToolSource
{
    public async Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct)
    {
        // 从 service catalog / projection 发现可用 GAgent
        // 为每个适配为 GAgentToolAdapter
    }
}
```

### 5.5 约束

- GAgent-as-Tool 调用走标准 command dispatch，不绕过 actor 边界。
- 结果获取走 projection observation，不直接读 actor state。
- Tool 执行超时须事件化（`ScheduleSelfDurableTimeoutAsync`），不阻塞调用方 actor turn。

### 5.6 影响范围

```
新增：
  src/Aevatar.AI.ToolProviders.GAgent/     — GAgentToolAdapter + GAgentToolSource
  src/Aevatar.AI.ToolProviders.MAF/        — MAFAgentToolAdapter（可选，依赖 MAF 包）
```

---

## 6. Phase 3 — ChatMiddleware Pipeline 增强

### 6.1 现状

Aevatar 已有 `IAgentRunMiddleware`、`ILLMCallMiddleware`、`IToolCallMiddleware` 三层 middleware。

### 6.2 参考 MAF 增强项

| MAF 能力 | Aevatar 对应增强 | 优先级 |
|----------|-----------------|--------|
| `ChatTelemetryLayer` | `LLMCallTelemetryMiddleware` — 标准 OTel span（model, tokens, latency） | 高（Phase 1 可先做） |
| `FunctionInvocationLayer` | 现有 `ToolApprovalMode` 已覆盖 | 已有 |
| `PurviewMiddleware` | `ContentGovernanceMiddleware` — 内容合规过滤（可选） | 低 |
| `AIContextProvider` | `IAgentRunMiddleware` pre-hook 可实现等价注入 | 已有等价 |

### 6.3 约束

- Middleware 在 `ChatRuntime` 内组合，不影响 actor 事件处理管线。
- 新增 middleware 通过 DI 注册，遵循现有 priority 排序机制。

---

## 7. Phase 4 — 声明式 Workflow DSL 参考（可选）

### 7.1 MAF 模式

MAF 提供 `WorkflowBuilder` 声明式 API：

```csharp
var workflow = new WorkflowBuilder()
    .AddExecutor("planner", plannerAgent)
    .AddExecutor("coder", coderAgent)
    .AddEdge("planner", "coder", condition: ctx => ctx.HasPlan)
    .Build();
```

### 7.2 对 Aevatar 的参考价值

Aevatar 已有 YAML-driven workflow，但可考虑在上层增加 **C# fluent builder**，编译到现有 YAML + Module 体系：

```csharp
// 概念示例 — 编译到 WorkflowDefinition YAML 等价结构
var definition = new AevatarWorkflowBuilder()
    .WithRole("planner", cfg => cfg
        .SystemPrompt("You are a planning assistant.")
        .Provider("openai").Model("gpt-5.4"))
    .WithStep("plan", step => step
        .Type(StepType.LLMCall)
        .Role("planner")
        .Prompt("Create a plan for: {input}"))
    .WithStep("execute", step => step
        .Type(StepType.Parallel)
        .Steps(s => s.ToolCall("search_api"), s => s.ToolCall("db_query")))
    .Build();
```

### 7.3 约束

- Fluent builder 是**语法糖**，最终产物仍是 `WorkflowDefinition`，不引入第二套执行引擎。
- 优先级低，仅在 YAML 定义维护成本显著上升时考虑。

---

## 8. 不引入的部分

| MAF 能力 | 不引入原因 |
|----------|-----------|
| `AgentSession` / `StateBag` | 与 actor event-sourcing state 冲突，会造成双轨状态管理 |
| `ChatHistoryProvider` | Aevatar 的 `ChatHistory` 由 GAgent state 管理，无需外部 session store |
| MAF Workflow Runner | Aevatar 已有 actor-based workflow，替换成本高且丧失 event-sourcing 优势 |
| MAF Checkpoint Store | Aevatar 用 event store + projection，无需退化到快照模式 |
| `ProviderSessionState<T>` | Actor 天然单线程隔离，无需额外 session 隔离机制 |

---

## 9. 路线图总览

```
Phase 1 (低成本，1-2 周)
  ├── 升级 Microsoft.Extensions.AI 到最新稳定版
  ├── AIFunction schema 生成复用
  └── OTel Gen AI 语义约定 + LLMCallTelemetryMiddleware

Phase 2 (标准化，3-4 周)
  ├── A2A Protocol adapter（Controller + AdapterService + AgentCard）
  └── AG-UI 协议对齐（agui_events.proto 审查 + SSE 格式兼容）

Phase 3 (能力扩展，2-3 周)
  ├── Agent-as-Tool 双向适配器（GAgent ↔ MAF Agent）
  └── ChatMiddleware 增强（Telemetry, Governance）

Phase 4 (可选)
  └── C# Fluent Workflow Builder（编译到 YAML/WorkflowDefinition）
```

---

## 10. 依赖引入清单

| Phase | NuGet 包 | 用途 |
|-------|---------|------|
| 1 | `Microsoft.Extensions.AI` (升级) | IChatClient + AIFunction |
| 1 | `System.Diagnostics.DiagnosticSource` (已有) | OTel ActivitySource |
| 2 | 无新依赖（A2A 协议自实现） | HTTP endpoint + 协议类型 |
| 3（可选） | `Microsoft.Agents.AI` | MAFAgentToolAdapter 反向适配 |

---

## 附录：MAF 核心抽象速查

| 概念 | .NET 类型 | 说明 |
|------|-----------|------|
| Agent | `IAIAgent` / `AIAgent` | 核心 agent 抽象，`RunAsync()` / `RunStreamingAsync()` |
| Tool | `AIFunction` / `AITool` | 函数工具，自动 schema 生成 |
| Session | `AgentSession` | 会话状态 + `StateBag` |
| Chat Client | `IChatClient` | Provider 无关 LLM 接口 |
| Context Provider | `AIContextProvider` | 横切关注点注入 |
| Middleware | `ChatMiddlewareLayer` | 请求/响应拦截管线 |
| Workflow | `Workflow` / `WorkflowBuilder` | 图编排（Executor + Edge） |
| A2A | `A2AAgent` | 跨进程 agent 通信 |
