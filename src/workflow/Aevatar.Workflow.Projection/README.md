# Aevatar.Workflow.Projection

`Aevatar.Workflow.Projection` 是 Workflow 领域的 CQRS 读侧扩展层。

## 职责边界

- 应用层投影端口实现：
  - `IWorkflowExecutionProjectionLifecyclePort`（`Ensure/Attach/Detach/Release`）
  - `IWorkflowExecutionProjectionQueryPort`（`Snapshot/Timeline/GraphEdges/GraphSubgraph/GraphEnriched`）
  - 默认实现分别为 `WorkflowExecutionProjectionLifecycleService` 与 `WorkflowExecutionProjectionQueryService`
  - 两个实现分别继承 `ProjectionLifecyclePortServiceBase` / `ProjectionQueryPortServiceBase`，通用端口编排已下沉到 `Aevatar.CQRS.Projection.Core`
- 编排组件拆分（避免单类过重）：
  - `WorkflowProjectionActivationService`（projection 启动与上下文激活）
  - `WorkflowProjectionReleaseService`（idle 检测与停止/释放）
  - `WorkflowProjectionLeaseManager`（ownership acquire/release）
  - `WorkflowProjectionSinkSubscriptionManager`（live sink attach/detach/replace）
  - `WorkflowProjectionLiveSinkForwarder`（sink 推送与失败策略路由）
  - `WorkflowProjectionSinkFailurePolicy`（sink 异常策略）
  - `WorkflowProjectionReadModelUpdater`（read model 元信息更新）
  - `WorkflowProjectionQueryReader`（query 映射读取）
  - Store 选择采用显式 providerName（Document/Graph 分离）
- 领域上下文：`IWorkflowExecutionProjectionContextFactory`、`WorkflowExecutionProjectionContext`
- 实时输出契约：`WorkflowRunEvent`、`IWorkflowRunEventSink`、`WorkflowRunEventChannel`（定义于 `Aevatar.Workflow.Application.Abstractions`）
- 领域投影实现：reducers、projectors、read model（不包含 Provider Store 实现）
- 领域 DI 组合：`AddWorkflowExecutionProjectionCQRS(...)`
- Provider 启动校验：由 `WorkflowReadModelStartupValidationHostedService` 执行 Document/Graph 两条独立 fail-fast 校验
- ReadModel 选择规则：DI store 解析与 Startup validation 统一复用 Document/Graph runtime options（`ProjectionDocumentRuntimeOptions`、`ProjectionGraphRuntimeOptions`）

本项目依赖：

- `Aevatar.CQRS.Projection.Core.Abstractions`（投影管线/端口抽象）
- `Aevatar.CQRS.Projection.Stores.Abstractions`（Document/Graph 存储契约）
- `Aevatar.CQRS.Projection.Runtime.Abstractions`（Provider 选择与 materialization 编排契约）
- `Aevatar.CQRS.Projection.Core`（通用生命周期/订阅/协调实现）
- `Aevatar.Foundation.Projection`（最小 read model 基类与读侧能力接口）
- `Aevatar.Workflow.Extensions.AIProjection`（可选扩展：组合 `Aevatar.AI.Projection` 的通用 reducer/applier）

## 统一运行链路

1. `EnsureActorProjectionAsync` 由 `WorkflowExecutionProjectionLifecycleService` 转发到 `WorkflowProjectionActivationService`，先通过 `WorkflowProjectionLeaseManager`（底层复用 `Aevatar.CQRS.Projection.Core` ownership coordinator）申请 ownership，再创建 projection 上下文并注册 actor stream 订阅
2. 每条 `EventEnvelope` 进入统一 coordinator，一对多调用已注册 projector
3. `WorkflowExecutionReadModelProjector` 驱动 reducers 生成并更新 read model，并通过 `IProjectionMaterializationRouter` 执行 Document/Graph 单写或双写
4. AI 通用事件通过 `Aevatar.Workflow.Extensions.AIProjection` 扩展接入，扩展内部复用 `Aevatar.AI.Projection` 的默认 applier + reducer，将事件写入 `WorkflowExecutionReport` 的 AI 能力字段，业务层无需重复维护映射代码
5. AGUI 分支与读模型分支共享同一输入事件流；AGUI projector 将 run 输出发布到 `workflow-run:{actorId}:{commandId}` 事件流

AGUI 输出与 CQRS 读模型共享同一链路，只是在 projector 分支不同。
应用层通过 `AttachLiveSinkAsync/DetachLiveSinkAsync` 订阅/退订 run-event stream（由 `WorkflowProjectionSinkSubscriptionManager` 承载，sink 写入由 `WorkflowProjectionLiveSinkForwarder` 统一转发）；
AGUI 分支实现位于 `Aevatar.Workflow.Presentation.AGUIAdapter`。
`ReleaseActorProjectionAsync` 由 `WorkflowProjectionReleaseService` 在无 live sink 绑定时停止投影并释放协调 Actor ownership；保证同一 `rootActorId` 不会并发启动多个投影视图（release 前由 `WorkflowProjectionReadModelUpdater` 写入 stopped 元信息）。
run-event 严格按 `EventEnvelope.CorrelationId` 与 commandId 绑定匹配，不对空 correlation 做广播投递。

## 订阅判定规则（事件如何进入 ReadModel）

关键区分：

1. `ReadModel` 字段/能力接口（如 `IHasProjectionTimeline`）只表示“可被写入”。
2. 真正决定“是否订阅某事件”的是 reducer 的 `EventTypeUrl` 声明 + DI 注册。
3. applier 只负责把已命中的强类型事件写入字段，不负责事件入口匹配。

最小落地清单：

1. 定义 ReadModel：声明业务字段与能力接口。
2. 定义 reducer：实现 `IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>` 并声明 `EventTypeUrl`。
3. 定义 applier（可选但推荐）：把字段映射逻辑从 reducer 拆出到 `IProjectionEventApplier<,,>`。
4. DI 注册 reducer/applier：未注册则不会进入投影链路。
5. projector 在运行时按 `payload.TypeUrl` 命中 `reducersByType`，只执行匹配项。

FAQ：

1. 只有 ReadModel 能处理某事件，就算“定义了事件订阅”吗？不是，必须有 reducer 注册。
2. 同一个事件能被多个 ReadModel 处理吗？可以，多 projector/多 reducer 并行订阅同一 `EventTypeUrl`。
3. 一个 ReadModel 能处理多个事件吗？可以，注册多个不同 `EventTypeUrl` 的 reducer 即可。

## 扩展方式

- 新增 reducer：
  - 实现 `IProjectionEventReducer<WorkflowExecutionReport, WorkflowExecutionProjectionContext>`
  - 在 DI 中注册
- 新增事件 applier（推荐）：
  - 实现 `IProjectionEventApplier<WorkflowExecutionReport, WorkflowExecutionProjectionContext, TEvent>`
  - 由对应事件 reducer 调用；Foundation/AI 通用事件建议放在对应分层项目，不在 Workflow 层重复实现
- 新增 projector：
  - 实现 `IProjectionProjector<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>`
  - 在 DI 中注册
- 扩展 ReadModel Provider（推荐）：
  - 文档存储注册：`IProjectionStoreRegistration<IDocumentProjectionStore<WorkflowExecutionReport, string>>`
  - 图存储注册：`IProjectionStoreRegistration<IProjectionGraphStore>`
  - 在 Host/Extensions 侧注册（例如 `Aevatar.Workflow.Extensions.Hosting.AddWorkflowProjectionReadModelProviders(...)`）
  - 通过 `Projection:Document:*` 与 `Projection:Graph:*` 配置选择 Provider

## Provider 配置

- Provider 选择统一配置入口：
  - `Projection:Document:Provider`：`InMemory`（默认）/`Elasticsearch`/`Neo4j`
  - `Projection:Graph:Provider`：`InMemory`（默认）/`Neo4j`
- `Projection:Policies:DenyInMemoryGraphFactStore`：禁用 InMemory graph 作为事实源（生产建议开启）
- 文档 Provider 配置：
  - `Projection:Document:Providers:Elasticsearch:*`
  - `Projection:Document:Providers:Neo4j:*`
- 图 Provider 配置：
  - `Projection:Graph:Providers:Neo4j:*`
- `WorkflowExecutionProjection:ValidateDocumentProviderOnStartup`：启动阶段预校验 document provider（默认 `true`）
- `WorkflowExecutionProjection:ValidateGraphProviderOnStartup`：启动阶段预校验 graph provider（默认 `true`）
- 图查询参数：
  - `/actors/{actorId}/graph-edges` 支持 `direction` 与 `edgeTypes`
  - `/actors/{actorId}/graph-subgraph` 支持 `direction` 与 `edgeTypes`
  - `/actors/{actorId}/graph-enriched` 支持 `direction` 与 `edgeTypes`
- 扩展 run 输出协议：
  - 保持 `WorkflowRunEvent` 不变，新增 presentation adapter 进行协议映射
  - 不改 Application 用例编排代码

## 与 API 的关系

`Aevatar.Workflow.Host.Api` 通过 `Aevatar.Workflow.Application` 调用本项目，不直接编排投影内核细节。API 仅负责协议适配（SSE/WebSocket/HTTP Query）。
