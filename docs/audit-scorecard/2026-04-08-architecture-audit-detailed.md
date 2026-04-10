---
title: Architecture Audit — Detailed Analysis Report
status: active
owner: Loning
---

# Architecture Health Scorecard — 2026-04-08 (Detailed)

Audit scope: Milestone-oriented (Living with AI Demo 04-17, NyxID M0 04-18)
Audit method: Automated baseline plus manual deep dives on projection, lifecycle, and guard blind spots
Reviewed by: arch-audit skill, 5 parallel exploration agents

> 本文档是 `2026-04-08-architecture-audit.md` 的深度展开版，包含每个维度的代码级证据、数据流分析和修复方案。
>
> 评分只纳入已验证证据；需要运行时进一步确认的风险在文中标记为 `NEEDS_VALIDATION`，不直接计入总分。

---

## 1. 综合评分

| Dimension | Score | Summary |
|-----------|-------|---------|
| CI Guards 合规 | 8/10 | 全通过，但 agents/+apps/ 不在扫描范围；3 条规则未自动化 |
| 分层合规 | 6/10 | Application 层干净；Host 层 ScopeServiceEndpoints 是 God Class，直达 Infrastructure |
| 投影一致性 | 4/10 | Host 层 6+ 处直接订阅 EventEnvelope + TCS 阻塞，完全绕过 Projection Pipeline |
| 读写分离 | 8/10 | Application 层 Query 全部 CLEAN；Host 层投影 bypass 是唯一破口 |
| 序列化 | 7/10 | 核心路径全 Protobuf；JSON 仅在 Host/Adapter 边界，合规 |
| Actor 生命周期 | 5/10 | StreamingProxyGAgent 影子状态机 + agents/ 3 个 ConcurrentDictionary 单例 |
| 前端可构建性 | 2/10 | CLI Frontend + Console Web 均构建失败 |
| 测试覆盖 (全局) | 3/10 | 聚合覆盖率掩盖关键项目缺口；认证、Orleans Streaming 与 agents/ 仍缺回归保护 |
| AI.Core 中间件合规 | 4/10 | SkillRegistry 7 处 lock + StreamingToolExecutor + ToolApprovalMiddleware 违规 |
| 工作流引擎韧性 | 5/10 | _executionItems 非持久化 + Sub-Workflow 无超时 + Lease 恢复风险 |
| 投影端口合规 | 5/10 | EventSinkProjectionLifecyclePortBase §111 违规：ConcurrentDictionary 做订阅管理 |
**综合架构健康度: 5.2 / 10** (11 dimensions)

---

## 2. 投影一致性 — 深度分析 (4/10)

### 2.1 违规全景

共发现 **6 处投影管线绕过**，分布在 3 个文件中：

| # | 文件 | 行号 | 模式 | 严重度 |
|---|------|------|------|--------|
| P1 | `ScopeGAgentEndpoints.cs` | 352-430 | TCS + SubscribeAsync\<EventEnvelope\> + 120s await | Critical |
| P2 | `NyxIdChatEndpoints.cs` | 136-154 | TCS\<string\> + SubscribeAsync + 120s await | Critical |
| P3 | `NyxIdChatEndpoints.cs` | 353-371 | 同 P2 (ToolApproval stream) | Critical |
| P4 | `NyxIdChatEndpoints.cs` | 769-809 | 同 P2 (ContinueChat stream) | Critical |
| P5 | `StreamingProxyEndpoints.cs` | 129-132 | SubscribeAsync + MapAndWriteEvent (无 TCS) | High |
| P6 | `StreamingProxyEndpoints.cs` | 241-244 | 同 P5 (MessageStream) | High |

### 2.2 P1 详解 — ScopeGAgentEndpoints.HandleDraftRunAsync

**违规代码路径** (`src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs`):

```
HTTP Request → CreateAsync(agentType) → SubscribeAsync<EventEnvelope>(actor.Id)
  → callback: TryMapEnvelopeToAguiEvent(envelope) → writer.WriteAsync(aguiEvent)
  → if (RunFinished|RunError|TextMessageEnd) → tcs.TrySetResult()
  → main thread: await Task.WhenAny(tcs.Task, Task.Delay(120_000))
```

**违反的 CLAUDE.md 规则**:
1. **统一投影链路** — AGUI 与 CQRS 应走同一套 Projection Pipeline，禁止双轨
2. **EventEnvelope 是唯一投影传输壳** — Host 端不应做映射决策
3. **跨 actor 等待 continuation 化** — 禁止当前 turn 同步等待
4. **投影编排 Actor 化** — 禁止中间层进程内注册表/字典持有事实状态

**TryMapEnvelopeToAguiEvent 映射范围** (行 507-641):
- `TextMessageStartEvent` → `AGUIEvent.TextMessageStart`
- `TextMessageContentEvent` → `AGUIEvent.TextMessageContent`
- `TextMessageEndEvent` → `AGUIEvent.TextMessageEnd`
- `ToolCallStartEvent` → `AGUIEvent.ToolCallStart`
- `ToolCallEndEvent` → `AGUIEvent.ToolCallEnd`
- `RunFinishedEvent` → `AGUIEvent.RunFinished`
- `RunErrorEvent` → `AGUIEvent.RunError`

这 7 种映射全部在 Host endpoint 内完成，本应在 Projector 中统一处理。

### 2.3 P2-P4 详解 — NyxIdChatEndpoints (agents/)

**三处同构违规** (`agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.cs`):

```
HandleChatStreamAsync (L136):        TCS<string> + SubscribeAsync + MapAndWriteEventAsync + 120s
HandleToolApprovalStreamAsync (L353): TCS<string> + SubscribeAsync + MapAndWriteEventAsync + 120s
HandleContinueChatStreamAsync (L769): TCS<string> + SubscribeAsync + MapAndWriteEventAsync + 120s
```

每处的 `MapAndWriteEventAsync` 在 callback 中解析 EventEnvelope，检测终端帧（TEXT_MESSAGE_END 等），触发 `tcs.TrySetResult(terminalFrame)`。

**NEEDS_VALIDATION — 分布式影响**: `SubscribeAsync` 实现依赖本地 stream provider。若 Orleans 跨节点部署时订阅无法跨 silo 透传，Actor 在远程节点执行时可能导致 **SSE 流静默超时**。这一点需要结合实际 provider 配置做运行时验证。

### 2.4 正面证据 — 正确的投影路径

**WorkflowExecutionRunEventProjector** (`src/workflow/Aevatar.Workflow.Presentation.AGUIAdapter/`):
- 继承 `ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, WorkflowRunEventEnvelope>`
- 在 `ResolveSessionEventEntries` 内做类型映射
- 返回 `ProjectionSessionEventEntry` 由管线分发
- 支持异步多消费者（readmodel, search, graph）

**WorkflowExecutionCurrentStateProjector** (`src/workflow/Aevatar.Workflow.Projection/Projectors/`):
- 实现 `ICurrentStateProjectionMaterializer<WorkflowExecutionMaterializationContext>`
- 通过投影管线接收 EventEnvelope，物化到 document store
- 版本号来自权威 actor committed state

### 2.5 修复方案

**目标架构**:
```
HTTP → Send Command (receipt) → SSE subscribe to IProjectionSessionEventHub<AGUIEvent>
                                     ↑
                    Projection Pipeline (统一入口)
                         ↑
                    Committed EventEnvelope from Actor
```

**P0**: 创建 `AGUIEventProjector`（类似 WorkflowExecutionRunEventProjector），在投影管线内统一映射 AI 事件 → AGUIEvent
**P1**: 创建 `IProjectionSessionEventHub<AGUIEvent>` 会话级分发
**P2**: 改 ScopeGAgentEndpoints / NyxIdChatEndpoints 为 receipt-based + session-hub subscription

---

## 3. 中间件状态泄露 — 深度分析 (5/10)

### 3.1 CRITICAL 违规（生产阻塞）

| # | 文件 | 字段 | DI Scope | 数据 | 风险 |
|---|------|------|---------|------|------|
| S1 | `agents/.../NyxIdChatActorStore.cs:20` | `ConcurrentDictionary<string, List<ActorEntry>>` | Singleton | 对话 actor 注册 | 进程重启丢失所有对话 |
| S2 | `agents/.../StreamingProxyActorStore.cs:11` | `ConcurrentDictionary<string, List<RoomEntry>>` | Singleton | 房间注册 | 无法多节点 |
| S3 | `agents/.../StreamingProxyActorStore.cs:12` | `ConcurrentDictionary<string, List<ParticipantEntry>>` | Singleton | 参与者注册 | 无法多节点 |
| S4 | `agents/.../StreamingProxyGAgent.cs:34` | `StreamingProxyGAgentState _proxyState` | Actor field | 影子状态机 | grain 重激活状态不一致 |

**S1 代码上下文** — NyxIdChatActorStore 注册为 Singleton:
```csharp
// ServiceCollectionExtensions.cs:14
services.TryAddSingleton<NyxIdChatActorStore>();
```
代码注释明确标注 "Phase 1: in-memory store. Phase 2: persistent storage"，但 Phase 2 未实现。

**S4 代码上下文** — StreamingProxyGAgent 影子状态:
```csharp
// StreamingProxyGAgent.cs:34
private StreamingProxyGAgentState _proxyState = new();

// TransitionState (L107-116):
// 在 TransitionState 中通过 ApplyProxyEvent() 修改 _proxyState
// 但 _proxyState 不参与 event sourcing，grain 重激活后丢失
```

### 3.2 HIGH 违规（缺过期/一致性机制）

| # | 文件 | 字段 | 问题 |
|---|------|------|------|
| S5 | `src/.../EndpointSchemaProvider.cs:20-21` | 2 个 ConcurrentDictionary (schema + descriptor cache) | 500 项上限但无 TTL，跨节点不一致 |
| S6 | `src/.../FileBackedWorkflowCatalogPort.cs:80` | `Dictionary<string, ParsedWorkflowCacheEntry>` | 无 TTL 过期机制 |

### 3.3 MEDIUM 违规（设计允许但需确认）

| # | 文件 | 字段 | 备注 |
|---|------|------|------|
| S7 | `src/.../WorkflowStepTargetAgentResolver.cs:18` | `ConcurrentDictionary<string, Type>` (Singleton) | 类型缓存，无 TTL |
| S8 | `src/.../CachedScriptBehaviorArtifactResolver.cs:9` | `ConcurrentDictionary<string, Lazy<...>>` (Singleton) | 编译 artifact 缓存 |
| S9 | `src/.../ToolApprovalMiddleware.cs:23` | `Dictionary<string, int>` | denial 计数，每 agent 创建 |

### 3.4 InMemory 基础设施（仅限开发/测试）

| 文件 | 字段 | 备注 |
|------|------|------|
| `InMemoryEventStore.cs:22` | `ConcurrentDictionary<string, EventStreamState>` | 需 guard 防止生产使用 |
| `InMemoryStateStore.cs:15` | `ConcurrentDictionary<string, TState>` | 同上 |
| `InMemoryStreamProvider.cs:20` | `ConcurrentDictionary<string, InMemoryStream>` | 同上 |
| `LocalActorRuntime.cs:24` | `ConcurrentDictionary<string, LocalActor>` | 本地运行时注册表 |

---

## 4. 查询/读诚实性 — 深度分析 (8/10)

### 4.1 结论: Application 层 CLEAN

所有 7 个 Application 层查询服务均严格遵守 readmodel-only 原则:

| 查询服务 | 依赖的 Reader 接口 | 状态 |
|---------|-------------------|------|
| `ServiceLifecycleQueryApplicationService` | `IServiceCatalogQueryReader`, `IServiceRevisionCatalogQueryReader`, `IServiceDeploymentCatalogQueryReader` | CLEAN |
| `ServiceServingQueryApplicationService` | `IServiceServingSetQueryReader`, `IServiceRolloutQueryReader`, `IServiceTrafficViewQueryReader` | CLEAN |
| `WorkflowExecutionQueryApplicationService` | `IWorkflowExecutionCurrentStateQueryPort`, `IWorkflowExecutionArtifactQueryPort` | CLEAN |
| `ScopeScriptQueryApplicationService` | `IScriptCatalogQueryPort` | CLEAN |
| `ScopeWorkflowQueryApplicationService` | `IServiceLifecycleQueryPort`, `IWorkflowActorBindingReader` | CLEAN |
| `ServiceGovernanceQueryApplicationService` | `IServiceConfigurationQueryReader` | CLEAN |
| `ScriptReadModelQueryApplicationService` | `IScriptReadModelQueryPort` | CLEAN |

**违规检查结果**:
```
IEventStore 引用:          0 matches
Replay 调用:               0 matches
GetGrain 直接访问:          0 matches
.State 在查询路径:          0 matches
Query-time 事件重放:        0 matches
写端基础设施导入:            0 matches
```

所有 Query Reader 实现均使用 `IProjectionDocumentReader<TReadModel, TKey>`，走标准投影 document store。

### 4.2 唯一破口: Host 层投影绕过

8/10 而非 10/10 的原因：Host 层 ScopeServiceEndpoints 中的 `HandleStaticGAgentChatStreamAsync` (L956-1070) 和 `HandleScriptingServiceChatStreamAsync` (L1114-1194) 直接调用 `actorRuntime.CreateAsync` / `actor.HandleEventAsync`，绕过了 Application 层的 Command/Query 边界。

---

## 5. Actor 生命周期 — 深度分析 (5/10)

### 5.1 影子状态机: StreamingProxyGAgent

**文件**: `agents/Aevatar.GAgents.StreamingProxy/StreamingProxyGAgent.cs:34`

```csharp
private StreamingProxyGAgentState _proxyState = new();
```

**问题链**:
1. `_proxyState` 包含 RoomName, Messages (List\<StreamingProxyChatMessage\>), Participants
2. 在 `TransitionState()` (L107-116) 中通过 `ApplyProxyEvent()` 修改
3. **不参与 event sourcing** — grain 重激活后，`_proxyState` 重置为 `new()`
4. 但基础 `RoleGAgentState` 可能已恢复 → 两个状态不一致

**修复**: 将 _proxyState 字段迁移到 `RoleGAgentState.CustomFields` 或创建专用事件状态。

### 5.2 线程安全: 全部 CLEAN

```
lock 关键字:               0 matches in actors
Monitor 类:               0 matches in actors
ConcurrentDictionary:     0 matches in actor classes
Task.Run 直接修改状态:      0 matches
.Wait()/.Result:          0 matches in actors
```

所有 actor 遵守单线程执行模型。

### 5.3 自续期模式: WorkflowRunGAgent (正确范例)

```csharp
// WorkflowRunGAgent.cs:93-94
(actorId, evt, token) => SendToAsync(actorId, evt, token),
(callbackId, dueTime, evt, token) =>
    ScheduleSelfDurableTimeoutAsync(callbackId, dueTime, evt, ct: token)
```

遵循标准 self-message 进入 inbox 再消费的模式。

---

## 6. Host 层耦合度 — 深度分析 (6/10)

### 6.1 ScopeServiceEndpoints — God Class

**文件**: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs`

| 指标 | 值 | 阈值 |
|------|---|------|
| 行数 | 2329 | <500 |
| 方法数 | 38+ | <10 |
| 命名空间引用 | 30 | <8 |
| 职责数 | 6 | 1-2 |
| 最高圈复杂度 | ~30 | <15 |
| 类型耦合 | 205 | <96 |

**6 个混合职责**:
1. 工作流管理 (HandleDraftRunAsync, HandleInvokeDefaultChatStreamAsync... 15+ methods)
2. 服务生命周期 (HandleGetServiceRevisionsAsync... 8+ methods)
3. 服务绑定 (HandleUpsertBindingAsync... 6+ methods)
4. 服务运行执行 (HandleInvokeAsync, HandleResumeRunAsync... 6+ methods)
5. GAgent 静态部署 (HandleStaticGAgentChatStreamAsync — 直操 IActor)
6. 脚本服务执行 (HandleScriptingServiceChatStreamAsync — 直操 IActor)

**分层违规**:
- 直达 `Workflow.Infrastructure.CapabilityApi` (应走应用层)
- 直达 `CQRS.Core.Abstractions` (应走应用层)
- 直操 `IActorRuntime` / `IActor` (L956-1070, L1114-1194)

### 6.2 ScopeGAgentEndpoints — 部分 God Class

**文件**: `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs`

| 指标 | 值 | 阈值 |
|------|---|------|
| 行数 | 835 | <500 |
| 命名空间引用 | 27 | <8 |
| 职责数 | 4 | 1-2 |
| 类型耦合 | 111 | <96 |

**4 个混合职责**:
1. GAgent 类型枚举与反射 (~200 行)
2. GAgent 草稿运行 (~250 行, 含投影绕过)
3. GAgent 演员 CRUD (~80 行)
4. 事件映射与转换 (~200 行, TryMapEnvelopeToAguiEvent)

### 6.3 agents/ 项目依赖

| 项目 | 不当依赖 |
|------|---------|
| NyxidChat | `Aevatar.Studio.Infrastructure` (应走抽象) |
| StreamingProxy | CLEAN |
| ChatbotClassifier | CLEAN |

---

## 7. CI Guards — 盲区分析 (9/10)

### 7.1 扫描范围矩阵

| 目录 | architecture_guards.sh | 专项 guards | 覆盖 |
|------|----------------------|------------|------|
| `src/` | YES | YES (12+ scripts) | FULL |
| `test/` | YES | YES | FULL |
| `demos/` | YES | PARTIAL | OK |
| **`agents/`** | **NO** | **NO** | **NONE** |
| **`apps/`** | **NO** | **NO** | **NONE** |
| `tools/Cli/Frontend/` | NO | playground_asset_drift | PARTIAL |

### 7.2 规则覆盖矩阵

| CLAUDE.md 规则 | Guard 脚本 | 行号 | agents/ 覆盖 |
|---------------|-----------|------|-------------|
| GetAwaiter().GetResult() | architecture_guards.sh | L50 | NO |
| TypeUrl.Contains() | architecture_guards.sh | L345 | NO |
| Workflow.Core ← AI.Core | architecture_guards.sh | L539 | N/A |
| 中间层 ID 映射 Dic | architecture_guards.sh | L729-776 | NO |
| 投影端口 actorId 反查 | architecture_guards.sh | L695-700 | NO |
| Reducer 必测试引用 | architecture_guards.sh | L446-486 | NO |
| EventTypeUrl 路由 | projection_route_mapping_guard.sh | L1-96 | NO |

**最严重盲区**: `agents/` 是 NyxID M0 关键路径，但完全不在任何 CI guard 扫描范围内。

### 7.3 缺失的 Guard

| 建议新增 | 优先级 | 原因 |
|---------|--------|------|
| `agents/` 加入 architecture_guards.sh 扫描根 | P0 | NyxID M0 关键路径无守卫 |
| Host 层禁止 `SubscribeAsync<EventEnvelope>` | P0 | 投影绕过是最大漂移 |
| Host 层禁止直操 `IActorRuntime.CreateAsync` | P1 | 端点不应管理 actor 生命周期 |
| InMemory stores 禁止在非测试 DI 注册 | P1 | 防止生产泄漏 |
| Frontend build check (npm/pnpm/bun) | P2 | Demo 可构建性 |

---

## 8. 序列化合规

```
Proto 定义文件: 12 个
JSON 在 State/Event 路径: 0 matches
JSON 仅在边界: ChatWebSocketCommandParser, SseChatTransport, EventQueryTool (全部合规)
```

---

## 9. 漂移清单 (Updated)

### MILESTONE_BLOCKER (影响 04-17/04-18)

| # | 位置 | 违反规则 | 严重度 | 详情 |
|---|------|---------|--------|------|
| 1 | `agents/.../NyxIdChatActorStore.cs:20` | 中间层状态约束 | Critical | Singleton ConcurrentDictionary。进程重启丢失所有对话。Phase 2 (持久化) 未实现。 |
| 2 | `agents/.../StreamingProxyActorStore.cs:11-12` | 中间层状态约束 | Critical | 2 个 Singleton ConcurrentDictionary (rooms + participants)。同 #1。 |
| 3 | `agents/.../StreamingProxyGAgent.cs:34` | Actor 执行模型 | Critical | `_proxyState` 影子状态机不参与 event sourcing，grain 重激活后状态不一致。 |
| 4 | `agents/` 全部 3 个项目 | 测试要求 | Critical | 零测试覆盖。NyxID M0 关键路径无自动化验证。 |
| 5 | `test/.../ScopeServiceEndpointsTests.cs:1087` | — | High | InvokeStreamEndpoint 3 个测试失败 (500 error)。 |
| 6 | `tools/Aevatar.Tools.Cli/Frontend/` | — | High | TypeScript 编译失败。缺 @types/node, @tanstack/react-virtual, vitest。 |
| 7 | `apps/aevatar-console-web/` | — | High | 构建失败。`max` CLI 未安装。 |

### ARCHITECTURAL_DEBT (记录但不修)

| # | 位置 | 违反规则 | 严重度 | 保质期 |
|---|------|---------|--------|--------|
| 8 | `ScopeGAgentEndpoints.cs:352-430` | 统一投影链路 | Critical | v0.2 投影统一时解决 |
| 9 | `NyxIdChatEndpoints.cs:136,353,769` | 统一投影链路 | Critical | 同上，3 处同构违规 |
| 10 | `StreamingProxyEndpoints.cs:129,241` | 统一投影链路 | High | 同上，2 处 |
| 11 | `ScopeServiceEndpoints.cs` | God Class | High | 2329 行/38+ 方法/6 职责/205 类型耦合 |
| 12 | `ScopeServiceEndpoints.cs:956-1070` | 分层合规 | High | Host 直操 IActorRuntime.CreateAsync |
| 13 | `ScopeServiceEndpoints.cs:1114-1194` | 分层合规 | High | Host 直操 actor.HandleEventAsync |
| 14 | `ScopeServiceEndpoints.cs` | 依赖反转 | Medium | 直达 Workflow.Infrastructure.CapabilityApi |
| 15 | `ScopeGAgentEndpoints.cs` | 类耦合 | Medium | 111 类型耦合 (限制 96) |
| 16 | `NyxidChat.csproj` | 依赖反转 | Medium | 直接引用 Aevatar.Studio.Infrastructure |
| 17 | `EndpointSchemaProvider.cs:20-21` | 缓存无 TTL | Low | 500 项上限但无过期 |
| 18 | `FileBackedWorkflowCatalogPort.cs:80` | 缓存无 TTL | Low | Dictionary 无过期机制 |

---

## 10. Guard 补充建议 (Updated)

| 建议 | 优先级 | 原因 |
|------|--------|------|
| `architecture_guards.sh` 扫描范围扩展到 `agents/` | P0 | agents/ 完全不在 CI 守卫范围，是 NyxID M0 关键路径 |
| 新增 guard: Host 层禁止 `SubscribeAsync<EventEnvelope>` | P0 | 投影绕过是最大架构漂移 (6 处) |
| 新增 guard: Host 层禁止直操 `IActorRuntime.CreateAsync`/`IActor.HandleEventAsync` | P1 | 端点不应管理 actor 生命周期 (2 处) |
| 新增 guard: InMemory stores 禁止非测试 DI 注册 | P1 | 防止生产环境使用内存存储 |
| playground asset drift guard 修复 (npm/bun fallback) | P2 | 当前因 pnpm 未安装而跳过 |
| Frontend build check 作为 CI 步骤 | P2 | CLI + Console Web 构建失败无人发现 |

---

## 11. 修复优先级路线图

### Sprint 1 (04-08 → 04-14): Milestone Blockers

1. **agents/ 持久化存储** — NyxIdChatActorStore + StreamingProxyActorStore 迁移到 Actor State 或持久化存储
2. **StreamingProxyGAgent 影子状态** — `_proxyState` 迁移到事件溯源
3. **agents/ 基础测试** — 至少覆盖 actor 创建/消息处理/状态恢复
4. **InvokeStreamEndpoint 500 修复** — 3 个集成测试修复
5. **Frontend 依赖修复** — npm install 缺失依赖

### Sprint 2 (04-15 → 04-21): Guard 加强

6. **CI guard 扩展** — agents/ 加入扫描范围
7. **投影绕过 guard** — 禁止 Host 层 SubscribeAsync\<EventEnvelope\>
8. **ScopeServiceEndpoints 拆分** — 至少分离 InvokeStream 和 StaticGAgent 到独立端点

### Sprint 3 (04-22+): 投影统一

9. **AGUIEventProjector** — 统一 AI 事件 → AGUI 映射
10. **IProjectionSessionEventHub\<AGUIEvent\>** — 会话级 SSE 分发
11. **Host 端 TCS 消除** — 改为 receipt-based + session-hub subscription

---

## 附录 A: 文件违规热力图

```
src/platform/Aevatar.GAgentService.Hosting/
  Endpoints/
    ScopeServiceEndpoints.cs         ████████████ 8 findings (God class + 直操 actor + 投影绕过)
    ScopeGAgentEndpoints.cs          ██████ 4 findings (投影绕过 + 耦合)
    ScopeWorkflowEndpoints.cs        ██ 1 finding (Host 端映射)

agents/
  Aevatar.GAgents.NyxidChat/
    NyxIdChatActorStore.cs           ████ Critical (Singleton ConcurrentDictionary)
    NyxIdChatEndpoints.cs            ██████ 3 findings (3x TCS + SubscribeAsync)
  Aevatar.GAgents.StreamingProxy/
    StreamingProxyActorStore.cs      ████ Critical (2x Singleton ConcurrentDictionary)
    StreamingProxyGAgent.cs          ████ Critical (影子状态机)
    StreamingProxyEndpoints.cs       ████ 2 findings (2x SubscribeAsync)
```

## 12. 测试覆盖分析 — 修正版 (6/10)

> **方法论修正**: 初版按项目名称匹配得出"56/81 零测试"，实际上一个测试项目可通过 ProjectReference 覆盖多个 src 项目。以下基于 .csproj 引用链 + 传递依赖 + 覆盖率门禁配置重新分析。

### 12.1 覆盖率门禁配置

**`tools/ci/coverage_quality_guard.sh`**:
- 行覆盖率阈值: **85%** (`COVERAGE_LINE_THRESHOLD`)
- 分支覆盖率阈值: **72%** (`COVERAGE_BRANCH_THRESHOLD`)
- 工具: `dotnet test aevatar.slnx --collect:"XPlat Code Coverage"` + ReportGenerator
- **关键限制**: 门禁测的是**聚合**覆盖率，不是逐项目覆盖率。单个项目 0% 不会触发失败，只要整体达标。

### 12.2 测试项目引用覆盖

**主要覆盖提供者**:

| 测试项目 | 直接引用 src 项目数 | 覆盖范围 |
|---------|-------------------|---------|
| `Aevatar.Architecture.Tests` | 28 | 架构约束验证，覆盖面最广 |
| `Aevatar.GAgentService.Tests` | 12 | 平台服务全栈 |
| `Aevatar.AI.Tests` | 11 | AI 子系统 |
| `Aevatar.CQRS.Projection.Core.Tests` | 9 | 投影管线 |
| `Aevatar.Integration.Tests` | ~13 | 端到端集成 |
| `Aevatar.Integration.Slow.Tests` | ~13 | 分布式一致性 |

**实际覆盖统计**:
- src 项目总数: ~87
- 被测试触达（直接或传递）: **~80 (92%)**
- 真正零覆盖: **7-9 个项目 (8%)**
- 被门禁排除但有测试: 12 个项目

### 12.3 真正零覆盖的项目

以下项目**没有任何测试项目直接或传递引用**:

| 项目 | 职责 | 风险 |
|------|------|------|
| `Aevatar.Authentication.Abstractions` | 认证抽象 | Medium — NyxID M0 关键路径 |
| `Aevatar.Authentication.Hosting` | 认证宿主 | Medium |
| `Aevatar.Authentication.Providers.NyxId` | NyxID 认证提供者 | High — NyxID M0 关键路径 |
| `Aevatar.Foundation.ExternalLinks.WebSocket` | WebSocket 传输 | Medium |
| `Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming` | Orleans 流式传输 | High — 事件分发核心 |
| `Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider` | Kafka 传输 | Medium — 生产传输层 |
| `Aevatar.AI.ToolProviders.ChronoStorage` | 时序存储工具 | Low |
| `Aevatar.AI.ToolProviders.Ornn` | Ornn 工具 | Low |
| `Aevatar.AI.ToolProviders.Scripting` | 脚本工具 | Low |
| `Aevatar.AI.ToolProviders.Web` | Web 工具 | Low |

### 12.4 门禁排除项（有测试但不计入覆盖率考核）

以下项目被 `coverage_quality_guard.sh` 的 assembly filter 排除:

| 项目 | 排除原因 |
|------|---------|
| `Aevatar.Bootstrap` | 宿主引导 |
| `Aevatar.*.Providers.*` (全部 Provider 实现) | 外部集成 |
| `Aevatar.AI.LLMProviders.*` | LLM 提供者 |
| `Aevatar.AI.ToolProviders.*` | 工具提供者 |
| `Aevatar.Workflow.Sdk` | SDK |
| `Aevatar.Workflow.Extensions.Bridge` | 桥接扩展 |
| `Aevatar.Workflow.Presentation.AGUIAdapter` | AGUI 适配器 |
| `Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet` | Garnet 持久化 |

**风险**: 这 12 个项目可以 0% 覆盖率而门禁仍通过。

### 12.5 风险评估

**主要风险不是覆盖面，而是**:
1. **聚合门禁掩盖个体缺口** — 单个项目 0% 不触发失败
2. **认证层完全裸奔** — 3 个 Authentication 项目零测试，NyxID M0 关键路径
3. **Orleans Streaming 零测试** — 事件分发核心无回归保护
4. **agents/ 零测试** — 已在 §9 #4 标记为 MILESTONE_BLOCKER

### 12.6 修复建议

| 优先级 | 目标 | 测试类型 |
|--------|------|---------|
| P0 | `agents/` 全部 3 个项目 | 单元 + 集成：actor 创建/消息/状态恢复 |
| P0 | `Authentication.Providers.NyxId` | 集成测试：认证流程 |
| P1 | `Orleans.Streaming` | 集成测试：事件分发端到端 |
| P1 | 覆盖率门禁改为**逐项目阈值** | 防止聚合掩盖个体缺口 |
| P2 | 门禁排除项中的 Provider 实现 | 至少 happy-path 测试 |

---

## 13. AI.Core 中间件违规 — 补充分析

### 13.1 SkillRegistry 进程内注册表

**文件**: `src/Aevatar.AI.Core/Tools/SkillRegistry.cs:16-77`

```
private readonly Dictionary<string, SkillDefinition> _skills = new();
```

**7 处 lock 语句**（行 22, 29, 39, 64, 70, 87），保护技能注册/查询/枚举操作。

**违反规则**:
- §105: 禁止中间层维护 ID → 上下文/事实状态的进程内映射
- §95: 无锁优先，需加锁 → 先判定为"破坏 Actor 边界"→ 重构为事件化串行模型

**风险**: 多节点部署时技能注册不一致；进程回收后注册丢失。

**修复**: 迁移为 skill catalog actor（长期事实拥有者），或提升为分布式状态服务。

### 13.2 StreamingToolExecutor 锁状态

**文件**: `src/Aevatar.AI.Core/Tools/StreamingToolExecutor.cs:31`

```
private readonly object _lock = new();
```

**2 处 lock 语句**（行 59, 98），保护工具执行状态跟踪。

**违反规则**: §95 无锁优先原则。

**风险**: 工具执行状态在并发场景下的竞争条件；lock 持有时间未审计。

### 13.3 ToolApprovalMiddleware denial 计数

**文件**: `src/Aevatar.AI.Core/Middleware/ToolApprovalMiddleware.cs:23`

```
private readonly Dictionary<string, int> _denialCounts = new();
```

**已在 §3.3 列为 S9**，此处补充分析：
- 每个 agent 创建独立实例，作用域限于 agent 生命周期
- 但仍为进程内状态，不参与 event sourcing
- Agent 重激活后 denial 计数重置 → 安全策略失效

---

## 14. 工作流引擎状态风险 — 补充分析

### 14.1 WorkflowRunGAgent._executionItems 非持久化

**文件**: `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs:113-133`

```csharp
private readonly Dictionary<string, object> _executionItems = new();
```

**问题链**:
1. `_executionItems` 存储工作流执行过程中的临时上下文（模块中间结果等）
2. 不参与事件溯源，不持久化到 event store
3. Actor 去激活/重激活后，`_executionItems` 重置为空字典
4. 但 `WorkflowRunState`（Protobuf）已恢复 → 执行状态与上下文不一致
5. 后续模块访问 `_executionItems` 取不到前序模块的中间结果 → **工作流恢复后执行失败**

**影响范围**: 所有长时间运行的工作流（跨 actor 去激活周期）。

### 14.2 Sub-Workflow 续接无超时兜底 (`NEEDS_VALIDATION`)

**文件**: `src/workflow/Aevatar.Workflow.Core/Orchestration/SubWorkflowOrchestrator.cs`

**问题**: 父工作流发起子工作流后，等待 `SubWorkflowCompletedEvent`。从代码阅读看，若子工作流：
- 执行失败但未发出完成事件
- 所在 silo 宕机且未恢复
- 事件在传输层丢失

→ 父工作流将**无限挂起**，无超时或补偿机制。

**修复**: 在 `SubWorkflowOrchestrator` 中增加 durable timeout + 重试/补偿策略。

### 14.3 Callback Lease 恢复风险 (`NEEDS_VALIDATION`)

**文件**: `src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs`

`ScheduleSelfDurableTimeoutAsync()` 创建 `WorkflowRuntimeCallbackLeaseState`。若：
- Lease 过期检测依赖进程内定时器
- 进程重启后定时器未自动恢复
→ 超时回调永远不触发，工作流卡死。

需确认：lease 恢复是否在 actor 重激活时自动重建。确认前将其视为恢复风险，不直接纳入分数。

---

## 15. 投影端口 §111 违规 — 补充分析

### 15.1 EventSinkProjectionLifecyclePortBase

**文件**: `src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:23-24`

```csharp
private readonly ConcurrentDictionary<object, IAsyncDisposable> _sinkSubscriptions = new();
```

**违反规则**:
- §111: 投影端口禁止 `actorId -> context` 反查管理生命周期，改为显式 `lease/session` 句柄传递
- §106: 禁止中间层维护 entity/session 等 ID → 上下文的进程内映射

**问题**: 使用 object identity 作为订阅键（注释："Keyed by object identity...to avoid collisions"），本质是进程内订阅注册表。多节点部署时订阅状态不可迁移。

**修复**: 替换为显式 lease token 或 session handle，由投影管线统一管理生命周期。

---

## 16. 持久化层锁模式 — 补充分析

### 16.1 per-agent SemaphoreSlim 模式

以下文件使用 `ConcurrentDictionary<string, SemaphoreSlim>` 做 per-agent 写入协调：

| 文件 | 行号 | 用途 |
|------|------|------|
| `Foundation.Runtime/Persistence/FileEventStore.cs` | 28 | 文件级事件流写入串行化 |
| `Foundation.Runtime/Persistence/FileEventSourcingSnapshotStore.cs` | 15 | 快照文件写入串行化 |
| `Foundation.Runtime/Persistence/DeferredEventStoreCompactionScheduler.cs` | 17 | 压缩任务 per-agent 串行化 |

**评估**:
- InMemory / File 实现标记为开发/测试用途，**模式本身在此场景可接受**
- 但 `_agentLocks` 字典无上限 → 大量 agent 时内存泄漏
- SemaphoreSlim 不在 actor 内部 → 不违反 §95（Actor 单线程），属于基础设施层协调
- **风险**: 若生产环境误用 File 实现，lock 争用成为性能瓶颈

### 16.2 Orleans/Kafka 传输层锁

**文件**: `Foundation.Runtime.Implementations.Orleans.Transport.KafkaProvider/Streaming/KafkaProviderQueueAdapterReceiver.cs`

4 处 lock 语句（行 102, 241, 255, 269），保护 Kafka 消费者接收缓冲区。

**评估**: 传输层锁在 Orleans streaming 适配器中**可接受**，属于基础设施协调而非业务状态保护。需确认 lock 持有时间不阻塞 Orleans 线程池。

---

## 17. CI Guard 缺口 — 补充分析

### 17.1 TypeUrl.Contains() 未做动态 grep

**现状**: `projection_route_mapping_guard.sh` 验证路由结构使用 `StringComparer.Ordinal`，但**不 grep `.Contains(` 用法**。

**建议**: 在 `architecture_guards.sh` 中增加：
```bash
rg '\.Contains\(' --glob '**/*Projection*/**/*.cs' --glob '**/*Reducer*/**/*.cs' \
  | grep -i 'typeurl\|eventtype' && exit 1
```

### 17.2 Reducer 测试覆盖未自动化

**CLAUDE.md 行 176**: "新增非抽象 Reducer 类必须被测试引用"

**现状**: 无自动化检查。新增 Reducer 可跳过测试。

**建议**: 创建 `reducer_test_coverage_guard.sh`，扫描所有非抽象 Reducer 类名并验证至少一个 test 文件引用。

### 17.3 StateVersion 权威源仅抽查

**现状**: `projection_state_version_guard.sh` 仅检查 8 个硬编码投影文件。

**建议**: 改为动态扫描所有 `*Projector*.cs` 文件，检查 `StateVersion` 赋值来源。

---

## 综合评分更新

| Dimension | 原评分 | 补充后评分 | 变化原因 |
|-----------|--------|-----------|---------|
| CI Guards 合规 | 9/10 | 8/10 | 3 个规则未自动化执行 |
| 分层合规 | 6/10 | 6/10 | 无变化 |
| 投影一致性 | 4/10 | 4/10 | 无变化 |
| 读写分离 | 8/10 | 8/10 | 无变化 |
| 序列化 | 7/10 | 7/10 | 无变化 |
| Actor 生命周期 | 5/10 | 5/10 | 无变化 |
| 前端可构建性 | 2/10 | 2/10 | 无变化 |
| 测试覆盖 (全局) | 0/10 | 3/10 | 聚合覆盖率高于单项目现实；关键路径仍存在明显空洞 |
| **AI.Core 中间件合规** | — | **4/10** | **新增维度**: SkillRegistry + StreamingToolExecutor + ToolApprovalMiddleware 违规 |
| **工作流引擎韧性** | — | **5/10** | **新增维度**: _executionItems 非持久化 + Sub-Workflow 无超时 + Lease 恢复风险 |
| **投影端口合规** | — | **5/10** | **新增维度**: EventSinkProjectionLifecyclePortBase §111 违规 |

**更新后综合架构健康度: 5.2 / 10** (11 dimensions)

---

## 漂移清单补充

### ARCHITECTURAL_DEBT 新增项

| # | 位置 | 违反规则 | 严重度 | 备注 |
|---|------|---------|--------|------|
| 19 | `Aevatar.AI.Core/Tools/SkillRegistry.cs:16-77` | §95 + §105 | High | 7 处 lock + Dictionary 注册表，非 Actor 持有 |
| 20 | `Aevatar.AI.Core/Tools/StreamingToolExecutor.cs:31` | §95 | Medium | 2 处 lock，工具执行状态保护 |
| 21 | `CQRS.Projection.Core/.../EventSinkProjectionLifecyclePortBase.cs:23` | §111 | High | ConcurrentDictionary 做订阅管理，应改 lease/session |
| 22 | `Workflow.Core/WorkflowRunGAgent.cs:113-133` | Actor 执行模型 | High | `_executionItems` 非持久化，actor 重激活后丢失 |
| 23 | `Workflow.Core/Orchestration/SubWorkflowOrchestrator.cs` | Actor 执行模型 | Medium | 子工作流续接无超时兜底 |
| 24 | 7-9 个 src/ 项目零覆盖 + 聚合门禁掩盖个体缺口 | 测试要求 | High | 认证层 + Orleans Streaming 零测试；门禁不检查逐项目覆盖率 |
| 25 | `architecture_guards.sh` | CI 门禁完整性 | Medium | 3 条 CLAUDE.md 规则未自动化 |

---

## 附录 B: 与上次审计的差异

| 维度 | 上次 (概要) | 本次 (详细+补充) | 变化 |
|------|-----------|-----------------|------|
| 投影一致性 | 3 处绕过 | 6 处绕过 (agents/ 新发现) | 证据更充分 |
| Actor 生命周期 | ConcurrentDictionary 违规 | + 影子状态机 + 18 处状态字段全量清单 | 更完整 |
| 分层合规 | Host 耦合度高 | + God Class 量化 + 3 处直操 actor + 依赖方向分析 | 8→6 |
| CI Guards | 1 个盲区 | agents/ + apps/ 双盲区 + 3 条规则未自动化 | 9→8 |
| 测试覆盖 | agents/ 零测试 | **修正**: 聚合覆盖率不能代表关键路径安全；认证、Streaming、agents/ 仍有缺口 | 0→3 (口径收紧) |
| AI.Core 中间件 | 未审计 | **新增维度**: SkillRegistry 7 lock + StreamingToolExecutor + ToolApprovalMiddleware | 新增 4/10 |
| 工作流引擎韧性 | 未审计 | **新增维度**: _executionItems 非持久化；其余恢复风险待运行时验证 | 新增 5/10 |
| 投影端口合规 | 未审计 | **新增维度**: EventSinkProjectionLifecyclePortBase §111 违规 | 新增 5/10 |
| **综合评分** | **5.1/10 (8维)** | **5.2/10 (11维)** | **本版起统一按已验证证据计分，详细结论以本表和总表为准** |
