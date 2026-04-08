# Aevatar

**Aevatar** 是基于**虚拟 Actor 模型**构建的自主 Agent 平台。它提供三种核心原语 — **GAgent**、**Workflow**、**Script** — 每一种都可以发布为 **Service**，并通过 **Aevatar Mainnet** 上的虚拟 Actor 对外提供服务。与 **[NyxID](https://github.com/ChronoAIProject/NyxID)** 平台的深度集成，为所有服务提供了身份感知的访问控制、凭证托管和跨平台连接能力。

**Mainnet 控制台：** [aevatar-console.aevatar.ai](https://aevatar-console.aevatar.ai/) | **NyxID 平台：** [nyx.chrono-ai.fun](https://nyx.chrono-ai.fun/)

> [English Version](README.md)

---

## 核心原语

### GAgent — 有状态 Agent 即虚拟 Actor

**GAgent**（Generic Agent）是 Aevatar 的基础构建单元。每个 GAgent 都是一个有状态、事件溯源的虚拟 Actor，具有串行邮箱、自动激活/停用和位置透明寻址能力。

**类层次：**

```
IAgent
  └─ GAgentBase                          (无状态事件管线)
      └─ GAgentBase<TState>              (事件溯源 + 状态管理)
          └─ GAgentBase<TState, TConfig> (可合并配置)
              ├─ AIGAgentBase<TState>     (LLM 组合：ChatRuntime、ToolManager、Middleware)
              │   └─ RoleGAgent           (LLM 驱动的 Agent，支持工具调用和流式输出)
              ├─ WorkflowRunGAgent        (每次运行的工作流编排器)
              └─ ScriptBehaviorGAgent     (动态脚本执行器)
```

**核心能力：**

- **事件溯源**：通过 `PersistDomainEventAsync()` 进行状态转换 → committed 事件流入统一投影管线。激活时从事件存储重建状态；快照支持快速恢复。
- **统一事件管线**：静态 `[EventHandler]` 方法 + 动态 `IEventModule` 合并排序执行，配合双通道 Hook 系统（虚方法覆写 + DI 注入的 `IGAgentExecutionHook` 管线）。
- **拓扑感知消息传递**：父子层级结构，支持方向性发布（`TopologyAudience.Children | Parent | Self`），以及点对点 `SendToAsync()` 和持久化定时器/超时回调。
- **模块组合**：运行时注册或替换 `IEventModule` 实例 — 工作流步骤执行、脚本行为和自定义处理都通过此机制插入，无需修改基类。
- **状态守卫**：基于 `AsyncLocal` 的写保护，确保状态变更只在事件处理器作用域或激活期间发生 — 永远不会来自任意线程。

**每个 GAgent 都以虚拟 Actor 方式运行**：首次收到消息时自动激活，空闲时停用，跨集群节点透明迁移。无需手动管理生命周期。

---

### Workflow — 声明式多 Agent 编排

Workflow 让你通过**声明式 YAML** 编排多 Agent 协作，无需编写代码。工作流定义 **角色**（配置了 LLM 的 Agent）和**步骤**（执行原语），由 Actor 化的编排器编译和执行。

**执行模型：**

```
WorkflowGAgent (定义 Actor)           ← 持有 YAML、编译结果、版本
    │
    └─ WorkflowRunGAgent (运行 Actor) ← 每次运行的编排器，事件溯源状态
        ├─ WorkflowExecutionKernel    ← 步骤分发引擎
        ├─ RoleGAgent (每个角色)       ← LLM 驱动的子 Agent
        └─ 子 WorkflowRunGAgent       ← 子工作流调用
```

**YAML 结构：**

```yaml
name: analysis_pipeline
description: 多 Agent 分析与审查
roles:
  - id: analyst
    name: 分析师
    system_prompt: "你是一位领域分析师..."
    provider: claude
    model: claude-sonnet-4-20250514
    temperature: 0.7
  - id: reviewer
    name: 审查员
    system_prompt: "你是一位严格的审查员..."
    provider: deepseek
    model: deepseek-chat
steps:
  - id: analyze
    type: llm_call
    role: analyst
    next: review
  - id: review
    type: llm_call
    role: reviewer
    next: finalize
  - id: finalize
    type: transform
```

**30+ 内置步骤类型**，覆盖完整的编排场景：

| 类别 | 步骤类型 |
|------|----------|
| **LLM 与工具** | `llm_call`、`tool_call` |
| **控制流** | `conditional`、`while`、`loop`、`workflow_call`、`sub_workflow`、`assign`、`guard` |
| **并行处理** | `parallel`、`fan_out`、`foreach`、`map_reduce`、`vote_consensus`、`race` |
| **人机协作** | `human_approval`、`human_input`、`secure_input` |
| **外部 I/O** | `connector_call`、`mcp_call`、`http_*`、`bridge_call` |
| **异步信号** | `wait_signal`、`emit`、`delay` |
| **评估与反思** | `evaluate`、`reflect`、`workflow_yaml_validate` |
| **子工作流** | `workflow_call`，基于 continuation 的父子协调 |

**关键设计决策：**
- 定义与运行分离：`WorkflowGAgent` 持有 YAML 和编译结果；`WorkflowRunGAgent` 拥有所有执行事实。清晰的 Actor 边界。
- 子工作流的 Continuation 模式：父级发送请求事件，结束当前 turn，在 reply 时恢复 — 纯事件驱动，无同步等待。
- 模块包系统：`IWorkflowModulePack` 提供步骤模块 + 依赖展开器 + 配置器。扩展（如 Maker）以模块包形式插入。

---

### Script — 运行时编译的自主 Agent

Script 提供**动态、热部署的 Agent 行为**，使用 C# 编写，通过 Roslyn 在运行时编译。脚本演化系统管理完整的生命周期：提案、沙箱验证、编译、发布和回滚 — 全程事件溯源。

**生命周期 Actor：**

```
ScriptEvolutionManagerGAgent        ← 所有提案的索引及状态跟踪
    │
    └─ ScriptEvolutionSessionGAgent ← 编排一个提案：验证 → 编译 → 发布
         │
         ├─ ScriptCatalogGAgent     ← 活跃版本注册表（按 scope）
         ├─ ScriptDefinitionGAgent  ← 持久化编译产物元数据
         └─ ScriptBehaviorGAgent    ← 脚本行为的运行时执行器
```

**演化管线：**

```
Proposed → BuildRequested → Validated/ValidationFailed → Promoted/Rejected
                                                              ↓
                                               RollbackRequested → RolledBack
```

**编译流程（基于 Roslyn）：**

1. **沙箱验证** — `ScriptSandboxPolicy` 阻止危险 API（反射、系统调用等）
2. **Proto 编译** — `.proto` 文件通过 `IScriptProtoCompiler` 编译为 C# 消息类型
3. **语义编译** — 完整的 Roslyn `CSharpCompilation`，使用精心筛选的引用程序集
4. **契约验证** — 要求至少一个 `IScriptBehaviorBridge` 实现
5. **产物创建** — 包含工厂方法、描述符、类型 URL 的 `ScriptBehaviorArtifact`

**运行时执行：**

脚本实现 `IScriptBehaviorBridge`，暴露：
- `DispatchAsync(inbound, context)` → 返回领域事件
- `ApplyDomainEvent(state, evt)` → 纯状态转换
- `BuildReadModel(state)` → 物化查询视图

`ScriptBehaviorGAgent` 绑定到编译后的定义，通过行为桥接器分发传入的请求。发出的 `ScriptDomainFactCommitted` 事件携带状态快照、读模型投影和原生物化（文档/图存储）。

**脚本可用的运行时能力：** 发布事件、向 Actor 发送消息、调度延迟信号、创建子 Actor、查询读模型 — 全部通过 `IScriptBehaviorRuntimeCapabilities` 提供。

---

## Service Binding — 从原语到 API

任何 GAgent、Workflow 或 Script 都可以通过服务绑定系统**发布为 Service**。这将内部的 Actor 能力转化为可寻址、版本化、可治理的 API 端点。

**服务生命周期：**

```
定义 → 创建版本 → 准备（适配） → 发布 → 部署（激活） → 服务
```

**三种实现适配器**处理绑定：

| 来源 | 适配器 | 端点类型 | 处理过程 |
|------|--------|----------|----------|
| **Workflow** | `WorkflowServiceImplementationAdapter` | `Chat` | YAML 规格 → 聊天端点；创建 `WorkflowServiceDeploymentPlan` |
| **Script** | `ScriptingServiceImplementationAdapter` | `Command`（每个脚本命令） | 从运行时语义提取命令端点；创建 `ScriptingServiceDeploymentPlan` |
| **GAgent** | 静态绑定 | 可配置 | 按类型解析或创建 Actor；直接分发 |

**服务身份**是层级化的：`{tenant_id}:{app_id}:{namespace}:{service_id}`。每个服务跟踪：
- **版本**，状态生命周期：`Created → Prepared → Published → Retired`
- **端点目录**，定义暴露的操作
- **绑定**到其他服务、连接器或密钥
- **治理策略**，用于访问控制

**调用流程：**

```
客户端请求
  → ServiceInvocationApplicationService（验证、规范化）
    → ServiceInvocationResolutionService（解析目标 Actor）
      → IInvokeAdmissionAuthorizer（检查策略）
        → DefaultServiceInvocationDispatcher（按实现类型路由）
          → 目标 Actor（Script / Workflow / GAgent）
```

**Scope 绑定**简化了常见模式：将工作流或脚本绑定到 scope，自动完成服务定义创建、版本准备、发布和端点目录管理。

---

## 虚拟 Actor 运行时 — Aevatar Mainnet

Aevatar Mainnet 上的所有服务都通过基于 [Microsoft Orleans](https://learn.microsoft.com/en-us/dotnet/orleans/) 的**虚拟 Actor** 提供。运行时将每个 GAgent 映射到 Orleans Grain，实现透明的分布、持久化和消息传递。

**架构分层：**

```
GAgentBase（业务逻辑）
    ↓
OrleansActor / OrleansAgentProxy（生命周期包装器）
    ↓
RuntimeActorGrain（Orleans Grain：状态持久化、流订阅、信封分发）
    ↓
Orleans Silo 集群（Garnet/Redis 状态、Kafka 流、Gossip 集群发现）
```

**关键运行时特性：**

- **位置透明**：Actor 通过字符串 ID 寻址；Orleans 处理放置、激活和跨 Silo 迁移。
- **串行邮箱保证**：每个 Actor 同时只处理一个事件 — 无锁、无并发状态变更。
- **延迟激活**：Agent 类型名存储在 Grain 状态中；实际实例在首次收到消息时通过 DI 创建。
- **层级化流拓扑**：父子关系存储在 Grain 状态中；`StreamTopologyGrain` 管理转发规则，支持 BFS 中继、环路防止和受众/类型过滤。
- **事件去重**：`IEventDeduplicator` 备忘表防止跨重试的重复处理。
- **持久化回调**：`RuntimeCallbackSchedulerGrain` 使用 Orleans Reminder 进行超时/定时器调度，支持基于 generation 的取消。

**生产基础设施：**

| 组件 | 技术 | 用途 |
|------|------|------|
| **集群发现** | Orleans Gossip / 固定成员 | Silo 发现和 Leader 选举 |
| **状态存储** | Garnet（Redis 兼容） | 跨 Silo 的 Grain 状态持久化 |
| **流传输** | Kafka | 跨 Silo 的事件投递与有序保证 |
| **流元数据** | Garnet | Kafka Provider 的 Pub/Sub 协调 |

**配置：**

```yaml
Orleans:
  ClusteringMode: "Development"      # 或 "Localhost"
  ClusterId: "aevatar-mainnet-cluster"
  ServiceId: "aevatar-mainnet-host-api"
Runtime:
  Provider: "Orleans"
  OrleansStreamBackend: "KafkaProvider"  # 或 "InMemory"
  OrleansPersistenceBackend: "Garnet"    # 或 "InMemory"
```

---

## NyxID 集成

Aevatar 与 **[NyxID](https://github.com/ChronoAIProject/NyxID)**（[nyx.chrono-ai.fun](https://nyx.chrono-ai.fun/)）— 一个身份与凭证托管平台 — 深度集成。NyxID 为 Mainnet 上的所有服务提供身份层、LLM 网关、工具访问控制和跨平台连接能力。

### 身份与认证

- **基于声明的身份**：`NyxIdClaimsTransformer` 使用瀑布式解析（`scope_id → uid → sub → NameIdentifier → *_id`）将 NyxID Token 映射到 Aevatar Scope。
- **Scope 隔离**：每个请求通过 `IAppScopeResolver` 解析到一个 Scope（租户/工作空间）。所有 Actor 操作、服务绑定和数据访问都在 Scope 内隔离。
- **按请求传递 Token**：无存储密钥 — 用户的 Bearer Token 通过 `AgentToolRequestContext`（AsyncLocal）流向每个工具和 Provider 调用。

### LLM 网关

- **凭证托管**：NyxID 存储用户在多个 Provider（OpenAI、Anthropic、DeepSeek）的 API Key。`NyxIdLLMProvider` 通过 NyxID 网关路由 LLM 调用，网关按请求注入正确的凭证。
- **动态路由**：`NyxIdRoutePreference` 控制端点选择 — `auto`（默认网关）、`gateway`（显式）、或相对路径用于代理服务。
- **无需本地密钥**：Agent 使用用户的 NyxID 托管凭证调用 LLM，全程不接触 API Key。

### 工具生态（18 个专用工具）

NyxID 为所有 AI Agent 暴露丰富的工具集：

| 类别 | 工具 |
|------|------|
| **账户与档案** | `NyxIdAccountTool`、`NyxIdProfileTool`、`NyxIdSessionsTool` |
| **凭证管理** | `NyxIdApiKeysTool`、`NyxIdExternalKeysTool`、`NyxIdMfaTool` |
| **服务管理** | `NyxIdServicesTool`、`NyxIdCatalogTool`、`NyxIdProvidersTool`、`NyxIdEndpointsTool` |
| **审批流程** | `NyxIdApprovalsTool` — 创建、批准/拒绝、管理授权、配置策略 |
| **基础设施** | `NyxIdNodesTool`、`NyxIdNotificationsTool`、`NyxIdLlmStatusTool` |
| **代码执行** | `NyxIdCodeExecuteTool` — Python、JavaScript、TypeScript、Bash |
| **代理与桥接** | `NyxIdProxyTool`、`NyxIdChannelBotsTool` |

### 跨平台连接

- **频道机器人**：在 Telegram、Discord、Lark、飞书上注册机器人。通过会话路由将平台对话映射到 Aevatar Agent — 让 AI Agent 跨消息平台服务用户。
- **凭证注入代理**：`NyxIdProxyTool` 通过 NyxID 代理发送 HTTP 请求，代理处理凭证注入和审批工作流（服务端审批，可配置超时）。
- **审批工作流**：`NyxIdToolApprovalHandler` 创建远程审批请求，支持多渠道通知（Telegram、FCM、APNs）和可配置的超时轮询。

### 已连接服务上下文

用户通过 NyxID 认证后，`ConnectedServicesContextMiddleware` 将可用服务预加载到 Agent 的系统消息中 — 实现自动服务发现，无需显式工具调用。

---

## 快速开始

### 1. 配置 LLM 访问

| 方式 | 操作 |
|------|------|
| **环境变量** | `export DEEPSEEK_API_KEY="sk-..."` 或 `export OPENAI_API_KEY="sk-..."` |
| **配置文件** | 在 `~/.aevatar/secrets.json` 中写入 Provider 和 API Key（见 [配置说明](src/Aevatar.Configuration/README.md)） |
| **NyxID（推荐）** | 在 NyxID 中存储 API Key — 无需本地密钥 |

### 2. 启动 Mainnet Host

```bash
dotnet run --project src/Aevatar.Mainnet.Host.Api
```

或仅用于工作流开发：

```bash
dotnet run --project src/workflow/Aevatar.Workflow.Host.Api
```

### 3. 发送 Chat 请求

```bash
curl -X POST http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -H "Accept: text/event-stream" \
  -d '{"prompt": "分析微服务架构的优缺点", "workflow": "simple_qa"}'
```

响应为 SSE 流，包含事件：`RUN_STARTED`、`STEP_STARTED`、`TEXT_MESSAGE_CONTENT`、`STEP_FINISHED`、`RUN_FINISHED`。

### API 端点

| 端点 | 说明 |
|------|------|
| `POST /api/chat`（SSE） | 执行工作流，流式返回结果 |
| `GET /api/ws/chat`（WebSocket） | 同上，WebSocket 协议 |
| `POST /api/workflows/resume` | 恢复人工审批/输入步骤 |
| `POST /api/workflows/signal` | 向 wait_signal 步骤发送信号 |
| `GET /api/workflows` | 列出可用工作流 |
| `POST /api/services/{id}/invoke/{endpoint}` | 调用已发布的服务 |

---

## 架构总览

```
┌─────────────────────────────────────────────────────────┐
│                    Aevatar Mainnet                       │
│                                                         │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐              │
│  │ GAgent   │  │ Workflow  │  │ Script   │  核心原语    │
│  └────┬─────┘  └────┬─────┘  └────┬─────┘              │
│       │              │              │                    │
│       └──────────────┼──────────────┘                    │
│                      ▼                                   │
│            ┌─────────────────┐                           │
│            │ Service Binding │  发布为服务               │
│            └────────┬────────┘                           │
│                     ▼                                    │
│            ┌─────────────────┐                           │
│            │  虚拟 Actor     │  Orleans 运行时            │
│            │    运行时       │  (Garnet + Kafka)         │
│            └────────┬────────┘                           │
│                     ▼                                    │
│            ┌─────────────────┐                           │
│            │     NyxID       │  身份、LLM 网关、          │
│            │    集成层       │  工具、审批                │
│            └─────────────────┘                           │
└─────────────────────────────────────────────────────────┘
```

**分层：** `Domain / Application / Infrastructure / Host` — 严格依赖反转，禁止跨层耦合。

**CQRS 管线：** `Command → Event → Projection → ReadModel` — 所有能力共享统一投影管线。

**序列化：** 全面 Protobuf — 状态、事件、命令、快照、跨 Actor 传输。JSON 仅在 HTTP 适配边界使用。

---

## 项目结构

```
src/
├── Aevatar.Foundation.*          # Actor 运行时、事件溯源、CQRS 内核
├── Aevatar.AI.*                  # LLM Provider、工具 Provider、AI 中间件
├── Aevatar.CQRS.*                # 投影管线、读模型存储
├── Aevatar.Scripting.*           # 脚本演化、编译、目录
├── platform/Aevatar.GAgentService.*  # 服务绑定、调用、治理
├── workflow/Aevatar.Workflow.*   # 工作流引擎、步骤模块、投影
├── Aevatar.Authentication.*      # NyxID 认证 Provider
├── Aevatar.Mainnet.Host.Api      # 生产统一宿主
└── Aevatar.Hosting               # 共享宿主基础设施
test/                             # 单元、集成和 API 测试
workflows/                        # YAML 工作流定义
docs/                             # 架构文档
```

---

## 构建与测试

```bash
# 恢复、构建、测试
dotnet restore aevatar.slnx --nologo
dotnet build aevatar.slnx --nologo
dotnet test aevatar.slnx --nologo

# 按域构建
dotnet build aevatar.foundation.slnf
dotnet build aevatar.workflow.slnf

# CI 架构门禁
bash tools/ci/architecture_guards.sh
```

---

## 文档

### 架构

- [Foundation 架构](docs/architecture/FOUNDATION.md) — 事件模型、管线、Actor 生命周期
- [项目架构](docs/architecture/PROJECT_ARCHITECTURE.md) — 分层、能力装配、宿主边界
- [CQRS 架构](docs/architecture/CQRS_ARCHITECTURE.md) — 写侧/读侧、统一投影管线
- [事件溯源](docs/architecture/EVENT_SOURCING.md) — 事件存储、状态重放、快照
- [Mainnet 架构](docs/architecture/MAINNET_ARCHITECTURE.md) — 分布式部署、Orleans 集群、基础设施
- [Scripting 架构](docs/architecture/SCRIPTING_ARCHITECTURE.md) — 脚本演化生命周期、编译、运行时
- [流转发架构](docs/architecture/STREAM_FORWARD_ARCHITECTURE.md) — 层级化事件中继拓扑
- [LLM 流式架构](docs/architecture/WORKFLOW_LLM_STREAMING_ARCHITECTURE.md) — 端到端 LLM 流式执行流程

### 指南

- [Workflow 指南](docs/guides/WORKFLOW.md) — 工作流引擎设计、步骤执行模型、模块包
- [Workflow 原语参考](docs/guides/WORKFLOW_PRIMITIVES.md) — 完整步骤类型参考与 YAML 示例
- [角色与连接器](docs/guides/ROLE.md) — Workflow YAML 角色、连接器配置、MCP/CLI/API 作为 Agent 能力
- [连接器参考](docs/guides/CONNECTOR.md) — 连接器类型、配置格式、示例
- [Workflow Chat API](docs/guides/workflow-chat-ws-api-capability.md) — SSE/WebSocket 协议详情
- [.NET SDK](docs/guides/SDK_WORKFLOW_CHAT_DOTNET.md) — 工作流聊天集成客户端 SDK
- [配置说明](src/Aevatar.Configuration/README.md) — API Key、密钥、连接器配置

### 集成

- [NyxID LLM Provider](docs/integration/nyxid-llm-provider-integration.md) — NyxID 网关路由、凭证托管、Provider 配置
- [NyxID Chatbot 协议](docs/integration/CHATBOT_3RD_PARTY_INTEGRATION_SPEC.md) — 第三方 AI 服务集成规范
- [MAF 集成](docs/integration/MAF-INTEGRATION.md) — Microsoft Agent Framework 集成策略

### 设计提案

- [外部链接框架](docs/design/EXTERNAL_LINK_FRAMEWORK.md) — WebSocket/gRPC/MQTT 外部连接
- [多 Agent 演进](docs/design/WORKFLOW_MULTIAGENT_EVOLUTION.md) — TaskBoard、原生消息、Agent 协调 RFC

### 投影与读模型

- [投影核心](src/Aevatar.CQRS.Projection.Core/README.md) — 统一投影生命周期、协调器、读模型契约
- [工作流投影](src/workflow/Aevatar.Workflow.Projection/README.md) — 工作流特定读模型与实时输出事件
