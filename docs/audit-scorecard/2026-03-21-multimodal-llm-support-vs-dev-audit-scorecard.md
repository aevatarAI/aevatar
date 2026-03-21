# 2026-03-21 `codex/feat/2026-03-16_multimodal-llm-support` vs `dev` 架构审计评分卡

## 1. 审计范围

- **分支**: `codex/feat/2026-03-16_multimodal-llm-support` (92ed94fd)
- **基线**: `dev`
- **变更规模**: 1077 files changed, 148309 insertions(+), 44393 deletions(-)
- **审计工具**: Claude Opus 4.6 独立审计
- **覆盖面**: C# 源码 (777 files)、Proto 文件、前端 TS/JS/HTML/CSS (114 files)、CI 门禁/文档/脚本 (101 files)、构建配置

---

## 2. 门禁执行结果

| 门禁 | 结果 | 备注 |
|------|------|------|
| `architecture_guards.sh` | **FAIL** | 在 `playground_asset_drift_guard` 步骤失败：CLI playground 与 Demo Web 静态资源不同步（app.js/app.css diff 不为空）。前置子门禁全部通过。 |
| `query_projection_priming_guard.sh` | PASS | |
| `projection_state_version_guard.sh` | PASS | |
| `projection_state_mirror_current_state_guard.sh` | PASS | |
| `projection_route_mapping_guard.sh` | PASS | |
| `workflow_binding_boundary_guard.sh` | PASS | |
| `test_stability_guards.sh` | PASS | |
| `dotnet build aevatar.slnx` | **PASS** | 0 Error, 19 Warning |
| `dotnet restore aevatar.slnx` | PASS | |

> **说明**: `architecture_guards.sh` 整体 exit code 1，原因是 playground asset drift guard 失败（前端静态资源未同步）。其他所有架构子门禁均通过。

---

## 3. 总评

| 总分 | 等级 | 结论 |
|------|------|------|
| **80 / 100** | **B+** | 方向正确的大规模重构：StateMirror 整体删除、Projection 全面统一到 Actor-based Scope 模型（净减 5,211 行）、RunManager ConcurrentDictionary 移除、lock-free 改造、Protobuf 强类型化、架构约束测试体系建立。多模态 LLM 支持以强类型 Proto 子消息实现，符合核心语义强类型原则。主要扣分项：playground 资源漂移导致 CI 主门禁失败、`ChatRequestEvent.metadata` 未完成 rename、前端巨型组件（5,493 行单文件）、前端无 CI pipeline、auth token 存 localStorage。 |

---

## 4. 六维评分

| 维度 | 权重 | 得分 | 说明 |
|------|-----:|-----:|------|
| 分层与依赖反转 | 20 | 18 | 新增 `ContentPartProtoMapper` 在 Abstractions 层；Kafka 传输从 MassTransit 迁移到 Orleans-native 后端，边界清晰。扣分：`ChatRequestEvent.metadata` 字段 3 commit 消息声称已 rename 为 `headers` 但实际未落地，语义不清。 |
| CQRS 与统一投影链路 | 20 | 19 | StateMirror 整体删除消除双轨实现。旧并行系统（`ProjectionCoordinator`/`ProjectionDispatcher`/`ProjectionLifecycleService`/`ProjectionSubscriptionRegistry`）全部删除，统一到 `ProjectionScopeGAgentBase<TContext>` 层次结构。`DurableMaterialization` 和 `SessionObservation` 两种运行时模式共用同一基类、状态机和故障追踪。查询端口正确拆分为 `IWorkflowExecutionCurrentStateQueryPort` 和 `IWorkflowExecutionArtifactQueryPort`。扣分：`EventSinkProjectionLifecyclePortBase` 中 `RuntimeHelpers.GetHashCode(sink)` 作为 ConcurrentDictionary key 存在哈希碰撞导致订阅泄漏的脆弱性。 |
| Projection 编排与状态约束 | 20 | 18 | Actor 化编排落地：`ProjectionScopeGAgentBase` 继承 `GAgentBase<ProjectionScopeState>`（event-sourced actor），水位推进使用权威 actor 的 `stateEvent.Version`（非本地计数器）。`RunManager` ConcurrentDictionary 删除。lock-free 改造完成。Materializer 分类正确（`ICurrentStateProjectionMaterializer` vs `IProjectionArtifactMaterializer`）。扣分：`EventSinkProjectionLifecyclePortBase` 的 `ConcurrentDictionary<int, IAsyncDisposable>` 是进程内订阅管理（非业务事实），但文档未明确标注其 transient 语义。`DefaultDetachedCommandDispatchService._drainComplete` TCS 字段重赋值存在竞态窗口（有 timeout fallback 缓解）。 |
| 读写分离与会话语义 | 15 | 13 | `EnsureProjectedRuntimeAsync` query-time priming 被正确移除（guard 通过）。所有 `IEventStore` 直读从投影编排中删除。`DispatchAsync` 返回 `CommandDispatchResult.Success(receipt)` 诚实 ACK。多模态 input/output parts 作为事件 payload 而非查询结果传递。扣分：前端 console web 中部分 API 调用在 save 后立即 read detail，假设 readmodel 与 command 同步可见。 |
| 命名语义与冗余清理 | 10 | 8 | 大量正向清理：`InMemoryConnectorRegistry` → `ConfiguredConnectorRegistry`、`InMemoryServiceRevisionArtifactStore` → `ConfiguredServiceRevisionArtifactStore`、`WorkflowDefinitionRegistry` → `WorkflowDefinitionCatalog`。扣分：commit 消息 "Rename ChatRequestEvent.metadata to headers" 未在 proto 落地；`ChatContentPartKind` enum 值前缀 `CHAT_CONTENT_PART_KIND_` 冗长（proto 风格惯例但可能更简洁）。 |
| 可验证性（门禁/构建/测试） | 15 | 4 | 构建通过（0 error）。65 个新测试文件，含完整 Architecture Tests 体系（`ActorModelConstraintTests`、`CqrsBoundaryTests`、`LayerDependencyTests`、`SerializationConstraintTests` 等）。`ReducerCoverageTests` 确保 Reducer 全覆盖。扣分：`architecture_guards.sh` 主门禁因 playground asset drift 失败（-3）；前端 66K 行代码无 CI pipeline（-3）；前端巨型组件降低可测试性（-2）；Biome 关键 lint 规则关闭（-1）；19 个编译警告未处理（-1）；文档命名规范违规（-1）。 |

**总分**: 18 + 19 + 18 + 13 + 8 + 4 = **80 / 100**

---

## 5. 分模块评分

| 模块 | 分数 | 结论 |
|------|-----:|------|
| Foundation + Runtime | 88 | Kafka 从 MassTransit 迁移到 Orleans-native，lock-free LLM provider factory 改造完成，`ConfiguredConnectorRegistry` 重命名正确。`InMemory` guard 添加。 |
| CQRS + Projection | 90 | 全面统一到 Actor-based Scope 模型。25 文件删除、22 文件新增（净减 5,211 行）。`ProjectionScopeState` 定义在 `projection_scope_messages.proto`。Materializer 分类（current-state vs artifact）语义正确。水位推进使用权威版本。无 `TypeUrl.Contains`/`GetAwaiter().GetResult()` 违规。 |
| Workflow | 82 | `WorkflowDefinitionCatalog` 重命名、scope_id 强类型化、`MakerRecursiveModule` 状态移入执行上下文。`SubWorkflowOrchestrator` 无 lock 违规。 |
| AI | 90 | 多模态支持以 `ChatContentPart` proto 强类型实现，`ContentPartProtoMapper` 双向映射清晰。`MediaContentEvent` 事件类型新增。MCP tool lock 改为 `Lazy<Task>`。 |
| Host + API | 78 | Neo4j 密码改为环境变量注入。但 playground asset drift 门禁失败；19 个编译警告。 |
| Platform (GAgentService) | 80 | 新增 `ServiceConfigurationProjector`、`ServiceRevisionCatalogProjector` 等投影器，proto readmodel 定义完整。scope 解析逻辑需确认安全边界。 |
| Frontend (console-web) | 60 | 114 个新文件引入完整 Ant Design Pro 应用。PKCE auth 流程正确，无 XSS 向量，有 runtime decoder 验证层。但巨型组件（5,493 行单文件）、auth token 存 localStorage、`noExplicitAny: off`、无 CI pipeline。 |
| Docs + Guards | 85 | 新增 `LOCAL_DEV_SETUP.md`、workflow_call 实践指南、Kafka 设计文档。架构测试体系（`Aevatar.Architecture.Tests`）是重大加分项。CodeMetrics 配置/脚本正确删除。 |

---

## 6. 关键加分项

### A1. StateMirror 整体删除

| | |
|---|---|
| **证据** | `src/Aevatar.CQRS.Projection.StateMirror/` 目录 6 个文件全部删除 |
| **影响** | 消除 CQRS 双轨投影实现，统一到 Projection Scope Actor 模型 |

### A2. Projection 全面 Actor 化统一

| | |
|---|---|
| **证据** | `src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionScopeGAgentBase.cs`（继承 `GAgentBase<ProjectionScopeState>`）、`ProjectionMaterializationScopeGAgentBase.cs`、`ProjectionSessionScopeGAgentBase.cs`；旧系统 `ProjectionCoordinator`/`ProjectionDispatcher`/`ProjectionLifecycleService`/`ProjectionSubscriptionRegistry` 全部删除（25 文件删除、22 文件新增、净减 5,211 行） |
| **影响** | 投影编排从服务层 Dictionary+注册表模式统一到 event-sourced Actor 持久态，消除中间层事实态字典，水位推进使用权威 actor committed version |

### A3. Architecture Tests 体系建立

| | |
|---|---|
| **证据** | `test/Aevatar.Architecture.Tests/Rules/` 下 10 个测试文件覆盖分层依赖、CQRS 边界、Actor 模型约束、序列化约束、Reducer 覆盖等 |
| **影响** | 将 CLAUDE.md 架构约束编码为可自动执行的测试，符合"治理前置"和"变更必须可验证"原则 |

### A3b. RunManager ConcurrentDictionary 删除

| | |
|---|---|
| **证据** | `src/Aevatar.Foundation.Abstractions/Context/IRunManager.cs` 和 `src/Aevatar.Foundation.Core/Context/RunManager.cs` 删除 |
| **影响** | 消除中间层 ID → context 事实态字典，符合中间层状态约束 |

### A4. Lock-free 改造

| | |
|---|---|
| **证据** | `ReloadableLLMProviderFactory`: `lock` → `ImmutableDictionary` + `Interlocked.CompareExchange`；`CachedScriptBehaviorArtifactResolver`: `lock+Dictionary` → `ConcurrentDictionary+Lazy`；`ToolCallModule`: `SemaphoreSlim` → `Lazy<Task>` |
| **影响** | 减少 Actor 边界外的锁竞争，向无锁优先原则靠拢 |

### A5. 中间层状态约束 100% 达标

| | |
|---|---|
| **证据** | `RunManager` ConcurrentDictionary 删除；`MakerRecursiveModule` 状态移入执行上下文；`ScriptReadModelMaterializationCompiler` lock+Dictionary 缓存删除；materialization plan cache 移入 actor-scoped transient 字段。所有新增 lock 均在 infrastructure 层（非 Actor 内部）。 |
| **影响** | 中间层事实态字典全部消除，符合 CLAUDE.md 中间层状态约束的全部条款 |

### A6. 多模态 LLM Protobuf 强类型建模

| | |
|---|---|
| **证据** | `ai_messages.proto`: `ChatContentPartKind` enum + `ChatContentPart` sub-message；`input_parts`/`output_parts` 作为 `repeated ChatContentPart` 类型字段 |
| **影响** | 核心语义强类型，未塞入通用 bag；符合"核心语义强类型"和"新增状态/事件先定义 .proto"原则 |

---

## 7. 主要扣分项

### M1. Playground Asset Drift 门禁失败 (Medium, -3)

| | |
|---|---|
| **文件** | CLI playground 与 Demo Web 的 `app.js`/`app.css` 静态资源 |
| **描述** | `playground_asset_drift_guard.sh` 检测到 CLI playground 和 Demo Web 静态资源内容不同步，导致 `architecture_guards.sh` 整体失败（exit code 1）。 |
| **修复** | 重新同步前端静态资源，或更新 guard 基线。 |

### M2. ChatRequestEvent.metadata 未完成 Rename (Medium, -2)

| | |
|---|---|
| **文件** | `src/Aevatar.AI.Abstractions/ai_messages.proto:25` |
| **描述** | Commit `0e4996e4` 消息声称 "Rename ChatRequestEvent.metadata to headers and promote command_id to typed field"，但 proto 中仍为 `map<string, string> metadata = 3;`。可能在后续 merge 中被 revert，但结果是 commit 历史与代码不一致。 |
| **修复** | 确认是否需要完成 rename；如已放弃，更新 commit 记录或添加说明。 |

### M3. 前端代码无自动化测试 (Medium, -2)

| | |
|---|---|
| **文件** | `apps/aevatar-console-web/` 114 个新文件 |
| **描述** | 完整的 Ant Design Pro 前端应用，包含 workflow editor、scripts studio、execution viewer 等功能组件。已有 29 个测试文件覆盖 auth/API/页面逻辑，但无 CI pipeline 执行。 |
| **修复** | 添加前端 CI pipeline 并扩展测试覆盖。 |

### M4. EventSinkProjectionLifecyclePortBase 订阅管理 (Medium, -1)

| | |
|---|---|
| **文件** | `src/Aevatar.CQRS.Projection.Core/Orchestration/EventSinkProjectionLifecyclePortBase.cs:21` |
| **描述** | `ConcurrentDictionary<int, IAsyncDisposable>` 使用 `RuntimeHelpers.GetHashCode(sink)` 作为 key，映射 sink → 订阅 disposable。非业务事实（进程内 I/O 句柄），但哈希碰撞会导致订阅泄漏。且未文档标注 transient 语义。 |
| **修复** | 使用 `object` key 或 `ConditionalWeakTable` 避免碰撞；添加注释说明进程内瞬态语义。 |

### M4b. DefaultDetachedCommandDispatchService drain TCS 竞态 (Minor, -1)

| | |
|---|---|
| **文件** | `src/Aevatar.CQRS.Core/Commands/DefaultDetachedCommandDispatchService.cs` |
| **描述** | `_drainComplete` TCS 字段在 `StartDetachedDrain` 中被重赋值（非原子操作），若 `DisposeAsync` 在赋值前读取旧 TCS，可能 await 已完成的 TCS 而新 drain 启动。有 timeout fallback 缓解。 |
| **修复** | 使用 `Interlocked.Exchange` 确保 TCS 替换的原子性。 |

### M5. 前端巨型组件文件 (Medium, -1)

| | |
|---|---|
| **文件** | `apps/aevatar-console-web/src/pages/studio/StudioWorkbenchSections.tsx` (5,493 行)、`studio/index.tsx` (3,766 行)、`settings/index.tsx` (3,636 行) |
| **描述** | 多个前端组件文件超过 3,000 行，代码维护性和可测试性差。 |
| **修复** | 拆分为职责单一的子组件。 |

### M6. 前端 Biome Linter 关键规则关闭 (Medium, -1)

| | |
|---|---|
| **文件** | `apps/aevatar-console-web/biome.json` |
| **描述** | `noExplicitAny: "off"` 和 `useExhaustiveDependencies: "off"` 关闭了 TypeScript 类型安全和 React Hook 依赖检查的关键规则。9 处 `as any` 在生产代码中。 |
| **修复** | 逐步启用规则，修复现有违规。 |

### M7. 前端 Auth Token 存储在 localStorage (Medium, -1)

| | |
|---|---|
| **文件** | `apps/aevatar-console-web/src/shared/auth/session.ts` |
| **描述** | Access/refresh/ID tokens 存储在 `localStorage`（key: `aevatar-console:nyxid:session`），易受 XSS 窃取。当前代码无 XSS 向量（无 `dangerouslySetInnerHTML`/`eval`），但 `httpOnly` cookie 更安全。 |
| **修复** | 评估迁移到 `httpOnly` cookie 或 BFF 模式。 |

### M8. 无前端 CI Pipeline (Medium, -1)

| | |
|---|---|
| **文件** | `.github/workflows/ci.yml` |
| **描述** | `apps/aevatar-console-web/` 66,755 行 TypeScript/React 代码无 CI 构建/lint/测试步骤。 |
| **修复** | 在 CI 中添加 `pnpm install && pnpm lint && pnpm build && pnpm test`。 |

### M9. 编译警告未处理 (Minor, -1)

| | |
|---|---|
| **证据** | `dotnet build aevatar.slnx` 产生 19 个 Warning，包括 `CA2024` (async stream EndOfStream) |
| **描述** | 19 个编译警告，主要集中在 CLI 工具和部分 API 项目。 |
| **修复** | 逐步修复或在 `.editorconfig` 中显式配置警告级别。 |

### M10. 文档命名规范违规 (Minor, -1)

| | |
|---|---|
| **文件** | `docs/AEVATAR_APP_IMPLEMENTATION_CN.md`、`docs/GAGENTSERVICE_WORKFLOW_INTEGRATION_CN.md` |
| **描述** | 新增文档未遵循 CLAUDE.md 要求的 `YYYY-MM-DD-` 日期前缀命名规范。 |
| **修复** | 重命名为 `2026-03-XX-aevatar-app-implementation-cn.md` 格式。 |

### M11. 架构测试 Skip 未跟踪 (Minor, -0)

| | |
|---|---|
| **文件** | `test/Aevatar.Architecture.Tests/Rules/ActorModelConstraintTests.cs` |
| **描述** | `LeaseClasses_ShouldNot_Declare_Lock_Fields` 测试标记为 `[Fact(Skip = "Known violation - _liveSinkGate in EventSinkProjectionRuntimeLeaseBase")]`。已知违规应有修复跟踪。 |
| **修复** | 创建 issue 跟踪 `EventSinkProjectionRuntimeLeaseBase._liveSinkGate` lock 消除。 |

### M12. 多模态端到端集成测试缺失 (Minor, -0)

| | |
|---|---|
| **描述** | Proto round-trip 测试（`AIAbstractionsProtoCoverageTests`）覆盖了序列化，但缺少完整的多模态 chat 流程集成测试（`ContentPart.ImagePart` → `ChatRuntime` → `MEAILLMProvider` → 响应 `ContentParts`）。 |
| **修复** | 后续迭代补充端到端多模态集成测试。 |

---

## 8. 非扣分观察项（基线口径）

1. **InMemory 实现保留**：`ConfiguredConnectorRegistry`（已从 `InMemoryConnectorRegistry` 重命名）、`ConfiguredServiceRevisionArtifactStore` 等仍为内存实现，但命名已明确定位且接口边界稳定。不扣分。
2. **Actor 仅 Local 实现**：分布式 Actor 尚未完全落地，但 Kafka 传输层已迁移到 Orleans-native，分布式路径正在推进。不扣分。
3. **ProjectReference 保留**：模块间仍使用 `ProjectReference`，但分片构建门禁可通过。不扣分。
4. **JSON 序列化在 Elasticsearch 边界层**：`ElasticsearchProjectionDocumentStorePayloadSupport` 中 `JsonSerializer.Serialize` 用于 ES 文档写入，属于 Infrastructure 边界适配。不扣分。

---

## 9. 改进优先级建议

### P1（合并前建议修复）

1. 同步 playground 静态资源使 `architecture_guards.sh` 通过。
2. 确认 `ChatRequestEvent.metadata` rename 状态，确保 proto 与 commit 历史一致。

### P2（后续迭代）

1. 添加前端 CI pipeline（`pnpm install && pnpm lint && pnpm build && pnpm test`）。
2. 拆分前端巨型组件（`StudioWorkbenchSections.tsx` 5,493 行 → 多个子组件）。
3. 启用 Biome `noExplicitAny` 和 `useExhaustiveDependencies` 规则，修复现有违规。
4. 评估 auth token 从 localStorage 迁移到 `httpOnly` cookie 或 BFF 模式。
5. 评估 `EventSinkProjectionSessionSubscriptionManager._sinkSubscriptions` 迁移到 Actor/lease 模型。
6. 处理 19 个编译警告。
7. 修复文档命名规范违规（`AEVATAR_APP_IMPLEMENTATION_CN.md` 等添加日期前缀）。
8. `ChatContentPartKind` enum 值考虑缩短前缀（如 `TEXT` 代替 `CHAT_CONTENT_PART_KIND_TEXT`），遵循 proto3 enum 命名最佳实践。
9. 跟踪并解决 `EventSinkProjectionRuntimeLeaseBase._liveSinkGate` lock 违规（架构测试已 Skip）。
10. 补充多模态端到端集成测试（`ContentPart.ImagePart` → `ChatRuntime` → LLM Provider → 响应）。
11. `ConfiguredConnectorRegistry.Register()` 使用 `ImmutableInterlocked.Update` 替代非原子赋值，与项目其他模式一致。
12. `ToolCallModule.GetOrDiscoverAsync` CAS 循环添加有界重试，防止持续失败时无限重试。

---

## 10. 变更亮点摘要

| 类别 | 关键变更 |
|------|---------|
| 多模态 LLM | `ChatContentPart` proto 强类型 + `ContentPartProtoMapper` 双向映射 + `MediaContentEvent` 流式事件 |
| 投影重构 | StateMirror 删除 → Projection Scope Actor 模型 + 统一激活/释放契约 |
| 并发安全 | RunManager 删除 + lock-free factory 改造 + `Lazy<Task>` 替代 SemaphoreSlim |
| 架构治理 | `Aevatar.Architecture.Tests` 10 个约束测试 + InMemory guard + Reducer 覆盖测试 |
| 命名治理 | `InMemory*` → `Configured*`、`Registry` → `Catalog`、语义化重命名 |
| Kafka 传输 | MassTransit → Orleans-native Kafka provider + 共享消费组支持 |
| 前端 | 完整 console web 应用 (Ant Design Pro) + workflow editor + scripts studio |
