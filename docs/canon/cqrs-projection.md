---
title: "Aevatar CQRS 架构（Maker 插件化后）"
status: active
owner: eanzhao
---

# Aevatar CQRS 架构（Maker 插件化后）

## 1. 目标

定义当前 CQRS 基线：

1. 写侧：`Application Command -> Actor Mailbox Message(EventEnvelope) -> Domain Event`
2. 读侧：`Projection -> ReadModel -> Query`
3. 插件：Maker 仅扩展 Workflow 模块，不新增第二套 CQRS 主链路

## 2. 顶层原则

1. Host 只做协议与组合，不做业务编排。
2. 命令执行必须走 CQRS Core 标准命令骨架，不允许每个 capability 私自拼一套 `resolve/ack/observe/finalize` 生命周期；同时不引入与 runtime 平行的命令总线壳层。
3. 读写分离保持单一事实源：`EventStore` 中的领域事件 + 投影读模型。
4. 中间层禁止维护 actor/run/session 事实态内存映射。

## 3. 项目分层

| 层 | 项目 | 职责 |
|---|---|---|
| CQRS Core | `Aevatar.CQRS.Core*` | 标准命令管线抽象、interaction/observation 模板、上下文策略、envelope/dispatch/receipt contract、输出流抽象 |
| Projection Core | `Aevatar.CQRS.Projection.*` | 投影生命周期、订阅、分发、协调 |
| Foundation/AI Projection | `Aevatar.Foundation.Projection` / `Aevatar.AI.Projection` | 通用读模型能力与 AI reducer |
| Workflow Projection | `src/workflow/Aevatar.Workflow.Projection` | Workflow 领域读模型与投影 |
| Maker Extension | `src/workflow/extensions/Aevatar.Workflow.Extensions.Maker` | 通过 `IWorkflowModulePack` 扩展模块，不承载独立 CQRS |

## 4. 主链路

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    API["Host API"] --> APP["Application Service"]
    APP --> ACT["Actor/GAgent"]
    ACT -->|"Append domain event"| ES["Committed EventStore"]
    ES -->|"Committed event identity + version"| ACT
    ACT -->|"CommittedStateEventPublished"| EVT["Actor Envelope Stream"]
    ACT -->|"Accepted version"| CP["Runtime publication checkpoint"]
    CP -.->|"Activation recovery watermark"| ACT
    EVT --> PROJ["Projection Pipeline"]
    PROJ --> RM["ReadModel"]
    RM --> Q["Query API / SSE / WS"]
```

口径澄清：

1. `EventEnvelope Stream` 是 runtime message stream，不是 Event Sourcing 的事实流。
2. Command 进入 Application 后，会被包装成 `EventEnvelope` 投递到目标 Actor 邮箱。
3. Actor 在自己的串行上下文里做决策，只有显式持久化的领域事件才进入 `EventStore`。
4. Projection 当前消费的是 Actor envelope 流，并把其中有业务语义的 payload 映射为 read model 与实时输出。
5. committed domain event 必须先进入 EventStore，再以 `EventEnvelope<CommittedStateEventPublished>` 进入同一 projection 主链；Runtime publication checkpoint 只记录投递进度，不定义业务事实。
6. activation 可在 checkpoint 后补发 committed fact；这是 write/runtime recovery，不是 query-time replay 或 projector 计算。

### 4.1 CQRS Core 统一命令骨架

CQRS 不应只提供零散 helper，而应定义所有 capability 复用的标准命令处理逻辑：

1. `Normalize Command`
   Host/Adapter 负责协议解析、鉴权、限流、基础校验，并把外部请求收敛为应用命令模型。
2. `Resolve Target`
   根据命令解析目标 actor 身份、创建/复用策略与必要的资源语义。
3. `Create CommandContext`
   统一分配 `commandId / correlationId / headers`，避免各子系统各自生成追踪语义。
4. `Build Envelope`
   把应用命令映射成统一 `EventEnvelope`，但 payload 的业务语义仍由 capability 自己定义。
5. `Dispatch via IActorDispatchPort`
   通过 `IActorDispatchPort` 完成 mailbox 语义下的 envelope 投递；目标 actor 的获取/创建与拓扑仍由 `IActorRuntime` 负责。具体 runtime adapter 不得在 dispatch 内追加目标 grain 存在性调用；例如 Orleans adapter 在 stream handoff 完成后即可返回 accepted，由 Orleans 在消费侧解析当前 activation。
6. `Create Accepted Receipt`
   统一返回 `Accepted + commandId (+ actorId/correlationId)`，只承诺可追踪，不承诺 committed / observed。
7. `Observe Result`
   只有交互式 SSE/WS 等需要实时输出的入口才启动 `ICommandObservationLifecycle<TCommand,TTarget,TReceipt,TError>`。该阶段在 dispatch 前仅 attach 到已经存在、可确定寻址的 projection session/lease；不得同步 ensure/activate projection。cold session 或 attach 不可用时返回既有 projection pending/unavailable/disabled 类错误，命令不得进入 actor inbox，也不得发出 accepted receipt。dispatch-only 命令不启动 live observation，后续完成态统一走 read model 或 actor/session stream 观察，而不是在 command API 内私自拼装会话生命周期。

职责归属：

1. CQRS Core 应拥有 `Resolve Target / Context / Envelope / Dispatch / Receipt` 的通用抽象与默认实现。
2. Capability 只提供领域命令模型、目标解析规则、payload 映射、accepted receipt 映射与领域特有的观察模型。命令准备阶段不得启动 projection/read-model activation、live sink attach 或 session lease；`ICommandObservationLifecycle` 也只能 attach 已存在 session，不能 ensure/activate projection。
3. Projection Core 只负责写后传播、读模型与实时观察，不回流承担命令入口语义。

现状映射：

1. `ICommandContextPolicy`、`ICommandEnvelopeFactory<TCommand>` 已经是 CQRS Core 抽象。
2. `DefaultCommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>` 已把 `Resolve Target -> Context -> Envelope -> Dispatch via IActorDispatchPort -> Accepted Receipt` 串成标准命令骨架；`PrepareAsync` 不做 projection/session attach。
3. `DefaultCommandInteractionService<TCommand,...>` 已把交互式入口串成 `Prepare -> Observe -> DispatchPrepared -> Accepted callback -> Pump -> Release`。`Observe` 使用 `ICommandObservationLifecycle<,,,>` attach 既有 observation session，失败时返回 start failure 且不 dispatch；dispatch 失败时由 target cleanup 释放已经附着的 observation。
4. `ActorCommandTargetDispatcher<TTarget>` 通过 `IActorDispatchPort` 落地 runtime-neutral envelope 投递；`IActorRuntime` 继续负责目标 actor 的获取/创建与拓扑语义；对外交互入口统一收敛为 target-erased 的 `ICommandInteractionService<...>`。

### 4.2 下一阶段蓝图（IActorDispatchPort 投递 + CQRS Core 统一命令骨架）

当前基线仍是 `Host -> Application -> Actor` 直连执行。  
如果要继续按最佳实践演进，目标方向是：

1. `Endpoint` 只做 normalize / validate / auth / 应用层组合。
2. 外部命令继续使用统一 `Envelope` 载体，不强制拆不同物理 envelope 类型。
3. CQRS Core 统一承载 target resolve / command context / envelope build / dispatch port / accepted receipt。
4. Infrastructure 通过 `IActorRuntime` 获取/创建目标 actor，并通过 `IActorDispatchPort` 投递 envelope。
5. 命令主链路不再额外引入 ingress queue/stream。
6. 对外同步 ACK 只表示 dispatch 成功，不承诺 committed / observed。
7. 实时输出与读侧仍然通过 actor envelope stream + projection 观察。

详细蓝图见：

- [2026-03-09-cqrs-command-actor-receipt-projection-blueprint.md](architecture/2026-03-09-cqrs-command-actor-receipt-projection-blueprint.md)

## 5. 投影约束

1. CQRS 与 AGUI/SSE/WS 共用统一 Projection 输入链路。
2. 事件订阅以 reducer 的 `EventTypeUrl` 精确匹配为准。
3. 未命中 reducer 的事件必须为 no-op。
4. Workflow 投影生命周期通过 lease/session 句柄管理，不允许 `actorId -> context` 反查。
5. 同一 `EventEnvelope` 分发到多个 projector 时采用“一对多全分支尝试”语义：单个 projector 失败不阻断其他 projector 执行，最终以聚合异常统一回传。
6. 禁止 `Projection:ReadModel:Bindings` 与任何 BindingResolver 路由；投影存储路由统一由 `IProjectionStoreDispatcher` + Store Binding (`IProjectionDocumentStore` / `IProjectionGraphStore`) 决策。
7. Host 组合层按配置仅注册所需 provider 组合，不允许无条件并列注册 InMemory/Elasticsearch/Neo4j。
8. 持久投影 scope 的激活由 committed-state publication owner hook 触发：Actor 完成 domain event commit 并构造 `EventEnvelope<CommittedStateEventPublished>` 前，Foundation 调用 `ICommittedStatePublicationHook`，Projection Core 根据精确的 actor type 与 state event descriptor 生成 `ProjectionScopeStartRequest` 并分发给已有 `IProjectionScopeActivationService<TLease>`。命令入口不得同步调用 projection activation facade，也不得新增 actor/lifecycle phase 来“预热”读模型。warm path 只能信任绕过进程内缓存的 authoritative source+target relay evidence；该 evidence 必须精确匹配 `HandleThenForward`、空 direction filter、唯一 `CommittedStateEventPublished` filter、scope actor 实际 kind，以及 actor-owned 的非零 activation generation，命中后不得再调用 actor runtime、kind verifier 或 dispatch ensure。cold path 必须同步创建/修复 scope、验证 kind、投递 ensure 并等待同一 exact evidence 可见，任何失败都 fail closed，不得 fire-and-forget 或 timeout 后继续 publication。唯一滚动兼容例外是旧节点写入、尚无 kind/generation 字段的 relay：它不得命中 warm path，且必须先由当前调用通过 expected kind 创建或 verifier 获得 ownership proof；若 authoritative legacy evidence 在 dispatch 前已经存在，当前节点先用唯一 `LeaseId` challenge 覆写同一转发形态，只有 scope owner 随后的 activate/ensure 把 challenge 重写回空 `LeaseId` legacy 形态，或写出 exact evidence，才可返回。单纯 dispatch admission 不是 handled/committed proof；后续调用继续走 cold path，直到兼容节点把 relay 收敛为 exact evidence。Orleans topology grain 的既有 RPC surface 不得为该 lookup 增加方法；authority 必须通过无进程缓存的既有 snapshot RPC 读取。active durable scope 的分布式 observation relay 必须跨普通 runtime deactivate 保留，只能由显式 scope release 删除；release 返回前必须通过同一 authoritative source+target lookup 确认 evidence 已消失。activation 或 relay publish 失败必须向 committed publication 回传，使 durable checkpoint 不前移并保留同一 committed event identity 的恢复机会。
9. Projection scope 的 repair backlog 由 scope actor 权威持有，完整失败记录、异常原因与 `EventEnvelope` 只允许保留在 actor state / event store 中供 replay，不得进入 committed-state observation。对外发布 `CommittedStateEventPublished` 前，publication hook 必须在副本中从权威 failure 列表重新计算强类型 `ProjectionScopeFailureSummary`，清除失败事件的 reason/envelope，并把每条 `ProjectionScopeState.failures` 替换为只含重试状态与最早时间的等量滚动升级兼容占位；新 status projector 必须优先读取 summary，旧消息无 summary 时才从 failures 回退。单次滚动发布期间不得截断该兼容列表，否则旧 projector 会持久化错误计数；只有所有 reader 已支持 summary 后，后续版本才可移除该兼容编码。该净化不得回写或裁剪权威 backlog。历史 pending publication 必须在同一 event id / version 下经过相同净化后重试并推进原 checkpoint，禁止为绕过超限伪造新版本。scope 激活或 committed publication 恢复后若仍有未耗尽自动重试的 retained failures，actor 必须向自身 inbox 发布 `ReplayProjectionFailuresCommand` continuation 并自动重放该批 envelope；已经耗尽的 failure 只允许通过显式 operator replay 再次尝试，不得在每次激活时形成自动重试循环。部署不会枚举 dormant scope，因此 host-owned failure recovery reconciler 必须逐页扫描 `ProjectionScopeStatusDocument` 中 active、未 release 且存在 unresolved failure 的候选，直到 reader cursor 结束，再向 scope actor 投递 typed automatic replay command；不得使用 restart-at-zero 的全局候选上限让尾部 scope 饥饿。readmodel 只负责候选发现，是否 replay 仍由 actor state 决定，query API 不得触发该 reconciler 或 activation。自动 replay 以候选观察到的 scope committed `StateVersion` 为 actor-owned admission token，同一版本只消费一次；admission event 与每条 replay 结果都会推进权威版本，使 admission 后崩溃或超过单批上限的 backlog 可由后续版本继续。不得要求运维或用户手工 replay 才能让仍可自动恢复的已提交终态进入 readmodel。
10. Elasticsearch projection schema-drift 的唯一权威是 provider 生成的 augmented mapping fingerprint 与稳定 alias lifecycle。query resolver / query reader / consistency probe 不得读取 live ES mapping 作为第二真相，也不得触发 repair / reindex；write-side `UpsertAsync -> EnsureIndexAsync` 只处理 greenfield / legacy bare lifecycle，遇到单一旧 fingerprint 或多 backing drift 必须 fail closed，不能 `_reindex` 或切 alias。alias 指向单一旧 fingerprint physical index 的 clean migration 只能由静态 provider-local startup reconcile（`IProjectionIndexReconcileTarget.ReconcileIndexAsync`）创建 expected physical、执行 old-to-new reindex、确认无 failures / timeout 后用一次 `_aliases` 原子切换；dynamic index scope 不参与 startup reconcile，不获得 clean drift migration。alias 多 backing、source 缺失、不兼容 mapping、reindex failure / timeout、partial copy 或非 static reconcile 路径仍必须 fail closed。
11. commit publication 是 at-least-once：current-state projector 必须以 `actorId + authoritative StateVersion` 做单调幂等覆盖，artifact/audit consumer 必须以 committed event identity 做幂等键；不得依赖 envelope 只出现一次。
12. Runtime fleet capability gate 只由固定 Authority actor 的 committed state 投影为 actor-scoped current-state document。Admission reader 只能读取该 document，验证唯一 exact gate、authoritative state version、membership freshness/digest/deployment 与所有 active member 的 typed capability advertisement，再产生 freshness-bearing admission proof；query/read path 不得回调 Authority、读取 runtime 偶然结构、触发 reconcile 或 projection priming。
13. Projection graph 当前采用明确的无限期保留契约：workflow run 与 script-native graph 在其 committed facts 保留期间持续可查询，archive/terminal 不触发删除，也不存在 query-time cleanup。容量、增长预测、告警阈值和未来有限保留的 hard gate 见 [Projection Graph Retention and Capacity](../operations/projection-graph-retention.md)。有限保留若被批准，必须由 typed committed retirement fact 和 durable actor-owned cleanup 驱动，禁止用字符串状态、进程内 owner registry 或读路径副作用实现。
14. `ProjectionScopeStatusDocument` 的写入者由 source projection scope actor 权威决定（`ProjectionScopeState.status_route`：typed contract id / version、单调 route epoch、cutover phase），status document 只复制该决定（`status_route` 字段），任何 writer 不得读 document 来选择路由。**写入者切换是 source actor 自己 turn 上的分阶段 cutover**，每个阶段都是 committed fact，任意两阶段之间重启都能在 activation 上续做（且发生在该 scope 自己的 observation relay——activation service 等待的 evidence——被写入之前，cold ensure 与 activation 两条路径一致）：
    1. **WARMING**：以新 epoch 提交 `ProjectionScopeStatusRouteWarmingStartedEvent`（`warm_started_version` = 该提交的版本），在自己的 stream 上安装新 writer 的 relay 并 ensure 其 actor；当前 writer 继续写。新 writer 只观察并向 source 发 `ProjectionScopeStatusWriterCaughtUpEvent{observed_version}`（terminal materializer 在 warming 时、legacy shadow 在 rollback warming 时都这样做）；空闲 source 在 activation 上提交 `WarmingProbedEvent` 让 relay 有东西可投递。
    2. **caught up**：`observed_version ≥ warm_started_version` 时提交 `CaughtUpEvent`。
    3. **BLOCKED**：提交 `BlockedEvent`；此时 source 拒绝任何 observation（`ProjectionScopeStatusRouteBlockedException`，可重试，checkpoint 不前移），不再发布新版本。
    4. **release previous writer（typed committed 确认，不是 dispatch 接受）**：向前 writer dispatch `ReleaseProjectionScopeCommand{status_route_epoch}`，route 保持 BLOCKED；前 writer 只有在自己 **提交** release 之后才回送 `ProjectionScopeStatusWriterReleasedEvent{route_epoch, last_observed_version}`（已经 released 时也回送，因此重复 dispatch 幂等）。source 收到该 continuation 才移除前 writer relay 并提交 `LegacyRouteReleasedEvent{epoch, released_writer_observed_version}`。在确认到达之前 relay 保留（前 writer 仍能观察到 BLOCKED 那笔 publication 完成 drain），durable continuation 按同一退避节奏重发 release。`blocked_version` 记录在 route 上：确认的 `last_observed_version` 低于它时记录 released-before-drained 警告。前 writer actor 不存在时无人可确认，直接移除 relay 并以 `released_writer_observed_version = 0` 记录。`IActorDispatchPort` 的接受只是 inbox admission，永远不等于 release。
    5. **ACTIVE**：提交 `RouteActivatedEvent`（phase Active、`flip_version`）；新 writer 此后写每个 terminal outcome，第一笔写是 epoch-fenced same-version takeover。
    fleet admission（`PROJECTION_SCOPE_STATUS_TERMINAL_V2` / `aevatar.projection.scope-status-terminal.v2`，reader version 2；gate 只在全部 silo 广播该 contract 后打开）只是采用 terminal writer 的 admission 证据，不是 per-source route 权威。**contract revision 是 mixed-binary hard gate**：phase-unaware 的旧 source 二进制把任何匹配的 v1 route 当作 ACTIVE（会在 WARMING/BLOCKED 期间重挂 relay 并 release legacy），因此当前二进制只广播 v2，且 authority 的 managed capability 从 v1 换成 v2——混合 fleet 中 v2 因不 unanimous 永不打开，v1 因不再 managed 被 revoke，两个方向都 fail closed。current terminal materializer 仍 **服务** v1 route（source 跨升级保留 writer），但不再创建 v1 route；ACTIVE 的 v1 route 在拿到 fresh v2 grant 后通过 `ProjectionScopeStatusRouteContractUpgradedEvent` 就地升到 v2（epoch+1、writer 不变、无 cutover，更高 epoch 让下一笔写成为对自己 document 的 epoch-fenced takeover）；没有 v2 grant 时 v1 route 保持不动且**永不因 v1 被 revoke 而回滚**——v1 的 revoke 是 contract revision 的必然结果，不是运维意图证据，而当前 writer 仍是权威——并通过 durable continuation 持续重试升级（always-active scope 不会再激活）。没有 fresh grant 时 source 自己 ensure legacy shadow（source 拥有该决定，activation service 不再凭 relay evidence 决定 legacy ensure——这消除了 warm-return 与采用并发的竞态），并通过 durable callback scheduler 以 30s/1m/2m/4m/8m、之后 8m 节奏持续重试采用。已 ACTIVE 的 terminal route 在每次 activation 上自愈 relay / materializer、释放未释放的前 writer，并通过 authoritative binding evidence 对账重新出现的 legacy relay（移除 + 再次 release）；legacy shadow 自身观察到 source 的 terminal route 处于 writing phase 时立即自我 release。**durable rollback**：fleet admission 被显式 `Revoked` 时，source 以 legacy contract（`aevatar.projection.scope-status-legacy.v1`）再走同一套 warming → caught up → blocked → release terminal → active，只能选中已 caught-up 的 route，任何时刻只有一个权威 writer、只有一个权威 status document。phase-less 的 route（旧二进制写入）视为 ACTIVE。
    **epoch fence**：`ProjectionScopeStatusDocument` 实现 `IProjectionRouteFencedReadModel`，`ProjectionWriteResultEvaluator` 在同一 source version 上只允许严格更高 route epoch 的写入 takeover（Applied），更低 epoch 为 Stale，相同 epoch 必须逐字节相同（Duplicate）否则 Conflict；跨 version 仍以更高 version 为准（不知道 route 的旧二进制 epoch=0 仍可把 document 推进）。这与 document 写入原子评估，是 #3476 hard gate 的 same-version takeover；mixed old/new binary 由 `ProjectionScopeStatusMixedBinaryTests` 以真实旧版 projector 代码（b64c96a45，无 route）与 8d47b5e5 形态（带 route、epoch 1）为 fixture 验证 rolling forward / delayed legacy delivery / rollback-drain 全程无 Conflict。
    terminal materializer 是独立 actor kind 与独立 protobuf state（旧二进制不能激活或修改），只消费 source scope 现有的 `CommittedStateEventPublished`，只在 committed `status_route` 命名其 contract 且处于 writing phase（ACTIVE/BLOCKED/phase-less）时写入，且只在 terminal outcome（除 Received / Attempted / Staged 之外的 source event）上写一次；它不产生 Received / Attempted / WatermarkAdvanced 记账流；store 抛异常时先提交 actor-owned `ProjectionScopeStatusWriteDeferredEvent`（typed `failure_kind`：transient / rejected；旧二进制留下的 Unspecified 按 transient 恢复）再确认 envelope（重试已 durable 且 actor-owned）；transient 失败通过 durable callback scheduler 做事件化退避重试（1s/5s/30s/2m/10m，之后以 10m 节奏持续；retry callback 必须同时匹配 pending 的 source coordinate 与 `attempt == pending.attempts`，延迟到达的旧 attempt 不得用更短退避覆盖 durable retry 状态），store 恢复后无需新 source event、手工 ensure 或 actor deactivation 即自动写入并以 `WriteRecoveredEvent` 清除 pending；连续 5 次失败进入显式可观测的 stalled 态（`WriteStalledEvent`、`pending_write.stalled`、error log + `IProjectionFailureAlertSink`），重试仍继续；同版本 Conflict / Gap 永不推进投递：observed 路径上 materializer 记录 error、告警并抛 `ProjectionScopeStatusWriteRejectedException`——不提交任何事实，observation 失败由 provider 重投且 target checkpoint 不前移；durable retry 路径上转为 rejected pending，以 10m 封顶节奏保持可重试（同 bytes 无法自愈，但事实保持可见，直到更高版本的成功写入清除它），告警每个 source 只发一次；source 已 released 且 detached 时 materializer 随之 release，但只在没有 pending write 时。legacy shadow projector 只在 route 选中它时写入（无 route、rollback 后的 legacy route writing phase、或 terminal route 仍在 WARMING）。

### 5.1 Projection-driven Split / Merge / Re-key

Projection-driven bootstrap 只服务 actor 事实拥有者变化的演进：split、merge、re-key、replace。它不是查询优化，也不是 lazy state migration 的替代品。完整 actor 演进判定树见 [actor-evolution.md](actor-evolution.md)。

v1 non-goal：复杂 owner-change bootstrap（split / merge / re-key / replace 的生产级编排、批量迁移、回滚与清理策略）不在 v1 范围内，延后到 v2 设计。v1 只锁定边界：查询与 read adapter 不允许通过 query-time replay、临时 state rebuild、projection materialization 或 projection priming 来补齐这类 owner-change 场景。

口径：

1. Split：旧 actor 的 committed fact 通过 projection materialize 出 bootstrap 输入；新 actor 必须提交自己的 domain event 后才成为新事实拥有者。
2. Merge：多个旧 actor 的 committed fact 只能作为 bootstrap 输入；聚合后的事实必须由新的 aggregate actor 拥有。
3. Re-key：旧 key 到新 key 的关系必须显式建模为 re-key redirect；调用方不得解析 actorId 字符串或把追踪 ID 当目标身份。
4. Replace：旧 owner 不再承载当前业务事实时，新 actor 必须提交自己的 domain event；旧 actor 只能通过显式 retire cleanup 退出。
5. Retire cleanup：旧 actor / 旧 readmodel / 旧索引的退役必须有显式清理语义，不能留给 query path 做“如果旧数据还在就忽略”的临时判断。
6. Bootstrap 输入只来自 committed domain event、committed state publication 或同源 durable feed；不得订阅 command、self continuation 或 actor 内部 state mirror 临时结构推测完成态。

禁止项：

1. 禁止 query-time replay / bootstrap：query 方法不得读取 `IEventStore`、重放事件、临时重建 actor state 或补跑 projection 后再返回。
2. 禁止 query-time priming：query/read adapter 不得激活 projection、ensure session、创建 actor、修复 index 或触发生命周期操作。
3. 禁止把 bootstrap 当成新的 core phase：本口径不新增 envelope kind、pipeline phase、actor 类型或 proto 字段；它只约束现有 projection materialization 与 actor-owned fact 的使用方式。
4. 禁止用 readmodel 反向定义业务事实：readmodel 只证明某个权威版本已物化可见，不决定 split / merge / re-key 的业务完成。

## 5.2 编排减重落地（当前实现）

1. CQRS 命令侧已统一为：
   `ICommandDispatchService<TCommand, TReceipt, TError>`（宿主入口） +
   `DefaultCommandDispatchPipeline<TCommand, TTarget, TReceipt, TError>`（标准骨架） +
   `ICommandTargetResolver<TCommand, TTarget, TError>`（目标解析） +
   `ICommandObservationLifecycle<TCommand, TTarget, TReceipt, TError>`（交互式 live observation 启动） +
   `ActorCommandTargetDispatcher<TTarget>`（`IActorDispatchPort` dispatch） +
   `ICommandReceiptFactory<TTarget, TReceipt>`（accepted receipt）。
2. Workflow 命令侧在此骨架上提供领域特化：
   `WorkflowRunCommandTargetResolver`（workflow source 解析） +
   `WorkflowRunObservationLifecycle`（attach-only projection/live sink 绑定；不做 pre-dispatch activation） +
   `WorkflowRunAcceptedCommandTargetResolver`（accepted-only receipt 路径） +
   `WorkflowRunAcceptedReceiptFactory`（receipt） +
   `ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>`（SSE/WS 交互入口） +
   `DefaultCommandDispatchService<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError>`（accepted-only facade，复用同一 command skeleton，不持有 live sink） +
   `ICommandDispatchService<WorkflowResumeCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>` / `ICommandDispatchService<WorkflowSignalCommand, WorkflowRunControlAcceptedReceipt, WorkflowRunControlStartError>`（run control 命令入口），以及 `ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>`（fork/run resume-from-step 命令入口）。
3. Scripting 命令侧已形成同一套 CQRS 接入模型：
   `RuntimeScriptEvolutionInteractionService`（generic interaction facade） +
   `ICommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>`（演化提案入口） +
   `ScriptEvolutionCommandTargetResolver` / `ScriptEvolutionObservationLifecycle` / `ScriptEvolutionEnvelopeFactory` / `ScriptEvolutionDurableCompletionResolver`（领域特化策略） +
   `ScriptingActorCommandTarget + AddSimpleScriptingCommandDispatch<...>`（definition/runtime/catalog 命令统一骨架） +
   `IScriptRuntimeProvisioningPort + RuntimeScriptProvisioningService`（runtime lifecycle 归 runtime 端口，命令链只负责 dispatch）。
4. Projection 端口实现已拆分为：
   `WorkflowExecutionProjectionPort`（投影端口） +  
   `WorkflowExecutionCurrentStateQueryPort` / `WorkflowExecutionArtifactQueryPort`（查询端口实现） +  
   `EventSinkProjectionLifecyclePortBase<>`（通用 session port 基类） +  
   `ProjectionSessionScopeActivationService<WorkflowExecutionRuntimeLease, WorkflowExecutionProjectionContext, WorkflowExecutionSessionScopeGAgent>`（激活） +  
   `ProjectionSessionScopeReleaseService<WorkflowExecutionRuntimeLease, WorkflowExecutionSessionScopeGAgent>`（释放） +  
   `ProjectionMaterializationScopeActivationService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationContext, WorkflowExecutionMaterializationScopeGAgent>`（durable 激活） +  
   `ProjectionMaterializationScopeReleaseService<WorkflowExecutionMaterializationRuntimeLease, WorkflowExecutionMaterializationScopeGAgent>`（durable 释放） +  
   `ProjectionSessionEventHub<WorkflowRunEventEnvelope>`（session stream hub） +  
   `WorkflowProjectionReadModelUpdater`（读模型元信息） +  
   `WorkflowExecutionCurrentStateQueryPort` / `WorkflowExecutionArtifactQueryPort`（查询映射；query 直接实现 read adapter，不再复用通用 query-port 基类）。
   `WorkflowExecutionMaterializationScopeGAgent` 保持既有 materialization kind，并以 schema v1 的纯 clone migration 绑定 `ProjectionIncrementalGraphV1` fleet admission；durable in-flight observation recovery 只在 runtime-owned schema context 含唯一 exact v1 adoption receipt 时启用。adoption receipt 只充当 activation fence；graph route 与 cutover phase 仍由 scope actor 的 protobuf 持久态唯一拥有。通过 fence 后，scope actor 才能推进 `Requested -> CandidateBuilt -> GoldenVerified -> Activated`：在隔离的 v2 physical namespace 做有界 full candidate、校验 report 的精确 `StateVersion + LastEventId` 与 golden graph、重新读取 fresh fleet admission proof，最后以 committed scope event 单调切换 route epoch。candidate 过期会回到 `Requested`，不会在 query path 修复。显式 rollback 复用同一 saga：`RequestProjectionMaterializationCutoverCommand` 只能指定不同的 versioned physical namespace 和紧邻的下一 route epoch；目标 namespace 必须重新追平当前 authoritative report 并通过 golden/fleet 校验后才能成为唯一 active route。
   激活后，report 与 graph 是同一 committed-event Projection Pipeline 内的两个独立完成条件：report 继续每版本物化；graph 使用 owner-scoped `ProjectionGraphDelta` 原子提交 mutation 与 source coordinate。report exact duplicate 不得跳过 graph 重试，graph exact duplicate 用于恢复“graph 已提交、scope watermark 尚未提交”的 crash boundary。普通非图事件只更新 run/version node；step/topology 事件只更新被触及的有界节点/边；未触及元素保留此前 timestamp，candidate 初建元素统一采用该 report 的 `UpdatedAt`。`NEXT` 使用 source-step 稳定 edge id，目标缺失时以 typed pending edge 保存，并在目标节点出现后 promotion；rewire 复用同一 edge id。
   Graph export query 由 scope actor 的 committed route（物化在 `ProjectionScopeStatusDocument.active_materialization_route`）决定读哪一个 store，这是 route-directed read，不是 fallback：status document 缺失或 route 仍是 compatibility/legacy route，说明该 owner 从未 cutover，其 graph 只存在于 legacy scope graph store，query 继续从 legacy `IProjectionGraphStore` 读取（与 incremental route 出现前完全一致，返回 DTO 不带 route fingerprint）；route 是 incremental route 时先读 atomic owner snapshot、再复读 status route，route epoch 变化、snapshot route 不匹配、source provenance 缺失或 snapshot 缺失都 fail closed，禁止回退 legacy graph、dual-read merge、query-time replay、priming 或 repair。incremental route 下返回 DTO 显式携带 route fingerprint 与精确 source coordinate。versioned subgraph 的 `take` 是 edge 预算：按 snapshot 稳定 edge id 顺序逐层（BFS，按 inbound / outbound / both 方向）选取至多 `take` 条端点均存在于 snapshot 的 edge，返回 nodes 恰为 root 加所有已返回 edge 的端点（至多 `take + 1` 个），任何返回 edge 的 `fromNodeId` / `toNodeId` 都必须出现在返回 nodes 中，遍历结果确定。
   Incremental delta 每次都幂等 upsert root actor node、run node 与 `OWNS` edge（有界常量），因此在 scope 已 cutover 之后开始的新 run 也能从其 actor 可达；`WorkflowCommandObservedEvent` 是唯一改变 `LastCommandId`（因而重键整张 owner graph）的 committed fact，incremental route 在该事件上执行一次 bounded full replacement（`RepairOrCutover` mode），其余事件保持 O(touched) delta。`RepairOrCutover` delta 的语义是“upsert 集合即完整期望 owner graph”：store 在同一 apply 事务内（owner-state lock 之下）删除该 owner 所有不在期望集合中的 node / live edge / pending edge（node id 与 edge id 是各自独立的身份空间，同一 token 可同时命名一个 node 和一个 edge，按类别分别对账），并在形成 effective delta 之后、任何 mutation 之前校验 upserts + explicit deletes + 生成的 stale deletes 的总数不超过 `ProjectionGraphDeltaContract.MaximumRepairOrCutoverMutationCount`（20 000，materializer 与 store 单一常量）：超限以 `MutationBoundExceeded` 原子拒绝（watermark、snapshot、event id 绑定均不变；duplicate / conflict 判定仍先于该校验执行），producer 不得读 pre-write snapshot 推导 delete 列表——delta fingerprint 因此只由 report 等稳定输入决定，graph 已提交但 scope watermark / CandidateBuilt phase 未落盘时的重放收敛为 `ExactDuplicate`，同一 eventId 但期望 graph 不同仍为 `EventIdConflict`；InMemory 与 Neo4j 语义一致（conformance suite `AssertRepairReplacementAsync`）。Neo4j provider 为 relationship 建立 `(scope, edgeId)` RANGE index，使 delta 内按 edge id 的读取/删除/rewire 都是 index seek；为 EdgeIdentity 建立并等待 ONLINE 的 `(physicalNamespace, projectionOwnerId)` RANGE index，使 repair/cutover 的 owned-element 读取与 owner snapshot 读取按 owner seek；`ApplyDeltaAsync` 与 legacy write 一样经过 provider-local write telemetry（operation=`apply_delta`，result 为有界 disposition 名），nodeCount/edgeCount 为本次 delta 的 mutation 数。
5. CI 增加编排类体量守卫与 capability 边界守卫：关键编排类的非空行数与直接依赖数有上限，`workflow/scripting` 外部入口不得回退到私有 lifecycle 主链。

## 5.3 Envelope / Annotation 口径（防理解偏差）

1. `EventEnvelope.Propagation` 与 `EventEnvelope.Runtime` 属于包络级上下文，用于传播/追踪/投递，不作为业务完成语义主来源。
2. `StepCompletedEvent.Annotations` 属于业务事件注解，Maker/Connector/Parallel 等模块信息写入此处。
3. ReadModel 聚合使用 `StepCompletedEvent.Annotations`，并落到 step `CompletionAnnotations` 与 timeline `Data`；控制流语义则走 typed 字段。
4. 实时输出是否带业务 annotations 由 mapper 明确定义；当前默认不自动透传 `StepCompletedEvent.Annotations` 全量字段。

## 5.4 Workflow normalized fork seed

`WorkflowExecutionCurrentStateDocument.normalized_fork_seed` 是 `workflow.run` committed normalized execution state 的 typed 查询副本。Projector 只 capture actor 已提交的 canonical values、bindings、completed ledger 与 current input reference，不在读侧推导第二套执行状态机。

1. Normalized document 的 query mapper 可以展开兼容 `Variables / CompletedStepIds` 供既有查询 DTO 使用，但 fork command handoff 必须保留 typed seed，不能把展开后的字符串 map 当权威输入。
2. Caller overrides 与 normalized seed 分字段传输；目标 kernel 验证引用完整性后应用 overrides，禁止同一 fork seed 同时携带 normalized values 与 expanded legacy variables。
3. Legacy document 未携带 `normalized_fork_seed` 时，继续读取既有 fork seed variables/completed ids。Projection 不为 legacy state 制造 canonical value identity 或 provenance。
4. 该扩展仍遵守 current-state 单调覆盖与 query-time priming 禁止项；fork 查询只读已物化的权威版本，不同步 replay actor events 或刷新投影。

## 5.5 Workflow read model 显式 index mapping

Workflow read model 的 Elasticsearch mapping 由各自的 `IProjectionDocumentMetadataProvider`（`src/workflow/Aevatar.Workflow.Projection/Metadata/`）显式声明，不依赖 dynamic mapping 碰巧得到可查询的字段：query port / reconciler / startup guard 会 filter、search 或 sort 的字段一律显式 `keyword` / `date`（proto enum 以 protobuf-JSON 名称存储并按 `keyword` 精确匹配，filter 值必须用 `ProjectionDocumentValue.FromProtoEnum` 生成同一形式，InMemory 与 Elasticsearch 由此口径一致）；opaque payload 文本（workflow yaml、input / output / error、prompt、tool arguments 等）`text` + `index:false`，只进 `_source` 不进倒排；从不查询的子树（admission plan、connector approval、input file refs、failed attempt / vote / file 材料）与所有 proto map 是 `object` + `enabled:false`。任何 mapping 变化都会改变 provider 的 schema fingerprint，并只通过既有 fingerprint / reindex / alias lifecycle（startup reconcile）滚动上线，禁止 query-time 修复；`test/Aevatar.Workflow.Host.Api.Tests/WorkflowReadModelEffectiveMappingTests.cs` 审计 augmented 后的有效 mapping 并 pin 当前 fingerprint。

## 6. 宿主接入规范

当前宿主：

1. `src/Aevatar.Mainnet.Host.Api/Program.cs`
2. `src/workflow/Aevatar.Workflow.Host.Api/Program.cs`

接入约束：

1. 必须使用 `AddAevatarDefaultHost(...)` + `UseAevatarDefaultHost()`。
2. Mainnet 与 Workflow Host 必须接入 `builder.AddAevatarPlatform(...)`（统一装配 Workflow capability、Scripting capability、AI features 与 Workflow AI projection extension）。
3. Mainnet 通过 `builder.AddAevatarPlatform(options => { options.EnableMakerExtensions = true; })` 启用 Maker 插件。
4. 禁止 `AddMakerCapability()` 与 `/api/maker/*` 独立路由模型。

## 7. Runtime 口径

1. 当前默认 `ActorRuntime:Provider=InMemory`（开发/测试）。
2. `ActorRuntime` 不是额外的“第二套通道”，而是构建在 stream 之上的 Actor 语义层，负责寻址、激活、邮箱串行与拓扑。
3. 生产目标：分布式 Actor Runtime + 非 InMemory 持久化（state/event/read model）。
4. 本口径下 `InMemory` 与 `Actor Local` 均不作为架构扣分项。

## 8. 门禁与验证

最低验证：

1. `bash tools/ci/architecture_guards.sh`
2. `dotnet build aevatar.slnx --nologo`
3. `dotnet test aevatar.slnx --nologo`
4. `bash tools/ci/test_stability_guards.sh`

关键门禁：

1. 禁止 `GetAwaiter().GetResult()`
2. 禁止 `TypeUrl.Contains(...)` 路由
3. 禁止 Host/Infrastructure 直接 `AddCqrsCore(...)`
4. 禁止独立 Maker Capability 工程与路由回流
5. 强制 Mainnet 插件化装配 Maker
6. 默认全量测试只承载快速主链路；分钟级脚本自治演化回归独立执行，避免把慢测静默耗时混入常规门禁。
