---
title: "Per-User NyxID Binding via OAuth Broker"
status: proposed
owner: eanzhao
---

# ADR-0017: Per-User NyxID Binding via OAuth Broker

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
- **`/init` 幂等语义**:已绑定 sender 再次 `/init` 不创建新 binding,不发起新 OAuth 跳转;runner 回复"已绑定 NyxID `<masked nyx_subject>`,先 `/unbind` 再 `/init` 可切换账号"。需要切账号是显式的两步动作

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
       -> { access_token, binding_id }   (broker_binding scope 下不返 refresh_token)
       初始 access_token 处理:可选地一次性用于调 /oauth/userinfo 拿 sub claim
                              做"已绑定 <name>"展示文案;**永不持久化、永不复用**
                              (后续 turn 必须走 token-exchange,handler 退出前丢弃)
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
| `nyx_subject`(opaque `sub` claim) | ✗(无明确用途,不缓存) | callback 阶段从初始 access_token 解,handler 内一次性使用,不持久化 | ✓ source of truth |
| Initial `access_token`(callback 拿到) | ✗ never | ✓ callback handler 内一次性使用(可选 /oauth/userinfo 调用),handler 退出前丢弃 | 签发方 |
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

## Outbound Send: AuthContext × Broker

`IChannelOutboundPort.ContinueConversationAsync(... AuthContext auth ...)` 在 `OnBehalfOfUser` 模式下用 `AuthContext.user_credential_ref`(`agents/Aevatar.GAgents.Channel.Abstractions/protos/channel_contracts.proto:138`)选择代用户身份。Broker 模式下:

- 在线协议保留 `user_credential_ref string = 2`,语义改为承载序列化的 `ExternalSubjectRef`(form: `lark:tenant_X:user_Y`),由 outbound adapter 反序列化
- outbound adapter 实现:每次发送前调 `IssueShortLivedAsync(externalSubject, scope)` 拿短期 access_token,**不缓存,不复用**;每次 outbound 都换新 handle
- 长期演进:`AuthContext` 加 typed `ExternalSubjectRef external_subject = 4` 字段,新代码用新字段;旧 `user_credential_ref` 标 deprecated,删除窗口同 §Actor Architecture 描述的 channel-runtime proto break
- 字段拆分原则与 §Actor Architecture 同源:旧 `user_credential_ref` 语义就是 per-bot 凭据指针,broker 模式下身份去 bot 化,字段语义跟新模型不匹配,要走显式 typed 字段

## INyxIdCapabilityBroker:Single Production Adapter

`INyxIdCapabilityBroker` 是 capability 层的 seam,业务代码(`ChannelConversationTurnRunner` 等)只依赖此接口。**所有 `externalSubject` 参数必须是 typed `ExternalSubjectRef`(proto-typed value object)**,不接受 string / generic bag(对齐 CLAUDE.md "核心语义强类型"):

- `StartExternalBindingAsync(ExternalSubjectRef externalSubject) -> BindingChallenge`
- `ResolveBindingAsync(ExternalSubjectRef externalSubject) -> BindingId?`(返回不透明 `binding_id`;不暴露 `nyx_subject`)
- `RevokeBindingAsync(ExternalSubjectRef externalSubject)`
- `IssueShortLivedAsync(ExternalSubjectRef externalSubject, CapabilityScope scope) -> CapabilityHandle`

唯一生产实现 `NyxIdRemoteCapabilityBroker`,内部:

- `IssueShortLivedAsync` 调 `POST /oauth/token`(grant_type=token-exchange,subject_token=binding_id);收到 `invalid_grant` 抛 `BindingRevokedException` 让上层事件化撤销
- `RevokeBindingAsync` 调 `DELETE /oauth/bindings/{binding_id}`(aevatar 主动撤销)
- `ResolveBindingAsync` 查 `ExternalIdentityBindingGAgent` projection(纯本地,不调 NyxID)
- `StartExternalBindingAsync` 构造 `/oauth/authorize` URL + PKCE pair,verifier 编码进 HMAC-签的 stateless state token(不落 actor state),返回 URL 由调用方通过私域 channel 投递

不引入 `LocalRefreshTokenCapabilityBroker`。"两个 adapter 才是真 seam"由 `InMemoryCapabilityBroker`(test fake)+ Remote 满足;test fake 不构成生产意义上的并行实现。

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

## Dependencies

aevatar 侧实现 gated on:

- **ChronoAIProject/NyxID#549** — OAuth broker bindings — NyxID-side refresh_token storage with opaque binding_id

NyxID #549 之前可平行落地的部分(本身不依赖 broker endpoint):

- `INyxIdCapabilityBroker` 接口 + proto(含 `ExternalSubjectRef` typed message)
- `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort`(state 仅存 `binding_id`,opaque)
- `ChannelConversationTurnRunner.RunInboundAsync` 的 slash-command 前置路由(`/init`、`/unbind`)
- `/api/oauth/nyxid-callback` endpoint 标准 OAuth redirect 处理框架(含 state_token HMAC 验证、写侧预挂接 projection 等待)
- `InMemoryCapabilityBroker` 测试 fake

NyxID #549 ready 后:

- `NyxIdRemoteCapabilityBroker` 实现接好真实 endpoint
- end-to-end 测试 + 灰度
- 切到生产路径
- 同步:NyxID#549 OpenAPI/proto 契约冻结(scope name 用 `urn:nyxid:scope:broker_binding`,subject_token_type 用 `urn:nyxid:params:oauth:token-type:binding-id`)前 ADR 保持 `proposed`;契约冻结并 aevatar 接入后升 `accepted`

### Projection Readiness

`ResolveBindingAsync` 走 `ExternalIdentityBindingGAgent` 的 readmodel projection。OAuth callback handler 落 `ExternalIdentityBoundEvent` 后,projection 物化是异步的。CLAUDE.md 禁 query-time priming(`Application/QueryPort/QueryService` 不得在请求路径读 ES、重放、补投影),所以处理是:

- callback handler 在 commit `ExternalIdentityBoundEvent` 时**写侧预挂接 projection** —— 同步等待该 event 在 binding readmodel 上的水位达成(actor committed version 对齐),再返回 callback HTTP 响应给浏览器
- 等待超时(配置上限,e.g. 3s)→ callback 响应"binding 已写入,读副本仍在传播,稍后重发消息";不进 query-time priming/replay 路径
- 此后用户回到 Lark 发任意消息,turn 路径调 `ResolveBindingAsync` 一定看得到 binding
- turn 路径在 `ResolveBindingAsync` 返回 null 时**禁止**走 ES replay / actor state mirror / 重建 priming;只能引导 sender 重新 `/init`

## Consequences

- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort` + `INyxIdCapabilityBroker`
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(标准 OAuth client redirect 处理,不是 webhook),含写侧预挂接 projection 等待
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由(`/init`、`/unbind`),`/init` 幂等
- `BuildReplyMetadata` 改成 `ResolveAsync` + `IssueShortLivedAsync`;metadata key 从 `nyxid.access_token` 改为 `nyxid.capability_handle`(诚实表达"短期、scoped、可撤销")
- 未绑定 sender(无论 1:1 还是群聊)统一强制 `/init`,不回落 bot owner;现有 bot owner-shared 模式在 implementation PR 切上线那一刻终止,bot owner 失去"默认用户身份"特权
- `ChannelUserBindingState.credential_ref` 字段进入 deprecation window(见 §Actor Architecture);`AuthContext.user_credential_ref` 同步进入 deprecation,新代码用 typed `external_subject` 字段(见 §Outbound Send)
- aevatar grain state / projection / log / metric span attribute 不出现 user secret material;arch test 守此边界,扫描所有 grain state proto 字段树
- 生产实现等 NyxID #549 ready 后才合并到生产路径;在此之前 aevatar 现有 bot owner-shared 模式继续运行(不 regression)

## Related

- aevatar Discussion `#400` — Per-sender NyxID binding for channel bots
- aevatar Discussion `#375` — Zero secret material + capability broker boundary
- ChronoAIProject/NyxID Discussion `#511` — External Subject Binding RFC
- ChronoAIProject/NyxID Issue `#549` — OAuth broker bindings(本 ADR 实现的依赖)
- ADR-0011 — Lark Nyx Relay Webhook Topology
- ADR-0012 — Channel Runtime Credential Boundary
- ADR-0013 — Unified Channel Inbound Backbone
