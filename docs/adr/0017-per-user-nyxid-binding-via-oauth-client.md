---
title: "Per-User NyxID Binding via OAuth Broker"
status: accepted
owner: eanzhao
---

# ADR-0017: Per-User NyxID Binding via OAuth Broker

## Context

Discussion `#400` 提出把 channel bot(Lark / Telegram / Discord)消息链路里的"sender"语义从 bot owner 改成**per Lark user 自己的 NyxID subject**:每个 sender 第一次交互走 `/init` 走一轮 NyxID 登录,之后用其自身 nyx subject 跑 LLM、tool、capability。

当前现实:

- 入站 webhook `Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` 只验 NyxID relay JWT,只解出 bot owner 的 `scope_id`
- `ChannelConversationTurnRunner.BuildReplyMetadata` 把 bot owner 的 `user_access_token` 透传给 LLM tool
- 任何 Lark user 跟 bot 聊天都在代表 **bot owner** 的 NyxID 账号说话
- 没有 `external subject → nyx subject` 的持久映射,也没有 `INyxIdCapabilityBroker`

`#400` 原 RFC 要求 NyxID 加 5 个端点(challenge 签发、`/cli-auth` 扩展 `binding_jti`、主动 webhook 回调、bindings 查询、bindings revoke)。回扫 NyxID surface 后这 5 个 ask 大部分能用现有 OAuth/OIDC primitive 替代;唯一真正缺失的是 broker 形态的"接入方代用户拿短期 access_token,但永不接触 refresh_token"。

本 ADR 第一版草稿曾接受 `LocalRefreshTokenCapabilityBroker`(aevatar 加密持 refresh_token)作为过渡实现。讨论后否决,详见 Decision Rationale。当前决定走 broker 路径,实现依赖 NyxID 侧新 issue。

## Decision

aevatar 实现 per-user NyxID binding,**作为 NyxID broker 的 OAuth 接入方**:

- 用标准 OAuth Authorization Code + PKCE 流程发起 binding(`/oauth/authorize` + `state` 承载 correlation_id + redirect_uri 浏览器跳转)
- **aevatar 不接收、不持有 user refresh_token**;binding 完成时 NyxID 返回不透明 `binding_id`,aevatar 仅持 `(external_subject_ref) → binding_id` 映射
- 每次 turn 用 client_credentials 调 NyxID `POST /oauth/bindings/{binding_id}/token` 拿短期 access_token,塞进 `AgentToolRequestContext`
- 用户撤销同步 `DELETE /oauth/bindings/{binding_id}`,NyxID 是 source of truth

aevatar grain state、projection、log、metric 持有 zero long-lived secret material,对齐 `#375` 不变量。

`/init` 流程在 OAuth + broker primitive 上的等价改写:

```
/init
  -> ChannelConversationTurnRunner 前置 slash-command 路由(不进 LLM)
  -> aevatar 生成 state(=correlation_id) + PKCE pair,
     落 BindingChallengeIssuedEvent 到 ExternalIdentityBindingGAgent
  -> 回 Lark "{nyxid}/oauth/authorize?client_id=aevatar-channel-binding
     &redirect_uri=https://aevatar/api/oauth/nyxid-callback
     &state={correlation_id}&code_challenge=...&scope=openid+broker_binding"
  -> 用户登录 → NyxID 302 回 aevatar /api/oauth/nyxid-callback?code=...&state=...
  -> aevatar callback handler:
       state -> ExternalSubjectRef
       /oauth/token (PKCE verifier) -> { access_token, binding_id }
                                       (broker_binding scope 下不返 refresh_token)
     落 ExternalIdentityBoundEvent { external_subject, nyx_subject, binding_id }

turn
  -> ResolveAsync(externalSubject) -> binding_id
  -> POST {nyxid}/oauth/bindings/{binding_id}/token (client_credentials)
     -> short-lived access_token
  -> 塞 AgentToolRequestContext (key 名 `nyxid.capability_handle`)
```

## Storage Boundary

| 数据 | aevatar grain state | NyxID |
|---|---|---|
| `(platform, tenant, external_user_id) → binding_id` | ✓ | |
| `nyx_subject`(opaque `sub` claim) | ✓ 缓存以加速 resolve | ✓ source of truth |
| `binding_id`(opaque) | ✓ | ✓ 索引到内部 refresh_token |
| User refresh_token | ✗ never | ✓ encrypted |
| Short-lived access_token | per-turn `AsyncLocal`,不持久化 | 签发方 |

`binding_id` 在 RCE 场景下的语义跟 refresh_token 不同:它必须配合 aevatar 的 `client_secret` 才能换 token,而 NyxID 可以对 `(client_id, binding_id)` 做 rate limit、异常 audit、用户主动 revoke。NyxID 因此是真正的 control point,而不只是"换个地方存的 refresh_token"。

## INyxIdCapabilityBroker:Single Production Adapter

`INyxIdCapabilityBroker` 是 capability 层的 seam,业务代码(`ChannelConversationTurnRunner` 等)只依赖此接口:

- `StartExternalBindingAsync(externalSubject) -> BindingChallenge`
- `ResolveBindingAsync(externalSubject) -> NyxSubjectRef?`
- `RevokeBindingAsync(externalSubject)`
- `IssueShortLivedAsync(externalSubject, scope) -> CapabilityHandle`

唯一生产实现 `NyxIdRemoteCapabilityBroker`,内部:

- `IssueShortLivedAsync` 调 `POST /oauth/bindings/{binding_id}/token`(client_credentials)
- `RevokeBindingAsync` 调 `DELETE /oauth/bindings/{binding_id}`
- `ResolveBindingAsync` 查 `ExternalIdentityBindingGAgent` projection(纯本地,不调 NyxID)
- `StartExternalBindingAsync` 构造 `/oauth/authorize` URL + PKCE,落 challenge event

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

- `INyxIdCapabilityBroker` 接口 + proto
- `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort`(state 仅存 `binding_id` + `nyx_subject`,均为 opaque)
- `ChannelConversationTurnRunner.RunInboundAsync` 的 slash-command 前置路由(`/init`、`/unbind`)
- `/api/oauth/nyxid-callback` endpoint 标准 OAuth redirect 处理框架
- `InMemoryCapabilityBroker` 测试 fake

NyxID #549 ready 后:

- `NyxIdRemoteCapabilityBroker` 实现接好真实 endpoint
- end-to-end 测试 + 灰度
- 切到生产路径

## Consequences

- 新增模块 `Aevatar.GAgents.Channel.Identity`(并列于 `Aevatar.GAgents.Channel.NyxIdRelay`):承载 `ExternalIdentityBindingGAgent` + projection + `IExternalIdentityBindingQueryPort` + `INyxIdCapabilityBroker`
- 新增 OAuth callback endpoint `/api/oauth/nyxid-callback`(标准 OAuth client redirect 处理,不是 webhook)
- `ChannelConversationTurnRunner.RunInboundAsync` 开头加 slash-command 前置路由
- `BuildReplyMetadata` 改成 `ResolveAsync` + `IssueShortLivedAsync`;metadata key 从 `nyxid.access_token` 改为 `nyxid.capability_handle`(诚实表达"短期、scoped、可撤销")
- 群聊场景采 `#400` Open Questions 第 1 项的 A 选项:未绑定 sender 强制 `/init`,不回落到 bot owner 配额
- aevatar grain state / projection / log / metric span attribute 不出现 secret material;arch test 守此边界,扫描所有 grain state proto 字段树
- 生产实现等 NyxID #549 ready 后才合并到生产路径;在此之前 aevatar 现有 bot owner-shared 模式继续运行(不 regression)

## Related

- aevatar Discussion `#400` — Per-sender NyxID binding for channel bots
- aevatar Discussion `#375` — Zero secret material + capability broker boundary
- ChronoAIProject/NyxID Discussion `#511` — External Subject Binding RFC
- ChronoAIProject/NyxID Issue `#549` — OAuth broker bindings(本 ADR 实现的依赖)
- ADR-0011 — Lark Nyx Relay Webhook Topology
- ADR-0012 — Channel Runtime Credential Boundary
- ADR-0013 — Unified Channel Inbound Backbone
