# CQRS Framework Software Engineering Audit

> **日期**：2026-03-18
> **范围**：`Aevatar.CQRS.Core*`、`Aevatar.CQRS.Projection.*`、关联投影消费者
> **方法**：SOLID 原则 + CLAUDE.md 架构约束 + 代码逐文件审查
> **严重级别**：CRITICAL / HIGH / MEDIUM / LOW

---

## 0. 审计概要

| 级别 | 数量 | 关键词 |
|------|------|--------|
| CRITICAL | 3 | Fire-and-forget 线程模型、Scope Actor 职责膨胀、Activation/Release 接口对称冗余 |
| HIGH | 5 | 泛型爆炸、Lease 混责、Port 基类重复、Dispatcher 过度泛化、Cleanup 异常吞没 |
| MEDIUM | 5 | Context Factory 空转发、DispatchResult 语义模糊、Lease ActorId 泄漏、Marker Interface 不可执行、CancellationToken 丢失 |
| LOW | 3 | 硬编码 Mode 映射、EventChannel 配置不灵活、Receipt 缺少完成观察提示 |

**总体评价**：Projection 读侧设计优秀（统一 EventEnvelope、精确 TypeUrl 路由、版本单调覆盖、CI 门禁完备）；问题集中在 **CQRS Core 命令侧线程模型**、**Projection 编排层过度抽象** 和 **DI 注册泛型爆炸**。

---

## 1. CRITICAL 级问题

### C-1: DefaultDetachedCommandDispatchService Fire-and-Forget 违反 Actor 哲学

**文件**：`src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs:52-131`

**现象**：
```csharp
_ = Task.Run(async () =>
{
    // 后台线程监控 live sink、解析 durable completion、执行 cleanup
    await _outputStream.PumpAsync(..., CancellationToken.None);
    await _durableCompletionResolver.ResolveAsync(receipt, CancellationToken.None);
    await target.ReleaseAfterInteractionAsync(..., CancellationToken.None);
}, CancellationToken.None);
```

**违反的架构原则**：
1. **"回调只发信号"**（CLAUDE.md）：`Task.Run` 回调直接读写 `observedCompleted`/`observedCompletion` 状态，且执行 target cleanup。
2. **"业务推进内聚"**（CLAUDE.md）：completion 判定与 cleanup 发生在请求上下文之外的线程池线程，不在 Actor 事件处理流程内。
3. **"跨 actor 等待必须 continuation 化"**（CLAUDE.md）：应通过"发送请求事件 -> 结束当前 turn -> 由 reply/timeout event 唤醒"模型，而非后台轮询 stream。

**风险**：
- 后台线程无 CancellationToken 传播，应用关停时 cleanup 可能无限挂起。
- 异常仅 `LogWarning`，调用方永远观察不到真实完成状态。
- Target actor 不知道自己正被后台监控，存在 `RequireLiveSink()` 竞态。

**建议修复方向**：
- 方案 A：将后台监控改为显式后台 Actor（持有 receipt + timeout continuation）。
- 方案 B：将 detached 语义改为纯 fire-and-forget dispatch，不监控 completion（由 projection 负责完成态物化）。

---

### C-2: ProjectionScopeGAgentBase 职责膨胀（7 职责）

**文件**：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs`（255 行）

**现象**：单个基类承载：
1. Actor 生命周期管理（`OnActivateAsync` / `OnDeactivateAsync`）
2. Scope 命令处理（`HandleEnsureAsync` / `HandleReleaseAsync`）
3. 观察流订阅管理（`ProjectionObservationSubscriber`）
4. 观察信号自转发（`ForwardObservationAsync` → `PublishAsync` Self）
5. 分发执行与路由（`DispatchObservationAsync` → `ProcessObservationCoreAsync`）
6. 失败记录与告警（`RecordDispatchFailureAsync` → `IProjectionFailureAlertSink`）
7. 失败回放（`HandleReplayAsync`）
8. 水位推进（`PersistDomainEventAsync(WatermarkAdvancedEvent)`）

**违反**：SRP（Single Responsibility Principle）。

**风险**：
- 任何职责的变更都需要修改此基类，影响所有 Scope 实现。
- 失败逻辑与分发逻辑耦合，难以独立测试。
- 水位推进与分发结果耦合在同一方法链中。

**建议**：拆分为：
- `ProjectionObservationSubscriptionManager`：订阅 attach/detach
- `ProjectionScopeFailureTracker`：失败记录 + 回放 + 告警
- `ProjectionScopeGAgentBase`：只保留生命周期 + 命令处理 + 分发委派

---

### C-3: Session / Materialization 激活释放接口对称冗余

**文件**：`src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/`

**现象**：四个接口具有完全相同的契约签名：
- `IProjectionSessionActivationService<TLease>`
- `IProjectionMaterializationActivationService<TLease>`
- `IProjectionSessionReleaseService<TLease>`
- `IProjectionMaterializationReleaseService<TLease>`

它们的实现（`ProjectionSessionScopeActivationService` vs `ProjectionMaterializationScopeActivationService`）逻辑完全一致，只是 DI 注册时的泛型参数不同。

**违反**：
- **"不保留无效层"**（CLAUDE.md）：空转发、重复抽象。
- **"扩展对称性"**（CLAUDE.md）：内建能力与扩展能力应遵循同一抽象模型。

**风险**：
- DI 注册代码大量重复（Session 和 Materialization 各注册一遍同样的服务集）。
- 新增投影模式时必须同步新增一对 activation/release 接口。
- 测试 double 必须成对编写。

**建议**：统一为 `IProjectionScopeActivationService<TLease>` + `IProjectionScopeReleaseService<TLease>`，通过 `ProjectionRuntimeMode` 枚举区分 Session 和 Materialization。

---

## 2. HIGH 级问题

### H-1: DI 注册泛型参数爆炸

**文件**：`src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializationRuntimeRegistration.cs`

**现象**：
```csharp
AddProjectionMaterializationRuntimeCore<TContext, TRuntimeLease, TScopeAgent>(...)
```
三个泛型参数 + lambda 注入，调用方需要手动指定全部类型。命令侧更甚：
```csharp
DefaultDetachedCommandDispatchService<TCommand, TTarget, TReceipt, TError, TEvent, TFrame, TCompletion>
```
**7 个泛型参数**，阅读与调试成本极高。

**建议**：引入 builder pattern 或 options object 收敛泛型参数。

### H-2: EventSinkProjectionRuntimeLeaseBase 锁破坏 Actor 模型

**文件**：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionRuntimeLeaseBase.cs`

**现象**：`EventSinkProjectionRuntimeLeaseBase<TEvent>` 使用 `lock` + `List<LiveSinkSubscription>` 管理 live sink 订阅状态。

**违反**：
- **"单线程事实源"**（CLAUDE.md）：Actor 运行态应在事件处理主线程修改。
- **"无锁优先"**（CLAUDE.md）：需要加锁的设计应重构为事件化串行模型。

### H-3: Port 基类重复

**文件**：
- `src/Aevatar.CQRS.Projection.Core/Orchestration/MaterializationProjectionPortBase.cs`
- `src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs`

两个基类的 `EnsureProjectionAsync` / `ReleaseProjectionAsync` 逻辑完全一致，仅后者额外持有 event hub。

**建议**：合并为单一基类，event hub 作为可选依赖注入。

### H-4: ProjectionStoreDispatcher 过度泛化

**文件**：`src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionStoreDispatcher.cs`（176 行）

**现象**：设计为多 binding 重试 + 补偿的通用分发器，但实际只有 `ProjectionDocumentStoreBinding` 一个实现。Graph 写入走独立的 `IProjectionGraphWriter`，不进入 dispatcher。

**违反**：**"不保留无效层"** + **"抽象优先"的反面——过度抽象**。

### H-5: DefaultCommandInteractionService Cleanup 异常吞没

**文件**：`src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs:125-133`

**现象**：
```csharp
catch (Exception ex) when (executionException != null)
{
    _logger.LogWarning(ex, "..."); // 吞没 cleanup 异常
}
```
当 `executionException == null`（执行成功）时，cleanup 异常会作为未观察异常逃逸；当执行失败时，cleanup 异常被静默记录。

**建议**：使用 `AggregateException` 聚合异常，或确保 cleanup 异常始终被显式处理。

---

## 3. MEDIUM 级问题

### M-1: ProjectionScopeContextFactory 空转发

**文件**：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeContextFactory.cs`

**现象**：28 行代码仅包装一个 `Func<ProjectionRuntimeScopeKey, TContext>` lambda。每次观察分发都需要 DI lookup + factory call。

**违反**：**"不保留无效层"**。

### M-2: ProjectionScopeDispatchResult 语义模糊

**文件**：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs:257-271`

**现象**：
- `LastSuccessfulVersion` 始终等于 `LastObservedVersion`（见 `Success()` 工厂方法），是冗余字段。
- `EventType` 仅用于诊断日志，不参与业务决策。
- 字段值仅在 `Handled = true` 时有效，但类型层面不强制。

### M-3: IProjectionRuntimeLease 泄漏 ActorId

**文件**：`src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionRuntimeLease.cs`

**现象**：`RootEntityId` 属性暴露了 actor 身份到 lease 契约。

**违反**：**"actorId 对调用方是不透明地址"**。

### M-4: Materializer Kind Marker Interface 不可执行

**文件**：`src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializerKinds.cs`

**现象**：`ICurrentStateProjectionMaterializer<TContext>` 和 `IProjectionArtifactMaterializer<TContext>` 是空 marker interface。类型系统不阻止同时实现两者或违反 current-state 只覆盖写入的语义约束。

### M-5: CancellationToken 全面丢失

**文件**：多处

**现象**：`DefaultDetachedCommandDispatchService` 和 `DefaultCommandInteractionService` 在 finally 块中统一使用 `CancellationToken.None`，cleanup 操作无法取消。包括：
- `ReleaseAfterInteractionAsync(..., CancellationToken.None)`
- `_durableCompletionResolver.ResolveAsync(receipt, CancellationToken.None)`
- `_outputStream.PumpAsync(..., CancellationToken.None)`

---

## 4. LOW 级问题

### L-1: ProjectionScopeModeMapper 硬编码魔数

**文件**：`src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeActorId.cs:25-34`

**现象**：
```csharp
(ProjectionScopeMode)(mode == ProjectionRuntimeMode.DurableMaterialization ? 1 : 2)
```
应直接使用 protobuf 枚举值。

### L-2: EventChannel 配置不灵活

**文件**：`src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSink.cs:38-50`

**现象**：
- `SingleWriter = false` 允许多并发写入，可能导致事件乱序。
- 默认容量 1024 不可在应用层按场景调整。
- `BoundedChannelFullMode.Wait` 模式下读取端挂起会导致写入端无限阻塞。

### L-3: Receipt 缺少完成观察提示

**文件**：`src/Aevatar.CQRS.Core.Abstractions/Commands/ICommandReceiptFactory.cs`

**现象**：Receipt 不携带完成观察路径（如 read model 位置、projection session ID、预期等待时间），调用方必须自行发明观察策略。

---

## 5. 优秀实践（正面评价）

以下是框架中值得保留和推广的设计：

### 5.1 统一 EventEnvelope + CommittedStateEventPublished

`CommittedStateEventEnvelope` 静态辅助类提供了一致的 unpack 语义：`TryUnpack` / `TryUnpackState<TState>` / `TryGetObservedPayload` / `TryCreateObservedEnvelope`，所有 projector 共享同一条解包链路。

### 5.2 精确 TypeUrl 路由 + CI 门禁

`projection_route_mapping_guard.sh` 强制所有 reducer 使用 `EventTypeUrl` 精确键路由 + `StringComparer.Ordinal` + `TryGetValue`，杜绝了 `TypeUrl.Contains()` 模糊匹配。

### 5.3 版本单调覆盖语义

`ProjectionWriteResultEvaluator.Evaluate()` 实现了完整的版本语义判定（Applied / Stale / Gap / Conflict），且与 actor_id 一致性校验绑定，保证了 readmodel 的权威性。

### 5.4 Protobuf 全链路序列化

State、事件、命令、投影 payload 全部走 Protobuf。ES 存储层的 JSON 转换被限制在 provider boundary 内，未向核心模型扩散。

### 5.5 Query 路径与 Projection 路径严格分离

`query_projection_priming_guard.sh` 门禁确保 query reader 不调用 activation/priming/lifecycle 操作，readmodel 查询只读取已物化结果。

### 5.6 Scope Actor 状态转换解耦

`ProjectionScopeStateApplier` 使用纯函数 `StateTransitionMatcher` 实现状态转换，可独立测试，不依赖 Actor 运行时。

---

## 6. InMemory vs Elasticsearch 行为一致性分析

| 维度 | InMemory | Elasticsearch | 一致性 |
|------|----------|---------------|--------|
| 写入语义 | `ProjectionWriteResultEvaluator` 共享 | `ElasticsearchOptimisticWriter` 独立实现 | **需验证** |
| 查询过滤 | 反射 `PropertyInfo` + dot-path | ES Query DSL 映射 | **可能不一致** |
| 排序 | `Comparer` + reflection | ES `_sort` field mapping | **可能不一致** |
| 分页 | offset-based cursor | search_after cursor | **语义不同** |
| 缺失 index 行为 | 返回空 | 可配置 Throw/ReturnEmpty | **不同** |
| 动态索引 | 不支持 | 支持 `indexScopeSelector` | **不对称** |

**风险**：开发环境（InMemory）通过的查询在生产环境（Elasticsearch）可能行为不一致，尤其是复杂过滤和排序场景。

**建议**：增加 cross-provider 查询行为一致性集成测试。

---

## 7. 量化指标

| 指标 | 当前值 | 建议阈值 |
|------|--------|----------|
| ProjectionScopeGAgentBase 职责数 | 7 | ≤ 3 |
| DefaultDetachedCommandDispatchService 泛型参数 | 7 | ≤ 4 |
| Session/Materialization 重复接口对 | 4 (2 activation + 2 release) | 2 (1 activation + 1 release) |
| Port 基类重复代码行 | ~60 行 | 0 |
| ProjectionStoreDispatcher binding 实现数 | 1 | ≥ 2 或删除 multi-binding |
| DispatchResult 冗余字段 | 1 (LastSuccessfulVersion) | 0 |
| CancellationToken.None 在 cleanup 中使用数 | 7 处 | 0 |

---

## 8. 修复优先级路线图

```
Phase 1 (CRITICAL - 立即)
├── C-1: 消除 fire-and-forget Task.Run，改为 continuation 化或纯 dispatch 语义
├── C-3: 合并 Session/Materialization 激活释放接口
└── H-5: 修复 cleanup 异常吞没

Phase 2 (HIGH - 近期)
├── C-2: 拆分 ProjectionScopeGAgentBase（订阅 + 失败 + 基类）
├── H-2: EventSinkProjectionRuntimeLeaseBase 去锁化
├── H-3: 合并 Port 基类
└── H-4: 评估 ProjectionStoreDispatcher 是否保留 multi-binding

Phase 3 (MEDIUM - 中期)
├── H-1: 引入 builder pattern 收敛泛型参数
├── M-1: 删除 ProjectionScopeContextFactory 空转发
├── M-2: 清理 DispatchResult 冗余字段
└── M-5: 在安全的 cleanup 路径传播 CancellationToken

Phase 4 (LOW - 后续)
├── L-1: Mode 映射使用 protobuf 枚举值
├── L-2: EventChannel 配置化
├── L-3: Receipt 增加完成观察元信息
└── InMemory/ES 行为一致性测试
```

---

## 9. 文件索引

### CQRS Core（命令侧）
| 文件 | 问题 |
|------|------|
| `src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs` | C-1, M-5 |
| `src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs` | H-5, M-5 |
| `src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSink.cs` | L-2 |
| `src/Aevatar.CQRS.Core.Abstractions/Commands/ICommandReceiptFactory.cs` | L-3 |

### Projection Core（投影侧）
| 文件 | 问题 |
|------|------|
| `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs` | C-2, M-2 |
| `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/` (4 activation/release) | C-3 |
| `src/Aevatar.CQRS.Projection.Core/DependencyInjection/ProjectionMaterializationRuntimeRegistration.cs` | H-1 |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionRuntimeLeaseBase.cs` | H-2 |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/MaterializationProjectionPortBase.cs` | H-3 |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs` | H-3 |
| `src/Aevatar.CQRS.Projection.Runtime/Runtime/ProjectionStoreDispatcher.cs` | H-4 |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeContextFactory.cs` | M-1 |
| `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Ports/IProjectionRuntimeLease.cs` | M-3 |
| `src/Aevatar.CQRS.Projection.Core.Abstractions/Abstractions/Pipeline/IProjectionMaterializerKinds.cs` | M-4 |
| `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeActorId.cs` | L-1 |

### Providers
| 文件 | 问题 |
|------|------|
| `src/Aevatar.CQRS.Projection.Providers.Elasticsearch/Stores/ElasticsearchProjectionDocumentStore.cs` | 行为一致性 |
| `src/Aevatar.CQRS.Projection.Providers.InMemory/Stores/InMemoryProjectionDocumentStore.cs` | 行为一致性 |
