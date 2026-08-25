---
title: NyxID Chat Agent Profile Binding
status: canonical
owner: Aevatar AI
---

# NyxID Chat Agent Profile Binding

NyxID Chat 是第一阶段唯一正式消费 Agent Profile 的 runtime。管理面的 Definition 由 `AgentProfileGAgent` 持有，新 Conversation 只从 Actor-backed protected execution read model 解析一次 published snapshot，并把 immutable clone 固化到 Conversation Actor state。

## 身份边界

- `scopeId` 是用户 Profile owner；system 是独立 typed platform owner。
- `profileId` 是 Profile 的 opaque identity；`profileSlug` 只用于 owner namespace 内的人类引用。
- `actorId` 是 Conversation 地址，不能从 profileId/slug 推导，也不承载 Profile 事实。
- NyxID `userId` 只用于 system Admin 授权与审计，不得与 scopeId 比较、互换或由字符串规则推导。

显式 create reference 只有 `ownerKind = caller | system` 与 `profileSlug`。请求不能提交 profileId、其他用户 scope/user ID、inline Profile、文件路径或通用 object。Host/Application 在授权后把 human reference 一次解析为 typed owner + opaque profileId；后续 execution resolution 以 profileId 为 load-bearing identity。

## 创建解析

解析顺序固定为显式 reference、scope default、system default rollout、genuinely unprofiled。每一级都先从 Catalog read model 得到 typed binding target，再从 Protected execution read model 读取 snapshot，并核对：

- target owner/profileId 与 execution document identity 完全一致；
- published revision 与 snapshot digest 与 binding 证据一致；
- outer published digest 与 inner runtime snapshot digest 均有效；
- agent kind 为 `nyxid.chat`，route tool set 是 Host 静态允许项；
- system binding 的 enabled/cohort admission 对当前新 actorId 生效。

明确 binding 不可用时必须在创建 Actor 之前 fail closed，返回 typed unavailable/integrity failure；不得继续尝试下一层。只有没有任何显式或默认 binding 时，create command 才不携带 Profile snapshot。

Profile 解析完成后，create resolver 才解析 direct-chat route。已选 sealed Profile 的 `routeToolSetRef` 只补充 route 的隐式值：projected `ForwardToModel` 未写 `toolSetRef` 时使用 Profile route；没有 route snapshot 时，Profile route 可以替换 Host fallback 中的默认 tool set。fallback decision 必须先 clone，禁止把单请求 override 写回可复用对象并跨 caller 泄漏。route policy 显式写出的 `toolSetRef` 始终更权威，与 Profile route 不一致时必须在创建 Actor 前 fail closed；未选 Profile 时保持既有 Host fallback 行为。

Create resolver 不访问 Ornn、文件、event store 或进程内 Profile registry，也不在 query 方法里 prime projection/activate Actor。Chat route 的明确拒绝仍是 403；Profile unavailable/integrity failure 与 route/Profile mismatch 使用稳定的 `ADMISSION_UNAVAILABLE`，不得伪装为 `PROJECTION_UNAVAILABLE`；成功仍是 honest `202 Accepted`，不暗示 Conversation state 已提交或已投影。

## Conversation 固化

`RoleGAgentState.agent_profile` 是存量 Conversation 的唯一 Profile 事实源：

- 未绑定且 create command 带有效 snapshot：先提交 `AgentProfileBoundEvent`；
- 已绑定且 deterministic bytes 相同：幂等继续；
- 已绑定后收到不同、缺失或 digest 无效的 snapshot：拒绝，不改写既有 binding；
- genuinely unprofiled create：保持 `agent_profile` 缺失。

Profile 重新发布只影响之后创建的 Conversation。旧实例不 hot-upgrade、不 lazy rebind、不 replay/backfill；Host/Actor 重启也不会重新解析其 Profile。

## Turn Authority

Turn-local materialization 继续使用已固化 snapshot 与既有 `AgentTurnToolCatalogMaterializer`。`RoleGAgentState.agent_profile_turn_authority` 是当前 turn authority 的唯一 durable fact，`session_id + attempt` 是 reconciliation fence。Exact skill body、prompt layer、tool object、token、credential、header 和 adapter/runtime instance 不进入 actor state/read model。

Runtime 先对 route-owned tools、registered tools、typed visibility、Profile maximum/recovery/task policy 与 caller authorization 取交集，再执行 bounded routing/classification。任何 profile、exact fetch、identity、collision、capability 或 integrity 失败都只能继续缩权；交集为空即 restricted-empty，不能退回 unrestricted。

Published Profile 的 classifier 与 exact-skill fetch budget 都固定为 `15000ms`。Classifier 走正式
streaming LLM provider；`600ms` 小于已观测的正常分类调用时延，会把已发现且已授权的 request-local
operation 错误收窄成 empty recovery，因此不再是可发布配置。Exact Ornn skill 读取同样保留
`15000ms` 的边界预算；超时仍 fail closed，不得跳过 exact identity/hash 校验，也不得回退到
unprofiled tool surface。Selected skill body 的固定 ceiling 是 `65536` UTF-8 bytes；Profile
validate/publish 必须从 exact Ornn package 校验唯一 `SKILL.md` 的实际大小，禁止发布运行时必然
降级的超限 snapshot。修改预算必须形成新的 published revision，并由新 Conversation 固化；旧
Conversation 不会热更新。

每层 Profile tool policy 内的 literal tool name、tool-set ref 与 connected-service selector 是并集，maximum、recovery 与选中的 task policy 仍按既有规则取交集。connected-service selector 只包含 canonical `catalog_service_slug` 与非空 `READ_ONLY/WRITE` risk 集；它只能匹配 route discovery 已经得到的工具，并读取 server-sealed `AgentToolOperationAdmission.catalog_service_slug` 与 `execution_policy.risk`。它不得读取展示 descriptor、opaque tool name、`service_instance_id`、HTTP method 或 path 来猜测安全语义，也不得触发额外 discovery 或扩大 route ceiling。相同 catalog service 的多个 exact connection 会各自保留 exact admission 并同时匹配；未匹配 selector 只贡献空集。

Task/recovery selector 的 risk 集必须是 maximum 中同 slug selector 的子集。非法 slug、空 risk、`UNSPECIFIED/DESTRUCTIVE` 或同一 policy 内重复 slug 在 validate/publish 时拒绝；runtime 对无效 sealed snapshot 继续 fail closed。maximum 过滤动态工具时只按 typed presentation kind 输出 bounded count diagnostic，不记录 opaque name、connection identity、endpoint 或参数，且该诊断不改变既有降级判定。

connected-service selector 可以额外封存 `readiness.requested_scopes`，用于声明该 catalog service 在零 exact operation 时的连接要求。只要 `readiness` 存在，`requested_scopes` 就必须是非空、去重、规范化且由 Profile 作者从目标 NyxID catalog entry 的真实 capability 中选择；Profile validate/publish 必须拒绝空数组，`nyxid_require_service` 仍以运行时 catalog 校验具体 scope，不能把错误留给用户 turn。该字段的 presence 才启用确定性 readiness：当选中 task policy 的全部 connected-service selector 都没有 exact match，且只有一个 selector 需要恢复时，runtime 必须绕过 LLM，按封存的 `catalog_service_slug + requested_scopes` 精确调用 route-owned `nyxid_require_service`。它不得让模型决定是否连接、选择其他 service/scope，或生成 CLI/手工查询说明。工具返回的 typed authorization receipt 继续沿既有 browser-action 投影生成连接卡片；缺少 readiness、多个 selector 同时缺失或 required tool 不在最终 ceiling 时，turn 返回 typed failure，不得降级为自然语言兜底。只要 selector 已匹配到 exact connected-service operation，就不调用 readiness tool，模型只能使用匹配后的真实 operation。

Request-local `AgentTurnToolCatalog` 不是 DI service、cache 或进程级上下文。非 Profile consumer 必须显式传 `null`；当前只有 genuinely unprofiled NyxID Chat 允许这个值表达未绑定。

## Structured context attachments

Conversation creation may carry a typed `ConversationContextAttachmentSet` alongside the profile
reference. This is a separate authority: an attachment names an exact ContentArtifact identity
and chooses `FOLLOW_CURRENT` or `PINNED_REVISION`; it does not grant write access or turn the
artifact into profile content. The create resolver validates the artifact read model before actor
creation (active lifecycle, owner/reader ACL, `TEXT`/`MARKDOWN`/`STRUCTURED_DOCUMENT` kind, and
available pinned revision). Any failure is `ADMISSION_UNAVAILABLE` and no actor is created.

The Conversation actor commits `ConversationContextAttachmentsBoundEvent` once. Equal protobuf
bytes are idempotent; a different or absent later declaration is rejected. The sealed set is
passed through transient LLM operation carriers and materialized each turn into the existing
conversation prompt layer. `FOLLOW_CURRENT` therefore observes a newly committed current
revision on the next turn while `PINNED_REVISION` remains stable. Materialization uses the
verified ContentArtifact read path and emits an identity header (`artifact_id`, actual
`revision_id`, content hash prefix, media type). Redaction, tombstone, retention expiry, ACL or
backing failures, read-model unavailability, and either prompt budget produce a typed unavailable
placeholder plus diagnostic; content is never truncated or persisted in Conversation state,
read models, or transcript history.

## Static route tool ceiling

Profiled 与 genuinely unprofiled NyxID Chat 共用 Host 静态注册的
`agent-profile.nyxid-chat` route tool set。Profile 只能在这个 ceiling 内继续收窄；
unprofiled turn 直接使用同一 ceiling，不会回退为枚举所有 DI `IAgentToolSource`。
因此注册新的通用 tool source 不会自动扩大 NyxID Chat surface，扩大 surface 必须通过
明确的 route tool set 变更。

这里描述的是 Conversation turn 的实际工具 ceiling；create-time chat route 仍是独立的准入输入，
不得把 Host 的 `workspace.default` fallback 当成 Profile binding 的 route 事实。系统默认 Profile
上线时由 sealed `routeToolSetRef` 填充上述隐式 route 值，不能通过全局扩大
`workspace.default` 来制造短暂的 unprofiled 权限窗口。

这个固定 tool set 只提供审查过的只读 NyxID management wrappers、readiness/browser-action
handoff、单一 `web_search`、单一 `ask_user` typed input，以及其他明确允许的
route-owned tools。生产 `web_search` 通过 `tavily-search` NyxID service binding 执行；
它和 `ask_user` 分别通过只含一个工具的窄 source 挂载，不让 `web_fetch` 或完整
`WebAgentToolSource` 进入 ceiling。声明 `ExcludeFromNyxIdChat` 的通用 proxy
不会进入模型可调用面。声明 `RequiresHumanSession` 的 Class-R read 只有在当前 turn 携带
可验证的 source-readable user bearer 时才会被提供；credential 缺失、类型错误或仅有
不可读 delegation 时，该 read 在 tool discovery 阶段即被移除。`RequiresHumanSession`
只证明 credential shape，不证明 platform admin、operator 或 organization admin authority；
因此 admin-only 的 service-account wrapper 在没有 typed authority admission 前不进入默认
route ceiling，但其 typed REST adapter 可以由显式管理 surface 复用。无论是否绑定 Profile，
mutation schema、secret-bearing 参数与新注册但未列入 ceiling 的工具都保持不可调用。

Ornn workflow skill 的发现、发布、加载与执行是同一条受限能力链。ceiling 显式提供
`list_external_workflow_capabilities`、`inspect_external_workflow_capability_readiness`、
`ornn_publish_skill` 与 `use_skill`，并且只提供 `aevatar_start_workflow`、
`aevatar_observe_run` 与 `aevatar_read_workflow_run_artifact` 三个 workflow 执行工具。前两个
capability 工具来自只读窄 source；Ornn authoring 只挂载 private publish，不暴露
`ornn_update_skill` 或完整 authoring source。这样 Assistant 可以按 exact descriptor 检查
readiness、发布私有 skill、启动已挂载或显式 inline fallback 的定义，并以 committed read model
与 typed artifact 判定结果。workflow 自身的 capability admission、scope authority、tool approval
和外部副作用策略仍逐层生效；这组工具不会引入通用 GAgent/team/member invoke、schedule
provisioning 或 raw proxy。新增其他 workflow/control 工具仍必须通过单独的 route ceiling 审查，
不能因注册了 `IAgentToolSource` 而自动暴露。
