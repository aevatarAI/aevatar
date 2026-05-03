---
title: Architecture Audit — Full Repo Detailed Report (post 04-08 follow-up)
status: active
owner: Loning
---

# Architecture Health Scorecard — 2026-05-03 (Detailed)

**Audit scope**: full repo, milestone-aware (cross-channel LLM selection 已合 / Studio team-first 进行中 / NyxID OAuth healing 进行中)
**Audit method**: arch-audit skill — environment validation → 8 CI guards → Architecture.Tests → Step 2a–2f deep dives → milestone path verification
**Compared against**: [2026-04-08 detailed audit](2026-04-08-architecture-audit-detailed.md)
**Auditor**: arch-audit (claude-opus-4-7[1m])

> 本次审计的核心结论：自 04-08 以来 **投影一致性大幅好转**（4/10 → 7/10），ScopeGAgentEndpoints 与 StreamingProxyEndpoints 的 6 处 TCS-bypass 全部消除，StreamingProxyGAgent 影子状态已重构为纯事件溯源。**唯一遗留**的关键投影 bypass 集中在 `agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs`（被 chat + approve 两个端点共享）。其它新增风险点为 `NyxIdRelayReplayGuard` 进程内事实状态、`WorkflowRunGAgent._executionItems` 非持久化运行态，以及 4 个 OpenTelemetry 中危依赖漏洞。
>
> 评分只纳入已验证证据；需要运行时验证的列为 `NEEDS_VALIDATION`，不计入分数。env-tooling 缺失（pnpm/max/buf）保留为里程碑跟踪项，不计入 architecture evidence。

---

## 1. 综合评分

| Dimension | 04-08 | 05-03 | Δ | 主要证据 |
|-----------|-------|-------|---|---------|
| CI Guards 合规 | 8/10 | **9/10** | ▲ | architecture_guards.sh + 7 个专项 guard + 105 Architecture.Tests 全通过；唯一缺失：`buf` 未安装（proto_lint_guard 软警告）|
| 分层合规 | 6/10 | **8/10** | ▲ | Application 层 query/command 端口干净；Hosting 层不再做投影映射，仅剩 Script execution 一处用 `EnsureAndAttachAsync` 的合规路径 |
| 投影一致性 | 4/10 | **7/10** | ▲▲ | ScopeGAgentEndpoints 0 处 bypass（04-08 = 6+），StreamingProxyEndpoints 已重构为 RoomSessionEventProjector 主链；**唯一遗留**：NyxIdChatStreamingRunner（agents/）— 但其修复方向不是套 projection（见 §3.3）|
| 读写分离 | 8/10 | **9/10** | ▲ | Application Query 全 CLEAN（grep IEventStore/Replay/.State/GetGrain 在 Queries 路径 0 命中）|
| 序列化 | 7/10 | **8/10** | ▲ | 核心路径全 Protobuf，guards 验证；JSON 仅在 Adapter 边界 |
| Actor 生命周期 | 5/10 | **6/10** | ▲ | StreamingProxyGAgent 影子状态已清理（重大改进）；`WorkflowRunGAgent._executionItems` 非持久化仍存在；新增 `NyxIdRelayReplayGuard` 进程内事实态 |
| 投影端口合规 | 5/10 | **7/10** | ▲ | EventSinkProjectionLifecyclePortBase 仍有 `_sinkSubscriptions` ConcurrentDictionary，但代码注释明确将其定义为 "process-local transient I/O handles"，符合 Step 2e 设计意图 |
| 治理子系统实质度 | 未评 | **3/10** | — | ServiceConfigurationGAgent / InvokeAdmissionService / ServiceGovernanceQueryApplicationService 仍是 CRUD + 静态 ACL，无 Goal / ObjectiveFunction / 三层治理建模 |
| 前端可构建性（release readiness） | 2/10 | **3/10** | ▲ | 主要被 env-tooling（缺 pnpm/max）阻塞；CLI Frontend 还有真实 TS 编译错误（implicit any × 6 + 缺 `vitest`/`@tanstack/react-virtual`/`@chenglou/pretext` 类型）|
| 测试覆盖 (关键路径) | 3/10 | **6/10** | ▲ | 工作流核心 61 个 test 文件 + Architecture.Tests 105 通过 + SubWorkflowOrchestrator 三套覆盖；NyxIdChat / Scope endpoints 都已纳入测试 |
| 依赖安全 | 未评 | **6/10** | — | 4 处 NU1902 中危：`OpenTelemetry.Api` 1.15.0 / `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.0（GHSA-g94r-2vxg-569j 等）|

**综合架构健康度**：**6.5 / 10**（11 个维度；env-tooling 单独跟踪，不计架构分）

> 04-08 的 5.5/10 → 05-03 的 6.5/10 = **+1.0 净改善**，主要驱动：投影管线主干统一、Streaming 影子态消除、Application 层 query/command 边界完成。

---

## 2. 自动化基线

### 2.1 环境
- `dotnet --version` = 10.0.103 ✅
- `dotnet restore aevatar.slnx` ✅（160 项目，4 处 NU1902 中危依赖告警，见 §10）
- `dotnet build aevatar.slnx` ✅ **0 errors, 150 warnings**（主要为 `UserAgentCatalog.*` 字段 `[Obsolete]`，提示进行中的迁移；少量 `CS8613` nullability 不一致）
- `rg` 已安装 ✅
- `pnpm` ❌（apps/aevatar-console-web 需 pnpm@10.2.1）
- `buf` ❌（proto_lint_guard 跳过 schema lint）

### 2.2 CI Guards（全部通过）

| Guard | 结果 | 备注 |
|---|---|---|
| architecture_guards.sh（含 7 个内嵌专项）| ✅ pass | "buf is required" 软警告，proto lint 被跳过 |
| query_projection_priming_guard.sh | ✅ pass | |
| projection_state_version_guard.sh | ✅ pass | |
| projection_state_mirror_current_state_guard.sh | ✅ pass | |
| projection_route_mapping_guard.sh | ✅ pass | TypeUrl 派生 + 精确键路由 |
| workflow_binding_boundary_guard.sh | ✅ pass | |
| test_stability_guards.sh | ✅ pass | polling allowlist 有效 |
| `dotnet test test/Aevatar.Architecture.Tests/` | ✅ 105 passed / 1 skip / 0 fail | 4s |

**判定**：所有可自动化的架构契约都被验证。剩余风险全部在 guard 扫描盲区。

---

## 3. 投影管线一致性 — 7/10

### 3.1 04-08 → 05-03 主要改进（实证）

| 文件 | 04-08 状态 | 05-03 状态 |
|---|---|---|
| `src/platform/.../ScopeGAgentEndpoints.cs` | TCS+SubscribeAsync × 6+ | **0** 命中 ✅ |
| `agents/.../StreamingProxyEndpoints.cs` | TCS+SubscribeAsync × 2 | **0** 命中 ✅，已替换为 `StreamingProxyRoomSessionEventProjector` 主链 |
| `agents/.../StreamingProxyGAgent.cs` | 影子 `_proxyState` 字段 | **已删除**，改为纯事件溯源 + `IProjectedActor` |
| `src/platform/.../ScopeServiceEndpoints.cs:1882` 区块 | TCS pattern | 通过 `scriptExecutionProjectionPort.EnsureAndAttachAsync(...)` + `IEventSink<EventEnvelope>` 走正式 projection lease ✅ |

`rg "SubscribeAsync<EventEnvelope" src agents` 全仓库现在只有 **2 个命中**：

| 命中 | 性质 |
|---|---|
| `agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs:38` | **VIOLATION** — 见 3.2 |
| `src/Aevatar.Foundation.Runtime.Implementations.Local/Actors/LocalActor.cs` | **OK** — 这是 `IActorEventSubscriptionProvider` 的本地内置实现，属于抽象基础 |

### 3.2 唯一遗留 bypass：`NyxIdChatStreamingRunner`

**位置**：`agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs:35-74`

**违规模式**（与 ADR 0015 §17 明文反例完全吻合）：

```csharp
// L35
var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

// L38 — 直接 host-layer 订阅 actor stream
await using var subscription = await subscriptionProvider.SubscribeAsync<EventEnvelope>(
    subscriptionActorId,
    async envelope => {
        var terminalFrame = await mapAndWriteEventAsync(envelope, messageId, writer);
        if (!string.IsNullOrWhiteSpace(terminalFrame))
            completion.TrySetResult(terminalFrame);  // 进程内 TCS 完成
    },
    ct);

await dispatchAsync(messageId, ct);

// L57 — 进程内 120s timer 决定终态
var completedTask = await Task.WhenAny(completion.Task, Task.Delay(120_000, ct));
```

**调用方**：`agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs`
- L80–124：`HandleStreamMessageAsync`（普通 chat 流）
- L265–299：`HandleApproveAsync`（tool approval 续流）

两个端点都通过 `NyxIdChatStreamingRunner.RunAsync(...)` 共享同一段 bypass 代码。

**对照 ADR 0015**（agui-sse-projection-session-pipeline.md §17）：
> "completion 依赖 `TaskCompletionSource`、`Timer`、`Channel close` 等进程内偶然状态" — 列为反例

**违反的 CLAUDE.md 条款**：
1. 投影编排 Actor 化：禁止中间层进程内字典持有事实状态 → 这里 TCS 是事实状态。
2. 跨 actor 等待 continuation 化：禁止当前 turn 同步 await actor reply → 这里同步等 120s。
3. 统一投影链路：AGUI/CQRS 同一套 Projection Pipeline → 这里直绕 hub。
4. ACK 诚实：同步返回不应承诺 committed → 这里把 `TextMessageEnd` 当 committed-equivalent 用。

**与 ADR 0013（统一 channel inbound backbone）的关系**：
> "the Nyx relay HTTP endpoint path that directly orchestrated NyxIdChatGAgent, subscriptions, reply accumulation, and error classification"

ADR 0013 已要求 NyxId **inbound webhook**（`NyxIdChatEndpoints.Relay.cs`）走统一 channel 入站主干；但 **outbound streaming**（`NyxIdChatEndpoints.Streaming.cs` + `NyxIdChatStreamingRunner`）尚未有对应的 ADR。

**NEEDS_VALIDATION（分布式影响）**：`subscriptionProvider.SubscribeAsync` 当 Orleans 跨节点部署时是否能透传 — 若不能，actor 在远程 silo 执行时 SSE 必然静默 120s 超时。这一点需要搭分布式环境实测；本次审计仅在源码层确认 bypass 存在。

### 3.3 修复方向澄清 — Projection Pipeline 不是 long-lived stream 的正确抽象

> 本节根据 Auric（2026-05-03）反馈修订。原始审计草稿曾把"折回 Projection Pipeline"作为 M1 的修复方向，但那是错误的形状匹配。

**两种连接，两种语义**：

| 性质 | request-reply / fact replay | long-lived bidirectional stream |
|---|---|---|
| 例子 | Script execution chat（`ScopeServiceEndpoints.cs:1860`）、CQRS 查询、`workflow_call` continuation | NyxID chat SSE、未来 realtime audio/video、tool approval 长连接 |
| 完成判定 | terminal frame / committed-state version 抵达 | 客户端断开 / 业务握手关闭 |
| 时序敏感 | 弱（只关心终态可见）| 强（每一帧顺序就是业务语义）|
| 正确抽象 | **Projection lease + IEventSink** | **Actor 一等的 stream port** |

`scriptExecutionProjectionPort.EnsureAndAttachAsync(...) + IEventSink<EventEnvelope>` 是为 "等已 committed 的事实物化到可见水位" 设计的。把它套到 long-lived 双向流上会出两个问题：
1. **时序错位**：projection hub 在 actor 与 sink 之间多一跳，多模态 frame（音视频）经过 hub 会被合并/重排成 "已物化事件"，破坏严格 frame ordering。
2. **承载错位**：projection 是 "事实复制层"，承担长连接的会话/握手/back-pressure 是错位 — 把"会话还活着吗"塞到 projection lease 里会让 hub 不再幂等。

**正确方向**：让 `IActor` 直接拥有 stream 端口

```
inbound stream  → IActor.AttachInboundStream(streamId, IAsyncEnumerable<Frame>) → actor handler
outbound stream ← IActor.SubscribeOutbound(streamId, IAsyncEnumerable<Frame>) ← actor handler
```

- frame 类型用 `oneof`（text chunk / audio frame / video frame / tool-approval prompt / control signal），统一一个 `StreamFrame` proto
- 端口由 actor runtime 提供，跨节点通过 Orleans/transport 透传 stream 半双工 / 全双工句柄；host 拿到的就是已绑定到正确 silo 的 stream，不再需要 host-side 订阅 + TCS
- 终态判定由 stream 关闭语义承担（`OnCompleted`、`OnError`、客户端 cancel），不需要 host 内进程内 TCS+Timer

### 3.4 已对齐的两条邻接证据

- **`Aevatar.Foundation.VoicePresence.*`** — 仓库已在为 realtime 多模态承接做地基（`RemoteActorVoicePresenceSessionResolver` 通过 `IActorEventSubscriptionProvider` 解析远程 actor 的 voice session）。M1 的修复必须**与 VoicePresence 共用 stream port 抽象**，否则后续 audio/video 通路会再做一次平行实现。
- **Script execution 路径**（`ScopeServiceEndpoints.cs:1860-1936`）保留为 **request-reply / fact-replay** 形状的正确模板，**不**作为 long-lived stream 的模板。两类形状要在 ADR 中明确区分。

---

## 4. 读写分离 — 9/10

```bash
rg -n "IEventStore|Replay|GetGrain|\.State\b" \
   src/platform/Aevatar.GAgentService.Application \
   src/workflow/Aevatar.Workflow.Application/Queries
```
**结果：0 命中**。Application 层 query/command 路径完全干净。

`query_projection_priming_guard.sh` 全仓扫描通过 — 无 query-time replay/priming/state mirror 重建。

唯一保留的 1 分扣减来自前述 NyxIdChatStreamingRunner 的 host-side bypass（不在 Application/Queries 路径，但属于 host 层读写边界）。

---

## 5. Actor 生命周期 / 状态完整性 — 6/10

### 5.1 验证为 `OK` 的关键 actor

| Actor | 验证结果 |
|---|---|
| `StreamingProxyGAgent`（agents/...）| ✅ 完全事件溯源，`TransitionState` matcher + `PersistDomainEventAsync`，无影子字段 |
| `SubWorkflowOrchestrator` | ✅ 使用 `_scheduleSelfTimeoutAsync(callbackId, dueTime, evt, ct)` 走 durable callback；`DefaultDefinitionResolutionTimeoutMs=30_000` 有显式超时；`CancelDurableCallbackAsync` 提供清理 |
| `ServiceConfigurationGAgent` | ✅ Pure event-sourced CRUD，`Stamp(state, version, eventId)` 维护 `LastAppliedEventVersion` |

### 5.2 `WorkflowRunGAgent._executionItems` — NEEDS_VALIDATION

`src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs:39`
```csharp
private readonly Dictionary<string, object?> _executionItems = new(StringComparer.Ordinal);
```

**性质**：actor 内部、非持久化的执行上下文 dictionary。生命周期清理点：
- L450 `_executionItems.Clear()`
- L904 `CompleteStopAsync`
- L1107（绑定切换）

**风险**：actor deactivation 后再激活时 `_executionItems` 是空的，但 `State.RunId` 等持久化字段还在。如果 workflow 跨 tick 依赖 `_executionItems` 中的项，**reactivation 之后会拿到 null**。

**判定**：
- CLAUDE.md 允许 "Actor 内部运行态集合可保留在内存或 Actor State；前提：不作为跨节点事实源，按生命周期及时清理"。
- 当前调用域内，`_executionItems` 看起来确实是 single-tick 内的临时数据（`SetExecutionItem` 都跟随 `PersistDomainEventAsync` 提交事实），但需要追踪所有 setter→getter 路径，以确认没有跨 tick 依赖。
- **本次评估为 NEEDS_VALIDATION**（不扣分），但建议为该字段加单元测试覆盖 "actor reactivation → _executionItems empty" 场景，或将其改成纯 method-local 传递。

### 5.3 `NyxIdRelayReplayGuard._claims` — VIOLATION

`agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/NyxIdRelayReplayGuard.cs:12`
```csharp
private readonly ConcurrentDictionary<string, DateTimeOffset> _claims = new(StringComparer.Ordinal);
private readonly TimeSpan _window;  // 默认 5 分钟
```

**功能**：NyxID relay webhook 的 5 分钟滑动窗口去重。

**违规**：CLAUDE.md "中间层状态约束" 明确禁止：
> 跨 Actor/跨节点一致性状态：优先 Actor 持久态；无法放入时用抽象化分布式状态服务；禁止中间层进程内缓存作为事实源。

`replayKey → expiry` 是 "已认领的 replay 键" — **典型的跨请求一致性事实**。多节点部署时不同节点会重复认领同一 key。

**严重度**：High（业务上 Webhook 重放保护失效 = potential 重复消息处理；但 Lark/Telegram 的 `update_id` 已提供消息层去重，所以是**纵深防御被跳过**而非单点失守）。

**修复方向**：
- 将 `_claims` 改为按 `replay_key` 寻址的 GAgent（`NyxIdRelayClaimGAgent`），通过 actor 持久态保证 single-fact ownership；
- 或挂到分布式 cache（如 Garnet）+ TTL，明确写入 ADR 标记非 actor 路径。

### 5.4 `EventSinkProjectionLifecyclePortBase._sinkSubscriptions` — OK by design

`src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:23`
```csharp
// Keyed by object identity (ReferenceEqualityComparer) to avoid RuntimeHelpers.GetHashCode collisions.
private readonly ConcurrentDictionary<object, IAsyncDisposable> _sinkSubscriptions =
    new(ReferenceEqualityComparer.Instance);
```

代码自带注释："Sink subscriptions are process-local transient I/O handles (not business fact state)"。

调用语义：`AttachLiveSinkAsync(lease, sink, ct)` 时按 sink 引用注册订阅，`DetachLiveSinkAsync` 时按 sink 引用销毁。这是**进程局部 I/O lifetime 表**，不构成事实状态。

**判定**：符合 Step 2e "lifecycle ownership carried by handles"。Audit blueprint 把它列为重点审视文件，本次确认 **设计正确**。轻微改进空间：可以把 sink 注册表改造为更纯的 disposable composition（每个 lease 自带 subscription bag），消除 ConcurrentDictionary 字段、彻底无视 guard 工具的 "字段名 = 单例事实状态" 启发式误报。

### 5.5 `CachedScriptBehaviorArtifactResolver._artifacts` — OK

`src/Aevatar.Scripting.Infrastructure/Compilation/CachedScriptBehaviorArtifactResolver.cs:9`：纯编译缓存（key = scriptId+revision+packageHash），idempotent，多节点重复编译产物等价。**无业务事实状态**。

---

## 6. Sub-Workflow / 工作流编排 — 8/10

`SubWorkflowOrchestrator`（1580 行）通过依赖注入接受 9 个回调 (persist / publish / send / scheduleTimeout / cancelCallback)，符合 "Actor 内聚 + 通过 facade 解耦" 模式。

- `DefaultDefinitionResolutionTimeoutMs = 30_000` ✅ 显式超时
- `_scheduleSelfTimeoutAsync(callbackId, dueTime, evt, ct)` ✅ 走 durable callback（`RuntimeCallbackLease`）
- `CancelDurableCallbackAsync(lease, ct)` ✅ 提供回滚
- `WorkflowCallInvocationIdFactory` ✅ 显式 invocation id 对账
- `CleanupPendingInvocationsForRunAsync` ✅ 终态时清理悬挂 invocation
- 测试：`SubWorkflowOrchestratorTests` + `BranchCoverageTests` + `StateCoverageTests` 三套

**唯一改进点**：与 `_executionItems` 同源的临时态依赖问题（见 §5.2），但 SubWorkflowOrchestrator 自己未使用该字段，状态全部走 `WorkflowRunState.PendingInvocations` 等持久化集合。

---

## 7. 治理子系统实质度 — 3/10

### 7.1 现状

| 文件 | 性质 |
|---|---|
| `ServiceConfigurationGAgent`（394 行）| Bindings / Endpoints / Policies 三类配置的 CRUD + Retire + LegacyImport，纯事件溯源 |
| `InvokeAdmissionService`（111 行）| 静态 ACL 评估器：读 catalog + configuration → 解析 PolicyId → 检查 caller / endpoint / required bindings → allow/violation |
| `ServiceGovernanceQueryApplicationService`（41 行）| 三个方法纯转发 `IServiceConfigurationQueryReader` 快照 |

### 7.2 与产品 thesis 对照

CLAUDE.md / 审计 blueprint 期望 governance 体现：
- Goal / Scope / ObjectiveFunction 建模
- 三层治理（order definition / optimization / adaptability）
- 递归组合模式

**当前代码未体现以上任何一项**。这部分功能 = "服务定义注册 + 端点白名单 + 策略 ACL"，本质是 RBAC + service catalog，不是 organizational governance。

### 7.3 判定

- 如果当前里程碑只需要 "channel/agent 调用方权限管控"，3/10 已满足；
- 如果产品 thesis 计划在 Studio team-first 之上承载 "组织治理"（典型驱动场景：team policies、跨成员 objective alignment、可演进的服务网格），则需要在 ADR 中为治理子系统补一份模型升级方案。
- 不强制为 BACKLOG，建议下一次 plan-eng-review 把 "治理是 ACL 还是 governance" 显式裁定。

---

## 8. 前端可构建性 — 3/10（release readiness 跟踪）

| 项目 | 包管理器 | 结果 | 性质 |
|---|---|---|---|
| `apps/aevatar-console-web` | pnpm-lock.yaml + `max build` | ❌ `sh: max: command not found`（node_modules 没有 `@umijs/max`）| **env-tooling**（缺 pnpm，且 npm install 没装 max）|
| `tools/Aevatar.Tools.Cli/Frontend` | package-lock.json + `tsc + vite build` | ❌ TS 编译错误 × 6 + 缺 `vitest`/`@tanstack/react-virtual`/`@chenglou/pretext` 类型 | **混合**：缺包是 env-tooling；implicit any 6 处是真代码缺陷 |
| `tools/Aevatar.Tools.Cli/Desktop` | package-lock.json | 未单独运行 | 待补 |

**真代码缺陷**（即使依赖齐了仍会失败）：
- `src/config-explorer/ExplorerContentView.tsx`：L252/260/278 implicit any
- `src/runtime/ScopePage.tsx`：L2014/2016/4162 implicit any

**判定**：env-tooling 不计入架构分；implicit any 是低成本可修真问题，列入 BACKLOG。

如果下一里程碑要 demo console-web，请在 `tools/ci/` 增加一个 frontend build smoke 守卫（参考 04-08 audit 的同名建议）。

---

## 9. 测试覆盖 — 6/10

- 总测试文件：**632 个 `*Tests.cs`**
- 工作流核心：`test/Aevatar.Workflow.Core.Tests/Primitives/` 下 SubWorkflowOrchestrator 三套覆盖
- Architecture.Tests：105 通过 / 1 跳过
- NyxIdChat：`NyxIdChatEndpointsCoverageTests` / `NyxIdChatGAgentTests` / `NyxIdChatServiceCollectionExtensionsTests`
- Workflow Host：`Aevatar.Workflow.Host.Api.Tests` 单独可重建（2.6s）
- ChannelRuntime：`UserAgentCatalogProjectorTests` 等专项

**盲点**：
- NyxIdChatStreamingRunner 的 TCS 路径没有 "Orleans 跨 silo subscribe failure" 测试 — 即使有单元 coverage，也没法捕获 §3.2 描述的分布式 bypass 风险
- 前端 `src/runtime/*.test.ts` 缺 `vitest` 类型 — 很可能 jest workflow 之外的 vitest 单元测从未执行

---

## 10. 依赖安全 — 6/10（新增维度）

`dotnet restore` 报告 **4 处 NU1902 中危**，全部集中在 `src/workflow/Aevatar.Workflow.Host.Api`：

| 包 | 版本 | CVE | 影响 |
|---|---|---|---|
| `OpenTelemetry.Api` | 1.15.0 | GHSA-g94r-2vxg-569j | moderate |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.0 | GHSA-4625-4j76-fww9 | moderate |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.0 | GHSA-mr8r-92fq-pj8p | moderate |
| `OpenTelemetry.Exporter.OpenTelemetryProtocol` | 1.15.0 | GHSA-q834-8qmm-v933 | moderate |

**修复**：升级 OpenTelemetry 1.15.0 → 1.16.x 或更高；统一在 `Directory.Packages.props` 调整一处。

---

## 11. 漂移清单

### 11.1 MILESTONE_BLOCKER（必须立刻处理）

| # | 位置 | 违反规则 | 严重度 | 描述 |
|---|------|---------|--------|------|
| M1 | `agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs:35-74` | CLAUDE.md 投影编排 Actor 化、跨 actor continuation 化、ACK 诚实 | **Critical** | TCS+SubscribeAsync<EventEnvelope>+120s Task.Delay；分布式部署下高概率 silent timeout。**修复方向 = 引入 actor 一等 stream port（见 §3.3），不要折回 projection lease**——projection 是 fact-replay 形状，不能承载 long-lived 双向时序流（未来 realtime 多模态）。需出 ADR 与 VoicePresence 共用同一 stream 抽象 |
| M2 | OpenTelemetry 1.15.0 × 4 | 依赖安全 | High | `Aevatar.Workflow.Host.Api` 启用 OTEL，存在已知中危漏洞 |

### 11.2 BACKLOG（30 天保质期）

| # | 位置 | 违反规则 | 严重度 | 保质期 |
|---|------|---------|--------|--------|
| B1 | `agents/channels/.../NyxIdRelayReplayGuard.cs:12` | 中间层进程内事实状态 | Medium | 多节点上线前必须修；当前单节点纵深防御失效但有上游 update_id 兜底 |
| B2 | `src/workflow/.../WorkflowRunGAgent.cs:39` `_executionItems` | actor 内运行态非持久化 | Medium-Low | 增加 reactivation 测试或改 method-local；当前未发现真实跨 tick 依赖 |
| B3 | 150 build warnings：`UserAgentCatalogEntry.NyxApiKey/Platform/OwnerNyxUserId` 系列 `[Obsolete]` | 进行中迁移未收口 | Medium | 与 channel-identity / cross-channel LLM 迁移合并清理 |
| B4 | `tools/Aevatar.Tools.Cli/Frontend/src/{config-explorer,runtime}/*.tsx` implicit any × 6 | TS strict 偏移 | Low | 一次性补 type 即可 |
| B5 | `EventSinkProjectionLifecyclePortBase._sinkSubscriptions` ConcurrentDictionary | guard 视角误报，但仍可重构 | Low | 改为 lease-owned subscription bag，彻底消除中间层 dict |
| B6 | 治理子系统未承载 Goal/ObjectiveFunction 建模 | 产品 thesis 对齐 | Optional | 与下一里程碑（Studio team-first / 组织治理）联动决策 |
| B7 | `agents/Aevatar.GAgents.NyxidChat/NyxIdRelayReplies.cs:84` 残留 TCS | 检查是否已经在 Channel Runtime 主线之外 | Low | 若是 ADR 0013 路径外的兜底，需文档化或迁移 |

### 11.3 已修复（vs 04-08）

- ✅ ScopeGAgentEndpoints 6+ 处 TCS+SubscribeAsync — 已删除
- ✅ StreamingProxyEndpoints 2 处 SubscribeAsync — 已替换为 `StreamingProxyRoomSessionEventProjector` 主链
- ✅ StreamingProxyGAgent `_proxyState` 影子字段 — 已删除
- ✅ ScopeServiceEndpoints script-execution 路径 — 改为 `EnsureAndAttachAsync` + `IEventSink<EventEnvelope>` + projectionLease

---

## 12. CI Guard 补充建议

| 建议 | 优先级 | 原因 |
|------|--------|------|
| 新增 `nyxid_chat_streaming_bypass_guard.sh`：禁止 `agents/Aevatar.GAgents.NyxidChat/` 内 `subscriptionProvider.SubscribeAsync<EventEnvelope>` + `TaskCompletionSource` 共现 | High | 防止 M1 修复后回退 |
| 新增 `replay_guard_actor_ownership_guard.sh`：扫描所有 `*ReplayGuard*` 类，禁止 `ConcurrentDictionary` 字段 | Medium | 防止 B1 模式扩散 |
| 在 `architecture_guards.sh` 主入口增加 `agents/` 扫描根 | Medium | 04-08 已建议；本次仍需复检（部分 guard 已含，主入口未统一） |
| 新增 `frontend_build_smoke_guard.sh`：在 CI 跑 `apps/aevatar-console-web` 与 `tools/Aevatar.Tools.Cli/Frontend` 构建 | Medium | env-tooling + 真实 TS 错误一直被跳过 |
| 在 `Directory.Packages.props` 升级流程加 `dotnet list package --vulnerable` 强制门禁 | Medium | 防止 OpenTelemetry 类 NU1902 持续累积 |
| 在 `proto_lint_guard.sh` 中区分 "buf 缺失=skip" vs "buf 存在=必须通过" | Low | 当前软警告状态下 schema lint 实际未执行 |

---

## 13. 里程碑路径核验

最近 10 个 PR 主题集中在：cross-channel LLM selection（#557 已合）、Studio team-first orchestration（#542 OPEN）、Studio member async bind（#548 DRAFT）、NyxID OAuth healing（#552/#541 已合）、Workflow suspension type enum（#554 OPEN）。

| 里程碑路径 | 关键代码 | 测试 | 风险 |
|---|---|---|---|
| Cross-channel LLM selection | `chatRequest.Metadata[LLMRequestMetadataKeys.NyxIdAccessToken]` 注入（NyxIdChatEndpoints.Streaming.cs:100）+ `LLMRequestMetadataKeys.ModelOverride` 透传（WorkflowRunGAgent.cs:1007）| `Aevatar.AI.Tests/NyxIdChat*` | M1 风险直接命中此路径：streaming runner bypass 影响 LLM 选择路径的可观察性 |
| Studio team-first console flows（#542）| `Aevatar.Studio.Application/...` | `Aevatar.Studio.Tests` | 一处 CS8613 nullability 不匹配（StudioMemberPRReviewFixesTests.cs:255）|
| NyxID OAuth healing | `NyxIdRelayReplayGuard` + OCC `PersistDomainEventAsync` callback overload（最近 commit `adcce099`）| `Aevatar.GAgents.ChannelRuntime.Tests` 大量 `UserAgentCatalogProjectorTests`，但伴随 `[Obsolete]` 字段警告 | B1 / B3 风险共存 |

---

## 14. 团队会议讨论建议

按 audit blueprint 收尾原则："scorecard goes to team meeting. Ask: 'which of these are intentional vs accidental?'"

| 待裁定项 | 提案 |
|---|---|
| M1 NyxIdChatStreamingRunner | **不要**沿用 `WorkflowExecutionRunEventProjector` / `scriptExecutionProjectionPort` 模板（那是 fact-replay 形状）。先出一份 "Actor Stream Port" ADR：`IActor` 拥有 `AttachInboundStream`/`SubscribeOutbound`，frame 用 `oneof`（text / audio / video / tool-approval / control），由 actor runtime 跨节点透传；与 `Aevatar.Foundation.VoicePresence.*` 共用同一抽象。NyxIdChat 与 VoicePresence 都成为该 port 的首批消费者 |
| B1 NyxIdRelayReplayGuard | 多节点目标日期前必须 actor 化或外置；如果中长期单节点，写 ADR 标记并加多节点上线门禁 |
| B6 治理子系统 | 在 `/plan-eng-review` 显式裁定：当前 ServiceConfigurationGAgent 是否就是终态？是 → 重命名 "ServiceCatalog/ACL"，去掉 "Governance" 字眼；否 → 给出 Goal/ObjectiveFunction 模型升级 ADR |
| OpenTelemetry NU1902 × 4 | 升级到 1.16.x，本周内合 |
| `_executionItems` 设计 | 选择：(a) 加 reactivation 单测；(b) 改 method-local；(c) 升级到 `State.RuntimeItems` 持久化；按当前依赖深度选最便宜的 |

---

**Generated by**: arch-audit skill on 2026-05-03
**Inputs verified**: dotnet build (0 err) · 8 CI guards (all pass) · 105 Architecture.Tests (pass) · 4 manual deep-dive scans · prior audit comparison
