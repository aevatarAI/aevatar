# GAgent Types 全景分析与底座能力评估

> 生成时间：2026-05-14
> 范围：`src/` + `agents/` 全量 GAgent 类

---

## 1. GAgent 继承体系

> 2026-05-25 更新：本文件保留 2026-05-14 盘点背景，但 issue #643
> 已删除 Foundation MultiAgent 实验 actor，并确认 Studio empty-state
> generation 降级为 Application authoring preview helper，不再作为 GAgent
> 类型列入当前清单。

### 1.1 基类层级

```
GAgentBase                              ← 无状态底座
  ├── GAgentBase<TState>                ← 有状态 + Event Sourcing
  │     └── GAgentBase<TState, TConfig> ← 有状态 + 可配置（class defaults + state overrides）
  │           └── AIGAgentBase<TState>   ← AI 能力组合（ChatRuntime + ToolManager + Hooks）
  │                 └── RoleGAgent       ← 角色型对话 Agent（YAML 可配置）
  └── GAgentBase (直接继承)              ← 无状态桥接/适配 Agent
```

| 基类 | 职责 | 提供的能力 |
|---|---|---|
| `GAgentBase` | 统一事件管线 | `HandleEventAsync` pipeline、模块管理、双通道 Hook（virtual + DI）、`PublishAsync/SendToAsync`、External Links、Durable Callback |
| `GAgentBase<TState>` | 状态 + 事实持久化 | Event Sourcing（Replay + Commit + Snapshot）、`PersistDomainEventAsync`、`TransitionState`、`OnStateChangedAsync`、CommittedStateEventPublisher |
| `GAgentBase<TState, TConfig>` | 类默认值 + 状态覆盖合并 | `MergeEffectiveConfig`、`OnEffectiveConfigChangedAsync`、class defaults provider |
| `AIGAgentBase<TState>` | AI 能力组合 | ChatRuntime + ToolManager + ChatHistory + ToolCallLoop + AI Hook Pipeline + Middleware chains |
| `RoleGAgent` | 对话角色 | Handle ChatRequestEvent → LLM streaming → AG-UI event publishing → tool approval lifecycle |

### 1.2 所有 GAgent Types 清单

#### 1.2.1 Foundation / Core 层

Issue #643 已删除旧 Foundation MultiAgent 实验 actor；当前 Foundation/Core 层不保留
`TaskBoardGAgent` / `TeamManagerGAgent` 生产 GAgent 表面。

#### 1.2.2 AI 层

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `RoleGAgent` | `AIGAgentBase<RoleGAgentState>` | `RoleGAgentState` | 通用对话角色（YAML 配置、LLM streaming、tool calling） |

Studio empty-state generation 已降级为
`ScriptAuthoringPreviewGenerator` / `WorkflowAuthoringPreviewGenerator`
Application helper，不再作为 AI 层 GAgent 类型。

#### 1.2.3 Workflow 层

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `WorkflowGAgent` | `GAgentBase<WorkflowState>` | `WorkflowState` | 工作流定义 Actor（YAML 绑定、编译、版本管理） |
| `WorkflowRunGAgent` | `GAgentBase<WorkflowRunState>` | `WorkflowRunState` | 工作流运行 Actor（执行内核、步骤调度、状态持久化） |

#### 1.2.4 Scripting 层

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `ScriptCatalogGAgent` | `GAgentBase<ScriptCatalogState>` | `ScriptCatalogState` | 脚本目录索引 |
| `ScriptDefinitionGAgent` | `GAgentBase<ScriptDefinitionState>` | `ScriptDefinitionState` | 脚本定义（spec + version lifecycle） |
| `ScriptBehaviorGAgent` | `GAgentBase<ScriptBehaviorState>` | `ScriptBehaviorState` | 脚本行为绑定（definition → agent） |
| `ScriptEvolutionManagerGAgent` | `GAgentBase<ScriptEvolutionManagerState>` | `ScriptEvolutionManagerState>` | 脚本演化调度器（session lifecycle） |
| `ScriptEvolutionSessionGAgent` | `GAgentBase<ScriptEvolutionSessionState>` | `ScriptEvolutionSessionState` | 脚本演化会话（propose/promote/rollback） |

#### 1.2.5 Platform / Service 层

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `ServiceDefinitionGAgent` | `GAgentBase<ServiceDefinitionState>` | `ServiceDefinitionState` | 服务定义 |
| `ServiceRunGAgent` | `GAgentBase<ServiceRunState>` | `ServiceRunState` | 服务运行实例 |
| `ServiceRevisionCatalogGAgent` | `GAgentBase<ServiceRevisionCatalogState>` | `ServiceRevisionCatalogState` | 服务版本目录 |
| `ServiceConfigurationGAgent` | `GAgentBase<ServiceConfigurationState>` | `ServiceConfigurationState` | 服务配置 |
| `ServiceServingSetManagerGAgent` | `GAgentBase<ServiceServingSetState>` | `ServiceServingSetState` | 服务流量集管理 |
| `ServiceDeploymentManagerGAgent` | `GAgentBase<ServiceDeploymentState>` | `ServiceDeploymentState` | 服务部署管理 |
| `ServiceRolloutManagerGAgent` | `GAgentBase<ServiceRolloutExecutionState>` | `ServiceRolloutExecutionState` | 服务灰度发布 |
| `LlmSessionGAgent` | `GAgentBase<LlmSessionState>` | `LlmSessionState` | LLM 会话管理 |
| `ResponsesAgentToolStateGAgent` | `GAgentBase<ResponsesAgentToolState>` | `ResponsesAgentToolState` | Agent 工具状态 |

#### 1.2.6 Projection / CQRS 层（GAgent 承载的 Projection 编排）

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `ProjectionScopeGAgentBase<TContext>` | `GAgentBase<ProjectionScopeState>` | `ProjectionScopeState` | 投影作用域 Actor 基类 |
| `ProjectionMaterializationScopeGAgentBase<TContext>` | 继承 ProjectionScope | 同上 | 物化作用域 |
| `ProjectionSessionScopeGAgentBase<TContext>` | 继承 ProjectionScope | 同上 | 会话观察作用域 |

#### 1.2.7 Agents 目录（业务 Agent）

| GAgent | 基类 | 状态类型 | 职责 |
|---|---|---|---|
| `GAgentRegistryGAgent` | `GAgentBase<GAgentRegistryState>` | `GAgentRegistryState` | Agent 注册表 |
| `RoleCatalogGAgent` | `GAgentBase<RoleCatalogState>` | `RoleCatalogState` | 角色目录 |
| `StudioTeamGAgent` | `GAgentBase<StudioTeamState>` | `StudioTeamState` | Studio 团队 |
| `StudioMemberGAgent` | `GAgentBase<StudioMemberState>` | `StudioMemberState` | Studio 成员 |
| `StudioMemberBindingRunGAgent` | `GAgentBase<StudioMemberBindingRunState>` | `StudioMemberBindingRunState` | 成员绑定运行 |
| `UserConfigGAgent` | `GAgentBase<UserConfigGAgentState>` | `UserConfigGAgentState` | 用户配置 |
| `UserMemoryGAgent` | `GAgentBase<UserMemoryState>` | `UserMemoryState` | 用户记忆 |
| `ChatHistoryIndexGAgent` | `GAgentBase<ChatHistoryIndexState>` | `ChatHistoryIndexState` | 聊天历史索引 |
| `ChatConversationGAgent` | `GAgentBase<ChatConversationState>` | `ChatConversationState` | 聊天会话 |
| `ConnectorCatalogGAgent` | `GAgentBase<ConnectorCatalogState>` | `ConnectorCatalogState` | Connector 目录 |
| `DeviceRegistrationGAgent` | `GAgentBase<DeviceRegistrationState>` | `DeviceRegistrationState` | 设备注册 |
| `StreamingProxyGAgent` | `GAgentBase<StreamingProxyGAgentState>` | `StreamingProxyGAgentState` | 流式代理房间 |
| `ConversationGAgent` | `GAgentBase<ConversationGAgentState>` | `ConversationGAgentState` | 渠道对话（Lark/Telegram） |
| `ChannelBotRegistrationGAgent` | `GAgentBase<ChannelBotRegistrationStoreState>` | `ChannelBotRegistrationStoreState` | 渠道 Bot 注册 |
| `ChannelUserBindingGAgent` | `GAgentBase<ChannelUserBindingState>` | `ChannelUserBindingState` | 渠道用户绑定 |
| `ExternalIdentityBindingGAgent` | `GAgentBase<ExternalIdentityBindingState>` | `ExternalIdentityBindingState` | 外部身份绑定 |
| `AevatarOAuthClientGAgent` | `GAgentBase<AevatarOAuthClientState>` | `AevatarOAuthClientState` | OAuth 客户端管理 |
| `NyxIdChatGAgent` | `RoleGAgent` | `RoleGAgentState` | NyxID 对话 Agent |
| `ChatbotClassifierGAgent` | `RoleGAgent` | `RoleGAgentState` | 分类器 Agent |
| `HouseholdEntity` | `AIGAgentBase<HouseholdEntityState>` | `HouseholdEntityState` | 家户实体 Agent（IoT 设备管理） |
| `AgentRunGAgent` | `GAgentBase<AgentRunGAgentState>` | `AgentRunGAgentState` | NyxID 运行 Actor |
| `SkillRunnerGAgent` | `AIGAgentBase<SkillRunnerState>` | `SkillRunnerState` | 定时技能运行 |
| `UserAgentCatalogGAgent` | `GAgentBase<UserAgentCatalogState>` | `UserAgentCatalogState` | 用户 Agent 目录 |

---

## 2. GAgent 按职责分类（Taxonomy）

从上面的完整清单，按实际运行态职责可以归纳为 **6 种角色原型**：

### 2.1 Catalog / Index（目录索引型）

**特征**：拥有 `map<string, Entry>` 或 `repeated Entry` 的集合状态，提供 CRUD + tombstone + compact 操作。

| Agent | 集合语义 |
|---|---|
| `ScriptCatalogGAgent` | script_name → ScriptCatalogEntry |
| `ServiceRevisionCatalogGAgent` | service → revisions |
| `GAgentRegistryGAgent` | agent_type + name → registry entry |
| `RoleCatalogGAgent` | role_name → role spec |
| `ConnectorCatalogGAgent` | connector_name → connector config |
| `UserAgentCatalogGAgent` | user + agent_id → catalog entry |
| `ChatHistoryIndexGAgent` | user → conversation list |

**共性模式**：`Upsert → Tombstone → Compact`，状态内维护 `last_applied_event_version`，projection 直出 current-state read model。

### 2.2 Definition / Configuration（定义配置型）

**特征**：持有单个 spec/record 状态，支持 bind/update/versioned lifecycle。

| Agent | 持有物 |
|---|---|
| `WorkflowGAgent` | YAML definition + compile result |
| `ScriptDefinitionGAgent` | Script spec + version history |
| `ScriptBehaviorGAgent` | Definition → agent binding |
| `ServiceDefinitionGAgent` | Service spec |
| `ServiceConfigurationGAgent` | Service config overrides |
| `StudioTeamGAgent` | Team definition + membership |
| `StudioMemberGAgent` | Member profile + bindings |
| `UserConfigGAgent` | User preferences |
| `AevatarOAuthClientGAgent` | OAuth client spec |
| `ExternalIdentityBindingGAgent` | External identity binding |
| `ChannelBotRegistrationGAgent` | Bot registration per scope |
| `ChannelUserBindingGAgent` | Channel ↔ user binding |
| `DeviceRegistrationGAgent` | Device identity |

**共性模式**：`Bind/Initialize → Update → Versioned state → current-state read model`。

### 2.3 Run / Session / Execution（执行会话型）

**特征**：有 `status`、`correlation_id`、`timestamps`、`input/output`，是短生命周期事实拥有者。

| Agent | 运行物 |
|---|---|
| `WorkflowRunGAgent` | Workflow run (step execution kernel) |
| `AgentRunGAgent` | NyxID chat run |
| `StudioMemberBindingRunGAgent` | Member binding run |
| `ServiceRunGAgent` | Service execution run |
| `LlmSessionGAgent` | LLM session |
| `SkillRunnerGAgent` | Scheduled skill execution |
| `ConversationGAgent` | Channel conversation turn |

**共性模式**：`Start → Execute → Complete/Fail → Cleanup`，状态内有明确的 status lifecycle，可能包含 nested sub-states via `map<string, Any>`。

### 2.4 AI Role（AI 角色型）

**特征**：继承 `RoleGAgent` 或 `AIGAgentBase`，组合 LLM + Tools + History。

| Agent | 特化 |
|---|---|
| `RoleGAgent` | 通用角色（YAML 配置） |
| `NyxIdChatGAgent` | NyxID 对话（继承 RoleGAgent） |
| `ChatbotClassifierGAgent` | 分类器（继承 RoleGAgent） |
| `HouseholdEntity` | IoT 家户实体（AIGAgentBase） |

**共性模式**：`ChatRequestEvent → ChatStreamAsync → AG-UI event publishing → tool approval lifecycle → self continuation`。

### 2.5 Manager / Orchestrator（管理编排型）

**特征**：协调多个子 actor 的生命周期，持有跨 actor 编排状态。

| Agent | 编排物 |
|---|---|
| `ServiceServingSetManagerGAgent` | 服务流量集分配 |
| `ServiceDeploymentManagerGAgent` | 服务部署进度 |
| `ServiceRolloutManagerGAgent` | 服务灰度发布进度 |
| `ScriptEvolutionManagerGAgent` | 脚本演化 session 调度 |
| `ScriptEvolutionSessionGAgent` | 单次演化会话推进 |
| `StreamingProxyGAgent` | 流式房间管理（topic broadcast） |
| `ResponsesAgentToolStateGAgent` | Agent 工具状态追踪 |

**共性模式**：持有 child-reference 列表、progress tracking、completion aggregation。

### 2.6 Bridge / Adapter（桥接适配型）

**特征**：无状态或极轻状态，在 Actor 系统与外部系统之间做协议转换。

| Agent | 桥接物 |
|---|---|
| `UserMemoryGAgent` | User memory store |
| `ChatConversationGAgent` | Chat conversation store |

---

## 3. 底座能力评估：GAgent 是否胜任当前 Harness 工程？

### 3.1 底座已提供的能力

| 能力 | 来源 | 评价 |
|---|---|---|
| 统一事件管线 | `GAgentBase.HandleEventAsync` | ✅ 成熟。静态 + 动态模块管线、Hook 双通道、pipeline cache |
| Event Sourcing | `GAgentBase<TState>` | ✅ 成熟。Replay + Commit + Snapshot + OCC absorption |
| 配置合并 | `GAgentBase<TState, TConfig>` | ✅ 通用。class defaults + state overrides |
| AI 能力组合 | `AIGAgentBase<TState>` | ✅ 内聚。ChatRuntime + ToolManager + History + Hooks + Middleware |
| 消息发布 | `PublishAsync / SendToAsync` | ✅ 清晰。Topology 路由 + Direct 路由 |
| Durable Callback | `ScheduleSelfDurableTimeoutAsync / TimerAsync` | ✅ 已事件化 |
| External Links | `IExternalLinkAware + ExternalLinkManager` | ✅ 插件式 |
| Projection 编排 | `ProjectionScopeGAgentBase` 系列 | ✅ Actor 化，符合 AGENTS.md 约束 |
| Runtime 可替换 | `LocalActorRuntime / OrleansActorRuntime` | ✅ runtime-neutral 抽象 |

### 3.2 底座当前的不适配点

#### 3.2.1 Agent Loop = Agent Continuation 的缺失

当前系统中的 "agent loop"（LLM tool calling 循环、workflow step 推进循环、对话 turn 循环）**没有统一抽象为 agent 间消息传递**。

现状：
- `ToolCallLoop.ExecuteAsync` 是 **进程内同步循环**，在单个 actor turn 内跑完全部 tool rounds
- `WorkflowExecutionKernel` 是 **EventModule 内部循环**，通过模块内部状态机推进
- `ConversationGAgent` 的 turn runner 是 **durable callback + 自事件** 模式（最接近理想态）

理想态（AGENTS.md "agent continuation" 语义）：
```
Agent A 执行一步 → 发布 intermediate event → Agent B（或 A 自身）消费 → 发布 next event → ...
```

**差距**：底层基础设施（EventEnvelope、PublishAsync、SendToAsync、Durable Callback）已完全支持 agent continuation，但上层缺少一个 **continuation protocol** 来把 "loop" 声明式地建模为 agent 间消息传递。ToolCallLoop 和 WorkflowExecutionKernel 各自内部实现了循环，没有复用同一套 continuation 抽象。

#### 3.2.2 Run / Session 型 Agent 缺乏统一基类

当前所有 Run/Session 型 GAgent 都直接继承 `GAgentBase<TState>`，各自重新实现：
- Status lifecycle（start → running → completed/failed）
- Input/Output binding
- Timeout and cleanup
- Correlation tracking
- Completion event publishing

`AgentRunGAgent`、`WorkflowRunGAgent`、`StudioMemberBindingRunGAgent`、`ServiceRunGAgent`、`SkillRunnerGAgent` 都有高度相似的状态结构，但没有任何共享抽象。

#### 3.2.3 Catalog 型 Agent 缺乏通用 CatalogGAgent

所有 Catalog/Index 型 Agent 重复实现：
- `map<string, Entry>` 状态
- Upsert/Tombstone/Compact 事件
- `last_applied_event_version` 追踪
- Current-state read model projection

没有 `CatalogGAgentBase<TKey, TEntry>` 这样的通用基类。

#### 3.2.4 Streaming / Continuation 的半成品

`TurnStreamingReplySink` 和 `NyxIdChatStreamingRunner` 各自实现了 streaming → actor → SSE 的半链路，但没有统一的 "streaming continuation" 抽象把 LLM 流式输出、actor 间传递、SSE/WS 推送串成一条声明式管道。

---

## 4. 类型精简分析：GAgent Types 能否进一步抽象？

### 4.1 潜在的通用基类

#### 4.1.1 `RunGAgentBase<TState>` — 执行会话基类

**适用对象**：`AgentRunGAgent`、`WorkflowRunGAgent`、`StudioMemberBindingRunGAgent`、`ServiceRunGAgent`、`SkillRunnerGAgent`

**可抽取的通用能力**：
```
abstract class RunGAgentBase<TState> : GAgentBase<TState>
  where TState : class, IMessage<TState>, new()
{
  // 通用 status lifecycle
  protected abstract string Status { get; }     // running/completed/failed/stopped
  protected abstract string RunId { get; }
  
  // 通用 completion publishing
  protected Task PublishRunCompletedAsync(...);
  protected Task PublishRunFailedAsync(...);
  
  // 通用 cleanup (via durable callback)
  protected Task ScheduleCleanupAsync(TimeSpan delay);
  
  // 通用 stale-input detection
  protected bool IsStaleRun(string expectedRunId);
}
```

**预估影响**：5 个 Run 型 GAgent 可共享生命周期管理代码，减少 ~30% 重复逻辑。

#### 4.1.2 `CatalogGAgentBase<TState, TKey, TEntry>` — 目录索引基类

**适用对象**：`ScriptCatalogGAgent`、`GAgentRegistryGAgent`、`RoleCatalogGAgent`、`ConnectorCatalogGAgent`、`UserAgentCatalogGAgent`、`ChatHistoryIndexGAgent`

**可抽取的通用能力**：
```
abstract class CatalogGAgentBase<TState> : GAgentBase<TState>
  where TState : class, IMessage<TState>, new()
{
  // 通用 upsert/tombstone/compact 事件处理
  // 通用 last_applied_event_version 追踪
  // 通用 current-state read model 投影契约
}
```

**预估影响**：7 个 Catalog 型 GAgent 可共享 CRUD + projection 逻辑。

#### 4.1.3 Agent Continuation Protocol — 不新增 GAgent type，而是新增行为协议

**关键洞察**：Agent continuation 不需要新的 GAgent 基类，而是需要一套 **消息协议** 把 "loop" 建模为 agent 间消息传递。

**建议的 Continuation 协议**：
```protobuf
// 一个 continuation step 完成后，发布此事件请求下一步
message ContinueRequested {
  string continuation_id = 1;    // 会话标识
  string step_type = 2;          // 当前步骤类型
  google.protobuf.Any step_result = 3;  // 步骤结果
  google.protobuf.Any step_context = 4; // 步骤上下文（传递到下一步）
}

// continuation 完成或失败
message ContinuationCompleted { ... }
message ContinuationFailed { ... }
```

**适用范围**：
- ToolCallLoop → 改为：RoleGAgent 每轮 tool call 结束后发布 `ContinueRequested` → 自身消费 → 下一轮
- WorkflowExecutionKernel → 改为：WorkflowRunGAgent 每步结束后发布 `ContinueRequested` → 自身消费 → 下一步
- ConversationGAgent turn runner → 已接近此模式

**好处**：
1. 每个 "loop iteration" 变成一次 actor turn，天然可观测、可恢复
2. 中间状态通过 `step_context` 在事件链中传递，不再依赖进程内变量
3. 天然适配 streaming：每个 continuation step 可以发布 streaming chunk
4. 可与现有 `ScheduleSelfDurableTimeoutAsync` 结合，实现 timeout-based continuation

### 4.2 不建议合并的 Types

以下 GAgent 虽然可能有表面相似性，但 **业务语义足够独立**，不建议强行合并：

| GAgent | 不合并原因 |
|---|---|
| `ConversationGAgent` | 渠道特化的 turn runner（Lark streaming、NyxRelay）逻辑复杂且不可通用 |
| `StreamingProxyGAgent` | topic/message broadcast 语义与 Catalog/Run 都不同 |
| `ServiceRolloutManagerGAgent` | 灰度发布是专用 domain，state machine 复杂 |
| `ScriptEvolutionSessionGAgent` | 演化 session 的 propose/promote/rollback 有专用状态机 |

### 4.3 精简路径总结

| 动作 | 影响 | 复杂度 |
|---|---|---|
| 引入 `RunGAgentBase<TState>` | 5 个 GAgent 共享生命周期 | 中 |
| 引入 `CatalogGAgentBase<TState>` | 7 个 GAgent 共享 CRUD + projection | 中 |
| 引入 Continuation Protocol | ToolCallLoop + WorkflowKernel 统一为消息传递 | 高 |
| 保持现状 | 零风险，但重复度持续累积 | — |

---

## 5. Agent Continuation 深度分析

### 5.1 为什么 Agent Loop 应该实现为 Agent Continuation

当前代码中的三种 "loop" 实现方式：

| Loop 类型 | 当前实现 | 问题 |
|---|---|---|
| LLM Tool Calling Loop | `ToolCallLoop.ExecuteAsync` — 进程内 `while` 循环 | 1. 整个 loop 在单次 actor turn 内完成，无法被外部观察 2. 中间 tool call 状态不在 event store 中 3. 无法跨节点恢复 |
| Workflow Step Loop | `WorkflowExecutionKernel` — EventModule 内部推进 | 1. 模块内部状态机不经过 actor event pipeline 2. 模块状态通过 `map<string, Any>` 持久化，但不产出独立 domain event 3. 可观测性受限 |
| Conversation Turn Loop | `ConversationGAgent` + durable callback + self-event | ✅ 最接近理想态。每轮 turn 都是独立 event，可恢复、可观测 |

**核心论点**：Agent continuation 不是 "更好的 loop"，而是 **把 loop 的每一步都变成可观测、可恢复、可跨节点的事实**。

### 5.2 Continuation 与现有机制的配合

```
                     ┌─────────────────────┐
                     │   GAgentBase         │
                     │   HandleEventAsync   │
                     └──────┬──────────────┘
                            │
                     ┌──────▼──────────────┐
                     │  Continuation Step   │
                     │  (EventHandler)      │
                     └──────┬──────────────┘
                            │
                 ┌──────────┼──────────┐
                 │          │          │
          ┌──────▼──┐ ┌─────▼────┐ ┌──▼──────────┐
          │ Publish │ │ Persist  │ │ Schedule    │
          │ Stream  │ │ Domain   │ │ Durable     │
          │ Chunk   │ │ Event    │ │ Timeout     │
          └─────────┘ └────┬─────┘ └──┬──────────┘
                          │           │
                          │    ┌──────▼──────────┐
                          │    │ Self-Continue    │
                          │    │ (timeout or      │
                          │    │  completion)     │
                          │    └──────┬──────────┘
                          │           │
                          └─────► Next Turn
```

这个模型完全在现有 `GAgentBase` 的能力范围内：
- `PublishAsync` → streaming chunks
- `PersistDomainEventAsync` → 事实持久化
- `ScheduleSelfDurableTimeoutAsync` → timeout-based continuation
- `SendToAsync(Id, continueEvent)` → self continuation

**不需要新的 GAgent 基类，只需要一套 continuation 消息协议 + 推荐模式。**

### 5.3 从 ToolCallLoop 到 Agent Continuation 的演进路径

**当前**：
```csharp
// ToolCallLoop.ExecuteAsync — 进程内循环
while (rounds < maxRounds) {
    var response = await CallLLMAsync(...);
    if (no tool calls) return response;
    var results = await ExecuteToolCallsAsync(...);
    history.AddToolResults(results);
    rounds++;
}
```

**目标态**：
```csharp
// RoleGAgent.HandleContinueRequested — 每个 continuation step 是一个 actor turn
[EventHandler(AllowSelfHandling = true)]
public async Task HandleContinueRequested(ContinueRequested evt)
{
    // 1. 从 step_context 恢复当前轮次状态
    // 2. 调用 LLM (streaming，每个 chunk 通过 PublishAsync 发布)
    // 3. 如果有 tool calls → 执行 → Persist tool results → SendToAsync(Self, next ContinueRequested)
    // 4. 如果没有 tool calls → Persist final response → Publish ContinuationCompleted
}
```

**代价**：每个 tool call round 变成一次完整的 actor turn（event store write + commit），延迟略增。
**收益**：每步可观测、可恢复、可跨节点、可被 hook 拦截。

### 5.4 WorkflowExecutionKernel 的 Continuation 化

Workflow 实际上已经部分 continuation 化了（`WorkflowRunGAgent` 的步骤推进），但步骤内部状态（`ExecutionStates` map）的变更不经过 `PersistDomainEventAsync`。如果引入 continuation 协议：

- 每个步骤完成 → `PersistDomainEventAsync(StepCompletedEvent)` → `PublishAsync(ContinueRequested)`
- 模块状态变更 → 独立 domain event（而非 `map<string, Any>` 的批量覆盖）
- 中间结果 → 可被 projection 直接消费

---

## 6. 结论与建议

### 6.1 底座评价

**GAgent 底座能力是合格的**。它提供了：
- 完整的事件管线、Event Sourcing、配置合并
- Runtime-neutral 的创建/调度/发布抽象
- AI 能力组合（LLM + Tools + Hooks）
- Durable callback（timeout/timer）
- Projection 编排

**不存在需要重写底座的理由**。

### 6.2 需要补充的是上层协议和模式，而非底层重构

| 优先级 | 建议 | 理由 |
|---|---|---|
| P0 | 定义 **Continuation Protocol**（消息协议，不是新基类） | 把 agent loop 统一为可观测的消息传递，这是架构层面最大的缺失 |
| P1 | 引入 `RunGAgentBase<TState>` | 5 个 Run 型 GAgent 的 status lifecycle 高度重复 |
| P2 | 引入 `CatalogGAgentBase<TState>` | 7 个 Catalog 型 GAgent 的 CRUD 模式高度重复 |
| P3 | Streaming Continuation 管道统一 | 把 `TurnStreamingReplySink` 和 `NyxIdChatStreamingRunner` 统一为 continuation protocol 的一部分 |

### 6.3 不建议做的事

- ❌ 不建议把所有 GAgent 合并成少数几个 "万能" 类型（业务语义差异太大）
- ❌ 不建议为 continuation 新增 GAgent 基类（现有 `GAgentBase` + continuation protocol 已足够）
- ❌ 不建议在 Foundation 层做 AI/workflow 特化（保持 Foundation 通用，特化在 AI/Workflow 层）
- ❌ 不建议一步到位把 ToolCallLoop 改成 continuation（渐进式：先在 RoleGAgent 加 continuation 入口，ToolCallLoop 作为 fast path 并存）

---

## 附录 A：GAgent Types 统计

| 分类 | 数量 | 占比 |
|---|---|---|
| Catalog / Index | 8 | 19% |
| Definition / Configuration | 13 | 31% |
| Run / Session / Execution | 7 | 17% |
| AI Role | 6 | 14% |
| Manager / Orchestrator | 9 | 21% |
| Bridge / Adapter | 2 | 5% |
| Projection Scope | 3 | 7% |
| **总计** | **~43** | 100% |

> 注：部分 GAgent 可同时归入多个分类（如 `ConversationGAgent` 既是 Run 型也是 Bridge 型）。统计按主要职责归入。

## 附录 B：关键文件索引

| 文件 | 作用 |
|---|---|
| `src/Aevatar.Foundation.Core/GAgentBase.cs` | 无状态底座 |
| `src/Aevatar.Foundation.Core/GAgentBase.TState.cs` | 有状态底座 + ES |
| `src/Aevatar.Foundation.Core/GAgentBase.TState.TConfig.cs` | 可配置底座 |
| `src/Aevatar.AI.Core/AIGAgentBase.cs` | AI 能力组合 |
| `src/Aevatar.AI.Core/RoleGAgent.cs` | 角色型 Agent |
| `src/Aevatar.AI.Core/Chat/ChatRuntime.cs` | Chat + Tool Loop |
| `src/Aevatar.AI.Core/Tools/ToolCallLoop.cs` | LLM Tool Calling 循环 |
| `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs` | Workflow Run Actor |
| `src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs` | Workflow 步骤推进内核 |
| `agents/Aevatar.GAgents.Channel.Runtime/Conversation/ConversationGAgent.cs` | 渠道对话（最接近 continuation 模式） |
| `agents/Aevatar.GAgents.NyxidChat/AgentRunGAgent.cs` | NyxID Run Actor |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs` | Projection 编排基类 |
