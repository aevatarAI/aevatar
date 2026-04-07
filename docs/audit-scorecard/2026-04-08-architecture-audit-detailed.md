---
title: Architecture Audit — Detailed Analysis Report
status: active
owner: Loning
---

# Architecture Health Scorecard — 2026-04-08 (Detailed)

Audit scope: Milestone-oriented (Living with AI Demo 04-17, NyxID M0 04-18)
Audit method: Two-layer (CLAUDE.md compliance + hot-path deep dive) + 5-dimension parallel probe
Reviewed by: arch-audit skill, 5 parallel exploration agents

> 本文档是 `2026-04-08-architecture-audit.md` 的深度展开版，包含每个维度的代码级证据、数据流分析和修复方案。

---

## 1. 综合评分

| Dimension | Score | Summary |
|-----------|-------|---------|
| CI Guards 合规 | 9/10 | 全通过，但 agents/ 和 apps/ 完全不在扫描范围 |
| 分层合规 | 6/10 | Application 层干净；Host 层 ScopeServiceEndpoints 是 God Class，直达 Infrastructure |
| 投影一致性 | 4/10 | Host 层 6+ 处直接订阅 EventEnvelope + TCS 阻塞，完全绕过 Projection Pipeline |
| 读写分离 | 8/10 | Application 层 Query 全部 CLEAN；Host 层投影 bypass 是唯一破口 |
| 序列化 | 7/10 | 核心路径全 Protobuf；JSON 仅在 Host/Adapter 边界，合规 |
| Actor 生命周期 | 5/10 | StreamingProxyGAgent 影子状态机 + agents/ 3 个 ConcurrentDictionary 单例 |
| 前端可构建性 | 2/10 | CLI Frontend + Console Web 均构建失败 |
| 测试覆盖 (agents/) | 0/10 | agents/ 零测试；集成测试 3/51 失败 |
| Governance 实质 | 3/10 | 有事件溯源 + admission 评估引擎，但无目标函数/自适应/递归组合 |

**综合架构健康度: 4.9 / 10**

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

**分布式影响**: `SubscribeAsync` 实现依赖本地 stream provider。Orleans 跨节点部署时，Actor 可能在远程 silo 执行，本地订阅无法收到事件 → **SSE 流静默超时**。

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

## 8. Governance 实质 — 深度分析 (3/10)

### 8.1 现有能力

**ServiceConfigurationGAgent** 处理 8 种命令:
- Binding CRUD: Create/Update/Retire ServiceBinding
- Endpoint CRUD: Create/Update ServiceEndpointCatalog
- Policy CRUD: Create/Update/Retire ServicePolicy

所有命令走完整事件溯源（Protobuf state + domain events），这比之前评估的 "纯 CRUD" 稍好。

**InvokeAdmissionService** 实现运行时策略评估:
- 检查目标服务定义存在性
- 验证端点发布状态
- 策略引用完整性
- 委托 `IInvokeAdmissionEvaluator` 做 4 项违规检查:
  1. `missing_policy` — 引用策略不存在
  2. `endpoint_disabled` — 端点暴露被禁用
  3. `inactive_deployment` — 需活跃 deployment 但无
  4. `caller_not_allowed` — 调用方不在白名单

**因此从 2/10 上调至 3/10** — 有运行时策略引擎，不是纯 config CRUD。

### 8.2 缺失的治理抽象

```bash
rg "Goal|Scope|Governance|Harness|Optimization|Adaptability" src/ --type cs
# 域模型层无匹配（仅在 Governance 命名空间名中出现）
```

缺失:
- **Goal/ObjectiveFunction** — 无目标函数建模
- **三层治理** (order definition / optimization / adaptability) — 只有 order definition (binding/policy)
- **递归组合** — 策略是扁平列表，无层级/继承/组合
- **自适应反馈环** — 无 optimization/adaptability 机制
- **Harness Theory** — 仅在 CEO 库文档中，代码层无体现

### 8.3 序列化合规

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
| 19 | Governance 子系统 | 产品 thesis | Low | 无 Goal/ObjectiveFunction/三层治理 |

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

## 附录 B: 与上次审计的差异

| 维度 | 上次 (概要) | 本次 (详细) | 变化 |
|------|-----------|-----------|------|
| 投影一致性 | 3 处绕过 | 6 处绕过 (agents/ 新发现) | -0 (分数不变, 证据更充分) |
| Actor 生命周期 | ConcurrentDictionary 违规 | + 影子状态机 + 18 处状态字段全量清单 | 更完整 |
| 分层合规 | Host 耦合度高 | + God Class 量化 + 3 处直操 actor + 依赖方向分析 | 8→6 |
| Governance | 0-2/10 config CRUD | 3/10 有 admission 评估引擎 | 上调 |
| CI Guards | 1 个盲区 | agents/ + apps/ 双盲区 + 规则覆盖矩阵 | 更完整 |
