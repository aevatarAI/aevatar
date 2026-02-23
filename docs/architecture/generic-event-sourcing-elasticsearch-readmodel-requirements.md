# Generic Event Sourcing + Provider ReadModel 需求与重构计划（必要文档）

## 1. 文档定位
- 状态：Active
- 日期：2026-02-23
- 目的：作为 Event Sourcing 与 Provider-Based ReadModel 的唯一执行文档（需求 + 计划一体化）。
- 范围：Foundation（ES）+ CQRS Projection（Provider Runtime）+ Workflow（接入层）。
- 兼容策略：以清晰正确为第一目标，不保留历史兼容壳。

## 2. 架构硬约束
1. 有状态 Actor 必须 Event Sourcing，`EventStore` 是事实源。
2. `Command -> Domain Event -> Apply -> State`，开发者显式构建 event。
3. ReadModel Provider 必须通用化，不绑定 Workflow 业务域。
4. CQRS 与 AGUI 必须共用同一 Projection Pipeline，不得双轨。
5. 中间层不得维护 `actor/run/session` 事实态进程内映射。
6. Runtime 不得通过反射注入 ES（`MakeGenericType` / `GetProperty().SetValue`）。

## 3. 当前代码基线（已验证）
### 3.1 Event Sourcing
- 契约：`src/Aevatar.Foundation.Core/EventSourcing/IEventSourcingBehavior.cs`
- 实现：`src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`
- 裁剪调度抽象：`src/Aevatar.Foundation.Core/EventSourcing/IEventStoreCompactionScheduler.cs`
- 运行时裁剪调度实现：`src/Aevatar.Foundation.Runtime/Persistence/DeferredEventStoreCompactionScheduler.cs`
- 状态转换扩展：`src/Aevatar.Foundation.Core/EventSourcing/IStateEventApplier.cs`
- Typed 状态转换基类：`src/Aevatar.Foundation.Core/EventSourcing/StateEventApplierBase.cs`
- 状态匹配器：`src/Aevatar.Foundation.Core/EventSourcing/StateTransitionMatcher.cs`
- 生命周期：`src/Aevatar.Foundation.Core/GAgentBase.TState.cs`
- Runtime 停用钩子抽象：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHook.cs`
- Runtime 停用钩子分发器：`src/Aevatar.Foundation.Runtime/Actor/IActorDeactivationHookDispatcher.cs`
- Runtime 停用钩子分发实现：`src/Aevatar.Foundation.Runtime/Actor/ActorDeactivationHookDispatcher.cs`
- Runtime 默认裁剪钩子：`src/Aevatar.Foundation.Runtime/Actor/EventStoreCompactionDeactivationHook.cs`
- 本地持久化 EventStore 基线：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`
- 运行时注入边界：
  - `src/Aevatar.Foundation.Runtime/Actor/LocalActorRuntime.cs`
  - `src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs`

当前语义：
1. `ActivateAsync` 强制 Replay 恢复状态。
2. `DeactivateAsync` 强制 `ConfirmEventsAsync + PersistSnapshotAsync`。
3. `GAgentBase<TState>` 不再暴露 `StateStore` 事实通道。
4. 未设置 `EventSourcing` 时，`GAgentBase<TState>` 在泛型上下文内用 `IEventStore` 静态构造 `AgentBackedEventSourcingBehavior`（继承 `EventSourcingBehavior<TState>`）。
5. 缺失 `IEventStore` 时 fail-fast。
6. `ConfirmDerivedEventsAsync` / `IDomainEventDeriver` / `EventSourcingAutoPersistenceOptions` 已从主链路移除。
7. 运行期通过 `PersistDomainEventAsync` / `PersistDomainEventsAsync` 执行“持久化 + 顺序 apply”；Replay 主要用于激活恢复。
8. `TransitionState` 可由 Agent override 或 `IStateEventApplier<TState>` 组合实现。
9. 默认启用自动快照与事件流裁剪：
   - 快照：`EventSourcingRuntimeOptions.SnapshotInterval`
   - 裁剪：快照成功后通过 `IEventStoreCompactionScheduler.ScheduleAsync(...)` 记录待清理版本，在空闲期由 runtime `IActorDeactivationHookDispatcher` 分发 deactivation hooks，默认裁剪钩子触发 `RunOnIdleAsync(...)` 异步调用 `IEventStore.DeleteEventsUpToAsync(...)`
   - 保留窗口：`RetainedEventsAfterSnapshot`

### 3.2 Provider Runtime
- 抽象：`src/Aevatar.CQRS.Projection.Abstractions`
- 运行时：`src/Aevatar.CQRS.Projection.Runtime`
- Provider：
  - InMemory：`src/Aevatar.CQRS.Projection.Providers.InMemory`
  - Elasticsearch：`src/Aevatar.CQRS.Projection.Providers.Elasticsearch`
  - Neo4j：`src/Aevatar.CQRS.Projection.Providers.Neo4j`
- StateMirror：`src/Aevatar.CQRS.Projection.StateMirror`

当前语义：
1. Provider 通过 `IProjectionReadModelStoreRegistration<TReadModel, TKey>` 注册。
2. Store 由 `ProviderRegistry + ProviderSelector + BindingResolver + StoreFactory` 统一创建。
3. 多 Provider 并存时必须显式指定 provider；否则选择失败。
4. 能力不匹配默认 fail-fast（`FailOnUnsupportedCapabilities=true`）。
5. InMemory / Elasticsearch / Neo4j 写路径均输出统一结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。
6. Provider 端到端回归支持环境变量门控集成测试与一键 smoke 脚本：
   - `test/Aevatar.CQRS.Projection.Core.Tests/ProjectionProviderE2EIntegrationTests.cs`
   - `tools/ci/projection_provider_e2e_smoke.sh`

### 3.3 Workflow 接入
- 组合入口：`src/workflow/Aevatar.Workflow.Infrastructure/DependencyInjection/WorkflowCapabilityServiceCollectionExtensions.cs`
- 投影 DI：`src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs`

当前语义：
1. Workflow 已接入 InMemory + Elasticsearch + Neo4j 三类 Provider 注册。
2. `Projection:ReadModel:*` 全局选项会映射到 Workflow 投影选项。
3. `ReadModelMode=StateOnly` 在 Workflow 组合阶段被拒绝（明确 fail-fast）。

## 4. 需求清单（必须满足）

### R-ES-01 强制事件优先
- 所有 `GAgentBase<TState>` 子类必须基于领域事件恢复状态，不得以快照替代事实。

### R-ES-02 显式事件构建
- 命令处理逻辑必须显式 `RaiseEvent`，不得依赖自动事件推导。

### R-ES-03 可重放同态
- 在线状态变更必须可通过 Replay 重建到同一结果。

### R-ES-04 静态泛型装配
- ES 行为构造必须在泛型上下文完成，不得回退到 Runtime 反射注入。

### R-ES-05 状态转换可组合
- `event -> state` 转换必须支持模块化拆分，避免在单个 Agent 中膨胀式 `switch`。
- 支持 `IStateEventApplier<TState>` 组合式 apply，顺序由 `Order` 控制。
- CI 必须禁止 `GAgentBase<TState>` 全继承链（含间接继承）子类直接修改 `State.xxx`，强制通过领域事件 + apply 路径变更状态。

### R-RM-01 Provider 解耦业务
- Provider 项目不得引用 Workflow/AI 业务读模型类型。

### R-RM-02 能力协商
- Provider 必须声明能力（索引类型、alias、schema 校验），ReadModel 需求必须在启动期校验。

### R-RM-03 路由确定性
- 多 Provider 并存时必须可预测选择，不允许隐式随机或“最后注册覆盖”语义。

### R-RM-04 统一观测
- Provider 写路径必须记录结构化日志：`provider/readModelType/key/elapsedMs/result/errorType`。

### R-WF-01 Workflow 仅消费抽象
- Workflow 层只依赖 Provider Runtime 抽象，不实现后端存储细节。

### R-WF-02 单链路投影
- Workflow CQRS 与 AGUI 必须从同一订阅与分发链路进入，不得维护平行投影系统。

### R-GOV-01 门禁强制
- 必须通过：
  - `bash tools/ci/architecture_guards.sh`
  - `bash tools/ci/projection_route_mapping_guard.sh`
  - `bash tools/ci/test_stability_guards.sh`

## 5. 重构计划（按优先级）

### P1（已完成）ES 强制化主链路
1. 移除自动反推事件接口与实现。
2. `GAgentBase<TState>` 生命周期切换到 Replay + Confirm。
3. Runtime 去除 ES 反射注入路径。

### P2（已完成）Provider Runtime 主干
1. 建立 Provider 注册/选择/校验/创建主链路。
2. 落地 InMemory/Elasticsearch/Neo4j 三类 Provider。
3. Workflow 接入统一 Provider 选择逻辑。

### P3（进行中）一致性与可维护性收口
1. 清理文档与代码中的历史双轨口径。
2. 补齐跨模块契约测试：`Command -> Events -> Replay -> State`。
3. 收敛 state transition 模型（Agent override + applier 组合）。
4. 统一配置示例与错误合同说明（启动失败与能力不匹配）。

### P4（待执行）性能与生产化增强
1. 为持久化 `IEventStore` 提供生产落地方案与压测基线（已落地本地持久化基线：`FileEventStore`，生产级后端仍待接入）。
2. 补齐 Elasticsearch/Neo4j 端到端集成脚本与回归套件（已落地基础 smoke + env-gated e2e，后续补 CI 常态化接入与更高负载回归）。
3. 细化快照策略与回放窗口控制（已落地自动快照 + 裁剪基础能力，后续补压测驱动的阈值治理）。

## 6. 验收标准（DoD）
1. 有状态 Actor 恢复路径全部来自 Replay，不存在 `StateStore.Load/Save` 事实回路。
2. Runtime/Core 不存在 ES 反射注入路径。
3. Provider 由统一 Runtime 选择并可校验能力。
4. Workflow 仅作为 Provider Runtime 消费方，不绑定 ES/Neo4j 实现细节。
5. 架构门禁、核心测试全绿。

## 7. 验证命令
- `dotnet test test/Aevatar.Foundation.Core.Tests/Aevatar.Foundation.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Foundation.Runtime.Hosting.Tests/Aevatar.Foundation.Runtime.Hosting.Tests.csproj --nologo`
- `dotnet test test/Aevatar.CQRS.Projection.Core.Tests/Aevatar.CQRS.Projection.Core.Tests.csproj --nologo`
- `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
- `bash tools/ci/architecture_guards.sh`
- `bash tools/ci/projection_provider_e2e_smoke.sh`

## 8. 变更原则
1. 删除优先于兼容。
2. 文档必须与当前代码语义一致，不保留“未来可能”但无代码支撑的条目。
3. 任何新扩展（Provider/ES）都必须接入现有主链路，不得开第二系统。
