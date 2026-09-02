---
title: "Per-User NyxID Binding via OAuth Broker"
status: accepted
owner: eanzhao
---

# ADR-0018: Per-User NyxID Binding via OAuth Broker

## Update 2026-07-24 - channel 历史 binding 通过 replacement 恢复

`/whoami` 证明 Aevatar 的 external-subject binding pointer 存在。读取该 sender 自己的 NyxID connected-service inventory 只要求该 exact binding 能换出窄的 request-local inventory capability；它不要求 binding 同时覆盖 Aevatar LLM route、Ornn、Sandbox 等全部 runtime services。完整 runtime route readiness 仍由严格 capability broker 独立校验。inventory 查询失败不得反推“未绑定”，也不得建议 `/init`；`/init` 只用于真实 binding 缺失/撤销、用户主动补充 runtime service 授权或 same-owner renewal。任何路径都不得使用 bot owner credential、容器内 NyxID CLI 登录态或 catalog 猜测用户已经连接的服务。

历史 Aevatar authorize URL 没有把 exact external subject 写入 NyxID binding。NyxID 的 in-place grant review 会联合校验 OAuth client、authenticated user、`binding_grant_id` 与 binding 中已经保存的 external subject；因此给旧 binding 的 review URL 临时补 external subject 仍会因“缺失 subject”或“subject 不匹配”失败，不能完成迁移。当前 channel contract 改为：

- `/oauth/authorize` 始终发送 `external_subject_platform`、可选的 `external_subject_tenant`、`external_subject_external_user_id` 与配置化必需 resources，让 NyxID 签发带 exact subject 的新 binding。
- 已有 binding 的 `SHA-256(binding_id)` 只放入 HMAC state 作为 callback CAS 预期值，不发送浏览器参数 `binding_grant_id`；raw binding id 也不进入浏览器 URL。
- callback 必须确认 state hash 仍匹配当前 readmodel binding，并确认新登录 NyxID owner 与旧 binding owner 相同。正常 owner 来自 binding readmodel；仅当迁移 2026-07-17 以前、缺少 `owner_scope_id` 的 binding 时，才通过 owning-client `GET /oauth/bindings/{binding_id}` 读取 NyxID 权威 owner。
- callback 在采用新 binding 前必须按 incoming binding id 试签一次 `proxy` capability，并验证 token 覆盖全部配置化必需 services。scope/service 不完整返回 409，binding 已失效返回 502，NyxID 校验暂不可用返回 503；三类失败都撤销 incoming binding，且不投递 commit/replacement。部署切换期间仍返回 `binding_updated=true` 的旧链接也必须对当前 binding 做同样试签后才能报告成功。
- 校验通过后 callback 投递 `ReplaceBindingCommand { expected_previous_binding_id, binding_id, owner_scope_id }`。`ExternalIdentityBindingGAgent` 在 actor turn 内做 CAS，先提交 replacement 事实，再把旧 binding 放入 actor-owned retirement 队列；清理失败由后续 activation 继续对账。
- owner 不同则拒绝并撤销新 binding。切换 NyxID 账号仍必须显式 `/unbind` 后再 `/init`，禁止 `/init` 静默换号。
- callback 继续兼容 NyxID 返回 `binding_updated=true` 的旧请求，但正常 channel `/init` 不再发起该协议。

普通 runtime turn 继续通过严格 binding token-exchange 获得覆盖配置化必需 services 的 request-local user capability。自然语言“我连接了哪些服务”进入普通 `LlmReplyRequested / AgentRun`，以 `ChatStreamAsync` 先执行只读 `use_skill(skill="nyxid-service-discovery")`，再执行 `nyxid_service_inventory`；连接、维护与调用请求分别加载 `nyxid-service-connect`、`nyxid-service-maintenance` 与 `nyxid-service-call`，禁止依赖已退役的通用 `nyxid` skill 名。inventory 工具只在执行阶段签发独立的窄 capability，并以 current sender 身份读取 `GET /api/v1/keys`，最终答案沿既有 CardKit streaming lifecycle 输出。remote skill read 与 inventory read 使用两个独立 issuer，均不得回退 bot owner token、持久化 bearer 或从 channel 字段猜 NyxID authority。该路径不运行 `code_execute` 或 `nyxid service list`。inventory read 失败只代表本次读取失败，不代表 binding 缺失；除非 typed binding 明确缺失或撤销，不得无条件建议 `/init`。省略 `mount_workflows` 或传 `false` 的 `use_skill` 只加载说明，只有显式 `mount_workflows=true` 才可能写 workflow。

## Update 2026-07-17 - 浏览器选择与 NyxID 授权事实分离

Studio Consent 的产品语义是“用户从 NyxID 已有且自己可授权的 service 中选择完整集合”,不是“Aevatar 预先决定完整集合”.所谓浏览器不能自造授权事实,不表示浏览器不能选择;它只表示浏览器提交的 ID 不能自行证明授权成立.NyxID 必须重新校验 service 存在性、当前用户的 ownership/org scope 与最终 Consent,再把结果写入 authorization code、refresh grant 和短期 access token.

当前 contract 为:

- Studio `/oauth/authorize` 继续不发送部署必需 `resource`,否则 NyxID 当前的 RFC 8707 交集语义会把 All Services 或用户额外勾选收窄为 Aevatar 最低集合.NyxID Consent 页面可以自由选择已有 service;未知、已删除或不属于当前用户的 ID 由 NyxID 服务端拒绝.
- authorization-code exchange、浏览器 refresh 和 binding token-exchange 都不发送 `resource`,继承 NyxID 已确认的完整 Consent grant.Aevatar 不回写、不替换也不缩窄该集合.
- broker 首先读取 NyxID token 的 `resources`、`allowed_service_ids` 与 `allow_all_services` 强类型 claim.若 `resources` 已覆盖部署最低集合则直接接受;否则使用同一枚完整 grant token 读取 NyxID `/api/v1/user-services`,取得权威 `UserService.id -> resource_uri` 映射.
- All Services 按 catalog 中当前可用的全部 `resource_uri` 校验;显式选择只使用 token 已签名 `allowed_service_ids` 对 catalog 映射做过滤.因此 catalog 中存在但用户未选中的 service 不能通过校验,浏览器单独提交的 ID 也不能越过 NyxID 签发事实.
- 校验成功后返回原始完整 grant token.用于校验的 catalog read 不产生第二枚 token,也不把部署最低集合变成用户授权上限.

## Update 2026-07-17 - OAuth client ID 以部署配置为唯一权威

生产 Console 同时存在静态 BackendConsole OIDC 配置与 `AevatarOAuthClient` Actor 中历史 DCR client ID，导致内嵌 `/admin` 使用配置值，而 Studio `/api/auth/nyxid/config`、authorization-code exchange、token-exchange 与 binding revoke 使用 Actor 旧值。一次登录的 client identity 因入口不同而漂移，且 DCR 失败时无法通过更新配置修复。

最终 contract 收敛为：

- `Aevatar:BackendConsole:OidcClientId` 是浏览器 PKCE 与 NyxID broker 操作的唯一 client ID 配置源。它是公开部署事实，不是 secret。
- `AevatarOAuthClientProjectionProvider` 从配置读取 `client_id`，从 Actor current-state readmodel 读取 NyxID authority、redirect/scope contract、HMAC key 与 broker capability observation。只有 readmodel 的 client ID 已与配置一致时才返回 snapshot；不一致期间 fail closed，禁止把新配置 ID 与旧 Actor 事实拼接，也禁止回退历史 client ID。
- Host bootstrap 把配置值封装为强类型 `ProvisionAevatarOAuthClientCommand` 投递给 well-known Actor。Actor 负责串行、幂等地物化配置，并清除历史 DCR retry；bootstrap 同步返回只承诺 dispatch accepted，不在启动调用栈等待 projection。
- `POST /api/oauth/aevatar-client/rebuild` 只允许平台管理员重新投递当前配置，request 不再接受 `client_id` 或 issued-at 字段，避免管理员请求体成为第二权威源。
- 配置缺失时 fail closed：Studio login config、broker 操作与 bootstrap 均不得使用 Actor/readmodel 中的旧 client ID 兜底。
- NyxID 侧必须预先把该 public client 注册为允许 canonical scope、完整 redirect URI allowlist 与 broker capability。正常生产启动不再依赖 DCR 生成 client ID。

## Update 2026-07-16 - 完整 Consent service 边界与 Studio 后端授权契约

授权页完成后,authorization code 与 broker binding 已经承载用户最终确认的 service 集合.如果 aevatar 在 authorization-code exchange 或后续 binding token-exchange 再发送固定 `resource` 集合,NyxID 会按 RFC 8707 将本次 token 收窄到该集合,使用户在 Consent 页面额外选择的 service 对 Aevatar runtime 不可用.配置化必需 resource 因此只能是校验下限,不能成为用户授权上限.

当前 contract 调整为:

- Studio 浏览器不再从环境变量维护默认 service,也不在 `/oauth/authorize` 拼装 `resource`.默认预选由 NyxID OAuth Client 的 `default_service_catalog_slugs` 负责,最终授权集合由用户在 Consent 页面确认.
- `/api/auth/nyxid/config` 只返回 Studio 登录所需的 authority、client id 与 scope,不再暴露服务器内部的必需 resource 集合,避免把运行时最低依赖误解为用户授权上限.
- Studio finalization 提供默认值为 `false` 的 typed `serviceAccessReview` 请求字段,供未来前端在用户主动发起授权审查时传入 `true`.前端应以 `prompt=consent` 进入 NyxID 的权威 Consent 页面;service picker 可以选择 NyxID catalog 中的已有资源,但服务端必须重新校验选择结果,Aevatar 不实现第二套授权事实源.
- channel `/init` 的 `/oauth/authorize` 仍显式请求配置化的运行必需 resource 集合:核心 `aevatar`、`Aevatar:NyxId:DefaultRoute`、`Aevatar:Ornn:NyxIdSlug`、`Aevatar:NyxId:SandboxServiceSlug` 以及 `Aevatar:NyxId:AdditionalRequiredServiceSlugs`;该 resource flow 是 channel 的最低能力 grant,与 Studio 的完整用户选择边界分开解释.
- authorization-code exchange 必须省略 `resource`,直接继承 authorization code 中已经完成的 Consent service 边界,不得由 callback/finalization 再次缩窄.
- broker 的短期 token-exchange 省略 `resource`,继承完整 binding grant.若 token 未枚举全部必需 `resources`,broker 结合 token 已签名的 All Services/显式 service ID grant 与 NyxID 权威 user-service catalog 校验最低集合,并继续把原始完整 token 交给 runtime.
- Studio finalization 只在显式 `serviceAccessReview` 或现有 binding 已失效时替换 binding.新 binding 必须先按 ID 完成一次短期 token 校验;actor 通过 `expected_previous_binding_id` 做 compare-and-swap,提交 replacement 后才撤销旧 binding.清理失败保存在 actor-owned `pending_retirement_binding_ids`,激活时继续对账,不使用进程内 registry.
- channel `/init` 与 Studio service access review 都使用新 binding + actor CAS replacement；2026-07-24 以前的 `binding_grant_id` / `binding_updated` 原地更新决定已被顶部 update 取代。

本节取代 2026-07-15 中“authorization-code exchange 重复发送必需 resource”以及 2026-07-10 中由 Studio 浏览器维护 resource 列表的决定.

## Historical update 2026-07-15 - 完整 service grant 与 binding 原地授权审阅

本节保留当时的 resource 问题与历史协议背景；其中 `binding_grant_id` / `binding_updated` 主链已被 2026-07-24 replacement contract 取代。

生产 Lark bot 暴露出一条 resource contract 断裂:sender 在 `/init` 前可以回复;`/init` 后 runtime 改用 sender binding token,调用默认 `chrono-llm-public`、Ornn `ornn-api` 与 Sandbox `chrono-sandbox` route 时被 NyxID 以 `api_key_scope_forbidden` 拒绝.线上 token 的 `allowed_service_ids` 只有 `aevatar`,没有实际被调用的 LLM、Ornn 与 Sandbox service.

根因是 `/oauth/authorize`、authorization-code exchange 和 broker token-exchange 三处都只发送了 `resource=.../aevatar`,而 runtime 把同一个 sender capability 用于 Aevatar capability、默认 LLM route、Ornn skill API 与 Sandbox code execution. NyxID 按 RFC 8707 正确地把 binding 和短期 token 收窄到所请求的 resource;因此这不是 proxy fallback 问题,也不能通过静默改用 bot owner 身份修复.

最终 resource contract 调整为:

- binding 的必需 resource 集合是 `aevatar`、部署默认 LLM、Ornn 与 Sandbox service. Mainnet Host 分别从 `Aevatar:NyxId:DefaultRoute`、`Aevatar:Ornn:NyxIdSlug` 和 `Aevatar:NyxId:SandboxServiceSlug` 注入实际 slug;Sandbox 未配置时使用 tool provider 的默认值 `chrono-sandbox`.`NyxIdBrokerOptions.AdditionalRequiredServiceSlugs` 作为其他 provider 的可配置扩展点,Identity 层不维护第二份 provider 默认值.
- channel `/oauth/authorize` 使用配置化必需集合;Studio `/oauth/authorize`、authorization-code exchange、broker token-exchange 与 `/api/auth/nyxid/config` 不携带该集合,以保留用户最终 Consent 边界.
- broker 收到短期 access token 后必须验证其授权覆盖整个必需集合.优先使用 `resources` claim;Consent-only grant 则结合签名的 `allowed_service_ids/allow_all_services` 与 NyxID user-service catalog 验证.只含 `aevatar` 的 token 不再视为可用 sender capability,但必需集合之外的用户授权必须保留.
- 历史决定：NyxID binding grant 是服务授权的唯一事实源;aevatar 只持有 opaque `binding_id`. 已绑定 sender 再次 `/init` 时,aevatar 把 `SHA-256(binding_id)` 放入浏览器可见的 `binding_grant_id`,同时发送 exact external subject并把同一哈希封入 HMAC state作为 callback 预期值;raw binding credential不离开服务端.
- 历史决定：NyxID 按 authenticated user、OAuth client 与 exact external subject 校验待审阅 binding,在 consent 页面展示当前授权、应用必需服务与可选新增服务. 必需服务不可单独取消;用户可拒绝整个请求,也可增删其他可选服务.
- 历史决定：授权确认后 NyxID 以 optimistic rotation 原地替换 binding 背后的 refresh grant,返回 `binding_updated=true`,不返回新 `binding_id`. aevatar callback 校验本地 binding 哈希未变化后直接成功,不提交新的 binding actor 事件.
- 历史决定：`invalid_target`、`invalid_scope` 或缺失 resource claim 表示 grant 不足,不是 binding 已撤销;调用侧必须保留本地 binding并引导 `/init` 原地审阅. 只有 NyxID 明确返回 `invalid_grant`、binding revoked 或 not-found 时才事件化清理本地 binding.

## Update 2026-07-16 - OAuth client projection ACL verification

`AevatarOAuthClientDocument` contains the state-token HMAC key, so an Elasticsearch-backed Mainnet host always registers `AevatarOAuthClientEsAclStartupGuard`. The deployable default is `Warn`: the guard verifies the internal projection wiring, runs the live `IOAuthClientEsAclProbe`, and emits an actionable warning when the grant cannot be confirmed without turning an otherwise healthy rollout into a crash loop. Mainnet does not overwrite the operator-bound enforcement mode or hardcode the ACL attestation.

`Strict` is an explicit deployment policy with two independent requirements: the live `IOAuthClientEsAclProbe` must return `Restricted`, and the operator-provided `ChannelIdentity:OAuthClient:ElasticsearchAcl:GrantMatchesGrainEventStoreInternal` attestation must be `true`. `Unverifiable` / `Unavailable` never pass by attestation alone. A deployment must not enable `Strict` until both prerequisites are installed.

The built-in `HttpOAuthClientEsAclProbe` calls `_has_privileges` with the same Elasticsearch identity as the projection store. That proves only the current identity's access; it cannot prove that other identities, wildcard roles, file realms, API keys, or service accounts are denied. It therefore reports `Unverifiable` for a security-enabled success response instead of fabricating `Restricted`.

An Elasticsearch deployment that enables `Strict` must pre-register exactly one stronger `IOAuthClientEsAclProbe` before `AddAevatarMainnetHost`. That verifier owns the environment-specific effective-permission or infrastructure-policy audit and may return `Restricted` only after proving the index grant. Mainnet replaces only the module's `UnavailableOAuthClientEsAclProbe` fallback; it preserves the deployment verifier and rejects multiple custom registrations. Without such a verifier, the stock Elasticsearch path stays in `Warn` and reports the unconfirmed restriction at startup.

## Update 2026-07-10 - NyxID service access 使用 RFC 8707 resource

NyxID 2026-07-06 至 2026-07-08 的 OAuth 更新把第三方应用 service access 改为 default-deny,并新增两种语义不同的入口:

- Developer App 的 `default_service_catalog_slugs` 只是 consent UI hint. NyxID 在构建授权页时把 catalog slug 解析成当前用户的 `UserService`,用于预选;用户仍可取消选择.
- OAuth `resource` 是 RFC 8707 的本次授权资源 contract. NyxID 把它解析为用户拥有的 service,写入 authorization code / refresh token / broker binding 的 service allowlist,并在 access token 的 `resources` claim 中回传.

`aevatar`、部署默认 LLM、Ornn 与 Sandbox service 是 Studio 登录、channel binding 和后续对话/skill/code execution 正常工作的必要资源,不是可选 UI 偏好. 因此最终 contract 为:

- channel `/init` 的 `/oauth/authorize` 请求显式携带配置化必需 resource 集合. `nyxid_api_base_url` 对应 NyxID backend `BASE_URL` / Aevatar `Aevatar:NyxId:ApiBaseUrl`,不得从浏览器 OAuth authority 或 JWT issuer 派生；各 service slug 由对应 provider 配置注入.控制台登录不发送该集合.
- NyxID authorization decision 必须在服务端校验前端提交的 service ID 是否存在且可由当前用户授权;前端异步加载的 service picker 负责选择体验,不能单独成为授权事实源.带 RFC 8707 `resource` 的 flow 还必须由服务端解析并合并对应必需 service ID.
- authorization-code exchange、控制台 refresh 与 broker 的主 token-exchange 都省略 `resource`,继承完整 Consent 边界;Aevatar 不把用户在前端选择的完整 grant 收窄为部署最低集合.
- broker 每次拿到完整 grant token 后先校验 `resources` claim.若 claim 未枚举必需 resource,使用 token 的 `allow_all_services/allowed_service_ids` 与 NyxID `/api/v1/user-services` 的权威 ID/resource 映射校验;catalog 中存在但 token 未授权的 ID 不计入显式 grant.校验只读,返回给 runtime 的仍是原始完整 grant token.
- `/api/auth/nyxid/config` 不返回 resource 列表;前端不得自行猜 service ID,也不得把 Developer App 默认项当作授权事实.

NyxID 当前 `/oauth/register` 的 `RegisterClientRequest` 不接受 Developer App 的默认 service 字段. 因此 aevatar 不在 DCR 中发送 `default_services`,也不在 actor state/readmodel 中记录无法从 NyxID 验证的“已注册默认项”. Developer App 可以额外配置同名默认项改善 consent 展示,但它不是运行正确性的事实源.

## Update 2026-04-30 — NyxID#576 / PR #578 contract alignment

ADR 第一版 (#549 时代) 假设 aevatar 持有一个**集群级 OAuth `client_secret`**,token-exchange / DELETE binding 走 confidential-client + Basic auth。implementation PR #521 自审过程中发现这条假设违反 aevatar "无 NyxID 颁发 secret 落地" 的不变量(`client_secret` 一旦泄漏,fleet-wide blast radius 覆盖所有用户),据此提了 [ChronoAIProject/NyxID#576](https://github.com/ChronoAIProject/NyxID/issues/576),要求 NyxID broker 接口接受**公共客户端 + PKCE**。

NyxID 在 [PR #578](https://github.com/ChronoAIProject/NyxID/pull/578) (commit `516abda`) 落地了三处对齐:

| 端点 | 之前(confidential only) | 现在(public + PKCE 也可) |
|---|---|---|
| `POST /oauth/register` (DCR) | 忽略 `body.scope`,硬编 `DEFAULT_MCP_ALLOWED_SCOPES` | 持久化请求的 `scope`(经 `validate_allowed_scopes`)进 client 的 `allowed_scopes` |
| `POST /oauth/token` (token-exchange) | 强制要 `client_secret`,403 if absent | `client_type=public` 时 `validate_client_secret` skip;`is_broker_client` 改为 `broker_capability_enabled OR allowed_scopes 含 broker_binding`,DCR-issued public client 直接命中 |
| `DELETE /oauth/bindings/{id}` | Basic auth `client_id+secret`,缺则 silent 204 | 接受 `?client_id=` query 参数;ownership check 仍在 `revoke_binding_by_client` |

**Aevatar 端最终态(PR #521):**

- DCR 自举注册公共客户端,scope 含 `urn:nyxid:scope:broker_binding`
- token-exchange 仅传 `client_id`,不传 `client_secret`(根本没有)
- DELETE binding 走 `?client_id=` query 参数
- 不存任何 NyxID 颁发的 secret;唯一持久态密钥是本地自生成的 state-token HMAC key(`AevatarOAuthClientGAgent.State.HmacKey`),不出集群边界
- broker 流不再需要 ops 一次性手动翻 `broker_capability_enabled` 开关,scope-based 自动触发

后文 §Decision / §Storage Boundary / §Security Threat Model 中所有对 `client_secret` 的提及保留作为历史背景说明,但**现状以本 Update 为准**。`#375` user-secret 不变量与本 ADR 的 zero-NyxID-secret 状态现在是一致的——aevatar 不再需要"service-level secret 走 KMS / config 加载"的例外条款。

## Context

Discussion `#400` 提出把 channel bot(Lark / Telegram / Discord)消息链路里的"sender"语义从 bot owner 改成**per Lark user 自己的 NyxID subject**:每个 sender 第一次交互走 `/init` 走一轮 NyxID 登录,之后用其自身 nyx subject 跑 LLM、tool、capability。

当前现实:

- 入站 webhook `Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` 只验 NyxID relay JWT,只解出 bot owner 的 `scope_id`
- `ChannelConversationTurnRunner.BuildReplyMetadata` 把 bot owner 的 `user_access_token` 透传给 LLM tool
- 任何 Lark user 跟 bot 聊天都在代表 **bot owner** 的 NyxID 账号说话
- 现有 `ChannelUserBindingGAgent`(`agents/Aevatar.GAgents.Channel.Runtime/UserBinding/ChannelUserBindingGAgent.cs`)是 per-(bot, channel, sender) actor,持有 bot 范围内的用户**偏好**(locale / timezone / mute)+ `credential_ref`(per-bot 凭据指针),scope 不同于 platform-级 NyxID identity
- 没有 platform 级 `(platform, tenant, external_user_id) → nyx_subject` 的持久映射,也没有 `INyxIdCapabilityBroker`

`#400` 原 RFC 要求 NyxID 加 5 个端点(challenge 签发、`/cli-auth` 扩展 `binding_jti`、主动 webhook 回调、bindings 查询、bindings revoke)。回扫 NyxID surface 后这 5 个 ask 大部分能用现有 OAuth/OIDC primitive 替代;唯一真正缺失的是 broker 形态的"接入方代用户拿短期 access_token,但永不接触 refresh_token"。

本 ADR 第一版草稿曾接受 `LocalRefreshTokenCapabilityBroker`(aevatar 加密持 refresh_token)作为过渡实现。讨论后否决,详见 Decision Rationale。当前决定走 broker 路径,实现依赖 NyxID 侧新 issue。

## Decision

aevatar 实现 per-user NyxID binding,**作为 NyxID broker 的 OAuth 接入方**:

- 用标准 OAuth Authorization Code + PKCE 流程发起 binding(`/oauth/authorize` 浏览器跳转;PKCE `code_verifier` 不入 grain state — 见 §Storage Boundary)
- **aevatar 不接收、不持有 user refresh_token**;binding 完成时 NyxID 返回不透明 `binding_id`,aevatar 仅持 `ExternalSubjectRef → binding_id` 映射(由新 actor `ExternalIdentityBindingGAgent` 拥有,见 §Actor Architecture)
- 每次 turn 用 RFC 8693 token-exchange (`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`,`subject_token=<binding_id>`,`subject_token_type=urn:nyxid:params:oauth:token-type:binding-id`,client 用 `client_secret` 鉴权)调 NyxID 拿短期 access_token,塞进 `AgentToolRequestContext`
- aevatar 主动撤销:`DELETE /oauth/bindings/{binding_id}`;**NyxID 主动撤销**(用户在 NyxID UI 直接 revoke):下次 token-exchange 收到 `invalid_grant` → aevatar 视作 binding 已亡,事件化撤销本地 binding actor 并要求 sender 重新 `/init`(NyxID 是 source of truth,aevatar 单向同步)
- **OAuth authorize URL 只通过私域回传**(Lark DM 等 sender-only channel),不在群聊明文返回;无 DM 能力的平台不接入 broker 模式 — 防 OAuth state hijack(群里他人点开 URL 用自己 NyxID 登录,callback 把 sender A 绑成 NyxID B)
- **未绑定 sender 一律强制 `/init`,不区分 1:1 vs 群聊,不回落到 bot owner**:`IExternalIdentityBindingQueryPort.ResolveAsync` 返回 null 时,turn runner 直接以 `/init` 引导取代 LLM 调用;bot owner 不享有"默认用户身份"特权,只承担注册/管理 bot 的角色
- **`/init` 双路径语义**:未绑定 sender 发起新 binding;已绑定 sender 发起 service authorization renewal,签发新 binding 后只允许 same-owner actor CAS replacement. 切换 NyxID 账号必须显式执行 `/unbind` 后再 `/init`
- **`/unbind` 行为**:slash-command 路由 → `RevokeBindingAsync(externalSubject)` → `DELETE {nyxid}/oauth/bindings/{binding_id}`(NyxID 同步 revoke,NyxID 是 source of truth)→ `ExternalIdentityBindingRevokedEvent` 落 `ExternalIdentityBindingGAgent`。NyxID 调用失败(网络 / 5xx)→ 本地不擅自标 revoked,返回错误并提示重试,**避免 source-of-truth 不一致**(本地认为已 revoke 但 NyxID 仍 active)。成功后 `ResolveAsync` 返回 null,sender 需重新 `/init`

aevatar grain state、projection、log、metric 持有 zero long-lived user secret material,对齐 `#375` 不变量;aevatar 自身的 service-level secret(OAuth `client_secret`、state-token HMAC 签名 key)按基础设施 secret 管理(rotation、KMS、out of scope of #375 user-secret 不变量)。

`/init` 流程在 OAuth + broker primitive 上的等价改写:

```
/init
  -> ChannelConversationTurnRunner 前置 slash-command 路由(不进 LLM)
  -> ResolveAsync(externalSubject)
     miss -> 新 binding path
     hit  -> renewal path:计算 SHA-256(binding_id),只放入 HMAC state
             作为 callback CAS 预期值;不发送 binding_grant_id,
             raw binding_id 不进浏览器 URL
  -> aevatar 生成 PKCE pair + correlation_id,把
     state_token = HMAC(service_key, {correlation_id, external_subject_ref,
                                       pkce_verifier, expected_binding_hash?, exp(<=5min)})
     编码进 OAuth `state` 参数(stateless,verifier 不落 grain state)
  -> 通过 Lark DM(私域,非群聊)回 sender:
     "{nyxid}/oauth/authorize?client_id=aevatar-channel-binding
      &redirect_uri=https://aevatar/api/oauth/nyxid-callback
      &response_type=code&code_challenge=...&code_challenge_method=S256
      &scope=openid+urn:nyxid:scope:broker_binding&prompt=consent
      &external_subject_platform=<platform>
      &external_subject_tenant=<tenant-if-present>
      &external_subject_external_user_id=<external-user-id>
      &resource=<aevatar>&resource=<default-llm>&resource=<ornn>&resource=<sandbox>
      &state=<state_token>"
  -> 用户登录 → NyxID 302 回 aevatar /api/oauth/nyxid-callback?code=...&state=...
  -> aevatar callback handler:
       验 state_token HMAC + exp -> 解出 ExternalSubjectRef + pkce_verifier
       POST {nyxid}/oauth/token
            (grant_type=authorization_code, code, code_verifier, client_secret)
       -> { access_token, id_token, binding_id }
       从同次返回的 id_token 解码 `sub`/`name` claim 做"已绑定 <masked sub>"展示文案;
       **不调 /oauth/userinfo**(OIDC 标准,sub claim 在 id_token 已自带,省一次 round-trip);
       **不持久化任何 token**(handler 退出前直接丢弃 access_token / id_token)
       用 incoming binding_id 试签短期 proxy capability并验证必需 services;
       校验失败撤销 incoming binding且不投递写命令
     miss -> 投递 CommitBindingCommand
     hit  -> 校验 expected_binding_hash + same owner,
             投递 ReplaceBindingCommand { expected_previous_binding_id,
                                          binding_id, owner_scope_id }
     endpoint 只返回 command accepted + stable command id;actor committed、
     readmodel visible 与旧 binding retired 均通过后续事件/投影观察

turn
  -> ResolveAsync(externalSubject) -> binding_id (查 ExternalIdentityBindingGAgent projection)
  -> miss -> 引导 /init,不调 LLM,不 fallback
  -> POST {nyxid}/oauth/token  (grant_type=urn:ietf:params:oauth:grant-type:token-exchange,
                                subject_token=<binding_id>,
                                subject_token_type=urn:nyxid:params:oauth:token-type:binding-id,
                                client_secret)
     -> short-lived access_token (TTL <=5min)
  -> 塞 AgentToolRequestContext (key 名 `nyxid.capability_handle`)
  -> 401/invalid_grant -> 事件化撤销本地 binding actor + 提示 sender 重新 /init
```

## Storage Boundary

| 数据 | aevatar grain state | aevatar 浏览器 cookie / 内存 | NyxID |
|---|---|---|---|
| `ExternalSubjectRef → binding_id` 映射(`ExternalIdentityBindingGAgent` 持) | ✓ | | |
| `binding_id`(opaque) | ✓ | | ✓ 索引到内部 refresh_token(source of truth) |
| `SHA-256(binding_id)` replacement CAS 预期值 | HMAC state 中短期携带 | OAuth state / callback 内存,exp ≤5min | ✗ 不发送给 NyxID |
| `owner_scope_id` | ✓ actor state + readmodel | callback 从新 id_token 解析;legacy binding 缺失时一次性 introspect | ✓ `nyx_subject` source of truth |
| PKCE `code_verifier`(short-lived) | ✗ never | ✓ 嵌在 HMAC-签的 stateless `state` token,exp ≤5min | |
| `nyx_subject`(opaque `sub` claim) | ✗(无明确用途,不缓存) | callback 阶段从同次返回的 `id_token` 解码,handler 内一次性用于展示文案,不持久化 | ✓ source of truth |
| Initial `access_token` / `id_token`(callback 拿到) | ✗ never | ✓ handler 内一次性使用(从 id_token 取 sub),退出前直接丢弃；不调 `/oauth/userinfo`。callback 另以 opaque incoming binding id 试签短期 capability，必要时用该短期 token读取权威 service catalog做必需授权校验 | 签发方 |
| User refresh_token | ✗ never | ✗ never | ✓ encrypted |
| Per-turn short-lived access_token | per-turn `AsyncLocal`(`AgentToolRequestContext`),不持久化 | | 签发方 |
| state-token HMAC key + OAuth `client_secret` | ✗ 不在 grain state(基础设施 secret) | ✓ 通过 KMS / config 加载到进程 | |

`binding_id` 在 RCE 场景下的语义跟 refresh_token 不同:它必须配合 aevatar 的 `client_secret` 才能换 token,而 NyxID 可以对 `(client_id, binding_id)` 做 rate limit、异常 audit、用户主动 revoke。NyxID 因此是真正的 control point,而不只是"换个地方存的 refresh_token"。

## Actor Architecture

为什么新增 `ExternalIdentityBindingGAgent` 而**不**扩展现有 `ChannelUserBindingGAgent`?

| | ChannelUserBindingGAgent(existing) | ExternalIdentityBindingGAgent(new) |
|---|---|---|
| Key | `(bot_instance_id, channel, sender_canonical_id)` | `(platform, tenant, external_user_id) = ExternalSubjectRef` |
| Scope | per-bot 用户**偏好** | platform-级 NyxID **身份绑定** |
| State | locale / timezone / mute / muted_topics / (deprecated) `credential_ref` | binding_id / bound_at |
| Lifecycle | 长期,随用户偏好高频更新 | 长期,绑定/撤销低频 |
| 事实源 | aevatar(用户口味) | NyxID(身份);aevatar 持 binding_id 是 NyxID 资源指针 |

CLAUDE.md "Actor 即业务实体"禁止的是按技术功能(读/写/投影)拆分**同一**实体;两个 actor 在不同 keying 域承载不同业务事实(用户口味 vs 平台级身份),不构成同一实体的拆分。

具体论据:同一 Lark user(`(lark, tenant_X, user_Y)`)在多个 bot 中讲话时,**期望使用同一份 NyxID 身份**(用户在 NyxID 那边只有一个账号,与对哪个 bot 讲话无关);但 mute / locale 偏好可以 per-bot 不同(在工作 bot 静音、在生活 bot 不静音)。前者按 platform 级 keying,后者按 per-bot keying,合并到单一 actor 会强迫"per-bot 重复绑定 NyxID",违反产品语义。

`ChannelUserBindingState.credential_ref`(`agents/Aevatar.GAgents.Channel.Runtime/protos/channel_user_binding.proto:29`)在 broker 模式下变冗余:

- Implementation PR(post-NyxID#549):`NyxIdRemoteCapabilityBroker` 上线后,turn 路径不再读 `credential_ref`,改 query `IExternalIdentityBindingQueryPort`
- proto 演进:`credential_ref` 字段标 `deprecated = true`,字段编号 4 保留不复用,同步停止写入;同时新增 `ExternalIdentityBindingState { ExternalSubjectRef external_subject = 1; string binding_id = 2; google.protobuf.Timestamp bound_at = 3; }`
- 删除窗口:broker 模式上线 + 一个有 channel-runtime proto break 的 release 后(下一次重大版本),正式删除字段;迁移期间已有 `credential_ref` 数据不读不写,只保留事件日志兼容
- **没有 fallback 顺序**:broker 上线后,turn 路径**只**读 `ExternalIdentityBindingGAgent.binding_id`;若 miss,直接引导 sender `/init`。**不**读、**不** fallback 到 `ChannelUserBindingState.credential_ref`(即便 grain 内还有遗留值)。这跟 Decision section "未绑定一律 /init,不回落"是一回事,在字段层面再确认一次,避免 deprecation window 中间态行为未定义

## Outbound Send: AuthContext × Broker

`IChannelOutboundPort.ContinueConversationAsync(... AuthContext auth ...)` 在 `OnBehalfOfUser` 模式下用 `AuthContext.user_credential_ref`(`agents/Aevatar.GAgents.Channel.Abstractions/protos/channel_contracts.proto:138`)选择代用户身份。Broker 模式下:

- `AuthContext` 新增 typed 字段 `ExternalSubjectRef external_subject = 4`,作为 broker outbound 的**唯一**身份字段。broker outbound adapter 只读这个 typed 字段,**不读、不重载**旧 `user_credential_ref string = 2`(对齐 CLAUDE.md "统一 Protobuf"禁自定义字符串格式 + "删除优先,不留 compat shim";broker 路径整体 gated on NyxID#549,无任何过渡期需靠字符串重载兜过)
- 每次发送前调 `IssueShortLivedAsync(externalSubject, scope)` 拿短期 access_token,**不缓存,不复用**;每次 outbound 都换新 handle
- 旧 `user_credential_ref` 字段:不被 broker outbound 读取,但仍由其他非 broker 路径继续使用按既有语义自然 deprecate;删除窗口跟 §Actor Architecture 一致(下次 channel-runtime proto break)
- 字段拆分原则与 §Actor Architecture 同源:旧 `user_credential_ref` 是 per-bot 凭据指针,broker 模式下走 platform-级 typed `ExternalSubjectRef`,无中间重载期

## INyxIdCapabilityBroker:Single Production Adapter

`INyxIdCapabilityBroker` 是 capability 层的 **write-side** seam:发起 binding、撤销 binding、签发短期 token。**Read-side**(resolve external subject → binding)走 `IExternalIdentityBindingQueryPort`;两边契约分离,业务代码必须按用途选 port,不混用。**所有 `externalSubject` 参数必须是 typed `ExternalSubjectRef`(proto-typed value object)**,不接受 string / generic bag(对齐 CLAUDE.md "核心语义强类型"):

- `StartExternalBindingAsync(ExternalSubjectRef externalSubject) -> BindingChallenge`
- `RevokeBindingAsync(ExternalSubjectRef externalSubject)`
- `IssueShortLivedAsync(ExternalSubjectRef externalSubject, CapabilityScope scope) -> CapabilityHandle`
  - 抛 `BindingNotFoundException`(没绑过)或 `BindingRevokedException`(NyxID 端已 revoke,`invalid_grant`);两个异常语义不同,调用方按需分支处理

`IExternalIdentityBindingQueryPort` 单方法读 seam:

- `ResolveAsync(ExternalSubjectRef externalSubject) -> BindingId?`(返回不透明 `binding_id`;不暴露 `nyx_subject`;读 projection,不调 NyxID,不重建 actor 直接态)

唯一生产 broker 实现 `NyxIdRemoteCapabilityBroker`,内部:

- `IssueShortLivedAsync` 调 `POST /oauth/token`(grant_type=token-exchange,subject_token=binding_id);收到 `invalid_grant` 抛 `BindingRevokedException` 让上层事件化撤销
- `RevokeBindingAsync` 调 `DELETE /oauth/bindings/{binding_id}`(aevatar 主动撤销)
- `StartExternalBindingAsync` 构造 `/oauth/authorize` URL + PKCE pair,verifier 编码进 HMAC-签的 stateless state token(不落 actor state),返回 URL 由调用方通过私域 channel 投递
- 内部为完成上述操作需要的 binding 解析 → 通过 `IExternalIdentityBindingQueryPort` 注入,**不**自己实现读路径

唯一生产 query 实现 `ExternalIdentityBindingProjectionQueryPort`(后续 PR),从 binding readmodel projection 读取。

不引入 `LocalRefreshTokenCapabilityBroker`。"两个 adapter 才是真 seam"由 `InMemoryCapabilityBroker`(test fake,同时实现 broker + query port 两个接口共享 in-memory 字典)+ Remote 满足;test fake 不构成生产意义上的并行实现。

## Decision Rationale

为什么否决 Local adapter:

aevatar 是跑任意 LLM tool 的 agent runtime,prompt injection 与 tool 越权属于固有 attack surface。这类 host 上 grain state 内的 secret material 即便静态加密,RCE 后攻击者几乎一定能拿到 `IDataProtection` key ring,加密形同虚设。"加密"在 aevatar 这种高 attack-surface 服务上不构成真正的纵深防御。

| 场景 | Local(aevatar 加密持 refresh_token) | Remote(NyxID broker) |
|---|---|---|
| Event store 备份泄露 | 加密保护;需同时拿到 `IDataProtection` key ring 才能解 | 完全不可解(grain 只存 binding_id + opaque sub) |
| Aevatar 进程 RCE | 全量 binding 的长期 refresh_token 一锅端,可静默 impersonate 所有用户 | 攻击者持 binding_id + client_secret 仍受 NyxID 端 rate-limit、audit、用户 revoke 约束;短期 token TTL ≤ 5 min |
| Prompt injection / tool 越权 | 任何能间接读 grain state 的越权路径 → 加密 token 暴露 | grain state 里没 secret material 可读 |
| User 主动 revoke | 必须双向同步,漏一步备份内 token 仍活 | NyxID 单向操作,source of truth |

第二个权衡是 unblock 速度 vs. 长期正确性:Local 能立即 unblock,但 NyxID #549 落地后再迁移要做数据 wipe + 加密字段下线,迁移成本不便宜;直接走 Remote 等待时间换取的是终态架构上的 zero secret material 不变量。aevatar 当前 bot owner-shared 模式仍可继续运行(不 regression),等待是可接受的代价。

## Bot-Owner-Shared 模式终止策略

切到 broker 模式那一刻,所有现有 Lark sender 必须各自 `/init` 后才能恢复响应,是产品断崖。ADR 不锁定具体策略(产品决策),但 explicit 列出可选项 + 留 placeholder,implementation PR 必须从其中选一个并写进发布 runbook:

- **A. 双轨期(per-bot opt-in)**:新 bot 默认 broker 模式;旧 bot 保持 owner-shared,bot owner 在管理端显式开关切换。最低断崖,迁移节奏由 bot owner 自定
- **B. 单轨硬切 + 通知期**:broker 上线前 N 天起,未绑定 sender 收到的 reply 加引导话术("X 月 X 日起需要 `/init` 才能继续使用");到期硬切,所有未绑定 sender 一律 `/init`
- **C. 单轨硬切(零通知)**:上线即切,所有 sender 一次性走 `/init`;通信责任在 bot owner

ADR 不强制选哪个,但要求 implementation PR 在合并到生产路径前做产品决策、把决策记到 runbook 与 release notes。

## Dependencies

**NyxID #549 已合入**(2026-04-28,PR ChronoAIProject/NyxID#555),contract 已冻结:

- broker scope `urn:nyxid:scope:broker_binding`(URN 命名)
- token-exchange `subject_token_type=urn:nyxid:params:oauth:token-type:binding-id`
- `POST /oauth/token` (auth_code) 在 broker scope 下返回 `binding_id`(不返 `refresh_token`)
- `DELETE /oauth/bindings/{binding_id}` + `GET /oauth/bindings/{binding_id}` + `GET /oauth/bindings?external_subject_*=`
- `oauth_broker_binding.revoked` HMAC-SHA256 签名 webhook(CAE 通道,见 ADR §Continuous Access Evaluation)
- 可选 V2 加固:RFC 9449 DPoP / RFC 8705 mTLS / RFC 9126 PAR

ADR 同步升 `accepted`。aevatar 侧实现并行展开:

NyxID#549 已就绪后可立即落地(本仓库内独立完成):

- `INyxIdCapabilityBroker` 接口 + proto(含 `ExternalSubjectRef` typed message)
- `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort`(state 仅存 `binding_id`,opaque)
- `ChannelConversationTurnRunner.RunInboundAsync` 的 slash-command 前置路由(`/init`、`/unbind`)
- `/api/oauth/nyxid-callback` endpoint 标准 OAuth redirect 处理框架(含 state_token HMAC 验证、写侧预挂接 projection 等待)
- `InMemoryCapabilityBroker` 测试 fake

### 后续工作(单独 PR)

- `NyxIdRemoteCapabilityBroker`:接 RFC 8693 token-exchange `POST /oauth/token` + `DELETE /oauth/bindings/{binding_id}` + 私域 authorize URL 构造
- `ExternalIdentityBindingProjector` + `ExternalIdentityBindingProjectionQueryPort`:projection 物化 + readmodel 反查
- `/api/oauth/nyxid-callback` endpoint(标准 OAuth redirect handler + state_token HMAC 验证 + 写侧预挂接 projection 等待)
- `ChannelConversationTurnRunner` slash-command 路由(`/init` / `/unbind`)+ 未绑定 sender 强制路径
- `AuthContext.external_subject = 4` typed 字段(`channel_contracts.proto`,单独 channel-runtime PR)
- **CAE 撤销 webhook 接收**:`/api/webhooks/nyxid-broker-revocation` 处理 NyxID 主动 revoke 通知,事件化撤销本地 binding actor

### Divergence from NyxID#549 Initial Sketch

NyxID#549 issue 第一版提出 broker token issuance 走专用端点 `POST /oauth/bindings/{binding_id}/token`(`client_credentials` 鉴权)。本 ADR 决定改走 RFC 8693 token-exchange `POST /oauth/token`(`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`,`subject_token=<binding_id>`,`subject_token_type=urn:nyxid:params:oauth:token-type:binding-id`,`client_secret` 鉴权)。

理由:

- **复用现有 framework**:NyxID 已有 `token_exchange_service.rs`(支持 `subject_token=access_token`)。新增 `subject_token_type=urn:nyxid:params:oauth:token-type:binding-id` 比新建专用端点更自然,implementation 偏小
- **OAuth 标准对齐**:`client_credentials` 在 OAuth 2.0 标准里语义是"client 以自己身份(不代表 user)拿 token";broker 端"client 凭 binding_id 代用户拿 user-scoped token"应走 token-exchange 而非 client_credentials,跟标准定义一致
- **discovery 友好**:走 `/oauth/token`,接入方扫 `.well-known/oauth-authorization-server` 即可发现 broker 能力,不需要单独文档化新路径

NyxID#549 已同步追加 comment 提议 align 到 RFC 8693 token-exchange。两侧契约最终冻结(`subject_token_type` URN 字符串、`invalid_grant` 错误码语义)前 ADR 保持 `proposed`。

### Projection Readiness

`ResolveBindingAsync` 走 `ExternalIdentityBindingGAgent` 的 readmodel projection。OAuth callback handler 落 `ExternalIdentityBoundEvent` 后,projection 物化是异步的。

**写侧 vs query 侧边界**:本节描述的"等 projection 水位"发生在 OAuth callback handler(write-side completion path),**不在 turn / query 路径上**。CLAUDE.md 禁的是 **query-time** priming(`QueryPort/QueryService/ApplicationService` 在请求路径读 ES、重放、补投影);callback handler 在事件提交时同步等待该事件 projection 物化属于 write-side 的正常完成性保证,不违反禁令。

具体处理:

- callback handler 在 commit `ExternalIdentityBoundEvent` 时**写侧预挂接 projection** —— 通过 `IProjectionReadinessPort.WaitForBindingStateAsync(externalSubject, expectedBindingId, timeout)` 同步等待 binding readmodel 对指定 external subject 物化出 expected binding state(actor committed version 对齐),再返回 callback HTTP 响应给浏览器
- 等待超时(配置上限,e.g. 3s)→ callback 响应"binding 已写入,读副本仍在传播,稍后重发消息";不进 query-time priming/replay 路径
- 此后用户回到 Lark 发任意消息,turn 路径调 `ResolveBindingAsync` 一定看得到 binding
- turn 路径在 `ResolveBindingAsync` 返回 null 时**禁止**走 ES replay / actor state mirror / 重建 priming;只能引导 sender 重新 `/init`

`IProjectionReadinessPort` 是 write-side 端口,只服务 callback handler 这一类完成性等待场景,query / turn 路径不依赖此端口。

## Consequences

- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort` + `INyxIdCapabilityBroker`
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(标准 OAuth client redirect 处理,不是 webhook),含写侧预挂接 projection 等待
- 新增 `IProjectionReadinessPort`(write-side 端口):callback handler 在事件提交后同步等待指定 external subject 的 expected binding state 在 binding readmodel 上可见;turn / query 路径不依赖此端口
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由(`/init`、`/unbind`),`/init` 根据 binding 是否存在进入新绑定或 same-owner authorization renewal 路径,`/unbind` 同步调 NyxID revoke
- `BuildReplyMetadata` 改成 `ResolveAsync` + `IssueShortLivedAsync`;metadata key 从 `nyxid.access_token` 改为 `nyxid.capability_handle`(诚实表达"短期、scoped、可撤销")
- 未绑定 sender(无论 1:1 还是群聊)统一强制 `/init`,不回落 bot owner;现有 bot owner-shared 模式终止策略由 implementation PR 选 §Bot-Owner-Shared 模式终止策略 中的 A/B/C 之一,记入 runbook
- `ChannelUserBindingState.credential_ref` 字段进入 deprecation window(见 §Actor Architecture);`AuthContext.user_credential_ref` 同步进入 deprecation,broker outbound 只读 typed `external_subject` 字段(见 §Outbound Send),无 string 重载过渡期
- aevatar grain state / projection / log / metric span attribute 不出现 user secret material;arch test 守此边界,扫描所有 grain state proto 字段树
- 生产实现等 NyxID #549 ready 后才合并到生产路径;在此之前 aevatar 现有 bot owner-shared 模式继续运行(不 regression)

## Implementation Notes

ADR 核心决策已 lock。以下是边界细节,reviewer 在 final review 提出后纳入,避免 implementation PR 阶段重新决策破坏 zero-secret / source-of-truth 不变量。

### 1. HMAC `state_token` 细节

- **载荷序列化**:payload 用 Protobuf message,并使用 deterministic serialization 生成 `payload_proto_bytes`;禁 JSON / `ToString()` / 自定义 join,对齐 CLAUDE.md "统一 Protobuf"
- **kid + rotation**:state_token header 携带 `kid` 标识签名 key 版本;HMAC service key 由 KMS / config 管理,rotation grace period 内按 `kid` 接受旧 key + 新 key 验签;grace period 必须严格 > `exp`(即 ≥10 分钟,因 `exp ≤ 5min`),保证 rotation 不打断在飞 binding
- **token 结构**:`base64url(kid_bytes) + "." + base64url(payload_proto_bytes) + "." + base64url(hmac_bytes)`(三段式);HMAC signing input 是前两段的 ASCII bytes(`base64url(kid_bytes) + "." + base64url(payload_proto_bytes)`),避免原始 bytes 拼接歧义
- **HMAC key storage tradeoff(implementation)**:零 appsettings 决策(见 §Bootstrap)要求 HMAC key 由 cluster-singleton actor 自播种(256-bit `RandomNumberGenerator.Fill`),并通过 projection mirror 暴露给 codec / webhook validator。这意味着 HMAC key bytes **会**进 projection document store(ES / InMemory),与 `service-level secret 应在 KMS / 环境变量 / 不进事实存储`的常规姿势相反。tradeoff 接受的前提:
  - projection store 物理位置同 grain event store(同集群、同读权限边界、同 backup 策略),不增加新的访问面
  - state_token TTL ≤ 5 min(见上),即使 key 泄漏,window 极短
  - rotation 在 actor 内一条 command 即可触发(`RotateAevatarOAuthClientHmacKeyCommand`),应急流程明确
  - 仅当 ES cluster 的读权限放宽到比 grain event store 更广的服务(如分析 / SRE 通用查询)时,这个 tradeoff 才真正成为新风险面;deployment 必须保持 `aevatar-oauth-client-*` 索引的访问范围与 actor state 持平。后续若 KMS / external secret store 已就绪,可改回从 secret store 加载、projection 仅 mirror 占位符的形态(无需变更 codec 接口)。

### 2. `/init` 并发幂等

用户快速连发两条 `/init`、projection 还未水位达成,两个 OAuth 流程并行:

- `ExternalIdentityBindingGAgent` 是单线程 actor,在 commit `ExternalIdentityBoundEvent` 时做**幂等检查**:同一 `ExternalSubjectRef` 已存在 active binding 时,拒绝后到的 event,actor 不变更状态;callback 返回"已绑定"
- 后到 callback 已经从 NyxID 拿到的新 `binding_id` 属于未采纳资源;callback handler 对该 rejected `binding_id` 做 best-effort `DELETE /oauth/bindings/{binding_id}` cleanup。cleanup 失败只记 metric / audit,不影响 actor 内已存在的 active binding
- aevatar **不要求** NyxID 端做 `(client_id, external_subject)` unique 约束(简化 NyxID 实现)
- 剩余 orphan binding 只可能来自 cleanup 失败或 callback 中断。aevatar 侧 ADR 不假设 NyxID reaper 行为;NyxID#549 SHOULD 自行处理 orphan binding(超时自动 revoke 或定期 reap),但不构成 aevatar 实现依赖

已绑定 authorization renewal 的 state token 固定携带开始授权时的 `expected_binding_hash`. callback 先与当前 readmodel binding hash 对账并校验 same owner;明显 stale 时撤销新 binding并返回 conflict.若两个 callback 在 readmodel 更新前都通过,它们分别把 `{expected_previous_binding_id,new_binding_id}` 作为 typed command 交给 binding actor.只有 actor 当前值仍等于 expected 值的 command 才提交 `ExternalIdentityBindingReplacedEvent`;提交后旧值进入持久化 retirement 队列并调用 NyxID revoke.CAS 失败的新 binding同样进入 retirement 队列,不得覆盖获胜 binding.

### 3. Callback Handler 错误 UX

| 错误类型 | HTTP 响应 | 用户可见文案 |
|---|---|---|
| `state_token` 过期 / HMAC 校验失败 | 400 | "绑定链接已过期或无效,请回到 Lark 重新发送 `/init`" |
| `/oauth/token`(authorization_code 兑换)失败 | 502 | "NyxID 绑定失败,稍后重试 `/init`" |
| projection 等待超时(已落 event 但 readmodel 未水位) | 200 | "绑定已写入,稍后重发消息即可生效" |
| 其他未分类 | 500 | "绑定遇到问题,请重试 `/init`" |

`exp ≤5min` 给用户留足登录时间;实际 P99 远小于 5min,不期望成为常见 fail mode。

### 4. `IssueShortLivedAsync` 失败处理

NyxID 不可用 / 5xx / timeout / connect refuse 时:

- **outbound 路径**:整次 outbound 失败,**不 fallback 到 bot owner token,不 fallback 到任何缓存 token / 旧 access_token**(zero-secret 不变量不接受任何"备用身份")。错误向上传递给调用方,记 metric / trace 但不静默吞掉
- **turn 路径**:同上,turn 失败,sender 收到通用错误回复(e.g. "服务暂时不可用,稍后再试");broker 健康通过 `IssueShortLivedAsync` p99 latency / error rate / `invalid_grant` rate 三个 metric 监控
- **rate limit 单一权威**:rate limit 由 NyxID#549 契约约定,aevatar **不做** client-side rate limit / per-binding semaphore;NyxID 是流控单一权威,接入方观察到 429 即冷却

### 5. `/unbind` → `/init` 时序保障

`/unbind` 成功后,`ResolveAsync`(走 projection)在 readmodel 物化前可能仍返回旧 `binding_id`,导致下一条 `/init` 误判"已绑定":

- `/unbind` handler 在 commit `ExternalIdentityBindingRevokedEvent` 后,**同步等 projection 水位**(复用 `IProjectionReadinessPort.WaitForEventAsync`),再返回成功响应给 sender
- 等待超时(配置上限,e.g. 3s)时,handler 返回"解绑已写入,读副本仍在传播,稍后重试 `/init`";不读取 actor 直接态,也不做 query-time priming
- 这跟 OAuth callback 的写侧预挂接同源(均属 write-side completion path,不违反 query-time priming 禁令)
- 不采"`/init` 幂等检查读 actor 直接态"备选方案 — 会把 turn 路径的"已绑定"判定拆成两条查询源(actor 直读 + projection),违反单一查询源原则

## Related

- aevatar Discussion `#400` — Per-sender NyxID binding for channel bots
- aevatar Discussion `#375` — Zero secret material + capability broker boundary
- ChronoAIProject/NyxID Discussion `#511` — External Subject Binding RFC
- ChronoAIProject/NyxID Issue `#549` — OAuth broker bindings(本 ADR 实现的依赖)
- ADR-0011 — Lark Nyx Relay Webhook Topology
- ADR-0012 — Channel Runtime Credential Boundary
- ADR-0013 — Unified Channel Inbound Backbone
