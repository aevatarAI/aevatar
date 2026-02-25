# 分支改动速览（`feat/generic-event-sourcing-elasticsearch-readmodel` 相对 `dev`）

## 一句话结论

这条分支不是功能点修补，而是一次“主链路级”重构：把 **Event Sourcing**、**Projection Runtime/Providers**、**Workflow 查询与投影编排**、**CI 架构门禁**统一成一套更严格、可验证的工程体系。

## 改动规模（`dev...HEAD`）

- 提交数：47
- 文件改动：282（`+13222 / -1512`）
- 文件状态：新增 140、修改 110、删除 11、其余为重命名/移动
- 变动最集中区域：
  - `src/workflow`（51 文件）
  - `src/Aevatar.CQRS.Projection.Core.Abstractions`（24 文件）
  - `src/Aevatar.Foundation.Core/EventSourcing`（12 文件）
  - `src/Aevatar.CQRS.Projection.Providers.*`（Elasticsearch/InMemory/Neo4j）
  - `tools/ci`、`.github/workflows/ci.yml`

## 老板主要写了什么（按主线）

### 1) 把有状态 Actor 强制拉到 Event Sourcing 主链路

核心文件：`src/Aevatar.Foundation.Core/GAgentBase.TState.cs`、`src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs`

- `GAgentBase<TState>` 激活时强制 `ReplayAsync` 恢复状态，停用时 `ConfirmEventsAsync + PersistSnapshotAsync`。
- 新增 `PersistDomainEventAsync/PersistDomainEventsAsync`，强调“先领域事件，再状态演进”。
- `State` 写入受 guard 约束，避免绕开事件链路直接改状态。
- `EventSourcingBehavior` 明确禁止把 `TState` 当“快照事件”写入 EventStore。

### 2) 补齐 EventStore 后端与快照后清理机制

核心文件：`src/Aevatar.Foundation.Runtime/Persistence/FileEventStore.cs`、`src/Aevatar.Foundation.Runtime.Persistence.Implementations.Garnet/GarnetEventStore.cs`、`src/Aevatar.Foundation.Runtime/Persistence/DeferredEventStoreCompactionScheduler.cs`、`src/Aevatar.Foundation.Runtime/Actor/EventStoreCompactionDeactivationHook.cs`

- 新增文件型 EventStore（本地可运行、带并发版本校验）。
- 新增 Garnet EventStore（Redis 脚本做原子 append/compaction）。
- 引入“延迟压缩”：快照后只调度，不阻塞主命令路径；由 actor idle/deactivation 时执行裁剪。

### 3) 重做 Projection 的分层与运行时分发

新增项目（已进 `aevatar.slnx`）：

- `src/Aevatar.CQRS.Projection.Stores.Abstractions`
- `src/Aevatar.CQRS.Projection.Runtime.Abstractions`
- `src/Aevatar.CQRS.Projection.Runtime`
- `src/Aevatar.CQRS.Projection.StateMirror`
- `src/Aevatar.CQRS.Projection.Providers.Elasticsearch`
- `src/Aevatar.CQRS.Projection.Providers.InMemory`
- `src/Aevatar.CQRS.Projection.Providers.Neo4j`

关键点：

- `ProjectionStoreDispatcher<TReadModel,TKey>` 支持一对多写分发（Document + Graph）。
- Query binding 最多允许一个，避免多读源不一致。
- 支持写失败补偿接口（compensator）。
- `StateMirror` 提供通用 `State -> ReadModel` 镜像能力（可忽略/重命名字段）。

### 4) Workflow 投影与 Provider 组合方式重构

核心文件：`src/workflow/extensions/Aevatar.Workflow.Extensions.Hosting/WorkflowProjectionProviderServiceCollectionExtensions.cs`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionLifecycleService.cs`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionQueryService.cs`

- Provider 注册下沉到 Host/Extensions 组合层（不是 Workflow.Infrastructure 直接绑定）。
- 强约束：Document Provider 必须且只能启用一个；Graph Provider 也必须且只能启用一个。
- 生命周期接口改为 lease 句柄语义，避免 `actorId -> context` 反查式管理。
- 启动期新增 provider 校验（fail-fast）：`WorkflowReadModelStartupValidationHostedService`。

### 5) Workflow 查询面增强（API + 图查询）

核心文件：`src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/ChatQueryEndpoints.cs`、`src/workflow/Aevatar.Workflow.Application/Queries/WorkflowExecutionQueryApplicationService.cs`、`src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowProjectionQueryReader.cs`、`src/workflow/Aevatar.Workflow.Projection/ReadModels/WorkflowExecutionReadModel.cs`

- 新增/强化查询端点：`/agents`、`/workflows`、`/actors/{actorId}`、timeline、graph edges、subgraph、graph-enriched。
- 查询侧可按方向/边类型过滤图关系。
- `WorkflowExecutionReport` 扩展为可产出图节点/图边的读模型（`IGraphReadModel`）。

### 6) CI 门禁与回归测试体系明显加码

核心文件：`.github/workflows/ci.yml`、`.github/actions/prepare-runner/action.yml`、`tools/ci/architecture_guards.sh`、`tools/ci/event_sourcing_regression.sh`、`tools/ci/projection_provider_e2e_smoke.sh`

- CI 新增/强化多类作业：`fast-gates`、`split-test-guards`、`projection-provider-e2e`、`event-sourcing-regression`、`coverage-quality` 等。
- 架构守卫新增硬规则：禁止 Workflow.Infrastructure 直接依赖 `Providers.*`、禁止中间层 ID 映射事实态字典、强化 replay 合同测试约束等。
- 测试侧新增大量契约/集成覆盖（例如 `RoleGAgentReplayContractTests`、`ProjectionProviderE2EIntegrationTests`）。

## 可以先这么理解你老板的设计意图

1. **事实源唯一化**：状态恢复必须依赖事件回放，不走隐式状态捷径。  
2. **读侧能力插件化**：Document/Graph provider 可替换，但走同一个 runtime 分发主链路。  
3. **边界再收紧**：Workflow 依赖抽象，不直接耦合 provider 具体实现。  
4. **治理前置**：把“架构约束”固化成 CI 门禁，不靠口头约定。  

## 建议你优先追问的 10 个问题

1. 生产环境默认推荐的 Document/Graph provider 组合是什么？为什么？
2. `Projection:Policies:DenyInMemoryGraphFactStore` 在各环境的默认策略怎么定？
3. Event compaction 的保留窗口（`retainedEventsAfterSnapshot`）如何给基线值？
4. Workflow 查询接口是否需要鉴权/限流/脱敏（尤其 graph-enriched）？
5. 现有历史数据从旧读模型迁移到新 provider 绑定的方案是什么？
6. 若 Neo4j 或 Elasticsearch 不可用，系统降级路径是失败启动还是降级运行？
7. 新增的 startup fail-fast 会不会影响本地开发体验？是否分环境开关？
8. 这条分支里“必须先合并”的最小闭环提交范围是哪几部分？
9. nightly smoke / 压测基线未闭环部分，计划和负责人怎么排？
10. 这次重构后，后续 feature 开发必须遵循的 3 条硬约束是什么？

