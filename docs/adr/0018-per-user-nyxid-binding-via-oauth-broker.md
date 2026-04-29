---
title: "Per-User NyxID Binding via OAuth Broker"
status: accepted
owner: eanzhao
---

# ADR-0018: Per-User NyxID Binding via OAuth Broker

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
- **`/init` 幂等语义**:已绑定 sender 再次 `/init` 不创建新 binding,不发起新 OAuth 跳转;runner 回复"已绑定 NyxID `<masked sub>`,先 `/unbind` 再 `/init` 可切换账号"。需要切账号是显式的两步动作
- **`/unbind` 行为**:slash-command 路由 → `RevokeBindingAsync(externalSubject)` → `DELETE {nyxid}/oauth/bindings/{binding_id}`(NyxID 同步 revoke,NyxID 是 source of truth)→ `ExternalIdentityBindingRevokedEvent` 落 `ExternalIdentityBindingGAgent`。NyxID 调用失败(网络 / 5xx)→ 本地不擅自标 revoked,返回错误并提示重试,**避免 source-of-truth 不一致**(本地认为已 revoke 但 NyxID 仍 active)。成功后 `ResolveAsync` 返回 null,sender 需重新 `/init`

aevatar grain state、projection、log、metric 持有 zero long-lived user secret material,对齐 `#375` 不变量;aevatar 自身的 service-level secret(OAuth `client_secret`、state-token HMAC 签名 key)按基础设施 secret 管理(rotation、KMS、out of scope of #375 user-secret 不变量)。

`/init` 流程在 OAuth + broker primitive 上的等价改写:

```
/init (新绑定 path)
  -> ChannelConversationTurnRunner 前置 slash-command 路由(不进 LLM)
  -> ResolveAsync(externalSubject) hit -> 返回幂等回复(见上),不进入下方 OAuth 流
  -> ResolveAsync miss -> aevatar 生成 PKCE pair + correlation_id,把
     state_token = HMAC(service_key, {correlation_id, external_subject_ref,
                                       pkce_verifier, exp(<=5min)})
     编码进 OAuth `state` 参数(stateless,verifier 不落 grain state)
  -> 通过 Lark DM(私域,非群聊)回 sender:
     "{nyxid}/oauth/authorize?client_id=aevatar-channel-binding
      &redirect_uri=https://aevatar/api/oauth/nyxid-callback
      &response_type=code&code_challenge=...&code_challenge_method=S256
      &scope=openid+urn:nyxid:scope:broker_binding&state=<state_token>"
  -> 用户登录 → NyxID 302 回 aevatar /api/oauth/nyxid-callback?code=...&state=...
  -> aevatar callback handler:
       验 state_token HMAC + exp -> 解出 ExternalSubjectRef + pkce_verifier
       POST {nyxid}/oauth/token
            (grant_type=authorization_code, code, code_verifier, client_secret)
       -> { access_token, id_token, binding_id }   (broker_binding scope 下不返 refresh_token)
       从同次返回的 id_token 解码 `sub`/`name` claim 做"已绑定 <masked sub>"展示文案;
       **不调 /oauth/userinfo**(OIDC 标准,sub claim 在 id_token 已自带,省一次 round-trip);
       **不持久化任何 token**(handler 退出前直接丢弃 access_token / id_token)
     落 ExternalIdentityBoundEvent { external_subject, binding_id, bound_at }
       到 ExternalIdentityBindingGAgent
     **写侧预挂接 projection**:同步等 binding readmodel 对该 event 水位达成
                              (timeout 配置上限,e.g. 3s)再返回浏览器响应

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
| PKCE `code_verifier`(short-lived) | ✗ never | ✓ 嵌在 HMAC-签的 stateless `state` token,exp ≤5min | |
| `nyx_subject`(opaque `sub` claim) | ✗(无明确用途,不缓存) | callback 阶段从同次返回的 `id_token` 解码,handler 内一次性用于展示文案,不持久化 | ✓ source of truth |
| Initial `access_token` / `id_token`(callback 拿到) | ✗ never | ✓ handler 内一次性使用(从 id_token 取 sub),退出前直接丢弃,**不发任何 NyxID 调用**(包括 /oauth/userinfo) | 签发方 |
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

- callback handler 在 commit `ExternalIdentityBoundEvent` 时**写侧预挂接 projection** —— 通过新增的 `IProjectionReadinessPort.WaitForEventAsync(eventId, readmodelId, timeout)` 同步等待该 event 在 binding readmodel 上的水位达成(actor committed version 对齐),再返回 callback HTTP 响应给浏览器
- 等待超时(配置上限,e.g. 3s)→ callback 响应"binding 已写入,读副本仍在传播,稍后重发消息";不进 query-time priming/replay 路径
- 此后用户回到 Lark 发任意消息,turn 路径调 `ResolveBindingAsync` 一定看得到 binding
- turn 路径在 `ResolveBindingAsync` 返回 null 时**禁止**走 ES replay / actor state mirror / 重建 priming;只能引导 sender 重新 `/init`

`IProjectionReadinessPort` 是 write-side 端口,只服务 callback handler 这一类完成性等待场景,query / turn 路径不依赖此端口。

## Consequences

- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort` + `INyxIdCapabilityBroker`
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(标准 OAuth client redirect 处理,不是 webhook),含写侧预挂接 projection 等待
- 新增 `IProjectionReadinessPort`(write-side 端口):callback handler 在事件提交后同步等待 specific event 在指定 readmodel 上的水位达成;turn / query 路径不依赖此端口
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由(`/init`、`/unbind`),`/init` 幂等,`/unbind` 同步调 NyxID revoke
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

### 2. `/init` 并发幂等

用户快速连发两条 `/init`、projection 还未水位达成,两个 OAuth 流程并行:

- `ExternalIdentityBindingGAgent` 是单线程 actor,在 commit `ExternalIdentityBoundEvent` 时做**幂等检查**:同一 `ExternalSubjectRef` 已存在 active binding 时,拒绝后到的 event,actor 不变更状态;callback 返回"已绑定"
- 后到 callback 已经从 NyxID 拿到的新 `binding_id` 属于未采纳资源;callback handler 对该 rejected `binding_id` 做 best-effort `DELETE /oauth/bindings/{binding_id}` cleanup。cleanup 失败只记 metric / audit,不影响 actor 内已存在的 active binding
- aevatar **不要求** NyxID 端做 `(client_id, external_subject)` unique 约束(简化 NyxID 实现)
- 剩余 orphan binding 只可能来自 cleanup 失败或 callback 中断。aevatar 侧 ADR 不假设 NyxID reaper 行为;NyxID#549 SHOULD 自行处理 orphan binding(超时自动 revoke 或定期 reap),但不构成 aevatar 实现依赖

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
