# Generic Event Sourcing + Index-Capable ReadModel 需求文档（Document/Graph 双索引抽象）

## 1. 文档元信息
- 状态：Draft
- 目标版本：v1
- 适用仓库：`aevatar`
- 编写日期：2026-02-22
- 本次更新：去具体后端绑定，改为 ReadModel Provider 能力模型

## 2. 背景与问题
当前仓库已具备：
- 基础 Event Sourcing 抽象：`IEventStore`、`EventSourcingBehavior<TState>`
- 有状态 Agent 抽象：`GAgentBase<TState>` + `IStateStore<TState>`
- 投影读模型抽象：`IProjectionReadModelStore<TReadModel, TKey>`
- InMemory 默认实现

当前缺口：
- 缺少“默认 State -> ReadModel”镜像能力，开发者需要手写大量投影胶水。
- ReadModel 存储语义与具体后端边界不清晰，容易把架构绑定到 Elasticsearch。
- 缺少“ReadModel 元数据 -> 索引能力”统一模型，无法做后端能力匹配。
- 缺少对两类索引形态的统一抽象：Document Index（Elasticsearch-like）与 Graph Index（Neo4j-like）。
- `EventEnvelope` 与 EventStore 持久化事件边界不清晰。
- persisted event 手写成本高，影响 Event Sourcing 落地。

## 3. 目标
1. Event Sourcing 与 Snapshot 语义保持不变。
2. 默认提供 State -> ReadModel 镜像投影（开发者无感接入），且 ReadModel 对开发者可选。
3. ReadModel 存储采用 Provider 抽象，不绑定具体后端。
4. ReadModel 元数据可描述“索引诉求”；仅索引能力后端可承接。
5. 同时支持两类索引 Provider：Document Index 与 Graph Index。
6. Elasticsearch 与 Neo4j 作为首批适配器，而不是架构绑定点。
7. EventStore 默认存储状态语义事件，由框架自动生成。
8. `EventEnvelope` 仅用于运行时处理与传播，不作为 EventStore 权威事件模型。
9. CQRS 各层采用“通用壳 + 领域插件”模式：`Application/Infrastructure/Host` 可通用，`Domain` 保持领域语义。

## 4. 非目标
- 不在本期引入新的业务编排模型。
- 不在本期实现跨存储分布式事务。
- 不在本期实现所有后端 Provider（先完成 InMemory + Elasticsearch + Neo4j）。
- 不在本期定义完整 ILM/冷热分层策略（仅保留扩展位）。
- 不在本期把领域不变量抽象成通用模板（领域规则仍由业务模块定义）。

## 5. 架构约束（强制）
- 严格分层：`Domain / Application / Infrastructure / Host`。
- 统一投影链路：默认镜像与自定义投影都必须走同一 Projection Pipeline。
- 读写分离：`Command -> Event`、`Query -> ReadModel`。
- 中间层不得以进程内映射充当跨节点事实源。
- 上层依赖抽象：业务代码不得依赖具体后端 SDK（如 Elasticsearch Client）。
- Provider 能力校验必须在启动期执行，避免运行期隐式降级。
- 分层通用化边界：`Application/Infrastructure/Host` 提供通用框架能力；`Domain` 仅通过契约插件接入，禁止把领域语义硬编码进通用层。

## 6. 术语
- `State`：`GAgentBase<TState>` 领域状态，由 `IStateStore<TState>` 持久化快照。
- `ReadModel`：查询视图模型。
- `Runtime Envelope`：运行时消息载体（`EventEnvelope`）。
- `Persisted State Event`：EventStore 中的状态语义事件。
- `ReadModel Provider`：`IProjectionReadModelStore<TReadModel, TKey>` 的具体后端实现。
- `Index-Capable Provider`：声明支持索引建模能力（mapping/settings/alias）的 Provider。
- `Document Index Provider`：面向文档索引模型（索引、mapping、alias）。
- `Graph Index Provider`：面向图索引模型（节点、关系、约束、路径查询优化）。

## 7. 方案总览

### 7.1 默认主链路
`Agent State` -> `StateMirrorProjection` -> `IProjectionReadModelStore<TReadModel, TKey>` -> `ReadModel Provider`

补充：
- 同一 `State` 可以扇出到多个 `ReadModel`（一对多）。
- 每个 `State/ReadModel` 绑定由一个“最终写入 owner projector”负责落库。

### 7.2 Provider 能力闸门
- ReadModel 元数据声明索引诉求（如主键、字段类型、索引别名）。
- Provider 声明能力（如 `SupportsIndexing`、`IndexKind`、`SupportsAliases`）。
- 启动期进行“ReadModel 诉求 vs Provider 能力”匹配：
  - 匹配成功：注册并运行。
  - 不匹配：按策略 fail-fast（默认）或显式禁用该 ReadModel。

路由规则补充：
- `IndexKind` 对 ReadModel 是可选项（`Auto/None`）。
- 当仅有一个索引型 Provider 可用时，可不标记 `IndexKind`，走默认路由。
- 当存在多个候选 Provider 时，必须通过 `IndexKind` 或显式绑定配置完成消歧，否则启动 fail-fast。

### 7.2.1 索引类型统一抽象
- `IndexKind=Document`：用于 Elasticsearch-like 后端。
- `IndexKind=Graph`：用于 Neo4j-like 后端。
- 统一抽象层负责：
  - 元数据标准化（ReadModel Index Profile）
  - 能力协商与注册校验
  - 将通用 ReadModel 变更翻译为后端特定写操作

### 7.3 开发者体验目标
- 最小路径：仅定义 `State`（可不定义 `ReadModel`）。
- 默认路径：不定义 `ReadModel` 时，框架可提供默认读视图（Default ReadModel）。
- 进阶路径：定义自定义 `ReadModel` 并可替换 `IStateToReadModelProjector<TState, TReadModel>`。
- 运维路径：通过元数据驱动索引初始化（仅限索引型 Provider）。

### 7.4 EventEnvelope 与 EventStore 边界
- 运行时仍以 `EventEnvelope` 处理与分发。
- EventStore 权威事件必须是 `Persisted State Event`。
- 原始 `EventEnvelope` 可选旁路落库用于审计，但不参与回放权威语义。

### 7.5 全层通用化模型（Generic CQRS Kernel）
- `Domain`：定义业务状态、业务命令、业务查询、领域规则（不可被框架语义吞并）。
- `Application`：提供通用 Command/Query 编排管道（校验、幂等、权限、审计、事务边界、投影触发）。
- `Infrastructure`：提供可替换存储与索引 Provider（EventStore/StateStore/ReadModelStore）。
- `Host`：提供统一模块装配、能力协商、配置绑定、健康检查。
- 领域模块仅需注册 handler/projector/readmodel 契约，即可接入通用内核。

ReadModel 可选模式：
- `State-only`：仅写侧或最小查询场景，不要求开发者定义 ReadModel。
- `Default ReadModel`：框架基于 State 生成默认读视图。
- `Custom ReadModel`：开发者定义专用查询模型与投影逻辑。

## 8. 范围（Scope）

### 8.1 In Scope
- Provider 抽象下的通用 ReadModel Store 语义。
- 默认 State 镜像投影（可替换）。
- ReadModel 元数据模型与能力匹配规则。
- Elasticsearch 适配器（Document Index Provider）实现。
- Neo4j 适配器（Graph Index Provider）实现。
- 通用 Application Service 管道抽象（Command/Query 通用壳）。
- 通用 Host 模块装配抽象（模块注册与能力协商）。
- DI 扩展：Provider 切换、默认镜像器注册、自定义覆盖。
- Workflow Projection 接入。
- 单元、集成、分布式一致化测试。

### 8.2 Out of Scope
- 全量后端 Provider 一次性交付。
- Elasticsearch 生产运维平台自动化。

## 9. 功能需求（Functional Requirements）

### FR-1 Event Sourcing 抽象
- append-only + `expectedVersion` 并发校验 + `fromVersion` 回放查询。
- 事件记录结构包含：`streamId`、`eventId`、`eventType`、`eventData`、`version`、`timestamp`、`metadata`。

### FR-2 Persisted State Event 模型约束
- EventStore 权威事件必须只表达状态变更语义。
- 不得耦合运行时路由字段（例如 `Direction`、转发链）。
- 原始 envelope 不作为回放权威源。

### FR-3 自动状态事件生成（开发者无感）
- 框架在统一处理管道内自动生成 `Persisted State Event`。
- 一次处理结束后基于 State 变更检测决定是否 append。
- 无状态变化不得产生冗余事件。
- 支持 `Snapshot` / `Delta` 策略扩展。

### FR-4 有状态 Agent 与 Snapshot
- `GAgentBase<TState>` 继续通过 `IStateStore<TState>` 完成 Load/Save。
- 启用 Event Sourcing 不改变 `IStateStore<TState>` 作为快照通道的语义。

### FR-5 通用 ReadModel Store（Provider 模式）
- 兼容 `IProjectionReadModelStore<TReadModel, TKey>`：
  - `UpsertAsync`
  - `MutateAsync`
  - `GetAsync`
  - `ListAsync`
- API 语义不绑定具体后端。

### FR-6 Provider 能力声明与匹配
- 定义 Provider 能力抽象（至少包含）：
  - `SupportsIndexing`
  - `IndexKind`（`Document` / `Graph`）
  - `SupportsAliases`
  - `SupportsSchemaValidation`
- ReadModel 注册时执行能力匹配。
- 对声明索引诉求的 ReadModel，必须由 `SupportsIndexing=true` 的 Provider 承接。
- 当 ReadModel 显式声明 `IndexKind` 时，Provider 必须类型匹配。
- 当 ReadModel 未声明 `IndexKind` 时，允许自动路由；若候选 Provider 不唯一则必须显式消歧。

### FR-7 默认 State 镜像投影
- 提供默认 `IStateToReadModelProjector<TState, TReadModel>`：
  - 默认策略：同名字段映射 + 可配置忽略字段。
  - 字段不匹配可由映射配置或自定义 projector 覆盖。
- 当开发者未定义 `ReadModel` 时，允许落到框架默认读视图（Default ReadModel）。

### FR-7.1 ReadModel 可选化
- 开发者不定义 `ReadModel` 时系统必须可运行（至少支持 `State-only` 模式）。
- 可通过配置选择：`State-only` / `Default ReadModel` / `Custom ReadModel`。
- 在 `State-only` 模式下，若调用依赖 ReadModel 的查询端点，应返回明确错误或能力不可用响应（按统一错误模型）。

### FR-8 自定义投影替换机制
- 允许业务侧通过 DI 替换默认镜像器。
- 同一 `State/ReadModel` 绑定仅允许一个“最终写入 owner projector”。
- owner projector 内部允许组合多个 reducer/applier/module 协同完成映射与聚合。
- `State -> ReadModel` 为一对多：一个 State 可以对应多个 ReadModel，但每个 ReadModel 绑定仍必须只有一个最终写入 owner。
- 优先级：显式业务注册 > 默认注册。

### FR-9 ReadModel 元数据驱动索引（能力感知）
- ReadModel 元数据可声明：
  - 通用：索引名/前缀、主键字段、版本、标签
  - Document Profile：字段 mapping（keyword/text/date/numeric/...）、settings、alias
  - Graph Profile：节点标签、关系类型、关系方向、唯一约束字段、可选索引提示
- 仅索引型 Provider 执行 metadata 建索引逻辑。
- 非索引型 Provider 遇到索引诉求时按策略 fail-fast（默认）。
- `IndexKind` 未声明时，可由 Provider 能力自动推断；推断不唯一时必须显式绑定。

### FR-10 双索引适配器实现
- 必须提供 Document Index 适配器（Elasticsearch-like）。
- 必须提供 Graph Index 适配器（Neo4j-like）。
- 两者都必须遵循统一抽象层，不得在业务层分叉接口。
- 双适配器是平台能力要求，不代表每个 ReadModel 都必须显式标记 `Document/Graph`。

### FR-11 Workflow Projection 接入
- `WorkflowExecutionReport` 可由 Provider 切换承接。
- 不改变上层 Query API 合同。

### FR-12 一致化验证
- 在 3 节点脚本中加入跨节点一致性测试并纳入 CI。

### FR-13 通用 Application Service 层
- 提供通用 `ICommandApplicationService` / `IQueryApplicationService` 编排入口。
- 通用应用层必须支持：校验、幂等、防重、审计、错误模型统一。
- 业务侧仅实现领域 handler/mapper/spec，不重复实现管道横切逻辑。

### FR-14 通用 Host 装配层
- 提供模块化注册机制，支持按模块装配：
  - 领域命令/查询 handler
  - 投影 projector/reducer
  - Provider 适配器
- 启动期执行统一能力协商与配置有效性校验。

## 10. 非功能需求（Non-Functional Requirements）

### NFR-1 一致性
- 读模型允许最终一致；同节点写后读在可配置窗口内收敛。
- EventStore 单流版本单调递增。

### NFR-2 性能
- Event append 与批量事件数量线性相关。
- `ListAsync` 必须有硬上限。

### NFR-3 可观测性
- 结构化日志至少包含：`provider`、`readModelType`、`documentId`、`stateVersion`、`elapsedMs`、`exceptionType`。
- 索引型 Provider 额外记录：`index`、`alias`。
- Graph Index Provider 额外记录：`nodeLabel`、`relationshipType`、`constraint`.

### NFR-4 可运维性
- Provider 配置支持 `appsettings` + 环境变量。
- 索引型 Provider 的索引命名必须可环境隔离。

### NFR-5 安全
- 日志不得输出凭据。
- 保留认证/TLS 参数透传位。

## 11. 配置需求

### 11.1 Event Sourcing
- `EventSourcing:Provider`
- `EventSourcing:Snapshot:*`
- `EventSourcing:PersistedEvent:Mode`（`Snapshot` / `Delta`）
- `EventSourcing:PersistedEvent:AutoGenerate`（默认 `true`）

### 11.2 ReadModel Provider
- `Projection:ReadModel:Provider`（示例：`InMemory` / `Elasticsearch` / future）
- `Projection:ReadModel:IndexKind`（可选：`Auto` / `Document` / `Graph`）
- `Projection:ReadModel:Mode`（`StateOnly` / `DefaultReadModel` / `CustomReadModel`）
- `Projection:ReadModel:DefaultMode`（`MirrorState` / `CustomProjector`，仅在非 `StateOnly` 下生效）
- `Projection:ReadModel:FailOnUnsupportedCapabilities`（默认 `true`）
- `Projection:ReadModel:Bindings:*`（可选，ReadModel 到 Provider 的显式绑定）

### 11.3 Provider 专属配置（示例）
- `Projection:ReadModel:Providers:Elasticsearch:Endpoints`
- `Projection:ReadModel:Providers:Elasticsearch:IndexPrefix`
- `Projection:ReadModel:Providers:Elasticsearch:RequestTimeoutMs`
- `Projection:ReadModel:Providers:Elasticsearch:ListTakeMax`
- `Projection:ReadModel:Providers:Elasticsearch:AutoCreateIndex`
- `Projection:ReadModel:Providers:Neo4j:Uri`
- `Projection:ReadModel:Providers:Neo4j:Database`
- `Projection:ReadModel:Providers:Neo4j:Username`
- `Projection:ReadModel:Providers:Neo4j:Password`
- `Projection:ReadModel:Providers:Neo4j:AutoCreateConstraints`

### 11.4 ReadModel Metadata
- `Projection:ReadModel:Metadata:StrictMode`
- `Projection:ReadModel:Metadata:ApplyAliases`

## 12. 验收标准（DoD）

### 12.1 单元测试
- 自动状态事件生成：变更检测、无变更不写、版本单调。
- EventEnvelope 边界：路由字段不进入权威 persisted event。
- Provider 能力匹配：支持/不支持索引能力的注册行为。
- Provider 类型匹配：`Document` 元数据不能落到 `Graph` Provider，反之亦然。
- 自动路由：单候选可自动承接，多候选未消歧必须 fail-fast。
- 默认镜像器映射与自定义覆盖优先级。
- Elasticsearch Provider 的 Upsert/Mutate/Get/List 与索引初始化。
- Neo4j Provider 的节点/关系写入与约束初始化。
- 通用 Application Service 管道：横切逻辑顺序与异常语义一致。
- Host 装配：模块注册冲突与能力协商失败路径覆盖。
- ReadModel 可选模式：`StateOnly`/`DefaultReadModel`/`CustomReadModel` 行为与能力边界覆盖。

### 12.2 集成测试
- Docker Elasticsearch 下验证索引型 ReadModel 的端到端写读。
- Docker Neo4j 下验证图模型 ReadModel 的端到端写读。
- 验证切换 Provider 后 Query 语义一致。
- 验证 `StateOnly` 模式下读取端点返回统一“能力不可用”语义。

### 12.3 分布式一致化测试
- 3 节点下 `workflows/agents` 查询跨节点一致。
- 测试纳入 CI 且可稳定判定失败。

### 12.4 合规门槛
- `build/test` 全通过。
- 架构 guard 与稳定性 guard 全通过。
- 文档、配置示例、能力矩阵同步。

## 13. 与现状对比
| 维度 | 现状 | 目标 |
|---|---|---|
| 后端绑定 | 容易向 Elasticsearch 语义靠拢 | Provider 抽象，不绑定具体后端 |
| 索引类型 | 仅文档索引思维 | Document/Graph 双索引统一抽象 |
| EventStore 事件语义 | 易混用 runtime envelope | 仅持久化状态语义事件 |
| Persisted Event 开发成本 | 业务手写为主 | 框架自动生成 |
| Application Service | 业务层重复实现横切逻辑 | 通用管道壳 + 领域 handler 插件 |
| Host 装配 | 模块能力协商分散 | 统一模块装配与能力校验 |
| ReadModel 定义要求 | 默认倾向开发者显式定义 | ReadModel 可选（StateOnly/Default/Custom） |
| 默认读模型构建 | 业务手写 reducer/projector | 默认提供 State 镜像投影 |
| 索引能力 | 无统一能力闸门 | ReadModel 元数据 + Provider 能力匹配 |

## 14. 风险与缓解
- 风险：能力不匹配导致运行时失败。
  - 缓解：启动期 fail-fast + 能力矩阵校验。
- 风险：默认镜像不足以覆盖复杂业务。
  - 缓解：保持自定义 projector 可替换。
- 风险：索引 mapping 演进兼容问题。
  - 缓解：版本化索引 + alias 切换策略（后续扩展）。

## 15. 里程碑建议
1. M1：Provider 能力抽象 + 默认镜像抽象。
2. M2：Document/Graph 元数据模型 + 能力匹配实现。
3. M3：Elasticsearch + Neo4j 双适配器与测试覆盖。
4. M4：Workflow 接入、3 节点一致化与 CI 收口。

## 16. 待确认项（Open Questions）
- 不支持索引能力时是否允许“显式降级”为无索引模式，还是一律 fail-fast？
- Provider 能力最小集合是否需要扩展到 `SupportsPartialUpdate`？
- ReadModel 元数据版本升级策略如何定义（兼容/阻断）？
- 同一 ReadModel 是否允许同时落 Document + Graph（双写），还是要求单一承接方？
