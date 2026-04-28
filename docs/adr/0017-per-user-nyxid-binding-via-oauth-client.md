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
- 现有 `ChannelUserBindingGAgent`(`agents/Aevatar.GAgents.Channel.Runtime/UserBinding/ChannelUserBindingGAgent.cs`)是 per-(bot, channel, sender) actor,持有 bot 范围内的 user 偏好 + `credential_ref`(per-bot 凭据指针),scope 不同于 platform-级 NyxID identity
- 没有 `external subject → nyx subject` 的持久映射(platform 级身份),也没有 `INyxIdCapabilityBroker`

`#400` 原 RFC 要求 NyxID 加 5 个端点(challenge 签发、`/cli-auth` 扩展 `binding_jti`、主动 webhook 回调、bindings 查询、bindings revoke)。回扫 NyxID surface 后这 5 个 ask 大部分能用现有 OAuth/OIDC primitive 替代;唯一真正缺失的是 broker 形态的"接入方代用户拿短期 access_token,但永不接触 refresh_token"。

本 ADR 第一版草稿曾接受 `LocalRefreshTokenCapabilityBroker`(aevatar 加密持 refresh_token)作为过渡实现。讨论后否决,详见 Decision Rationale。当前决定走 broker 路径,实现依赖 NyxID 侧新 issue。

## Decision

aevatar 实现 per-user NyxID binding,**作为 NyxID broker 的 OAuth 接入方**:

- 用标准 OAuth Authorization Code + PKCE 流程发起 binding(`/oauth/authorize` 浏览器跳转;PKCE `code_verifier` 不入 grain state — 见 §Storage Boundary)
- **aevatar 不接收、不持有 user refresh_token**;binding 完成时 NyxID 返回不透明 `binding_id`,aevatar 仅持 `ExternalSubjectRef → binding_id` 映射
- 每次 turn 用 RFC 8693 token-exchange (`grant_type=urn:ietf:params:oauth:grant-type:token-exchange`,`subject_token=<binding_id>`,`subject_token_type=urn:nyxid:params:oauth:token-type:binding-id`,client 用 `client_secret` 鉴权)调 NyxID 拿短期 access_token,塞进 `AgentToolRequestContext`
- aevatar 主动撤销:`DELETE /oauth/bindings/{binding_id}`;**NyxID 主动撤销**(用户在 NyxID UI 直接 revoke):下次 token-exchange 收到 `invalid_grant` → aevatar 视作 binding 已亡,事件化撤销本地 binding actor 并要求 sender 重新 `/init`(NyxID 是 source of truth,aevatar 单向同步)
- **OAuth authorize URL 只通过私域回传**(Lark DM 等 sender-only channel),不在群聊明文返回;无 DM 能力的平台不接入 broker 模式 — 防 OAuth state hijack(群里他人点开 URL 用自己 NyxID 登录,callback 把 sender A 绑成 NyxID B)

aevatar grain state、projection、log、metric 持有 zero long-lived user secret material,对齐 `#375` 不变量;aevatar 自身的 service-level secret(OAuth `client_secret`、state-token HMAC 签名 key)按基础设施 secret 管理(rotation、KMS、out of scope of #375 user-secret 不变量)。

`/init` 流程在 OAuth + broker primitive 上的等价改写:

```
/init
  -> ChannelConversationTurnRunner 前置 slash-command 路由(不进 LLM)
  -> aevatar 生成 PKCE pair + correlation_id,把
     state_token = HMAC(service_key, {correlation_id, external_subject_ref,
                                       pkce_verifier, exp(<=5min)})
     编码进 OAuth `state` 参数(stateless,verifier 不落 grain state)
  -> 通过 Lark DM(私域,非群聊)回 sender:
     "{nyxid}/oauth/authorize?client_id=aevatar-channel-binding
      &redirect_uri=https://aevatar/api/oauth/nyxid-callback
      &response_type=code&code_challenge=...&code_challenge_method=S256
      &scope=openid+broker_binding&state=<state_token>"
  -> 用户登录 → NyxID 302 回 aevatar /api/oauth/nyxid-callback?code=...&state=...
  -> aevatar callback handler:
       验 state_token HMAC + exp -> 解出 ExternalSubjectRef + pkce_verifier
       POST {nyxid}/oauth/token
            (grant_type=authorization_code, code, code_verifier, client_secret)
       -> { access_token, binding_id }   (broker_binding scope 下不返 refresh_token)
     落 ExternalIdentityBoundEvent { external_subject, binding_id, bound_at }
       到 ExternalIdentityBindingGAgent

turn
  -> ResolveAsync(externalSubject) -> binding_id (查 ExternalIdentityBindingGAgent projection)
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
| `nyx_subject`(opaque `sub` claim) | ✗(无明确用途,不缓存) | callback 阶段从 access_token 解,不持久化 | ✓ source of truth |
| User refresh_token | ✗ never | ✗ never | ✓ encrypted |
| Short-lived access_token | per-turn `AsyncLocal`,不持久化 | | 签发方 |
| state-token HMAC key + OAuth `client_secret` | ✗ 不在 grain state(基础设施 secret) | ✓ 通过 KMS / config 加载到进程 | |

新 actor `ExternalIdentityBindingGAgent` 的 key 是 `ExternalSubjectRef = (platform, tenant, external_user_id)`,**与现有 per-(bot, channel, sender) `ChannelUserBindingGAgent` 是不同 scope 的不同业务实体**:前者承载"Lark user X ↔ NyxID identity"的 platform 级身份(跟用户在哪个 bot 里讲话无关),后者承载 bot 范围内的 user 偏好。两者通过 `external_subject_ref` 在 query 路径上关联,不共享 state;现有 `ChannelUserBindingGAgent.credential_ref` 字段在 broker 模式下变冗余,implementation PR 负责标 deprecated 并迁移。

`binding_id` 在 RCE 场景下的语义跟 refresh_token 不同:它必须配合 aevatar 的 `client_secret` 才能换 token,而 NyxID 可以对 `(client_id, binding_id)` 做 rate limit、异常 audit、用户主动 revoke。NyxID 因此是真正的 control point,而不只是"换个地方存的 refresh_token"。

## INyxIdCapabilityBroker:Single Production Adapter

`INyxIdCapabilityBroker` 是 capability 层的 seam,业务代码(`ChannelConversationTurnRunner` 等)只依赖此接口:

- `StartExternalBindingAsync(externalSubject) -> BindingChallenge`
- `ResolveBindingAsync(externalSubject) -> NyxSubjectRef?`
- `RevokeBindingAsync(externalSubject)`
- `IssueShortLivedAsync(externalSubject, scope) -> CapabilityHandle`

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

- **ChronoAIProject/NyxID#549** — OAuth broker bindings — NyxID-side refresh_token storage with opaque binding_id。本 ADR 假设的契约 surface(token-exchange grant、binding_id subject_token_type、`DELETE /oauth/bindings/{binding_id}`、`invalid_grant` on revoked binding)需在 NyxID#549 OpenAPI / proto 落定后由 implementation PR pin 到具体版本;若 NyxID#549 协议偏离假设,本 ADR status 留 `proposed`,由 implementation PR 提交 ADR amendment 重新校准

NyxID #549 之前可平行落地的部分(本身不依赖 broker endpoint):

- `INyxIdCapabilityBroker` 接口 + proto
- `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort`(state 仅存 `binding_id`,opaque;不缓存 `nyx_subject`,见 §Storage Boundary)
- 私域投递抽象:`IChannelPrivateMessenger` 或等价契约,确保 `/init` 返回的 OAuth URL 必须走 sender-only channel;群聊不可达的平台拒绝接入 broker 模式
- `ChannelConversationTurnRunner.RunInboundAsync` 的 slash-command 前置路由(`/init`、`/unbind`)
- `/api/oauth/nyxid-callback` endpoint 标准 OAuth redirect 处理框架(state HMAC 签验、PKCE verifier 解包、code 兑换)
- `InMemoryCapabilityBroker` 测试 fake

NyxID #549 ready 后:

- `NyxIdRemoteCapabilityBroker` 实现接好真实 endpoint
- end-to-end 测试 + 灰度
- 切到生产路径

## Consequences

- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort` + `INyxIdCapabilityBroker`。**与现有 `Aevatar.GAgents.Channel.Runtime/UserBinding/ChannelUserBindingGAgent` 是 platform-级 vs bot-级两个不同业务实体**;CLAUDE.md "Actor 即业务实体" 不被违反,因为两者 scope 与生命周期不同(详见 §Storage Boundary)
- 现有 `ChannelUserBindingGAgent.credential_ref` 字段在 broker 模式下变冗余:per-bot 不再持有 user 凭据指针,改由 `ExternalIdentityBindingGAgent` 的 platform-级 binding 承载;implementation PR 负责字段 deprecation + 数据迁移
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(标准 OAuth client redirect 处理,不是 webhook;state HMAC 签验 + PKCE verifier stateless round-trip)
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由(`/init`、`/unbind`)
- `BuildReplyMetadata` 改成 `ResolveAsync` + `IssueShortLivedAsync`;metadata key 从 `nyxid.access_token` 改为 `nyxid.capability_handle`(诚实表达"短期、scoped、可撤销")
- 群聊场景采 `#400` Open Questions 第 1 项的 A 选项:未绑定 sender 强制 `/init`,不回落到 bot owner 配额
- **私域投递硬约束**:`/init` 返回的 OAuth URL 只通过 sender-only channel(Lark DM 等)回传,不在群聊明文返回;无 DM 能力的平台不接入 broker 模式。防 OAuth state hijack(群里他人点开 URL → 用自己 NyxID 登录 → callback 把 sender 绑成他人 NyxID 身份)
- **revoke 双向语义**:aevatar 主动 → `DELETE /oauth/bindings/{binding_id}`;NyxID 主动(用户在 NyxID UI revoke)→ 下次 token-exchange 收到 `invalid_grant` → aevatar 事件化撤销本地 binding actor 并提示 sender 重新 `/init`。NyxID 是 source of truth,aevatar 单向同步,不需要 NyxID 主动回调
- aevatar grain state / projection / log / metric span attribute 不出现 user secret material(refresh_token、PKCE verifier 都不入 grain state);arch test 守此边界,扫描所有 grain state proto 字段树。aevatar service-level secret(state HMAC key、OAuth `client_secret`)按基础设施 secret 管理,不在本不变量约束内
- 生产实现等 NyxID #549 ready 后才合并到生产路径;在此之前 aevatar 现有 bot owner-shared 模式继续运行(不 regression)

## Related

- aevatar Discussion `#400` — Per-sender NyxID binding for channel bots
- aevatar Discussion `#375` — Zero secret material + capability broker boundary
- ChronoAIProject/NyxID Discussion `#511` — External Subject Binding RFC
- ChronoAIProject/NyxID Issue `#549` — OAuth broker bindings(本 ADR 实现的依赖)
- ADR-0011 — Lark Nyx Relay Webhook Topology
- ADR-0012 — Channel Runtime Credential Boundary
- ADR-0013 — Unified Channel Inbound Backbone
