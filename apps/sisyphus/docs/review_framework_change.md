# Framework Layer Necessity Review — feature/sisyphus Branch

> Review date: 2026-03-02 (revised)
> Base branch: dev (897f276)
> Feature branch: feature/sisyphus (4f4eb33)
> 原则: **App 层应最大限度适配/复用 framework 已有能力，非必要不扩展、不新增**

---

## 审查标准

对每项 framework 改动，回答一个问题：**Sisyphus 能否不改 framework 而实现同样目标？**

- **NEEDED** — framework 确实缺失能力，app 层无法绕过
- **PREMATURE** — 能力有价值但当前无实际消费者或无真实痛点
- **NOT NEEDED** — dev 已有等价能力，或 app 层可自行实现
- **APP-LAYER** — 逻辑本身属于 Sisyphus，不应放在 framework

---

## 1. Foundation — Event Sourcing

### Dev 基线

Dev 已有完整的 event sourcing 骨架：`IEventStore` (3 methods)、`IEventSourcingBehavior<T>` (4 methods)、`EventSourcingBehavior<T>` (~80 行线性 replay)、`ISnapshotStrategy` + 2 实现、`InMemoryEventStore`、`GAgentBase<TState>` with `IStateStore<TState>` 持久化。

### 逐项判定

| 组件 | 行数 | 判定 | 理由 |
|------|------|------|------|
| `IEventSourcingSnapshotStore<T>` + `EventSourcingSnapshot<T>` | ~25 | **PREMATURE** | Sisyphus agent 每个实例仅产生 **<10 个事件**（`WorkflowGAgent` 2 个、`RoleGAgent` 1 个）。snapshot interval 200 永远不会触发。从 version 0 replay 5 个事件耗时微秒级，无性能痛点 |
| Compaction (`DeleteEventsUpToAsync` + `IEventStoreCompactionScheduler` + `DeferredEventStoreCompactionScheduler`) | ~110 | **PREMATURE** | 依赖 snapshot（上条已判定 premature）。更关键：`DeleteEventsUpToAsync` 是对 `IEventStore` 接口的 **breaking change**，强制所有实现都要实现它，代价不对等 |
| `FileEventStore` + `FileEventSourcingSnapshotStore` + `FileEventStoreOptions` | ~390 | **NOT NEEDED** | Sisyphus 代码 **零引用**。自身 Review.md 标注 O(N²)、无 fsync、semaphore 不释放。`InMemoryEventStore` 覆盖测试场景，Garnet 覆盖生产 |
| `GarnetEventStore` (独立项目) | ~282 | **NEEDED** | 分支将 `GAgentBase<TState>` 从 `IStateStore` 改为强制 event sourcing，生产必须有持久化 `IEventStore`。Garnet 已正确放在独立包 |
| `IEventSourcingBehaviorFactory<T>` + `DefaultEventSourcingBehaviorFactory` | ~90 | **NEEDED** | 分支架构下 `GAgentBase<TState>` 需要 runtime 注入 behavior，factory 是 DI 桥梁 |
| `IStateEventApplier<T>` + `StateEventApplierBase` pipeline | ~50 | **NOT NEEDED** | 全仓库 **零个具体实现**。所有 agent 都直接 override `TransitionState`。这是一个无消费者的扩展点 |
| `StateTransitionMatcher` — `TryExtract<T>` 部分 | ~30 | **NEEDED** | event sourcing 经过 `Any.Pack()` 后 replay 需要 unpack。没有 `TryExtract` 每个 agent 都要写 `Any.TryUnpack<T>()` 样板代码 |
| `StateTransitionMatcher` — Builder (`.On<T>().OrCurrent()`) | ~45 | **NOT NEEDED** | 纯语法糖。`if (TryExtract<T>(evt, out var e)) return Apply(state, e); return current;` 同样简洁，且避免每次 `TransitionState` 调用的 Builder 堆分配 |
| `IActorDeactivationHook` + Dispatcher + `EventStoreCompactionDeactivationHook` | ~80 | **NOT NEEDED** | 唯一实现是 compaction hook（上面已判定 premature）。Sisyphus **零引用**。dev 的 `OnDeactivateAsync` 虚方法已提供 per-agent deactivation 扩展点 |
| `EventSourcingRuntimeOptions` | ~25 | **PREMATURE** | 配置 snapshot interval + compaction 开关。snapshot/compaction 本身 premature，配置也 premature |
| `InMemoryEventSourcingSnapshotStore<T>` | ~36 | **PREMATURE** | 测试用 snapshot store。snapshot 本身 premature |
| `GenAIMetrics` | ~25 | **NEEDED** | LLM token 用量/延迟/工具调用指标。调付费 API 的系统 day-1 就需要成本监控 |
| `AevatarActivitySource` GenAI spans | ~80 | **NEEDED** | OpenTelemetry GenAI Semantic Conventions (`invoke_agent`, `chat`, `execute_tool`)。跨切面可观测性 |
| `AevatarObservabilityOptions` | ~10 | **PREMATURE** | 敏感数据开关，但实际未接入 `AevatarActivitySource` 的静态字段。wiring 未完成 |

### Foundation 小结

| 判定 | 组件数 | 行数 |
|------|--------|------|
| NEEDED | 5 | ~507 |
| PREMATURE | 5 | ~206 |
| NOT NEEDED | 4 | ~565 |

**结论**: Foundation 层 ~60% 的新增代码 (~771 行) 是 premature 或 not needed。核心问题是 **snapshot + compaction 整条链路在 Sisyphus 的事件规模下没有实际价值**，但它引入了 `IEventStore` breaking change + deactivation hook 系统 + runtime options 等连锁复杂度。建议保留 `GarnetEventStore`、`BehaviorFactory`、`TryExtract`、`GenAIMetrics`、`ActivitySource`，其余推迟到事件量真正成为瓶颈时再引入。

---

## 2. CQRS Projection

### Dev 基线

Dev 已有：`Aevatar.CQRS.Projection.Abstractions` (单包 20 files)、`IProjectionReadModelStore<TReadModel, TKey>` (Upsert/Mutate/Get/List)、`IProjectionCoordinator`、`IProjectionProjector`、`IProjectionDispatcher`、`IProjectionLifecycleService`、`ProjectionOwnershipCoordinatorGAgent`、`AevatarReadModelBase`、`InMemoryWorkflowExecutionReadModelStore`。

### 逐项判定

| 组件 | 行数 | 判定 | 理由 |
|------|------|------|------|
| Abstractions 三拆 (Core/Stores/Runtime) | ~300 | **NOT NEEDED** | 15 个接口签名不变、仅 namespace 移动。单包运行正常，三拆增加包管理复杂度，零功能收益 |
| `IProjectionDocumentStore` (重命名 `IProjectionReadModelStore`) | ~10 | **NOT NEEDED** | 方法签名完全相同 (Upsert/Mutate/Get/List)。仅添加 `IProjectionReadModel` marker 约束并改名。dev 接口已够用 |
| `IProjectionGraphStore` + 数据模型 | ~150 | **PREMATURE** | workflow 层有引用，但 Sisyphus app 层 **零引用**。Sisyphus 的知识图谱由独立的 Chrono Graph 服务（Neo4j REST API）提供，不走 projection graph store |
| `IProjectionStoreDispatcher` + 实现 | ~200 | **PREMATURE** | multi-store fan-out，但默认 compensator 是 **no-op**（`NoOpProjectionStoreDispatchCompensator`）。`MutateAsync` 有 TOCTOU race。给消费者虚假的一致性保证。app 层写个 50 行 `CompositeStore` 更诚实 |
| `IProjectionStoreBinding` family (3 interfaces) | ~50 | **NOT NEEDED** | 仅服务于 dispatcher（上条已判定 premature） |
| `ProjectionDocumentStoreBinding` | ~50 | **NOT NEEDED** | `IProjectionDocumentStore` → `IProjectionStoreBinding` 纯委托。跟随 dispatcher |
| `ProjectionGraphStoreBinding` | ~250 | **PREMATURE** | graph upsert 规范化 + managed owner lifecycle。跟随 graph store。且有 **N+1 严重问题**（100 nodes + 200 edges = 300+ 顺序 round-trips） |
| Port 抽象 (4 interfaces + 2 base classes) | ~200 | **NEEDED** | `ProjectionLifecyclePortServiceBase` / `ProjectionQueryPortServiceBase` 消除了 projection lifecycle/query 接入的 ~100 行重复 boilerplate。workflow 层 6 处实现。dev 无等价物 |
| `ProjectionStateMirror` (整个项目) | ~200 | **PREMATURE** | workflow 和 Sisyphus **零引用**。无消费者 |
| Elasticsearch Provider | ~800 | **NEEDED** | 生产级 document store。但应实现 dev 的 `IProjectionReadModelStore` 而非重命名后的 `IProjectionDocumentStore`。可作为独立 NuGet 包 |
| Neo4j Provider | ~830 | **PREMATURE** | 仅通过 `IProjectionGraphStore` 消费（上面已判定 premature）。自身有 session-per-operation / 无 batch / 无事务问题 |
| InMemory Provider (generic) | ~270 | **NEEDED** | 替代 dev 的 per-domain 手写 store (`InMemoryWorkflowExecutionReadModelStore` 120 行 clone logic)。通用泛型实现消除重复。但应实现 dev 的 `IProjectionReadModelStore` |
| `ProjectionOwnershipCoordinator` TTL + event sourcing | ~120 | **NEEDED** | 修复真实 bug：crash 后 session 永久锁定 projection scope。TTL 过期允许其他 session 接管。必须在 framework |
| `ProjectionReadModelBase<TKey>` | ~30 | **NEEDED** | dev 的 `AevatarReadModelBase` 混入了领域字段 (`RootActorId`/`CommandId`)。泛型基类正确分离 framework metadata vs domain fields |

### CQRS Projection 小结

| 判定 | 组件数 | 行数 |
|------|--------|------|
| NEEDED | 5 | ~1,420 |
| PREMATURE | 5 | ~1,630 |
| NOT NEEDED | 4 | ~410 |

**结论**: ~60% 的 CQRS Projection 新增代码 (~2,040 行) 是 premature 或 not needed。最大问题是 **Graph Store 整条链路**（抽象 + binding + Neo4j + InMemory graph）在 Sisyphus 无实际消费者——Sisyphus 的图谱由独立的 Chrono Graph 服务提供。`IProjectionStoreDispatcher` 的 no-op compensator + TOCTOU race 更是弊大于利。

建议保留 Port 抽象、ES provider (实现 dev 接口)、泛型 InMemory store (实现 dev 接口)、Coordinator TTL fix、`ProjectionReadModelBase<TKey>`。其余推迟。

---

## 3. AI Layer

### Dev 基线

Dev 已有：`ChatRuntime` (`ChatAsync` 非 streaming + `ChatStreamAsync` 返回 `string` delta)、`ToolCallLoop` (~148 行基本循环，无 streaming)、`RoleGAgent` (无 event sourcing)、`LLMStreamChunk.DeltaToolCall` (类型已定义但 provider 未填充)、`ExecutionTraceHook` (极简日志)、MCP stdio-only transport。

**关键架构缺陷**: dev 的 `ChatStreamAsync` **完全绕过 `ToolCallLoop`**——它直接调用 provider 的 streaming API，不做任何 tool calling。意味着 dev 上 streaming 和 tool calling 是二选一，无法同时使用。

### 逐项判定

| 组件 | 行数 | 判定 | 理由 |
|------|------|------|------|
| `ChatRuntime` `onContent` callback | +77 | **NEEDED** | **核心阻塞**。dev 的 streaming 绕过 ToolCallLoop，无法同时 stream + tool call。`onContent` callback 将 streaming 穿透到 tool-call loop 内部。`ToolCallLoop` 是 `ChatRuntime` 的内部封装，app 层无法注入 callback |
| `StreamingToolCallAccumulator` | +148 | **NEEDED** | 重组 streaming tool call delta 为完整 `ToolCall`。处理 provider 差异（匿名 ID、partial arguments、promotion）。app 层无法做——这是 provider-agnostic 的协议处理 |
| `ToolCallLoop` streaming path | +80 | **NEEDED** | 当 `onContent != null` 时用 `ChatStreamAsync` 替代 `ChatAsync`，在 tool-call 轮次间实时推送 token。这是让 streaming + tool calling 共存的唯一路径 |
| `ToolCallLoop` graceful exhaustion | +30 | **NEEDED** | dev 耗尽 maxRounds 后返回 `messages.LastOrDefault()?.Content`——可能是 partial tool call response 或 null。分支改为最后一轮 `Tools=null` 强制 LLM 生成文本总结。正确性修复 |
| `ToolCallLoop` context trimming (`TrimMessagesIfOverLimit`) | +50 | **PREMATURE** | `MaxContextChars=400K` / `MaxToolResultChars=200K` 硬编码。但 `ClearHistory()` 已在每个 workflow step 前清空历史，accumulation 被阻断。无证据表明 Sisyphus 当前触及 context limit。应为 opt-in 配置 |
| `MEAILLMProvider` / `TornadoLLMProvider` streaming 增强 | +185 | **NEEDED** | dev 的 provider `ChatStreamAsync` 只 emit `DeltaContent`，完全忽略 `FunctionCallContent`/`ToolCalls`。分支让 provider 实际填充 `DeltaToolCall`。更关键：MEAI provider 修复了 `AgentToolAIFunction` — dev 版用 `AIFunctionFactory.Create((string input) => ...)` 导致 LLM 看到单一 `string input` 参数而非真实 JSON Schema，**tool calling 在 dev 上实际是坏的** |
| `RoleGAgentFactory.ApplyModuleExtensions` 提取 | ~30 | **NEEDED** | dev 内联在 `ApplyConfig`。提取为 public static 使 event handler 也能调用。干净的重构 |
| MCP HTTP/SSE transport + `OAuthTokenHandler` | ~200 | **NEEDED** | Sisyphus 的 NyxId MCP Server 是远程 HTTP 服务 + OAuth `client_credentials`。dev 仅支持 stdio。`MCPClientManager` 是封装的框架代码，app 层无法注入不同 transport |
| `ExecutionTraceHook` 丰富日志 | +23 | **NOT NEEDED** | dev 已有 hook，仅缺细节。app 层可通过 `additionalHooks` 构造器参数注册自定义 hook 实现相同效果 |
| **`RoleGAgent.ClearHistory()`** | **1** | **APP-LAYER** | 注释写 "Each workflow step is independent"。这是 **Sisyphus workflow 约定**，非通用行为。独立对话场景需要历史连续性。应由 workflow 层在 dispatch `ChatRequestEvent` 前清除，或在 `ChatRequestEvent` 加 `ClearHistory` flag |
| **`RoleGAgent` event sourcing (`PersistDomainEventAsync` + `TransitionState`)** | **~30** | **APP-LAYER** | 不是所有 `RoleGAgent` 消费者都需要配置持久化。应为 opt-in：在 Sisyphus 子类中 override `HandleConfigureRoleAgent` 加 `PersistDomainEventAsync`，或用 `PersistentRoleGAgent` 子类 |
| **`ToolCallEventPublishingHook`** (built-in default) | **+41** | **APP-LAYER** | 硬编码在 `AIGAgentBase` 的内置 hook list 中，导致 **所有** AI agent 无条件发布 tool call 事件。应改为 opt-in：Sisyphus 通过 `additionalHooks` 注册 |
| **`ChatSessionKeys` 4-arg 重载** | **+14** | **APP-LAYER** | `runId`/`attempt` 是纯 workflow 概念。仅被 `src/workflow/` 的 3 个 module 调用。移到 `Workflow.Core/WorkflowSessionKeys` |
| **`MaxToolRounds` 默认 10→30** | **2** | **APP-LAYER** | 30 是 Sisyphus 多工具 agent 偏好。通用默认应保持 10，Sisyphus 在 role YAML 中 override |
| **`RoleConfigurationNormalizer`** | **~100** | **APP-LAYER** | 处理 YAML 中 top-level vs `extensions.*` 双来源合并。这是 Sisyphus workflow YAML schema 的便利层，与 framework 无关。`Connectors` 字段更是纯 Sisyphus 概念 |

### AI Layer 小结

| 判定 | 组件数 | 行数 |
|------|--------|------|
| NEEDED | 7 | ~750 |
| PREMATURE | 1 | ~50 |
| NOT NEEDED | 1 | ~23 |
| APP-LAYER | 6 | ~188 |

**结论**: AI 层是 **framework 改动最有正当性的部分**。核心阻塞是真实的——dev 的 streaming 和 tool calling 互斥，MCP 仅支持 stdio。~74% 的改动 (NEEDED) 修复的是 framework 的实质缺陷。但 ~19% (~188 行) 是 Sisyphus 逻辑入侵 framework（`ClearHistory`、`ToolCallEventPublishingHook` default、`ChatSessionKeys` 4-arg、`MaxToolRounds` 30、`RoleConfigurationNormalizer`）。

---

## 4. Configuration, Presentation, Bootstrap

### 逐项判定

| 组件 | 行数 | 判定 | 理由 |
|------|------|------|------|
| `MCPAuthConnectorConfig` + `Url`/`Headers` | ~60 | **NEEDED** | dev 的 `MCPServerConfig` 仅有 `Command` (stdio)。NyxId 是 HTTP + OAuth。`MCPClientManager` 封装了 transport 创建，app 层无注入点 |
| `ExpandEnvironmentPlaceholders` | ~15 | **NEEDED** | Sisyphus `connectors.json` 用 `${NYXID_MCP_URL}` 等。dev 的 `ReadString()` 原样返回。`AevatarConnectorConfig.LoadConnectors()` 自行读文件，app 层无法拦截预处理。15 行改动，12-factor 标准模式 |
| `ToolCallStartEvent.Args` | +3 | **NEEDED** | `ToolCallStartEvent` 是 sealed record。app 层无法扩展或子类化。backward-compatible optional property |
| `HumanInputRequestEvent` / `HumanInputResponseEvent` | ~40 | **NEEDED** | AGUI channel 只接受 `AGUIEvent` 子类型，SSE 序列化用 `Type` 鉴别器。用 `CustomEvent` 会丢失结构化字段和类型安全。**但 `StepId`/`RunId`/`SuspensionType` 应改为 optional 而非 required** |
| Bootstrap `RegisterMCPTools` 重写 (connectors.json + mcp.json 合并) | ~50 | **NEEDED** | HTTP MCP connector 需要从 `connectors.json` 注册，dev 只读 `mcp.json`。合并逻辑是 MCP HTTP 支持的必要配套 |
| Bootstrap multi-provider machinery (`ProviderKind`/`ProviderSemantic`/`ReadConfiguredProviders` 等 6 个 record/enum) | ~225 | **PREMATURE** | Sisyphus 实际只用一个 provider (deepseek)。dev 的 `AddMEAIProviders(factory => ...)` lambda 已支持多次注册。secrets-store-driven auto-discovery 无当前消费者。6 个内部类型过度工程 |
| **默认 provider `"deepseek"` → `"openai"`** | **2** | **APP-LAYER** | `options.DefaultProvider = "openai"` 一行代码即可在 Sisyphus 的 Startup 中设置。改 framework 默认影响所有 app |

---

## 5. 全量判定汇总

### 5.1 总表

| 层级 | NEEDED | PREMATURE | NOT NEEDED | APP-LAYER | 总行数 |
|------|--------|-----------|------------|-----------|--------|
| Foundation | 507 | 206 | 565 | — | 1,278 |
| CQRS Projection | 1,420 | 1,630 | 410 | — | 3,460 |
| AI | 750 | 50 | 23 | 188 | 1,011 |
| Config/Presentation/Bootstrap | 168 | 225 | — | 2 | 395 |
| **合计** | **2,845** | **2,111** | **998** | **190** | **6,144** |
| **占比** | **46%** | **34%** | **16%** | **3%** | |

### 5.2 详细清单

#### NEEDED — 真正需要的 framework 改动 (~2,845 行, 46%)

| # | 组件 | 层 | 行数 | 原因 |
|---|------|-----|------|------|
| 1 | `GarnetEventStore` | Foundation | 282 | 生产持久化，event sourcing 架构必需 |
| 2 | `IEventSourcingBehaviorFactory<T>` | Foundation | 90 | 强制 ES 架构下的 DI 桥梁 |
| 3 | `StateTransitionMatcher.TryExtract<T>` | Foundation | 30 | Any-unpacking 消除样板 |
| 4 | `GenAIMetrics` | Foundation | 25 | LLM 成本/延迟 day-1 监控 |
| 5 | `AevatarActivitySource` GenAI spans | Foundation | 80 | OTel GenAI 语义约定 |
| 6 | Port 抽象 (4 interfaces + 2 bases) | CQRS | 200 | projection lifecycle/query 接入减 ~100 行/域 boilerplate |
| 7 | Elasticsearch Provider | CQRS | 800 | 生产 document store (应实现 dev 的 `IProjectionReadModelStore`) |
| 8 | InMemory Provider (generic) | CQRS | 270 | 替代 per-domain 手写 store |
| 9 | `ProjectionOwnershipCoordinator` TTL | CQRS | 120 | 修复 crash 后永久锁定 bug |
| 10 | `ProjectionReadModelBase<TKey>` | CQRS | 30 | 分离 framework metadata vs domain fields |
| 11 | `ChatRuntime` `onContent` callback | AI | 77 | **核心阻塞**: dev 的 streaming 绕过 ToolCallLoop |
| 12 | `StreamingToolCallAccumulator` | AI | 148 | Provider-agnostic tool call delta 重组 |
| 13 | `ToolCallLoop` streaming + graceful exhaustion | AI | 110 | Streaming+tools 共存 + maxRounds 耗尽时的正确性修复 |
| 14 | Provider streaming 增强 (MEAI/Tornado) | AI | 185 | Provider 实际填充 `DeltaToolCall` + MEAI tool schema 修复 |
| 15 | `ApplyModuleExtensions` 提取 | AI | 30 | 使 event handler 也能调用模块注册 |
| 16 | MCP HTTP/SSE + `OAuthTokenHandler` | AI | 200 | NyxId 远程 HTTP + OAuth，stdio-only 是硬阻塞 |
| 17 | `MCPAuthConnectorConfig` + `Url`/`Headers` | Config | 60 | HTTP transport config，app 层无注入点 |
| 18 | `ExpandEnvironmentPlaceholders` | Config | 15 | `${VAR}` 展开，15 行，12-factor 标准 |
| 19 | `ToolCallStartEvent.Args` | AGUI | 3 | sealed record，app 层无法扩展 |
| 20 | `HumanInputRequest/Response` events | AGUI | 40 | AGUI channel 类型限制 (但 `StepId`/`RunId`/`SuspensionType` 应改 optional) |
| 21 | Bootstrap `RegisterMCPTools` 合并逻辑 | Bootstrap | 50 | HTTP connector 注册的必要配套 |

#### PREMATURE — 当前无实际价值，推迟到有真实痛点时引入 (~2,111 行, 34%)

| # | 组件 | 层 | 行数 | 原因 |
|---|------|-----|------|------|
| 1 | `IEventSourcingSnapshotStore<T>` + `EventSourcingSnapshot<T>` | Foundation | 25 | Agent 事件量 <10，interval 200 永不触发 |
| 2 | Compaction 全套 (`DeleteEventsUpToAsync` + scheduler + deactivation hook) | Foundation | 190 | 依赖 snapshot (premature)；对 `IEventStore` 是 breaking change |
| 3 | `EventSourcingRuntimeOptions` | Foundation | 25 | 配置 snapshot/compaction 开关，两者都 premature |
| 4 | `InMemoryEventSourcingSnapshotStore<T>` | Foundation | 36 | snapshot 的测试 store，snapshot 本身 premature |
| 5 | `AevatarObservabilityOptions` | Foundation | 10 | 未接入 ActivitySource 静态字段，wiring 未完成 |
| 6 | `IProjectionGraphStore` + data model | CQRS | 150 | Sisyphus 图谱由 Chrono Graph 独立服务提供，不走 projection graph |
| 7 | `IProjectionStoreDispatcher` + binding + compensator | CQRS | 300 | no-op compensator + TOCTOU race，app 层 50 行 CompositeStore 更诚实 |
| 8 | `ProjectionGraphStoreBinding` | CQRS | 250 | 跟随 graph store；N+1 严重问题 |
| 9 | `ProjectionStateMirror` | CQRS | 200 | 零消费者 |
| 10 | Neo4j Provider | CQRS | 830 | 仅通过 premature 的 graph store 消费 |
| 11 | InMemory Graph Store | CQRS | 100 | 跟随 graph store |
| 12 | `ToolCallLoop` context trimming | AI | 50 | `ClearHistory()` 已阻断跨 step 累积；无实际溢出证据 |
| 13 | Bootstrap multi-provider machinery | Bootstrap | 225 | Sisyphus 仅用 1 provider；dev lambda 已支持多注册；6 个内部类型过度工程 |

#### NOT NEEDED — Dev 已有等价能力 (~998 行, 16%)

| # | 组件 | 层 | 行数 | 原因 |
|---|------|-----|------|------|
| 1 | `FileEventStore` + snapshot store + options | Foundation | 390 | 零引用、O(N²)、critical bugs。InMemory 覆盖测试，Garnet 覆盖生产 |
| 2 | `IStateEventApplier<T>` pipeline | Foundation | 50 | 零具体实现。所有 agent 直接 override TransitionState |
| 3 | `StateTransitionMatcher` Builder | Foundation | 45 | 语法糖 + 堆分配。`if/return` 同样简洁 |
| 4 | `IActorDeactivationHook` system | Foundation | 80 | 仅服务 compaction (premature)。dev 有 `OnDeactivateAsync` |
| 5 | Abstractions 三拆 | CQRS | 300 | namespace 移动，零功能收益 |
| 6 | `IProjectionDocumentStore` (重命名) | CQRS | 10 | 与 dev 的 `IProjectionReadModelStore` 签名完全相同 |
| 7 | `IProjectionStoreBinding` family | CQRS | 50 | 仅服务 dispatcher (premature) |
| 8 | `ProjectionDocumentStoreBinding` | CQRS | 50 | 纯委托适配器，跟随 dispatcher |
| 9 | `ExecutionTraceHook` 丰富日志 | AI | 23 | app 层可通过 `additionalHooks` 注册自定义 hook |

#### APP-LAYER — 应在 Sisyphus 而非 framework (~190 行, 3%)

| # | 组件 | 层 | 行数 | 修复方式 |
|---|------|-----|------|----------|
| 1 | `RoleGAgent.ClearHistory()` | AI | 1 | `ChatRequestEvent` 加 `ClearHistory` flag，由 workflow 层设置 |
| 2 | `RoleGAgent` event sourcing | AI | 30 | 改为 opt-in `PersistentRoleGAgent` 子类或 workflow 层 override |
| 3 | `ToolCallEventPublishingHook` default | AI | 41 | 从 `AIGAgentBase` 内置列表移除，Sisyphus 通过 `additionalHooks` 注册 |
| 4 | `ChatSessionKeys` 4-arg | AI | 14 | 移到 `Workflow.Core/WorkflowSessionKeys` |
| 5 | `MaxToolRounds` 10→30 | AI | 2 | 恢复默认 10，Sisyphus role YAML 覆写 |
| 6 | `RoleConfigurationNormalizer` | AI | 100 | YAML schema 便利层，移到 workflow 层。`Connectors` 字段是纯 Sisyphus 概念 |
| 7 | 默认 provider `deepseek`→`openai` | Bootstrap | 2 | Sisyphus Startup 中 `options.DefaultProvider = "openai"` |

---

## 6. 行动计划

### P0 — 合并前必须修复（app 逻辑退出 framework）

| 项 | 改动量 | 做法 |
|----|--------|------|
| `ClearHistory()` | 1 行删除 + 1 proto field | 从 `RoleGAgent.HandleChatRequest` 移除。`ChatRequestEvent` 加 `bool clear_history`，workflow 的 `LLMCallModule` 设 `true` |
| `ToolCallEventPublishingHook` | 1 行移除 | 从 `AIGAgentBase` 内置 hook list 删除。在 `RoleGAgentFactory.ApplyConfig` 或 Sisyphus 的 DI 中按需注册 |
| `ChatSessionKeys` 4-arg | 移动 14 行 | 移到 `Workflow.Core`，更新 3 个 caller |
| `MaxToolRounds` 30 | 1 行 | 恢复默认 10 |
| `HumanInputRequestEvent` required fields | 3 字段 | `StepId`/`RunId`/`SuspensionType` 改为 optional (nullable) |
| 默认 provider `"openai"` | 1 行 | 恢复 `"deepseek"` 或改为 `""` |

### P1 — 合并前移除或推迟的 framework 代码

| 项 | 行数 | 做法 |
|----|------|------|
| `FileEventStore` + snapshot store + options | ~390 | 删除。如需文件 store 日后按 Garnet 模式建独立项目 |
| Snapshot 全套 (`IEventSourcingSnapshotStore` + `EventSourcingRuntimeOptions` + `InMemorySnapshotStore`) | ~86 | 删除或注释为 experimental。从 `EventSourcingBehavior.ReplayAsync` 移除 snapshot 路径 |
| Compaction 全套 (`DeleteEventsUpToAsync` + scheduler + deactivation hooks) | ~270 | 删除。恢复 `IEventStore` 为原始 3-method 接口 |
| `IStateEventApplier<T>` pipeline | ~50 | 删除。零消费者 |
| `StateTransitionMatcher` Builder | ~45 | 保留 `TryExtract`，删除 Builder/`Match`/`On`/`OrCurrent`。agent 用 `if/return` |
| Abstractions 三拆 | — | 恢复 dev 单包 `Aevatar.CQRS.Projection.Abstractions` |
| `IProjectionDocumentStore` 重命名 | — | 恢复为 `IProjectionReadModelStore`。ES/InMemory provider 实现 dev 接口 |
| Graph Store 全套 (抽象 + binding + Neo4j + InMemory graph) | ~1,330 | 移除。图谱需求由 Chrono Graph 独立服务覆盖 |
| `IProjectionStoreDispatcher` + binding family | ~350 | 移除。如需 multi-store，app 层写 CompositeStore |
| `ProjectionStateMirror` | ~200 | 移除。零消费者 |
| `RoleConfigurationNormalizer` | ~100 | 移到 `Workflow.Core` |
| Bootstrap multi-provider machinery | ~225 | 简化为 dev 的单 provider 模式 + MCP 合并逻辑 |
| Context trimming | ~50 | 移除硬编码。如需，改为 `ToolCallLoopOptions.MaxContextChars` opt-in 配置 |

### P2 — 合并后迭代

| 项 | 做法 |
|----|------|
| `RoleGAgent` event sourcing | 评估是否需要默认持久化。如需，提供 `PersistentRoleGAgent` 子类而非改基类 |
| `AevatarObservabilityOptions` | 完成 wiring——从 `IOptions<AevatarObservabilityOptions>` 注入到 `AevatarActivitySource` |
| `ExecutionTraceHook` | 如 Sisyphus 需要，在 app 层注册自定义 hook |
| `IProjectionGraphStore` | 当第二个消费者出现且 Chrono Graph 不满足需求时再引入 |
| Snapshot + Compaction | 当任何 agent 的事件量 >100 时引入。先加遥测确认瓶颈 |

---

## 7. 总结

**Framework 改动的核心价值集中在 AI 层**：dev 的 streaming/tool-calling 互斥是真实的架构缺陷，MCP stdio-only 是硬阻塞。这部分改动正当且必要。

**其余层的问题是 premature abstraction + app 逻辑入侵**：

- Foundation 的 snapshot/compaction 是为 <10 事件的 agent 准备的 200-event-interval 优化
- CQRS 的 graph store 全套是为已有独立服务 (Chrono Graph) 的需求重新造轮子
- `IProjectionStoreDispatcher` 的 no-op compensator 给消费者虚假的一致性保证
- 6 处 app-layer 逻辑（`ClearHistory`、`ToolCallEventPublishingHook` default、`ChatSessionKeys` 4-arg、`MaxToolRounds` 30、`RoleConfigurationNormalizer`、默认 provider 切换）将 Sisyphus 的约定硬编码为 framework 默认

**数字**: 在 ~6,144 行 framework 新增中，**46% NEEDED、34% PREMATURE、16% NOT NEEDED、3% APP-LAYER**。建议削减 ~3,100 行 (PREMATURE + NOT NEEDED) 以获得一个精简、有正当理由的 framework diff。
