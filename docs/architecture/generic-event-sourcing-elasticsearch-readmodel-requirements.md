# Generic Event Sourcing + Provider-Based ReadModel 需求文档（基线对齐版）

## 1. 文档元信息
- 状态：In Progress（Revised）
- 目标版本：v1（对齐当前仓库基线后可实施）
- 适用仓库：`aevatar`
- 首稿日期：2026-02-22
- 本次修订：2026-02-23
- 修订目的：将需求与当前代码/门禁/测试基线对齐，消除实现冲突

## 2. 当前仓库基线（截至 2026-02-23）
当前已具备能力：
- Event Sourcing 抽象：`IEventStore`、`IEventSourcingBehavior<TState>`、`EventSourcingBehavior<TState>`。
- 状态快照抽象：`IStateStore<TState>`，默认 InMemory 实现。
- 统一 Projection Pipeline：`ProjectionLifecycleService -> ProjectionSubscriptionRegistry -> ProjectionDispatcher -> ProjectionCoordinator`。
- 统一读模型存储契约：`IProjectionReadModelStore<TReadModel, TKey>`（`Upsert/Mutate/Get/List`）。
- Provider 能力模型与校验：`ProjectionReadModelProviderCapabilities`、`ProjectionReadModelRequirements`、`ProjectionReadModelCapabilityValidator`。
- Provider 选择与注册抽象：`IProjectionReadModelStoreRegistration<,>`、`ProjectionReadModelStoreSelector`。
- 通用 Provider 项目已落地：`Aevatar.CQRS.Projection.Providers.InMemory`、`Aevatar.CQRS.Projection.Providers.Elasticsearch`。
- Workflow 读侧完整链路：`WorkflowExecutionProjectionService` + Activation/Release/Lease/QueryReader/SinkForwarder 组件化编排。
- Workflow 读侧已切换 Provider 选择链路；Provider 注册在 Infrastructure 完成（InMemory + Elasticsearch）。
- CQRS 与 AGUI 共用同一输入事件流（不同 projector 分支输出）。
- 通用命令执行壳与 Capability Host 装配机制已存在。
- 架构门禁与稳定性门禁已生效（`architecture_guards`、`projection_route_mapping_guard`、`test_stability_guards`）。
- 分布式 3 节点一致性集成测试与 smoke 脚本已接入 CI。

当前未具备能力：
- Graph Provider（Neo4j-like）适配器落地。
- “State -> Default ReadModel” 的通用镜像层。
- 自动生成 Persisted State Event 的统一框架管道（当前为显式 `RaiseEvent/ConfirmEventsAsync`）。
- `Neo4j.Driver` 仅在集中版本文件声明，业务项目尚未引用并落地实现。

### 2.1 基线证据（关键代码位置）
| 主题 | 现状结论 | 证据 |
|---|---|---|
| Projection 关闭语义 | Workflow 命令入口 fail-fast，返回 `ProjectionDisabled` | `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunContextFactory.cs`；`test/Aevatar.Workflow.Application.Tests/WorkflowRunOrchestrationComponentTests.cs` |
| Query 关闭语义 | 关闭时应用层返回 `null/[]`，API 侧表现为 `404/200` | `src/workflow/Aevatar.Workflow.Application/Queries/WorkflowExecutionQueryApplicationService.cs`；`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatQueryEndpoints.cs` |
| ReadModel Store 契约 | 仅 `Upsert/Mutate/Get/List` 四类操作 | `src/Aevatar.CQRS.Projection.Abstractions/Abstractions/IProjectionReadModelStore.cs` |
| Provider 选择与装配 | Workflow 通过 Provider 注册 + Selector 选择 ReadModel Store | `src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs`；`src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/WorkflowCapabilityServiceCollectionExtensions.cs` |
| Persisted Event 模型 | `StateEvent` 无 `metadata` 字段 | `src/Aevatar.Foundation.Abstractions/agent_messages.proto` |
| ES 写入模式 | 仍是显式 `RaiseEvent/ConfirmEventsAsync` | `src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`；`docs/EVENT_SOURCING.md` |
| 运行时注入 | Runtime 注入 `StateStore`，未统一注入 ES 行为 | `src/Aevatar.Foundation.Runtime/Actor/LocalActorRuntime.cs` |
| Capability Host | 已有 capability 注册/映射防重复机制 | `src/Aevatar.Hosting/AevatarCapabilityHostExtensions.cs` |
| 分布式一致性基线 | 已有 3 节点集成测试 + smoke 脚本 | `test/Aevatar.Foundation.Runtime.Hosting.Tests/DistributedClusterConsistencyIntegrationTests.cs`；`tools/ci/distributed_3node_smoke.sh` |
| 架构约束自动化 | 路由精确匹配、禁止中间层 ID 映射事实态、lease/session 约束 | `tools/ci/architecture_guards.sh`；`tools/ci/projection_route_mapping_guard.sh` |

## 3. 问题定义
在当前基线上，仍有以下缺口：
- Provider 能力模型与选择链路已落地，但跨业务域统一治理（Registry/策略化选择/观测）仍不完整。
- Document Provider 已可用，但生产级增强（异常分级、索引策略细化、观测字段收口）仍需补齐。
- EventEnvelope 与 Persisted State Event 的边界在文档层仍需统一口径，避免误用。
- 目标中包含的 `StateOnly`、Graph Index、自动 Persisted Event 与当前运行语义存在冲突，需要分期。

## 4. 目标与分期

### 4.1 总体目标
1. 保持单一主链路：`Command -> Event -> Projection -> ReadModel`。
2. 在不破坏现有 Workflow 语义的前提下，引入 Provider 能力抽象与启动期校验。
3. v1 先交付 Document Index Provider（Elasticsearch-like）落地能力。
4. Graph Index（Neo4j-like）进入 vNext，先沉淀抽象，不在 v1 强制交付完整能力。
5. Event Sourcing 继续保持兼容，自动化增强采用显式分期与开关。

### 4.2 分期策略
- P0（当前文档阶段）：基线对齐、冲突消解、DoD 可执行化。
- P1（v1）：Provider 能力模型 + 启动期校验 + Document Index Provider 适配。
- P2（vNext）：Graph Index Provider 与通用 StateMirror/StateOnly 能力扩展。

### 4.3 v1 落地边界（结合现有代码）
- v1 以 Workflow 读侧为唯一落地点：`WorkflowExecutionReport` 先完成 Provider 化。
- 不在 v1 引入新的“全局第二投影框架”，而是复用现有 `AddWorkflowExecutionProjectionCQRS` 与 Provider 注册/选择链路。
- v1 不改写命令执行主链路，不变更 `WorkflowRunContextFactory` 的 Projection fail-fast 语义。
- v1 不改 Query API 合同，只保证替换存储后语义一致。

## 5. 非目标（v1）
- 不引入第二套 CQRS/Projection 主链路。
- 不破坏现有 Workflow 命令入口“投影关闭即 fail-fast”行为。
- 不在 v1 完成 Graph 查询 DSL、路径优化执行器。
- 不在 v1 引入跨存储分布式事务。
- 不在 v1 改造所有 Event Sourcing 调用为全自动模式。

## 6. 架构与治理约束（强制）
- 严格分层：`Domain / Application / Infrastructure / Host`。
- Host 仅做协议适配与能力装配，不承载业务编排。
- CQRS 与 AGUI 必须共用统一 Projection Pipeline，禁止双轨。
- 中间层禁止进程内 actor/run/session 事实态映射。
- 投影生命周期必须基于 lease/session 显式句柄，不允许 `actorId -> context` 反查。
- Event type 路由必须基于 `TypeUrl` 派生 + 精确键匹配（`TryGetValue`），禁止字符串模糊匹配。
- 能力不匹配在启动期 fail-fast（默认策略），不得运行期隐式降级。
- 所有变更必须通过既有门禁与测试。

## 7. 关键决策（本版定稿）

### D1 `StateOnly` 语义（Workflow 现状）
- 在当前 Workflow 能力中，`ProjectionDisabled` 会阻断命令执行（现有行为保持）。
- 因此 v1 不将 Workflow 场景纳入 `StateOnly` 支持范围；`StateOnly` 进入通用内核 vNext 议题。

### D2 Query API 合同兼容
- v1 不改现有查询端点对外合同（现有 `null/[]/404/200` 语义维持）。
- 若后续引入统一“能力不可用”错误模型，需单独版本化并提供迁移说明。

### D3 Event Sourcing 保持兼容
- v1 继续支持显式 `RaiseEvent/ConfirmEventsAsync` 路径。
- 自动 Persisted Event 仅作为扩展能力进入 P2，不作为 v1 合并前置条件。

### D4 Persisted Event 权威模型
- `EventEnvelope` 仅用于运行时传播/投影输入。
- EventStore 权威回放源是 `StateEvent`（Persisted State Event）。

### D5 Graph 能力分期
- v1：仅保留 Graph 抽象扩展位，不要求完整 Neo4j 读写/查询能力。
- vNext：Graph Provider 以单独 RFC + 里程碑方式落地。

### D6 v1 实施落点
- v1 的 Provider 化仅要求在 Workflow Projection 模块闭环，不强制外溢到所有业务域。
- 先确保“现有测试与门禁全绿 + 行为不回归”，再考虑通用化抽象下沉。

## 8. 范围（Scope）

### 8.1 In Scope（v1）
- 定义 ReadModel Provider 能力模型与协商机制。
- 启动期执行 “ReadModel 要求 vs Provider 能力” 校验。
- 保持 `IProjectionReadModelStore<TReadModel, TKey>` 兼容，不破坏现有 Workflow 读侧。
- 提供 Document Index Provider（Elasticsearch-like）适配器。
- Workflow `WorkflowExecutionReport` 可切换到 Document Provider 承接。
- 通过 `IProjectionReadModelStoreRegistration<WorkflowExecutionReport, string>` 注册 Provider，不新增平行投影链路。
- 配置模型与 DI 扩展补齐（保留与现有 `WorkflowExecutionProjection:*` 兼容）。
- 补齐单元/集成/门禁验证。

### 8.2 Out of Scope（v1）
- Graph Provider 的完整查询与关系建模能力。
- 通用 `StateMirrorProjection` 自动覆盖所有 `GAgentBase<TState>` 类型。
- 全量业务模块一次性迁移到新 Provider。
- 生产 ILM/冷热分层自动化。
- 新建独立“全局 ReadModel Infrastructure 大一统项目”并强制迁移全仓库。

## 9. 功能需求（FR）

### FR-1 Event Sourcing 抽象保持兼容
- 保持 `IEventStore` 追加、版本并发校验、按版本回放语义。
- 保持 `IStateStore<TState>` 快照通道语义不变。

### FR-2 Persisted State Event 与 Envelope 边界
- Persisted 回放源仅为 `StateEvent`。
- `EventEnvelope` 中运行时路由/传播字段不得进入回放权威语义。

### FR-3 StateEvent 结构约束（v1）
- v1 继续使用现有 `StateEvent` 字段模型：
  `event_id/timestamp/version/event_type/event_data/agent_id`。
- 若需 `metadata` 字段，作为 vNext 的 proto 升级任务，不阻塞 v1。

### FR-4 ReadModel Store 兼容约束
- `IProjectionReadModelStore<TReadModel, TKey>` 契约保持兼容：
  - `UpsertAsync`
  - `MutateAsync`
  - `GetAsync`
  - `ListAsync`
- 既有 Workflow 读侧代码无需修改业务语义即可接入新 Provider。
- v1 不在该接口上新增 Graph 专有方法，避免破坏现有实现与测试基线。

### FR-5 Provider 能力声明模型
- 新增能力抽象（至少包含）：
  - `SupportsIndexing`
  - `IndexKinds`（支持集合，至少可表达 `Document`）
  - `SupportsAliases`
  - `SupportsSchemaValidation`
- 能力模型可由 Store 实现直接声明，或由独立能力描述器声明。
- 无论采用哪种声明方式，都不得破坏现有 Store 接口二进制兼容性。

### FR-6 启动期能力校验
- ReadModel 注册/装配阶段执行能力匹配。
- 默认策略：不匹配 fail-fast。
- 必须输出结构化错误，包含 readModel/provider/requiredCapabilities。
- 能力校验应接入现有 Workflow Projection DI 装配流程，而不是额外旁路启动器。

### FR-7 Workflow Provider 可替换承接
- `WorkflowExecutionReport` 的存储后端支持通过 Provider 注册在 DI 中替换。
- 切换 Provider 后，Query 结果语义与字段口径保持一致。

### FR-8 Document Index Provider（v1 必做）
- 实现 Elasticsearch-like Provider：
  - 支持 `Upsert/Mutate/Get/List` 等价语义。
  - 支持索引命名环境隔离（如 prefix）。
  - 支持可配置索引初始化策略（create if missing）。

### FR-9 `StateOnly` 约束说明（v1）
- Workflow 能力 v1 保持现有行为：Projection 关闭时返回 `ProjectionDisabled`。
- `StateOnly/DefaultReadModel/CustomReadModel` 通用模式进入 vNext RFC，不在 v1 作为可验收项。

### FR-10 Event Sourcing 自动化分期
- v1 不强制实现“统一管道自动生成 Persisted Event”。
- 如实现实验能力，必须开关控制且默认关闭，不改变现有显式路径行为。

### FR-11 可观测性
- Provider 写入路径至少记录：
  `provider/readModelType/key(state id)/elapsedMs/result/errorType`。
- 启动期能力校验失败日志必须可定位到具体 ReadModel 与缺失能力。

### FR-12 分布式一致化验证
- 保持并复用现有 3 节点一致性验证链路与脚本接入。
- 新增 Provider 后，不得破坏现有 distributed smoke 稳定性。

### FR-13 复用现有通用壳能力
- Command 侧复用现有 `ICommandExecutionService<...>` 抽象，不新增平行命令执行框架。
- Host 侧复用既有 capability 注册机制，不新增第二套 capability 映射容器。

## 10. 非功能需求（NFR）

### NFR-1 一致性
- EventStore 单流版本必须单调递增。
- ReadModel 允许最终一致，但需在测试中可稳定判定收敛/失败。

### NFR-2 性能
- `ListAsync` 必须强制硬上限（Provider 侧可配置上限）。
- Provider 写入路径延迟应可观测（至少日志维度可统计）。

### NFR-3 可运维性
- Provider 配置支持 `appsettings` + 环境变量覆盖。
- 能力不匹配在启动期直接失败，不允许运行中静默降级。

### NFR-4 安全
- 日志不得输出明文凭据。
- 凭据通过配置系统注入，支持环境变量替换。

### NFR-5 门禁兼容性
- 新增实现不得触发现有 `architecture_guards`、`projection_route_mapping_guard`、`test_stability_guards` 违规。
- 新增测试不得引入未授权轮询等待；必要例外必须进入 allowlist 并给出理由。

## 11. 配置需求

### 11.1 当前有效配置（已存在）
- `ActorRuntime:Provider`（`InMemory/MassTransit/Orleans`）
- `WorkflowExecutionProjection:Enabled`
- `WorkflowExecutionProjection:EnableActorQueryEndpoints`
- `WorkflowExecutionProjection:EnableRunReportArtifacts`
- `WorkflowExecutionProjection:RunProjectionCompletionWaitTimeoutMs`
- `WorkflowExecutionProjection:RunProjectionFinalizeGraceTimeoutMs`

### 11.2 v1 新增配置（建议）
- `Projection:ReadModel:Provider`（`InMemory/Elasticsearch`）
- `Projection:ReadModel:FailOnUnsupportedCapabilities`（默认 `true`）
- `Projection:ReadModel:Bindings:*`（可选，ReadModel -> Provider 显式绑定）
- `WorkflowExecutionProjection:ReadModelProvider` / `FailOnUnsupportedCapabilities` / `ReadModelBindings` 保留为模块内覆盖位（可选）

### 11.3 Document Provider 示例配置
- `Projection:ReadModel:Providers:Elasticsearch:Endpoints`
- `Projection:ReadModel:Providers:Elasticsearch:IndexPrefix`
- `Projection:ReadModel:Providers:Elasticsearch:RequestTimeoutMs`
- `Projection:ReadModel:Providers:Elasticsearch:ListTakeMax`
- `Projection:ReadModel:Providers:Elasticsearch:AutoCreateIndex`
- `Projection:ReadModel:Providers:Elasticsearch:Username`
- `Projection:ReadModel:Providers:Elasticsearch:Password`

### 11.4 预留（vNext）
- `Projection:ReadModel:Providers:Neo4j:*`
- `Projection:ReadModel:Mode`（`StateOnly/DefaultReadModel/CustomReadModel`）

## 12. 验收标准（DoD）

### 12.1 单元测试
- Provider 能力声明与匹配（成功/失败/冲突路径）。
- 启动期 fail-fast 错误语义。
- Document Provider 的 `Upsert/Mutate/Get/List` 契约一致性。
- Workflow 切换 Provider 后 Query 结果语义保持一致。
- `ProjectionDisabled` 行为保持兼容（Workflow v1）。
- 覆盖 Provider 注册与选择链路：`IProjectionReadModelStoreRegistration<,>` + `ProjectionReadModelStoreSelector`。

### 12.2 集成测试
- Docker Elasticsearch 下验证 Workflow ReadModel 端到端写读。
- Provider 切换前后，关键 Query API 返回语义一致。

### 12.3 分布式一致化测试
- 保持并通过现有 3 节点一致性脚本链路。
- 测试稳定性门禁通过，不新增未授权轮询等待。

### 12.4 合规门槛（必须全绿）
- `dotnet build aevatar.slnx --nologo`
- `dotnet test aevatar.slnx --nologo`
- `bash tools/ci/architecture_guards.sh`
- `bash tools/ci/projection_route_mapping_guard.sh`
- `bash tools/ci/test_stability_guards.sh`
- `bash tools/ci/solution_split_test_guards.sh`

## 13. 与原稿差异（本次修订）
| 维度 | 原稿 | 本版 |
|---|---|---|
| `StateOnly` | v1 必做 | 与当前 Workflow 冲突，改为 vNext |
| Graph Provider | v1 必做 | 改为 vNext（v1 仅保留扩展位） |
| Persisted Event 自动化 | v1 强要求 | 改为分期，v1 保持兼容 |
| 配置模型 | 以 `Projection:ReadModel:*` 为主 | 先兼容现有 `WorkflowExecutionProjection:*`，渐进演进 |
| DoD | 泛化描述 | 对齐现有 CI 门禁与可执行命令 |

## 14. 风险与缓解
- 风险：Provider 能力模型设计不当导致后续扩展困难。  
  缓解：v1 先覆盖 Document 必需能力，Graph 通过独立 RFC 引入。
- 风险：切换后端导致 Query 语义漂移。  
  缓解：契约测试 + 端到端对照测试。
- 风险：过早推进自动 Persisted Event 改动写侧行为。  
  缓解：v1 保持显式路径，自动化能力仅实验开关。

## 15. 待确认项（Open Questions）
- `ReadModelBindings` 的优先级规则（显式绑定 vs 默认路由）最终口径。
- Graph RFC 的启动条件（抽象稳定性、测试基线、运维要求）。
- 多业务域接入时，Provider 默认路由策略是否需要支持“按 ReadModel 类型自动选择”。

## 16. 需求-实现映射（v1）
| 需求主题 | 优先改动位置 | 复用现有扩展点 | 说明 |
|---|---|---|---|
| Provider 能力声明与校验 | `src/Aevatar.CQRS.Projection.Abstractions/Abstractions` + `src/workflow/Aevatar.Workflow.Projection/DependencyInjection` | `AddWorkflowExecutionProjectionCQRS` | 通过 Selector + CapabilityValidator 在装配期做匹配与 fail-fast |
| Document Provider Store | `src/Aevatar.CQRS.Projection.Providers.Elasticsearch` | `IProjectionReadModelStoreRegistration<,>` | 通用 ES Provider，不绑定 Workflow 业务域 |
| Query 语义回归保障 | `src/workflow/Aevatar.Workflow.Projection/Orchestration` | `IWorkflowExecutionProjectionPort` | 不改 `null/[]/404/200` 现有合同 |
| 配置绑定扩展 | `src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection` | `WorkflowExecutionProjection` + `Projection:ReadModel` 配置节 | 新增统一 Provider 配置并保持模块级覆盖位 |
| 测试与门禁收口 | `test/Aevatar.Workflow.Host.Api.Tests`、`test/Aevatar.Workflow.Application.Tests`、`tools/ci` | 现有 CI 脚本链路 | 先增量覆盖，再跑全量门禁 |
