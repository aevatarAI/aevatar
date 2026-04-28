---
title: "Per-User NyxID Binding via OAuth Client"
status: proposed
owner: eanzhao
---

# ADR-0017: Per-User NyxID Binding via OAuth Client

## Context

Discussion `#400` 提出把 channel bot(Lark / Telegram / Discord)消息链路里的"sender"语义从 bot owner 改成**per Lark user 自己的 NyxID subject**:每个 sender 第一次交互走 `/init` 走一轮 NyxID 登录,之后用其自身 nyx subject 跑 LLM、tool、capability。

当前的现实:

- 入站 webhook `Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` 只验 NyxID relay JWT,只解出 bot owner 的 `scope_id`
- `ChannelConversationTurnRunner.BuildReplyMetadata` 把 bot owner 的 `user_access_token` 透传给 LLM tool
- 任何 Lark user 跟 bot 聊天都在代表 **bot owner** 的 NyxID 账号说话
- 没有 `external subject → nyx subject` 的持久映射,也没有 `INyxIdCapabilityBroker`

`#400` 原方案要求 NyxID 侧加 5 个新端点(challenge 签发、`/cli-auth` 扩展 `binding_jti`、主动 webhook 回调、bindings 查询、bindings revoke)。

回扫当前 NyxID surface 后,这 5 个 ask 几乎全是已有 OAuth/OIDC 能力的重复发明:

| `#400` 要 NyxID 加的 | NyxID 现状 |
|---|---|
| `POST /api/v1/bindings/challenges` 签 challenge JWT | OAuth `state` 参数(`/oauth/authorize`)已承载 correlation id |
| 扩展 `/cli-auth` 接受 `binding_jti` | `/cli-auth` 已透传任意 `state` |
| NyxID 主动 webhook 回调 aevatar | OAuth 标准是浏览器跳 `redirect_uri`,不需要服务端主动推 |
| `GET /api/v1/bindings?external_subject=...` | external→nyx 映射归 aevatar 自己持;拿 nyx subject 用 `/oauth/userinfo` |
| revoke 端点 | `/oauth/revoke` 已有(RFC 7009) |

外加 NyxID 已实现 RFC 8693 Token Exchange (`/oauth/token` with `grant_type:urn:ietf:params:oauth:grant-type:token-exchange`),正好对应 `#400` 里 `IssueShortLivedAsync` 的语义。

## Decision

aevatar 实现 per-user NyxID binding,**作为 NyxID 的标准 OAuth 2.0 client**,**不要求 NyxID 任何 surface 改动**。

具体口径:

- aevatar 注册 NyxID OAuth client(建议 client_id `aevatar-channel-binding`),用 Authorization Code + PKCE 流程
- correlation 用 OAuth `state` 参数承载,callback 走标准 `redirect_uri`(浏览器跳转,不依赖 NyxID 服务端主动推)
- 拿用户 `NyxSubjectRef` 用 `/oauth/userinfo` 的 `sub` claim
- 短期 capability handle 用 RFC 8693 Token Exchange(把用户 access_token 当 `subject_token` 换 delegated token)
- aevatar 自己持有 `(platform, tenant, external_user_id) → NyxSubjectRef` 映射,在新增的 `ExternalIdentityBindingGAgent`

`#400` 描述的 `/init` 流程在 OAuth primitive 上的等价改写:

```
/init
  -> ChannelConversationTurnRunner 前置 slash-command 路由(不进 LLM)
  -> aevatar 生成 state(=correlation_id) + PKCE pair,落 BindingChallengeIssuedEvent
     到 ExternalIdentityBindingGAgent
  -> 回 Lark "{nyxid}/oauth/authorize?client_id=aevatar-channel-binding
     &redirect_uri=https://aevatar/api/oauth/nyxid-callback
     &state={correlation_id}&code_challenge=...&scope=openid"
  -> 用户登录 → NyxID 302 回 aevatar /api/oauth/nyxid-callback?code=...&state=...
  -> aevatar callback handler:
       state -> ExternalSubjectRef
       /oauth/token (PKCE verifier) -> access_token + refresh_token
       /oauth/userinfo -> sub (= NyxSubjectRef)
     落 ExternalIdentityBoundEvent

turn
  -> ResolveAsync(externalSubject) -> nyx_subject
  -> Token Exchange (POST /oauth/token, grant_type=token-exchange) 换短期 handle
  -> 塞 AgentToolRequestContext (key 名 `nyxid.capability_handle`)
```

## INyxIdCapabilityBroker:Seam 与两个 Adapter

`INyxIdCapabilityBroker` 是 capability 层的 seam,业务代码(`ChannelConversationTurnRunner` 等)只依赖此接口。语义照 `#400` 定义,新增 `IssueShortLivedAsync`:

- `StartExternalBindingAsync(externalSubject) -> BindingChallenge`
- `ResolveBindingAsync(externalSubject) -> NyxSubjectRef?`
- `RevokeBindingAsync(externalSubject)`
- `IssueShortLivedAsync(nyxSubject, scope) -> CapabilityHandle`

落地两个 Adapter,满足"两个 adapter 才是真 seam"的原则:

| Adapter | 角色 | 实现 |
|---|---|---|
| `LocalRefreshTokenCapabilityBroker` | 过渡(本 ADR 接受) | aevatar 加密持 refresh_token;`IssueShortLivedAsync` 用 refresh_token 调 `/oauth/token` 换 access_token,再 Token Exchange 收窄 |
| `NyxIdRemoteCapabilityBroker` | 终态(`#375` / `#511` 落地后) | aevatar 不持 secret material;binding 落到 NyxID broker 端,handle 由 NyxID 签发 |

切换实现是一行 DI 配置变更,业务代码不动。

## Trade-off:Refresh Token Locality

`#375` 的 zero-secret-material-in-grain-state 不变量要求 aevatar 不持有 long-lived secret。NyxID 不动 → aevatar 必须自己持 refresh_token(标准 OAuth client 都这么做)。Local vs Remote 的 blast radius 对比:

| 场景 | Local(LocalRefreshTokenCapabilityBroker) | Remote(NyxIdRemoteCapabilityBroker) |
|---|---|---|
| Event store 备份泄露 | 加密保护;需同时拿到 `IDataProtection` key ring 才能解 | 完全不可解(grain 只存 opaque sub) |
| Aevatar 进程 RCE | **全量 binding 的长期 refresh_token 一锅端**,可静默 impersonate 所有用户 | 仅丢当前 turn 的 5 分钟 handle |
| Prompt injection / tool 越权 | 任何能间接读到 grain state 的越权路径 → 加密 token 暴露 | grain state 里没 secret material 可读 |
| Revoke | 必须 aevatar 主动调 `/oauth/revoke`,失败 = 备份内 token 仍可用 | NyxID 是 source of truth,revoke 一步到位 |

aevatar 是跑任意 LLM tool 的 agent runtime(prompt injection、tool 越权属于固有风险),长期看 Remote 是唯一对得起 `#375` 哲学的形态。

本 ADR 接受 Local 作为**显式的过渡实现**,前提是终态明确指向 Remote,且 Local 必须满足下文的硬约束。

## Local Adapter 的硬约束

`LocalRefreshTokenCapabilityBroker` 的实现必须满足:

1. **Key ring 隔离**:`IDataProtection` key ring 必须跟 event store 物理隔离(独立 KMS / KeyVault / 独立备份策略)。两份东西放进同一份 backup = 加密形同虚设。
2. **短 lifetime**:refresh_token lifetime ≤ 30 天,显著短于 NyxID 默认值,降低单次泄露价值。
3. **同步 revoke**:本地 unbind 必须同步调 NyxID `/oauth/revoke`;失败要重试或告警。漏掉一步则备份内 token 仍活,本地却看不到。
4. **arch test 守边界**:加密 refresh_token 只能存在 `ExternalIdentityBindingGAgent` 自身 state 字段;不得污染 `ConversationGAgent` / projection document / log / metric / trace span attribute。CI 守卫扫描所有 grain state proto 字段树。
5. **异常使用监控**:同一 `nyx_subject` 短时间多 IP / 多 platform / token rotation 频率异常 → 告警通道。

## Consequences

- channel bot 的 per-user binding 不再阻塞于 NyxID 侧 issue
- `INyxIdCapabilityBroker` 接口稳定,后续切到 Remote 实现不动业务代码
- 阶段性接受 grain state 内有加密 refresh_token,跟 `#375` zero-secret-material 哲学有张力,但路径明确
- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort`
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(替代 `#400` 原文里的 `/api/webhooks/nyxid-binding-callback`,语义更准:不是 webhook,是 OAuth redirect)
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由(`/init`、`/unbind`)
- `BuildReplyMetadata` 改成 `ResolveAsync` + `IssueShortLivedAsync`;metadata key 从 `nyxid.access_token` 改为 `nyxid.capability_handle`(诚实表达"短期、scoped、可撤销")
- 群聊场景采纳 `#400` Open Questions 第 1 项的 A 选项:未绑定 sender 强制 `/init`,不回落到 bot owner 配额。这点跟 ADR-0012 的 channel runtime credential boundary 一致

## Migration Path to #375 Compliant State

1. 本 ADR 落地 `LocalRefreshTokenCapabilityBroker` + Lark per-user binding(unblock `#400`)
2. NyxID `#511` / `#505` 落地 capability broker 端点
3. 实现 `NyxIdRemoteCapabilityBroker`,DI 配置切换
4. 数据迁移:wipe `ExternalIdentityBindingGAgent` 加密 refresh_token 字段;改持 nyx_subject + remote handle ref
5. 收紧 arch test:禁止任意 grain state 出现 secret material(无论是否加密)

切换完成后,本 ADR status 改为 `superseded`,由后续记录 Remote 形态的 ADR 替代。

## Related

- Discussion `#400` — Per-sender NyxID binding for channel bots
- Discussion `#375` — Zero secret material + capability broker boundary
- ChronoAIProject/NyxID Discussion `#505` — NyxID as Capability Broker scope
- ChronoAIProject/NyxID Discussion `#511` — Capability broker protocol
- ADR-0011 — Lark Nyx Relay Webhook Topology
- ADR-0012 — Channel Runtime Credential Boundary
- ADR-0013 — Unified Channel Inbound Backbone
