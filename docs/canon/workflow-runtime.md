---
title: "工作流引擎设计与实践"
status: active
owner: eanzhao
---

# 工作流引擎设计与实践

> 状态更新（2026-03-08）
>
> 当前权威实现已经从“`WorkflowGAgent + WorkflowLoopModule`”演进为“`WorkflowGAgent + WorkflowRunGAgent + WorkflowExecutionKernel`”。
>
> - `WorkflowGAgent` 只承载 definition facts，不再直接推进 run。
> - `WorkflowRunGAgent` 是单次 run 的唯一写侧事实源。
> - `Foundation` 统一只保留 `IEventModule<TContext>`；workflow step 模块实现 `IEventModule<IWorkflowExecutionContext>`，并通过 `WorkflowExecutionBridgeModule` 接入 Foundation pipeline。
> - `IEventContext` 是共性根接口；`IEventHandlerContext` 与 `IWorkflowExecutionContext` 只在能力上分化。
> - `WorkflowExecutionKernel` 已替代 `WorkflowLoopModule` 负责主循环推进。
>
> 下文保留了大量 DSL 与原语说明；凡提到旧的 `WorkflowLoopModule`/`WorkflowGAgent` 执行职责，均以上述现状为准。

这份文档回答三个问题：

1. 为什么需要工作流引擎（`Aevatar.Workflow.Core` + `IEventModule<TContext>`）？
2. 代码里怎么实现的？
3. 实际开发时怎么用？

---

## 一、它解决了什么问题？

普通 Agent 模式下，收到事件 -> 写固定代码处理 -> 发布下一个事件。直接但有两个限制：

- **流程变更成本高**：改一次步骤顺序就要改代码
- **复用性差**：`if/while/并行/投票` 这些控制逻辑会重复出现在很多 Agent 里

工作流引擎的思路：

- 把流程控制能力做成通用模块（Event Modules）
- 把业务流程写成 YAML（可配置）
- 让 `WorkflowGAgent` 负责 definition 绑定，让 `WorkflowRunGAgent` 在运行时装配模块并驱动流程

一句话：**硬编码 Agent 适合固定逻辑，工作流适合可编排、可调整、可复用的流程逻辑。**

口径先说清楚：

- workflow 运行主链路建立在 `EventEnvelope` 消息流之上。
- `EventEnvelope` 在这里是 runtime message envelope，不等于 Event Sourcing 的领域事件记录。
- `WorkflowRunGAgent` / `WorkflowGAgent` 只有在显式 `PersistDomainEventAsync(...)` 时，才把领域事实写入 EventStore。
- 定时触发属于 Aevatar workflow runtime 能力；NyxID 只保留 credential/proxy/audit 职责，ORNN 只保留 deterministic skill/payload-builder 职责。
- 认证 webhook start-run 是 Host/Adapter 触发面：raw JSON、HMAC、route binding 与 prompt mapping 留在 Host；进入应用层后只使用 typed `WorkflowChatRunRequest` 与 `WorkflowExternalIngressContext`。

---

## 二、核心概念

### WorkflowGAgent / WorkflowRunGAgent

当前实现下，workflow 职责拆成两个 Actor：

1. `WorkflowGAgent`
   - 持有 workflow YAML（definition facts）
   - 解析 YAML、校验结构、维护版本与编译结果
   - 作为 definition/source actor 被解析与绑定
2. `WorkflowRunGAgent`
   - 一次 run 一个 actor
  - 按 `roles` 创建 run-scoped role actor 树；`agent_kind` 由 Foundation runtime 解析，省略时默认 `workflow.role-agent`
   - 通过依赖推导（`IWorkflowModuleDependencyExpander`）确定所需模块，经 `WorkflowModuleFactory` 创建并安装
   - 收到 `ChatRequestEvent` envelope 后发布 `StartWorkflowEvent`
   - fork/resume-from-step seed 只走 request-level `WorkflowChatRequestEvent.fork_seed -> StartWorkflowEvent.fork_seed`；run bind 只表达 definition/run binding，不携带 seed。
   - 由 `WorkflowExecutionKernel` 推进 `StepRequestEvent -> StepCompletedEvent -> WorkflowCompletedEvent`

```
BindWorkflowDefinition(yaml)
  -> WorkflowParser.Parse (YAML -> WorkflowDefinition)
  -> WorkflowValidator.Validate (结构校验)
  -> BindWorkflowRunDefinition(yaml/run binding + capability admission plan)
  -> InstallCognitiveModules on WorkflowRunGAgent:
       IWorkflowModuleDependencyExpander[]: 推导模块名集合
       WorkflowModuleFactory: 按名称创建实例
       IWorkflowModuleConfigurator[]: 配置实例
       WorkflowExecutionBridgeModule: 接入 Foundation 事件管线
```

### External operation admission proof handoff

NyxID external operation 使用一条 actor-owned proof 主链，step selector 是 `PublishedEndpoint(endpoint_id)` 或 `AuthoredRequest(request_contract_digest)`。`PublishedEndpoint` 持久化 `NyxIdOperationSelector { user_service_id, endpoint_id }`；definition admission 读取 NyxID `/api/v1/mcp/config`，shared typed adapter 只接受 exact non-generic UserService endpoint，并生成 server-owned service slug、method、path template、parameter/body contract、typed response policy、source stamp 与 digest。`AuthoredRequest` 持久化 typed request contract proposal；definition admission 只读取 exact UserService inventory，且必须由 authenticated binder 确认当前 digest 与 risk，actor 才能持久化 `NyxIdExplicitRequestGrant` 并提交 proof。NyxID `catalog_digest` 是 `PublishedEndpoint` 的 normalized descriptor revision；Aevatar 不保存另一份 UserService/OpenAPI catalog，也不以 observation time 或本地 counter 冒充 revision。

Studio 的 authoring draft 是 proof 主链之前的编辑态，不是运行态。`/api/chat` 先发现 structured external capability；没有 exact descriptor 时，可以查询官方文档或推导最小 authoring shape，但只能保存不含 step-level `capability` 的 unresolved YAML。`aevatar_create_member_workflow_draft` 创建或复用 Team workflow member shell，并使用独立稳定 draft identity 保存 YAML；返回 canonical Studio URL、`runnable=false`、`binding_status=not_bound`、Accepted command receipt、`projection_pending` readiness 和 `NYXID_OPERATION_SELECTION_REQUIRED`。

unresolved draft 不调用 bind、schedule、provision、publish、run 或 `nyxid_proxy`。搜索结果、推测 API 形状、display text、slug、method 或 path 都不能构造 selector/proof。后续有 exact MCP descriptor 时，作者才能写入 `PublishedEndpoint` 的 `user_service_id + endpoint_id` selector；已知静态 HTTP contract 时，作者可写入 `AuthoredRequest` typed request contract proposal，但仍必须经 exact inventory admission 与 authenticated binder grant。两条路径都重新进入正式 definition admission；admission 通过后仍需独立 binding/publication，运行时继续按 committed proof 重验。

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
    A{"Step selector"}
    A -->|"PublishedEndpoint(endpoint_id)"| B["MCP descriptor"]
    A -->|"AuthoredRequest(request_contract_digest)"| C["Exact inventory at bind"]
    C --> D["Authenticated binder confirmation + NyxIdExplicitRequestGrant"]
    B --> E["Actor-owned WorkflowGAgent v4 invocation_admissions"]
    D --> E
    E --> F["BindWorkflowRunDefinitionEvent.capability_admission_plan"]
    F --> G["WorkflowRunState.capability_admission_plan"]
    G --> H["StepRequestEvent.external_invocation"]
    H --> I["WorkflowToolExecutionRequest.InvocationAdmission"]
    I --> J["AgentToolExecutionContext.OperationAdmission"]
    J --> K{"Committed selector"}
    K -->|"PublishedEndpoint"| L["Runtime MCP endpoint-digest revalidation"]
    K -->|"AuthoredRequest"| M["Validate proof + grant; no MCP/OpenAPI/inventory re-read"]
    L --> N["NyxIdAdmittedRequestBuilder"]
    M --> N
    N --> O["Exact NyxID Proxy HTTP request"]
```

`WorkflowRunActorPort` 从权威 definition binding 复制 plan 到 `BindWorkflowRunDefinitionEvent`，`WorkflowRunGAgent` 把它提交到本 run 的 state。`WorkflowExecutionKernel` 与 admission 共用 compiler，为 ordinary、nested、`foreach`/`for_each`/`foreach_llm` 和 `while`/`loop` 派生同一稳定 call-site；`ToolCallModule` 只从 run actor state 解析该 call-site 的唯一 proof。missing plan、missing/duplicate call-site、selector mismatch 或 tool mismatch 都在 dispatch 前 fail closed。foreach backpressure、while state 与 tool approval suspend/resume 都复制同一个 typed invocation，不按动态 item id 猜 proof。

AI adapter 把当前 proof 映射到 provider-neutral `AgentToolExecutionContext.OperationAdmission` with one typed identity union: `PublishedEndpoint { endpoint_id }` or `AuthoredRequest { request_contract_digest }`. An authored digest is never stored in `endpoint_id`, and no synthetic/empty operation ID exists. The proof carries typed `risk / approval / enforcement_owner / allowed_execution_modes`; authored requests additionally carry their matching `NyxIdExplicitRequestGrant`, and both participate in the admission digest. The single admitted-request builder accepts only declared `path_params`、`query`、`headers`、`body`; response mode is fixed by the proof. NyxID Proxy wire receives the server-derived route constraint, exact `user_service_id`, and HTTP request; endpoint IDs and digests never enter the wire.

Dynamic LLM exposure、definition admission 与 runtime authorization are separate policies. `nyxid_operation` admission reads its MCP descriptor; `nyxid_request` admission reads only authenticated exact UserService inventory and requires the independent binder grant. Neither selector expands normal current-turn tool exposure. `Shadow` only records a proofless/invalid-policy decision; `Enforce` rejects a managed workflow before token resolution, file ingress, or proxy HTTP when proof, grant, execution mode, or local digest is invalid.

Runtime does no raw OpenAPI read, definition-actor/read-model/event-store side read, admission refresh/priming, or process-local proof registration. `PublishedEndpoint` retains current MCP exact endpoint-digest revalidation. `AuthoredRequest` reads neither MCP nor UserService inventory: before dispatch it validates its committed plan, request identity, matching explicit grant, execution mode, and local digests, then sends exactly one exact NyxID proxy route using exact `user_service_id` and the server-derived route slug constraint. There is no slug-only fallback.

### Admission v4 与 forward-only migration

`external-capability-admission.v4` 只以 call-site scoped `invocation_admissions` 表达当前事实。proto field 4 `external_capabilities` 是 deprecated v2 deserialization slot；v4 creation 保持为空，v4 validation 对非空值 fail closed，禁止双事实源。Published-endpoint proof 必须带 `NYX_ID_MCP_CONFIG` source stamp; authored-request proof 必须带 `NYX_ID_USER_SERVICES` source stamp and a matching typed binder grant. Durable authored read additionally requires `DURABLE_AUTHORIZATION_CATALOG`; no source stamp can authorize a durable write/destructive request.

升级采用 forward-only 语义：旧 serving definition/run 不热替换；持久化 v2/v3 plan 一旦进入 reprepare、publish 或 rebind，就在解析旧 authoring 前返回 typed `CAPABILITY_ADMISSION_REBIND_REQUIRED` 与 rebind remediation，要求使用 `PublishedEndpoint(endpoint_id)` 或带独立 binder grant 的 `AuthoredRequest(request_contract_digest)` 重新 admission 并创建 v4 revision。runtime 不把旧 raw route 或 OpenAPI identity 当 fallback，也不 query-time 迁移。明确的 `schema_version` 字符串是版本边界。

Mainnet 的 `Enforce` startup gate 只读 actor-scoped current-state read models，不 activate、prime、replay 或 mutate projection。它分页校验所有未被 typed deployment state 明确标记为 deactivated 的 definition binding，以及所有非 `completed / failed / stopped` run current state；每个对象都必须携带完整且 digest-valid 的 v4 plan。已 deactivated service definition 可作为历史 revision 留存；缺 deployment relationship、active/failed/unknown deployment、普通 definition 和非终态 run 一律保守校验。失败使用稳定 blocker `CAPABILITY_ADMISSION_REBIND_REQUIRED`，仅含总数与每类最多八个 actor ID sample。`Shadow` 不执行 startup inventory scan。

### Local artifact compatibility before actor lifecycle

Every publish, deployment, chat, schedule, and fork producer supplies a typed, non-`Unspecified` `ExpectedExecutionMode`. The value is protocol evidence owned by that producer; it is never inferred from `scheduleId`, `runOrigin`, an actor ID, a route position, or the admission plan being checked. Definition and run bindings persist the same value, and a run cannot change mode after its first binding.

Before creating, linking, binding, repairing, registering, or dispatching a workflow actor, the Application preflight parses the root YAML and every distinct inline workflow with the canonical parser, evaluates external invocations with the canonical dependency evaluator, and validates the persisted capability plan locally. It performs no network call, catalog lookup, source-freshness check, event replay, projection priming, repair, or invocation-time `RevalidatePersistedAsync`. The authoritative `WorkflowRunActorPort` repeats this pre-mutation gate; exact service-run dispatch also performs it before service-run registration so a deterministic rejection leaves zero Run artifacts.

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
flowchart LR
  A["Typed selection or persisted workflow"] --> B["Committed read models"]
  B --> C["Local admission"]
  C -->|"accepted"| D["Actor inbox"]
  C -->|"rejected"| E["Typed repair action; zero Run"]
```

The stable local outcomes are deliberately bounded and safe:

| Condition | Stable code | Safe message | Repair action |
| --- | --- | --- | --- |
| Invalid root or inline YAML | `WORKFLOW_DEFINITION_INVALID` | Workflow definition is invalid. | Update and rebind workflow. |
| Retired direct NyxID authoring | `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED` | Workflow uses a retired NyxID tool contract. | Update and rebind workflow. |
| Missing or legacy plan | `CAPABILITY_ADMISSION_REBIND_REQUIRED` | Workflow capability admission must be rebuilt. | Update and rebind workflow. |
| YAML/plan or execution-mode mismatch | `CAPABILITY_ADMISSION_REBIND_REQUIRED` | Saved workflow and capability admission no longer match. | Update and rebind workflow. |

The exception exposes only the stable code and safe message. YAML, selectors, credentials, upstream response bodies, exception types, and stack traces do not enter state, projection, logs, or API summaries.

### Event Module

可插拔的事件处理器（实现 `IEventModule<TContext>`），四个要素：

- `Name`：模块名（如 `"llm_call"`）
- `Priority`：数值越小优先级越高
- `CanHandle(envelope)`：判断是否处理该事件
- `HandleAsync(envelope, ctx, ct)`：处理逻辑

当前分层里：

- `Foundation` 管线使用 `IEventModule<IEventHandlerContext>`
- workflow step 模块使用 `IEventModule<IWorkflowExecutionContext>`
- 两者共享 `EventEnvelope` 与 `IEventContext` 根抽象

模块和静态 `[EventHandler]` 方法一起进入统一事件管线。可以在不改业务代码的情况下替换流程行为。

### Scheduled Dispatch API

第一版定时触发只提供 API 配置面，不提供 UI。主 API 路径为 `/api/schedules`，支持 create/update/enable/disable/delete/list/get/preview/run-now。旧 `/api/workflow-schedules` 兼容入口已删除；workflow 内部调度只通过 actor-owned scheduled dispatch 应用契约进入统一主链路。

运行边界：

- `ScheduledDispatchGAgent` 是每个 schedule 的唯一写侧事实源，持有 cron、timezone、enabled、typed target descriptor、dispatch headers、next fire lease 与 recent fire records。
- workflow 内部的 `self_reschedule` / `schedule_workflow` step 只向 `ScheduledDispatchGAgent` 发送幂等 ensure 命令；跨 run schedule fact 不归 workflow run actor 持有。创建不同 schedule 时，step 只能复用可信 HTTP ingress 或 Scheduled Dispatch 写入 run actor 的 typed caller NyxID authority，并把该 subject + capability scope 映射为 `SenderNyxId` source；`scopeId` 只表达 Aevatar 资源边界，禁止把它构造成 NyxID `external_user_id`。没有 typed caller authority 的 legacy/untrusted run 必须 fail closed。更新当前 run 所属 schedule 时省略 auth，由 schedule actor 保留其已有 typed source，禁止用本次 run 的 bearer 覆盖 schedule auth。
- workflow schedule ensure 同步结果只表示 `accepted` command receipt（schedule id、schedule actor id、command id、correlation id）；readmodel freshness 通过 projection/readmodel 观察，不能由 step completion 暗示强一致。
- 外部 submit/poll job 必须建模为 split-run 模板，而不是 workflow core primitive。submit run 提交一次外部 job 并把 `job_id`、`idempotency_key`、确定性 `schedule_id`、poll cadence、deadline 与 attempt 预算交给 poll workflow；`ScheduledDispatchGAgent` 持有 schedule fact；每个 poll run 查询一次状态；终态分支用同一 `schedule_id` 幂等 ensure `enabled=false` 来停止后续 poll。
- `await_job` / `async_job` 不是 runtime 原语。`wait_signal` 最多持有一个 actor-owned durable callback/signal lease，当前上限为 24 小时；它不用于把 submit/poll job 扩成同 run long polling。poll handoff 的业务字段必须在 workflow 参数或 prompt payload 中显式表达，不能塞进 dispatch `Headers` 或泛化 `metadata`。
- 定时唤醒走 `ScheduleSelfDurableTimeoutAsync`，在 Orleans runtime 下由 durable callback/reminder 机制承载；回调只向 schedule actor 发 fire command，不在中间层保存 schedule 状态。
- schedule actor 只负责计算下一次 fire、生成幂等 key 并投递 prepared target envelope；workflow、GAgent service invocation 与 scripting 目标准备由 application/infrastructure adapter 承载，不进入 schedule actor core。
- workflow schedule 的 `WorkflowName`、`Prompt`、`ScopeId` 仅存在 typed workflow target descriptor 中；service invocation 与 envelope target 使用各自 typed target descriptor；dispatch `Headers` 只保留传输扩展。
- service invocation schedule auth 支持且仅支持一个 active typed credential source：HTTP/API 只接受 `senderNyxId` 或 `scopeOwnerNyxId`；trusted internal provisioning 还可写入 `durable` 或 `scheduledInvocationAgentKey` typed reference。HTTP/API、application service、actor port 与 runtime dispatch 均不接受新的 `durableSenderBearerToken`；该 proto 字段仅作为旧事件读取入口保留，reducer 必须把旧 raw bearer 丢弃并标记 legacy blocked，fire 时失败关闭并要求用 typed credential source 重新配置。raw bearer 不得写入 schedule actor current state、dispatch command、`ScheduledDispatchDocument` read model、query response 或 list/get API 回显。
- service invocation schedule 的 required-credential 判定由统一 `IScheduledDispatchCredentialRequirementPolicy` 承担。Application 在 create/ensure/update 进入 actor 前按 typed target kind 校验；ensure/update 省略 auth 时只能用既有 schedule readmodel 的 credential source kind 通过预检，command 仍保持 auth absent，最终由 actor-owned state 完成保留并重新校验。`ScheduledDispatchGAgent` 持久化 `credential_requirement_target_kind` 作为 actor-owned input classification，并在 fire/run-now 使用最终状态重新校验。`Envelope`、`StaticService`、`ScriptingService` 默认允许 no-auth；`WorkflowService` 与 `Connector` 必须带 typed service invocation credential source。Host 只负责把 HTTP body、认证 principal 与 service revision snapshot 映射成 typed config，不保留 endpoint-private binding/exchange gate；revoked binding 与 scope mismatch 仍由 downstream credential exchange fail closed。
- workflow caller state 只保留三种互斥 typed source：direct bearer 的 run-scoped secret reference、tag-7 durable secret reference、或 refreshable NyxID authority。scheduled workflow fire 不交换或持久化 presentation token；它把 typed subject + capability scope 交给 service-dispatch consumer，并在进入 workflow actor state 时归一化为 NyxID authority。connector、每次 LLM dispatch/stream、每次 tool execute 都必须在真实外呼前通过同一个 `IWorkflowCallerAccessTokenProvider` 重新签发 presentation token；首版不缓存、不写回 token，也不回读 schedule actor/event store。缺失或不完整 authority、不可用 provider、binding revoked/scope mismatch 均 fail closed。direct bearer 与 tag-7 durable secret 保持非刷新语义。consumer-first 混部期间，只有包含完整 embedded authority 的旧 scheduled ref 可在恢复边界归一化；authority 缺失的 scheduled ref 不得回退为可调用凭据。caller authority 与 token 都必须从 committed projection/readmodel payload 中移除。
- trusted internal `ScheduledInvocationAgentKey` 已经是 vault-backed credential。workflow fire 必须 exact 复用其 `SecretReference.Ref/Purpose/OwnerScopeKey + ApiKeyId` 构造 borrowed `DurableCallerCredentialRef(SourceKind=ScheduledDispatch)`；dispatch 不得 resolve、复制或重新 put secret，也不得注入 raw credential。borrowed handle 不归 workflow run 所有，因此 dispatch failure 与 run completed/stopped 都不得 revoke；每次 LLM/tool/connector 外呼仍统一通过 `TryGetCallerCredentialAsync` late resolve，使 rotation 生效，并对 revoke、expiry 或 identity mismatch fail closed。
- scheduled `ChatRequestEvent` 只携带 typed caller credential source：vault-backed caller 使用 `caller_durable_credential` handle，refreshable NyxID caller 使用其中的 typed authority。interactive NyxID proxy ingress 可同时接收用途隔离的 delegation execution credential 与 source-readable user bearer；service invocation 必须以 `caller_nyx_id_credential_kind` 和 `caller_source_readable_nyx_id_bearer_token` 分别传递 credential purpose 与 supplemental source credential，禁止借用 `llm_control` / `metadata`，也禁止按 token 相等、header 优先级或 route 字面推断。两者进入 run 后必须分别转换成 run-scoped runtime-secret reference；delegation 只供下游 proxy execution，source-readable bearer 只供 identity/inventory/readiness。caller raw token 与 authority 必须从 committed event、projection/readmodel payload、log 与 API response 中移除，任一 required reference 无法解析时整体 fail closed。没有 handle 的旧 run 继续走 legacy fallback，不热替换。外部 API 请求若自带 `caller_durable_credential` 必须 fail closed；projection/readmodel/log 只暴露 credential source kind。
- workflow fork 的 HTTP/automation 入口只构造 typed `WorkflowForkRunCommand` 并走 `ICommandDispatchService<WorkflowForkRunCommand, WorkflowForkRunAcceptedReceipt, WorkflowForkRunStartError>`；seed 来源读取 `IWorkflowRunForkSeedQueryPort` read model，不走 event-store replay 或 actor state side-read。
- public API identity fields 必须显式区分 `ScheduleActorId` 与 `TargetActorId`：`ScheduleActorId` 表示持有定时配置与 fire 事实的 schedule actor receipt，`TargetActorId` 表示最近一次或摘要中的投递目标；不得用一个 `ActorId` 混用 schedule actor receipt 和目标摘要。
- 幂等 key 格式固定为 `schedule:{scheduleId}:fire:{scheduledFireAtUtc:o}`，并随 scheduled fire dispatch headers 透传。
- schedule 查询只读取 `ScheduledDispatchDocument` read model；API 不读取 actor state，不在 query path replay event store。
- projection 使用 committed `ScheduledDispatchState` current-state payload 物化 read model，版本来自权威 actor committed version。

配置边界：

- cron 使用 standard 5-field format。
- timezone 为空时默认为 `UTC`，非空时必须能被 runtime `TimeZoneInfo` 解析。
- `Headers` 是 command dispatch headers，不用于承载 schedule 核心语义。

### Connected-Service Resource Fetch

Workflow runtime owns the canonical connected-service resource fetch use case. The only workflow-callable tool name is `workflow_connected_service_resource_fetch`; connected-service provider packages do not publish a same-name workflow tool.

运行边界：

- Tool arguments identify a narrow route with typed fields: `provider`、`operation`、`resource_kind`、`message_id`、`resource_key`。
- Workflow infrastructure resolves the route through registered `IWorkflowConnectedServiceResourceFetchAdapter` instances. Unsupported routes fail before any provider call.
- Provider packages only register binary adapters for their own public surface. The first Lark adapter exposes `lark/message_resource_download/image` and `lark/message_resource_download/file`.
- Downloaded bytes must enter workflow storage only through `IWorkflowFileIngressPort` with `WorkflowFileSourceKind.ConnectedServiceResource`; workflow commands, actor state, readmodels, logs, and tool results must not carry raw bytes or base64.
- The tool result is a sanitized `WorkflowFileRef` plus route facts. It does not expose provider response bodies, base64, or downloaded content.

### NyxID Proxy File Artifacts

`nyxid_proxy(response_mode=file_artifact)` is the only v1 public NyxID proxy binary download mode for workflow-managed runs. Missing `response_mode` and explicit `text` keep the existing string proxy behavior.

运行边界：

- `NyxIdProxyTool` owns response-mode parsing. Invalid modes fail closed; v1 does not expose `file_artifact_put`、`nyxid_binary_download`、provider-specific global download tools、`binary_base64` or `data_uri`.
- `response_mode=file_artifact` requires `GET`, no request body, a managed workflow runtime parent from typed `AgentToolRequestContext.Current.WorkflowRuntime`, a caller scope, and a host-registered `INyxIdProxyFileArtifactIngress`.
- The binary response is downloaded through `NyxIdApiClient.ProxyGetBinaryResponseAsync` with `ProxyFileArtifactMaxBytes` defaulting to 25 MiB and capped at 100 MiB. Content-length and streaming reads are bounded before workflow ingress.
- Persistence only goes through `IWorkflowFileIngressPort` with `WorkflowFileSourceKind.ConnectedServiceResource`; `nyxid_proxy` does not stage process-local handles or persist raw bytes itself.
- Success and failure results are structured JSON with `success`、`response_mode`、bounded source diagnostics, and a sanitized `WorkflowFileRef` projection on success. Results must not include raw bytes, base64, data URI, provider response bodies, or durable byte state.

### Workflow File Artifact Lifecycle

Workflow file artifacts use narrow ports with separate responsibilities:

- `IWorkflowFileIngressPort` writes bytes at Host/adapter ingress and returns a typed `WorkflowFileRef`.
- `IWorkflowFileArtifactReadPort` describes or opens an existing descriptor-backed artifact.
- `IWorkflowFileArtifactOwnershipPort` binds owner facts when a workflow run actor later claims an ownerless artifact.
- `IWorkflowFileArtifactCleanupPort` is cleanup-only lifecycle surface. It is triggered by Host background service, while the provider owns physical cleanup decisions.
- `WorkflowMultipartFileInputParser` is the shared Host/adapter boundary parser for multipart file input. It validates form shape and media constraints, returns raw payload JSON, `HasFiles`, and pending file bytes, but does not decide service kind and does not write artifacts by itself.

Runtime boundary:

- The artifact descriptor manifest is the readability commit record. Content without a descriptor is staged/incomplete and can be cleaned by the provider after its configured age.
- Workflow run ownership remains actor fact. Descriptor owner fields are only file-reference facts used by the artifact provider and do not replace actor-owned run state.
- Cleanup must be based on durable descriptor/index state. A provider must not depend on a process-local run/artifact registry, `actorId -> context` lookup, or query-time reconstruction to decide what to remove.
- `WorkflowChatRunRequest`、actor state、readmodels、logs、prompts 与 tool results continue to carry only `WorkflowFileRef` descriptors or sanitized derived fields. They must not carry file bytes, base64, multipart payloads, or provider raw response bodies.
- Scope service endpoints that accept multipart stream requests must resolve the service target first. Only workflow service targets may ingest pending files into artifact storage, and the owner scope must come from the path `scopeId`; static or scripting targets fail closed before artifact ingress.
- Host composition must fail closed for production/external backends. `WorkflowFileArtifacts:Backend=External` requires explicit registrations for ingress/read/ownership/cleanup ports; production policy rejects the implicit filesystem backend.
- The filesystem backend is the local/test concrete backend. Its cleanup removes expired descriptor-committed artifacts and stale staged directories without introducing a process-local artifact registry.

Workflow files can back a revisioned ContentArtifact without becoming the
ContentArtifact authority. The Host adapter accepts only
`backingObject.provider=workflow-file`, maps `objectKey` to the stable workflow
`FileArtifactRef.ArtifactId`, and requires descriptor Scope/Run ownership to
match the ContentArtifact revision provenance. Workflow file expiry and cleanup
remain unchanged; a missing file makes content unavailable while immutable
ContentArtifact metadata and provenance survive. See
[Content Artifacts](content-artifacts.md).

### Webhook Ingress API

`POST /api/workflow-webhooks/{routeKey}` 是 workflow 的第四个 start-run 入口。它和 `/api/chat` 复用同一条 `WorkflowChatRunRequest` accepted-only command dispatch 主干；它不是 workflow YAML 顶级 trigger，也不复用 channel inbound 或 `WorkflowSignalCommand`。

运行边界：

- Binding 由 Host-owned `WorkflowWebhookIngress` options/config 承载，包含 `routeKey`、`sourceId`、workflow 名称、scope、delivery id 来源、prompt 映射与 HMAC 策略。
- Host/Adapter 负责读取 raw body、校验 HMAC、解析简单 JSON path/template，并生成稳定 `webhook:{routeKey}:{sourceId}:{deliveryId}` command/correlation seed。
- 应用层只接收 typed `WorkflowChatRunRequest.ExternalIngress`，command envelope 写入 `WorkflowChatRequestEvent.external_ingress`；不得把 route、delivery、fingerprint、auth 等稳定语义塞进 `Metadata`。
- Replay/idempotency 权威是 `IWorkflowWebhookReplayStore`，生产实现必须是 durable/distributed first-writer-wins store；`InMemoryWorkflowWebhookReplayStore` 只在显式配置时用于本地或测试。
- Host 启用 webhook ingress 但没有 replay store 时返回 `503 WEBHOOK_REPLAY_STORE_UNAVAILABLE`，不能退化为无幂等的生产路径。
- HTTP 成功响应只返回 `202 Accepted + commandId/correlationId/actorId/statusUrl/deliveryId`，不暗示 committed、result 或 readmodel-observed。

不属于 v1 的范围：

- 不新增 `WorkflowWebhookTriggerGAgent`、trigger state proto、trigger projection/readmodel 或 `/api/workflow-triggers/{triggerId}/deliveries` endpoint family。
- 不在 endpoint 或中间层维护生产 `Dictionary` / `ConcurrentDictionary` / `MemoryCache` delivery ledger。
- 不依赖 NyxID、chrono-storage 或 Ornn 新增端点、schema 或能力。

### Workflow Lease

`WorkflowLeaseGAgent` 是 workflow 跨 run 单例 lease 的唯一事实源。一个 canonical `lease_key` 对应一个 deterministic lease actor；`WorkflowRunGAgent` 与 `LeaseModule` 只是 client，不保存可复用 credential，也不把进程内状态当成互斥事实。

运行语义：

- canonical key = trim 后 lower-invariant；actor id 由 `workflow.lease:` 加 key hash 生成，真实 key 保存在 actor state 和 typed event 中，调用方不得解析 actor id。
- acquire 空闲或已过期时生成新的 `holder_token`，`generation += 1`，并基于 actor state 持久化 holder、expiry 和 callback intent。
- renew/release 必须显式带 `holder_token + generation`；generation 不匹配、token 不匹配或 holder run 不匹配时返回 typed rejection，不修改 holder。
- renew 只延长 `expires_at_unix_ms`，不提升 generation。
- conflict policy v1 只支持 `fail` 或 FIFO `wait`；wait queue 上限固定为 32，不从 DSL 配置。
- TTL expiry 与 wait timeout 都通过 durable self callback 事件化；callback 回到 lease actor 后再次按 token/generation/request_id 对账，陈旧 callback 被忽略。
- release 或 TTL 清 holder 后由同一个 lease actor 授予 FIFO waiter；grant/reject 作为 continuation event 发送回请求方 run actor。
- `.refactor-loop/host.env` 不是生产事实源，不保存 branch topology、machine path、ledger authority 或 workflow lease 常量。

### WorkflowModuleFactory

按名称创建模块实例。DI 注册时每个模块有一个或多个名称：

```csharp
services.AddWorkflowModule<LLMCallModule>("llm_call");
services.AddWorkflowModule<ParallelFanOutModule>("parallel_fanout", "parallel", "fan_out");
```

YAML 里 `type: parallel` 会经工厂解析到 `ParallelFanOutModule`。

---

### Workflow Roles（正式 schema）

`workflow yaml` 里的 `roles` 现在是 role actor 的正式初始化入口，运行时会完整透传到 `InitializeRoleAgentEvent`：

```yaml
roles:
  - id: planner
    name: Planner
    agent_kind: workflow.role-agent
    system_prompt: "You are a planning assistant."
    provider: openai
    model: gpt-5.4
    temperature: 0.2
    max_tokens: 512
    max_tool_rounds: 4
    max_history_messages: 50
    event_modules: "llm_handler,tool_handler"
    event_routes: |
      event.type == ChatRequestEvent -> llm_handler
    connectors: [incident_api, search_mcp]
    allowed_tools: [web_search, issue_lookup]
    tool_sets: [nyxid.connected_services]
    extensions:
      event_modules: "fallback_module"
      event_routes: "event.type == X -> fallback_module"
```

语义规则：

- `workflow roles` 与 `role yaml` 共用同一份解析归一化逻辑（`RoleConfigurationNormalizer`）。
- `agent_kind` 是 role-level actor lifecycle 入口，可指向任意已注册 primary `[GAgent]` kind；step 只使用 `target_role` / `role`，不得通过参数选择 CLR 类型或 actor id。
- `allowed_tools` 是 role actor 上 agent tool 可见范围的上限；未配置表示兼容旧行为的全量工具，配置为空数组表示默认不暴露工具。
- `tool_sets` 是独立的 typed request-time source refs，不编码成静态 tool name。`allowed_tools` 与 `tool_sets` 两个维度分别合并：step 未声明某维度时继承 role，双方都声明时才对该维度求交，显式空数组只清空对应维度。有效 scope 写入 `WorkflowStepParameters.agent_tool_scope`，再由 `WorkflowLlmExecutionIntent.agent_tool_scope` 传给 AI 边界。
- `llm_call` step 可在根部配置 `allowed_tools` 继续收窄本次调用；静态工具维度映射到 `AgentToolExecutionContext.ToolVisibility`，named tool-set 维度保持 request-time source refs。
- Studio 的 `nyxid.connected_services` 每 turn 使用当前 caller token live resolve/discover，结果只存在 request-local catalog；resolution/discovery/collision failure 对本次动态工具 fail closed，不缓存为 role actor 或 process fact。
- 工具可见范围同时作用于 provider 看到的 `LLMRequest.Tools` 和 streaming tool executor 的实际 lookup；未授权工具调用会得到 not-available tool result，不会执行工具。
- `event_modules` / `event_routes` 支持平铺写法和 `extensions.*` 写法，且**平铺字段优先级更高**。
- 未配置 `event_modules` 时，`RoleGAgent` 不会额外装配 event modules（保持旧行为）。
- Refactor (iter31/cluster-032-chatruntime-taskrun-business-loop):
  Old pattern: ChatRuntime.ChatStreamAsync 用 Task.Run + Channel<LLMStreamChunk>/ChannelWriter 在 actor turn 外跑 LLM/tool/hook/history 业务循环,违反 actor execution integrity
  New principle: ChatStreamAsync owns the stream flow directly; the Task.Run + Channel owned-stream loop and stream_buffer_capacity config were removed; middleware wrapping stays inside private bridge adapters.

---

## 三、内置模块一览

| 类别 | YAML type | 模块 | 说明 |
|------|-----------|------|------|
| **引擎** | N/A | `WorkflowExecutionKernel` | 按步骤顺序派发，收到完成事件后推进下一步或结束 |
| **执行** | `llm_call` | `LLMCallModule` | 向目标 RoleGAgent 发 `ChatRequestEvent`，等回复转 `StepCompletedEvent` |
| | `tool_call` | `ToolCallModule` | 调用已注册的 Agent 工具（MCP/Skills） |
| | `connector_call` | `ConnectorCallModule` | 按名称调用配置好的 HTTP/CLI/MCP/host_callback connector |
| **并行** | `parallel` | `ParallelFanOutModule` | 拆 N 个子步骤并行发给不同 role，收齐后合并，可选触发 typed vote agreement |
| **共识** | `vote` | `VoteAgreementModule` | 基于 typed candidate/rule/decision 做结构化 agreement 判定（`vote_consensus` 为别名） |
| **迭代** | `foreach` | `ForEachModule` | 按分隔符拆分输入，逐项执行子步骤 |
| **流程** | `conditional` | `ConditionalModule` | 条件分支 |
| | `while` | `WhileModule` | 循环执行（别名 `loop`） |
| | `workflow_call` | `WorkflowCallModule` | 调用子工作流（别名 `sub_workflow`，支持 `lifecycle=singleton/transient/scope`） |
| | `dynamic_workflow` | `DynamicWorkflowModule` | 从 LLM 输出提取 YAML，动态重配后继续执行 |
| | `lease` | `LeaseModule` | 跨 run 显式 acquire/renew/release 单例 lease（别名 `mutex`） |
| | `assign` | `AssignModule` | 变量赋值 |
| | `checkpoint` | `CheckpointModule` | 检查点 |
| **数据** | `transform` | `TransformModule` | 纯函数变换（count/take/join/split/distinct 等） |
| | `retrieve_facts` | `RetrieveFactsModule` | 按关键词检索事实片段 |

每个原语的作用、参数和 YAML sample，见 [WORKFLOW_PRIMITIVES.md](./WORKFLOW_PRIMITIVES.md)。
如果要把 `human_input` / `human_approval` / `wait_signal` 接到真实应用交互，优先参考该文档中的“实际应用集成模式”小节。

`workflow_call` 关联规则补充：

- invocation id 统一由共享工厂生成，格式为 `<parent_run_id>:workflow_call:<parent_step_id>:<guidN>`；
- `parent_step_id` 必须非空；缺失时直接失败，不再生成兜底 step token；
- `WorkflowCallModule` 与 `WorkflowGAgent` 共用同一规则，避免双点实现漂移；
- 子流程 run id 复用 invocation id，便于父子流程关联追踪。
- 父子 run 的 root/depth/fanout 由父 `WorkflowRunGAgent` 持久态与 `SubWorkflowOrchestrator` 判定；`llm_call` / `tool_call` 只能透传 host stamped typed runtime context。
- workflow 内调用 `aevatar_start_workflow` 时，如果工具上下文带有可信 workflow runtime context，dispatcher 必须发布 `SubWorkflowInvokeRequestedEvent` 给父 run actor，由父 actor 完成 admission、registration、start、completion 与 cleanup；公开 tool 参数不得暴露 parent/root/depth 字段。

### 从 Foundation Orchestration 迁移

`Aevatar.Foundation.Core/Orchestration` 已移除，原能力统一收敛到 workflow 模块：

| 原类 | 推荐替代 |
|------|------|
| `SequentialOrchestration` | 线性 `steps`（由 `WorkflowLoopModule` 推进） |
| `ConcurrentOrchestration` | `type: parallel`（`ParallelFanOutModule`） |
| `VoteOrchestration` | `parallel + vote`（`VoteAgreementModule` typed agreement rule） |
| `HandoffOrchestration` | `type: conditional` / `type: switch` + 分支推进 |

最小迁移示例（并行 + 投票）：

```yaml
steps:
  - id: parallel_analysis
    type: parallel
    parameters:
      workers: "agent_a,agent_b,agent_c"
      vote_step_type: "vote"
      vote_param_rule_mode: "quorum"
      vote_param_quorum_count: "2"
```

---

## 四、运行链路（从请求到结果）

```
POST /api/chat { prompt, workflow?, workflowYaml?, source? }
  │
  ├── ICommandInteractionService<WorkflowChatRunRequest, WorkflowChatRunAcceptedReceipt, WorkflowChatRunStartError, WorkflowRunEventEnvelope, WorkflowProjectionCompletionStatus>.ExecuteAsync
  │     ├── WorkflowRunCommandTargetResolver: workflowYaml 优先；否则按 workflow 名查 registry；仅当 workflow/workflowYaml 同时为空时走默认 workflow（默认 direct，可配置为 auto）
  │     ├── WorkflowRunObservationLifecycle: attach 到既有 projection session + live sink，不做 pre-dispatch projection activation；accepted receipt 由 receipt factory 生成
  │     └── DefaultCommandDispatchPipeline / ActorCommandTargetDispatcher: 将 `ChatRequestEvent` 包装为 `EventEnvelope`，由 `IActorDispatchPort` 投递到 run actor；目标 actor 的获取/创建仍由 `IActorRuntime` 负责
  │
  ├── WorkflowRunGAgent 收到 `ChatRequestEvent` envelope
  │     ├── EnsureAgentTreeAsync: 按 roles 创建子 RoleGAgent
  │     └── 发布 StartWorkflowEvent (TopologyAudience.Self)
  │
  ├── WorkflowExecutionKernel 收到 StartWorkflowEvent
  │     └── 取第一个步骤，发布 StepRequestEvent
  │
  ├── 对应模块处理 StepRequestEvent
  │     ├── LLMCallModule: 转 ChatRequestEvent → SendTo RoleGAgent → 等 TextMessageEndEvent → StepCompletedEvent
  │     ├── ConnectorCallModule: 查 registry → 执行 connector → StepCompletedEvent
  │     ├── ParallelFanOutModule: 拆子步骤 → 收齐合并 → 可选投票 → StepCompletedEvent
  │     └── ...其他模块同理
  │
  ├── WorkflowExecutionKernel 收到 StepCompletedEvent
  │     ├── 有下一步 → 再发 StepRequestEvent（循环）
  │     ├── 有补偿 ledger 的终止失败 → 发布 CompensationRequestEvent 并进入补偿相位
  │     └── 无下一步 → 发布 WorkflowCompletedEvent
  │
  ├── run actor envelope 流进入统一 Projection Pipeline（一对多分发）
  │     ├── WorkflowExecutionCurrentStateProjector / WorkflowRunInsightReportArtifactProjector / WorkflowRunTimelineArtifactProjector / WorkflowRunGraphArtifactProjector: 按消费场景物化 current-state + durable artifacts
  │     └── WorkflowExecutionRunEventProjector: EventEnvelope -> WorkflowRunEventEnvelope run event stream
  │
  ├── DefaultEventOutputStream + IdentityEventFrameMapper: 从 sink 读事件 → 透传 WorkflowRunEventEnvelope → emitAsync
  └── SSE 流返回客户端
```

关键点：**流程控制由模块完成，不写死在单个 Agent 的方法里。**

### Saga 补偿生命周期

Workflow step 可以通过 `compensation` 声明一个已存在的 step id。静态校验阶段会解析该目标，引用不存在的补偿步骤会被拒绝。运行时中，`tool_call`、`connector_call`、`secure_connector_call` 这三个 side-effecting primitive 在 dispatch 前会由 `WorkflowRunGAgent` 持久化 `CompensableStepDispatchedEvent`，先写入 `PROVISIONAL` ledger 项；其他 primitive 即使声明 compensation，也只在成功完成后按 legacy 路径写入 `CONFIRMED` ledger 项。`compensable_ledger` 归 `WorkflowRunGAgent` 持有，是 run actor 的权威状态。

成功完成会把匹配的 `PROVISIONAL` ledger 项确认成 `CONFIRMED` 并补齐 captured output；没有 provisional 项的 legacy success 仍追加一条 `CONFIRMED` ledger。失败完成通过 typed `WorkflowStepFailureOutcome` 对账：`CALLEE_CONFIRMED`（含默认 `UNSPECIFIED`）删除匹配 provisional，表示 callee 已确认没有可补偿副作用；`OUTCOME_UNCERTAIN` 保留 provisional，timeout / force-fail / stop-to-failure 这类中断按“副作用可能已发生”处理。

当后续 step 发生终止失败且 ledger 非空时，run 不直接提交 `WorkflowCompletedEvent(success=false)`。`WorkflowExecutionKernel` 先请求 run actor 开启补偿相位，run actor 按 ledger 反向顺序提交 `CompensationRequestEvent`，再通过 self continuation 派发对应补偿 step。`PROVISIONAL` 和 `CONFIRMED` 项使用同一条 LIFO compensation walk；provisional 的 captured output 可为空，补偿 idempotency key 保持稳定，撤销未生效副作用必须是安全 no-op。补偿 step 派发使用补偿专用默认超时：若补偿 step 没有显式 `timeout_ms`，kernel 使用 `DefaultCompensationTimeoutMs = 30000`，再沿用 step timeout 的 `100..600000` ms clamp；forward step 省略 `timeout_ms` 的语义保持不变。补偿 step 完成后以 `CompensationStepCompletedEvent` 回到 run actor，由 actor 校验 `run_id + compensation_step_id + execution_id`，拒绝陈旧或重复完成事件。

补偿相位本身也有 actor-owned durable deadline。第一次进入补偿相位或 crash/reactivation 后重发当前 `CompensationRequestEvent` 时，`WorkflowExecutionKernel` 通过 `ScheduleSelfDurableTimeoutAsync` 安排 `WorkflowCompensationPhaseDeadlineFiredEvent`，相对超时为 `CompensationPhaseDeadlineMs = 300000`。deadline fired 后若 run 仍处于当前补偿相位且 callback lease 匹配，kernel 只向 `WorkflowRunGAgent` 报告 deadline exceeded；仍由 run actor 依据权威 `compensation_cursor` 提交 `WorkflowCompensationFailedEvent`，进入 `COMPENSATION_DEAD_LETTER` 并复用失败 `WorkflowCompletedEvent` 通知 caller。补偿正常完成或 dead-letter 终态会清理并取消 phase deadline lease；终态后迟到的 deadline fired event 会被忽略。

run actor 会把触发补偿的原始失败 step 持久化为 `compensation_origin_failed_step_id`，后续每个 `CompensationRequestEvent.failed_step_id` 都复用这个 actor-owned fact，不从当前 compensation cursor 反推。`terminal_workflow_completion_recorded` 是 `WorkflowCompletedEvent` redelivery 的幂等门禁；补偿 dead-letter 可以先把 run 标为 failed，但不会阻止第一次最终 completion fact 落账。

`workflow_call` child run 失败时先由 child run 自己完成补偿，再向 parent actor 发送 `SubWorkflowInvocationCompletedEvent(success=false, compensated=true)`。`compensated` 只表达 child compensation outcome；parent 侧仍把该 child failure 转成普通 `StepCompletedEvent(success=false)` 推进本 run，是否补偿 parent 的 `workflow_call` step 由 parent 自己的 ledger 决定。

Saga 状态由强类型枚举 `WorkflowSagaStatus`（`workflow_execution_messages.proto`）表达，生命周期为：

```text
WORKFLOW_SAGA_STATUS_UNSPECIFIED -> WORKFLOW_SAGA_STATUS_COMPENSATING -> WORKFLOW_SAGA_STATUS_COMPENSATED_FAILED
WORKFLOW_SAGA_STATUS_UNSPECIFIED -> WORKFLOW_SAGA_STATUS_COMPENSATING -> WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER
```

- `UNSPECIFIED`：非补偿阶段（run 仍是普通 `running` 运行态），provisional/confirmed compensable step 按 dispatch/完成顺序写入 ledger；saga_status 没有独立的 `running` 取值。
- `COMPENSATING`：终止失败已转入补偿相位，`compensation_cursor` 指向当前待补偿 ledger 项。
- `COMPENSATED_FAILED`：所有补偿按反向顺序成功，随后发布失败的 `WorkflowCompletedEvent`，表示原业务 run 失败但补偿已完成。
- `COMPENSATION_DEAD_LETTER`：某个补偿 step 失败或补偿耗尽，run actor 提交 `WorkflowCompensationFailedEvent`，记录失败补偿 step、剩余未补偿数量和错误；此状态不再走 on_error fallback，也不会静默丢弃。

补偿相位继续遵守 actor 化执行约束：补偿推进只通过 self message 进入 actor inbox，不在 callback 线程或 helper 内 inline 推进；deadline 是 durable self event，不使用 wall-clock 字段或 query-time 检查；crash/reactivation 时，actor 根据已提交 `CompensationRequestEvent` 和当前 cursor 重发当前 self continuation，不重复提交领域事件。

## Host Boundary For GitHub / Router / Closure

和 issue #1738 相关的几个职责边界在 runtime 层明确如下：

- GitHub inbound、label、merge、close 是 host 职责。
- 跨条目的 `phase9-router` 是 host 职责。
- `vibe-map` closure 是 host 职责。

Workflow engine 只接收这些 host 能力已经发布出来的表面契约，例如：

- `connector_call -> host_callback`
- 已镜像到 `workflow.usage.*` / `steps.<id>.usage.*` 的 usage facts

Workflow engine 不新增：

- 专用 GitHub controller primitive
- phase9-router built-in capability
- vibe-map closure built-in capability
- 为上述职责新增的 Aevatar endpoint

这条边界对应三个原则：

- `host-not-controller`
- `published-surfaces-only`
- `no-new-aevatar-endpoints`

### `/api/chat` 入参矩阵（推荐）

| 场景 | 推荐请求体 | 说明 |
|------|------------|------|
| 新建 Actor，按名称加载已注册 workflow | `{ "prompt": "...", "workflow": "direct" }` | `workflow` 按名称从 registry 查 YAML。 |
| 新建 Actor，`workflow/workflowYaml` 都不传 | `{ "prompt": "..." }` | 默认走 `direct`；如开启 `UseAutoAsDefaultWhenWorkflowUnspecified`，则默认走 `auto`。 |
| 复用已绑定 workflow 的 Actor | `{ "prompt": "...", "source": { "kind": "definition_actor", "definitionActor": { "actorId": "actor-123" } } }` | actor-targeted execution 只通过 typed source 子消息表达。 |
| 新建 Actor，直接提交 inline YAML | `{ "prompt": "...", "workflowYaml": "name: demo\\nroles: ...\\nsteps: ..." }` | 不依赖预存文件，服务端先解析 `workflowYaml`。 |
| 给指定 Actor 传 inline YAML | `{ "prompt": "...", "source": { "kind": "inline_yaml_bundle", "inlineBundle": { "actorId": "actor-123", "yamlDocuments": [{ "yaml": "..." }] } } }` | 仅允许“未绑定 actor 首次绑定”或“同名 workflow 更新”；不允许切换到其它 workflow 名。 |
| 同时传 `workflow` + `workflowYaml` | `{ "prompt": "...", "workflow": "demo", "workflowYaml": "name: demo\\n..." }` | 两者名称必须一致；不一致返回 `WORKFLOW_NAME_MISMATCH`（400）。 |

错误码要点：

- `INVALID_WORKFLOW_YAML`（400）：`workflowYaml` 解析/校验失败。
- `WORKFLOW_NAME_MISMATCH`（400）：`workflow` 与 `workflowYaml.name` 不一致。
- `WORKFLOW_BINDING_MISMATCH`（409）：目标 actor 已绑定其它 workflow。
- `AGENT_WORKFLOW_NOT_CONFIGURED`（409）：typed source 指定的 actor 未绑定且未提供 inline YAML。

异常回退语义：

- 应用层仅在“白名单 workflow + 白名单异常类型”命中时尝试一次 `direct` 回退执行。
- inline `workflowYaml` 与显式 `direct` 请求默认不触发自动回退；已进入回退阶段也不再二次回退（防循环）。

最小可用示例（复用已有 Actor）：

```json
{
  "prompt": "继续上一次分析，给我三条行动建议",
  "source": {
    "kind": "definition_actor",
    "definitionActor": {
      "actorId": "actor-123"
    }
  }
}
```

### 和 CQRS 投影的关系

- 同一条 `EventEnvelope` 并行进入多个 projector：ReadModel 分支（查询用）和 AGUI 分支（实时输出用）
- 投影管线统一入口、一对多分发，不搞双轨实现
- ReadModel 是事件投影的结果，不是 Agent State 的直接镜像
- 需要列表/统计等读模型时，扩展 reducer/projector + read-only store，通过 Query API 暴露

---

## 五、模块装配机制

`WorkflowRunGAgent` 不硬编码“哪个 workflow 需要哪些模块”，而是通过组合策略自动推导：

### 1. 依赖推导（`IWorkflowModuleDependencyExpander`）

按 Order 排序，依次调用，累积出所需模块名集合：

| Expander | 逻辑 |
|----------|------|
| `WorkflowLoopModuleDependencyExpander` | 现已等价为“确保执行内核存在”；兼容命名仍保留在依赖推导层 |
| `WorkflowStepTypeModuleDependencyExpander` | 遍历 steps，按 `type` 加入对应模块 |
| `WorkflowImplicitModuleDependencyExpander` | 补齐隐式依赖（如 `parallel` 隐式需要 `llm_call`） |

### 2. 实例配置（`IWorkflowModuleConfigurator`）

模块创建后，由 configurator 做初始化：

| Configurator | 逻辑 |
|--------------|------|
| `WorkflowLoopModuleConfigurator` | 历史命名；当前配置目标是 `WorkflowExecutionKernel` 相关执行上下文 |

### 扩展方式

新增模块不改 `WorkflowRunGAgent`，只需：

```csharp
// 1. 实现 IEventModule<IWorkflowExecutionContext>
public sealed class MyStepModule : IEventModule<IWorkflowExecutionContext> { ... }

// 2. DI 注册
services.AddWorkflowModule<MyStepModule>("my_step", "my_alias");

// 3. （可选）如果需要自定义推导或配置，新增 expander/configurator
services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowModuleDependencyExpander, MyExpander>());
```

---

## 六、Connector 机制

`connector_call` 把外部能力（HTTP / CLI / MCP）收敛到统一契约：

| 组件 | 位置 |
|------|------|
| 契约 | `Aevatar.Foundation.Abstractions/Connectors/IConnector.cs` |
| 注册表 | `Aevatar.Workflow.Core/Connectors/ConfiguredConnectorRegistry.cs` |
| 执行模块 | `Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs` |
| 配置加载 | `Aevatar.Configuration/AevatarConnectorConfig.cs` → `~/.aevatar/connectors.json` |

### 安全策略

HTTP 和 CLI connector 都采用白名单：
- **HTTP**：`allowedMethods`、`allowedPaths`、`allowedInputKeys`
- **CLI**：`allowedOperations`、`allowedInputKeys`

### 角色级授权

YAML 中角色可声明 `connectors` 列表，`ConnectorCallModule` 执行时校验：步骤指定的 connector 名称必须在角色允许列表内。

```yaml
roles:
  - id: coordinator
    connectors: [my_api, my_mcp]  # 只允许调这两个

steps:
  - id: call_api
    type: connector_call
    role: coordinator
    parameters:
      connector: my_api           # 必须在 coordinator.connectors 内
```

### 容错参数

| 参数 | 说明 |
|------|------|
| `retry` | 失败重试次数（0-5） |
| `timeout_ms` | 超时（100-300000ms） |
| `on_missing` | connector 不存在时：`fail`（默认）/ `skip` |
| `on_error` | 执行失败时：`fail`（默认）/ `continue` |
| `optional` | `true` 等价于 `on_missing: skip` |

---

## 七、示例

### 示例 1：最简单的工作流（单步 LLM 调用）

```yaml
name: simple_qa
roles:
  - id: assistant
    name: Assistant
    system_prompt: "You are a helpful assistant."
steps:
  - id: answer
    type: llm_call
    role: assistant
```

一个角色、一个步骤。用户输入直接发给 assistant 角色的 LLM，回复即为工作流输出。

### 示例 2：顺序多步

```yaml
name: research_then_summarize
roles:
  - id: researcher
    name: Researcher
    system_prompt: "You gather and organize information."
  - id: writer
    name: Writer
    system_prompt: "You write clear, concise summaries."
steps:
  - id: research
    type: llm_call
    role: researcher
  - id: summarize
    type: llm_call
    role: writer
```

先让 researcher 调研，输出传给 writer 做总结。

### 示例 3：并行 + 投票

```yaml
name: multi_perspective
roles:
  - id: analyst_a
    name: Analyst A
    system_prompt: "You analyze from a technical perspective."
  - id: analyst_b
    name: Analyst B
    system_prompt: "You analyze from a business perspective."
  - id: analyst_c
    name: Analyst C
    system_prompt: "You analyze from a user experience perspective."
steps:
  - id: parallel_analysis
    type: parallel
    parameters:
      workers: "analyst_a,analyst_b,analyst_c"
      vote_step_type: "vote"
      vote_param_rule_mode: "majority"
```

三个分析师并行工作，结果作为 typed candidates 扇入 `vote`；`vote` 根据配置的 agreement rule 产出 `agreed` / `rejected` / `inconclusive` 分支与结构化 decision。

### 示例 4：LLM + Connector 调外部 API

```yaml
name: analyze_and_post
roles:
  - id: coordinator
    name: Coordinator
    system_prompt: "You coordinate analysis tasks."
    connectors: [my_api]
steps:
  - id: analyze
    type: llm_call
    role: coordinator
  - id: post_result
    type: connector_call
    role: coordinator
    parameters:
      connector: my_api
      timeout_ms: "10000"
```

先用 LLM 分析，再把结果发到外部 API。

### 示例 5：循环 + 条件

```yaml
name: iterative_refinement
roles:
  - id: writer
    name: Writer
    system_prompt: "You write and refine content. When satisfied, include DONE in your response."
steps:
  - id: draft
    type: llm_call
    role: writer
  - id: refine_loop
    type: while
    parameters:
      max_iterations: "5"
    children:
      - id: refine
        type: llm_call
        role: writer
```

写初稿后循环打磨，直到回复中包含 "DONE" 或达到最大迭代次数。

---

## 八、代码定位（阅读顺序建议）

| 顺序 | 文件 | 看什么 |
|------|------|--------|
| 1 | `src/workflow/Aevatar.Workflow.Core/WorkflowGAgent.cs` | 入口、YAML 编译、模块装配、子 Agent 创建 |
| 2 | `src/workflow/Aevatar.Workflow.Core/Modules/WorkflowLoopModule.cs` | 引擎主循环：步骤派发与推进 |
| 3 | `src/workflow/Aevatar.Workflow.Core/Modules/LLMCallModule.cs` | LLM 调用：请求/响应关联、点对点发送 |
| 4 | `src/workflow/Aevatar.Workflow.Core/Modules/ParallelFanOutModule.cs` | 并行：扇出/收集/合并/投票 |
| 5 | `src/workflow/Aevatar.Workflow.Core/Modules/ConnectorCallModule.cs` | Connector：安全校验、重试、容错 |
| 6 | `src/workflow/Aevatar.Workflow.Core/Composition/` | 模块装配策略：expander + configurator |
| 7 | `src/Aevatar.CQRS.Core/Interactions/DefaultCommandInteractionService.cs` | 通用交互编排：dispatch → stream → finalize |
| 8 | `src/workflow/Aevatar.Workflow.Projection/` | 投影管线：reducer → ReadModel、AGUI 输出 |
| 9 | `src/Aevatar.Foundation.Core/GAgentBase.cs` | 模块如何进入统一事件管线 |

各项目的详细结构见 [src/workflow/README.md](../src/workflow/README.md)。

---

## 九、优缺点

### 优点

- **可配置**：流程从代码移到 YAML，业务人员可调整
- **可复用**：控制原语模块跨项目复用
- **可演进**：新增能力多数只需新增模块，不改 WorkflowGAgent
- **可治理**：模块统一做日志、容错、元数据记录

### 代价

- 调试链路变长（事件驱动 + 模块分发）
- 需要理解事件驱动思维
- 模块间通过事件通信，隐式依赖需要文档说明

建议：先从 1-2 个步骤的 workflow 开始，确保链路通了再逐步增加复杂度。

---

## 十、开发建议

- 每引入一个新模块，单独做用例验证
- 对关键模块加结构化日志（stepId、runId、duration）
- 不要在模块里藏隐式状态，状态尽量显式放在 workflow vars 或事件里
- 模块保持单一职责：一个模块处理一种 step type
- YAML 只写 connector 名称和调用意图，连接细节与安全策略放配置
- 每次 connector 调用的运行注解会写入 `StepCompletedEvent.Annotations`，便于回放与审计

---

## 十一、FAQ

### Q1：什么时候该用工作流？

当你需要以下任一能力时：

- 流程可配置（不改代码调整步骤）
- 复杂分支和循环
- 多 Agent 并行协作与结果汇总
- 业务团队希望通过 YAML 调整流程

### Q2：所有 Agent 都要改成 WorkflowGAgent 吗？

不需要。固定流程、简单任务型 Agent，用普通 `GAgentBase` + `[EventHandler]` 更直接。

### Q3：模块失败会怎样？

取决于模块实现、步骤配置和 saga ledger。`WorkflowExecutionKernel` 收到 `Success=false` 的 `StepCompletedEvent` 后，先用 `failure_outcome` 对账 provisional ledger；若当前 run 仍有非空 `compensable_ledger`，再进入补偿相位；补偿全部成功后才以 `WorkflowCompletedEvent(Success=false)` 结束。没有 compensable ledger 时，失败直接发布 `WorkflowCompletedEvent(Success=false)`。支持 retry/on_error 的 step 会先按对应策略处理，未被策略接管的终止失败才触发上述逻辑。

### Q4：怎么新增一种步骤类型？

三步：

1. 实现 `IEventModule`（`CanHandle` 过滤 `StepRequestEvent.StepType`，`HandleAsync` 执行逻辑，完成后发布 `StepCompletedEvent`）
2. DI 注册：`services.AddWorkflowModule<MyModule>("my_type")`
3. YAML 里写 `type: my_type`

### Q5：怎么替换投影存储？

默认是内存存储（`InMemoryWorkflowExecutionReadModelStore`）。替换为持久化实现：

```csharp
services.AddWorkflowExecutionProjectionReadModelStore<MyPersistentStore>();
```
