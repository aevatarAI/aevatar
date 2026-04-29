---
title: "RFC 8693 Token Exchange — Known Gaps (Client Side)"
status: discussion
owner: eanzhao
---

# RFC 8693 Token Exchange — Known Gaps (aevatar / Client Side)

## TL;DR

aevatar 通过 `NyxIdRemoteCapabilityBroker` 作为
**[RFC 8693](https://datatracker.ietf.org/doc/html/rfc8693) 客户端**调 NyxID
`POST /oauth/token`，覆盖 ADR-0018 broker 模式所需的最小请求集。**当前不是
完整 RFC 8693 客户端实现**，未来若要支撑跨服务委托链 / 跨 IdP 互通 / 受众
约束等场景，需要按本文清单补齐。对侧 NyxID 服务端的同类讨论见
`<NyxID repo>/docs/RFC_8693_TOKEN_EXCHANGE_GAPS.md`。

## Context

- 角色：RFC 8693 客户端（token requester）。
- 关键代码：
  - `agents/Aevatar.GAgents.Channel.Identity/Broker/NyxIdRemoteCapabilityBroker.cs:24-25`
    — grant_type / subject_token_type 常量（`urn:nyxid:params:oauth:token-type:binding-id` 是私有 URI）
  - `NyxIdRemoteCapabilityBroker.cs:167-225` — `IssueShortLivedByBindingIdAsync`，构造 form 调 `/oauth/token`
  - `NyxIdRemoteCapabilityBroker.cs:357-365` — `TokenResponse` 反序列化形状
- 设计依据：[ADR-0018 — Per-User NyxID Binding via OAuth Broker](../../adr/0018-per-user-nyxid-binding-via-oauth-broker.md)。
  ADR-0018 的目标是"零长期用户 secret + 5 分钟短期 token"，因此实现了
  RFC 8693 的子集就够用；本文不质疑这个目标，只是把 **RFC 完整性差距**
  显式记录下来，避免未来误以为我们已经"完整实现 RFC 8693"。

## 现状摘要（实际发出的请求 / 反序列化的响应）

请求端发送的 form 字段（`NyxIdRemoteCapabilityBroker.cs:178-185`）：

| [RFC 8693 §2.1](https://datatracker.ietf.org/doc/html/rfc8693#section-2.1) 字段 | 是否发送 | 取值 / 备注 |
|---|---|---|
| `grant_type` | ✅ | `urn:ietf:params:oauth:grant-type:token-exchange` |
| `subject_token` | ✅ | `binding_id`（NyxID 不透明指针） |
| `subject_token_type` | ✅ | `urn:nyxid:params:oauth:token-type:binding-id`（**私有 URI**） |
| `scope` | ✅ | 仅在非空时发 |
| `actor_token` | ❌ | — |
| `actor_token_type` | ❌ | — |
| `requested_token_type` | ❌ | — |
| `audience` | ❌ | — |
| `resource` | ❌ | — |

客户端鉴权用 HTTP Basic（`ApplyClientSecretBasic`，`L316-321`）。

响应端反序列化的字段（`TokenResponse` `L357-365`）：`access_token` /
`id_token` / `binding_id` / `scope` / `token_type` / `expires_in`。
**未声明 `issued_token_type`**（[RFC 8693 §2.2.1](https://datatracker.ietf.org/doc/html/rfc8693#section-2.2.1) 必需字段被悄悄丢弃）。

错误处理仅区分 HTTP 400 + `{"error":"invalid_grant"}` →
`BindingRevokedException`（`L197-204`）；其他 OAuth error code 全部
`EnsureSuccessStatusCode()` 抛通用异常。

## Gaps（按 RFC 8693 章节对照）

### [§2.1 请求参数](https://datatracker.ietf.org/doc/html/rfc8693#section-2.1)

1. **`actor_token` / `actor_token_type` 缺失**：当前只能表达"主体（用户）
   同意客户端以自己身份调用"，不能表达"A 在 actor B 的身份代理下行事"
   这一 [RFC 8693 §1.3](https://datatracker.ietf.org/doc/html/rfc8693#section-1.3)
   核心语义。一旦 aevatar 自己变成"代用户调下游服务（也用 NyxID 鉴权）"
   的中介，就需要 actor_token 来诚实传递 actor 身份，并让最终服务能审计
   真主与 actor。
2. **`requested_token_type` 缺失**：现在只能拿 `access_token`；如果未来
   场景需要拿 `id_token`（向其他 OIDC RP 出示）或 `jwt`（自描述、跨域
   可验证），需要补此参数。
3. **`audience` / `resource` 缺失**：当前签发的短期 token 没有受众限定，
   完全靠 NyxID 自己的 client / scope 边界来约束。一旦 token 被泄漏到
   非预期的下游服务，没有
   [RFC 8707](https://datatracker.ietf.org/doc/html/rfc8707) /
   [RFC 8693 §2.1](https://datatracker.ietf.org/doc/html/rfc8693#section-2.1)
   的受众绑定可阻止误用。

### [§2.2.1 响应](https://datatracker.ietf.org/doc/html/rfc8693#section-2.2.1)

4. **`issued_token_type` 不解析**：`TokenResponse` 字段表里没有这个字段；
   即使服务端按 RFC 写回，客户端也不会用。理由是当前调用点只关心
   "我拿到一个 access_token"，但若未来响应类型是动态的（例如
   `requested_token_type=jwt` 时拿 `urn:ietf:params:oauth:token-type:jwt`），
   客户端必须读这个字段才能正确处理。

### [§2.2.2 错误处理](https://datatracker.ietf.org/doc/html/rfc8693#section-2.2.2)

5. **错误码区分不足**：只识别 `invalid_grant`，对 `invalid_request` /
   `invalid_scope` / `invalid_target` / `unsupported_token_type` 等没有
   分类映射，失败时排障靠日志里的截断 body（`Truncate(body, 256)`）。

### Token type URI

6. **使用私有 `urn:nyxid:...` URI 作为 subject_token_type**：是 ADR-0018
   有意选择（明确表示"这是 NyxID broker binding，不是任何标准 token"），
   但意味着客户端代码 **不可移植**到非 NyxID 的 RFC 8693 服务器。

## 已经"做对了"的部分

- 标准的 `urn:ietf:params:oauth:grant-type:token-exchange` grant_type；
- 标准 `client_secret_basic` 客户端认证；
- 错误路径上对 `invalid_grant` 做了 ADR-0018 要求的 source-of-truth 同步
  （NyxID 主动 revoke → aevatar 事件化撤销本地 binding，详见 ADR-0018
  Decision §第 4 条）。

## Future Work（如果需要"完整 RFC 8693 客户端"，按此顺序补）

按收益从高到低：

1. **响应解析补 `issued_token_type`**（成本低、立刻让客户端 RFC 兼容）。
   只需在 `TokenResponse` 加一行字段并写到日志/审计上下文。
2. **错误码细分**（成本低，可观测性提升）。把 `IsInvalidGrant` 推广为
   `TryParseOAuthError` 返回结构化 `OAuthError { code, description, uri }`，
   按 code 抛不同异常或返回 `Result`。
3. **`audience` / `resource` 支持**（中等成本，需要 ADR）。需要先在
   `CapabilityScope` 上下文里建模"这个 token 给谁用"，再在 NyxID 端实现
   audience 检查；客户端只是把参数透传过去。
4. **`requested_token_type` 支持**（中等成本）。客户端要先有"我需要哪种
   token"的业务输入，再考虑实现。
5. **`actor_token` 链路**（高成本，跨 ADR）。需要重新设计 aevatar 内部
   "actor 身份"的建模——目前 aevatar 不持有自己的 NyxID 身份做 actor，
   要补这一层。优先级最低：除非出现"代用户调下游服务，且下游服务也用
   NyxID"这个明确业务场景，否则不值得做。
6. **替换私有 subject_token_type**：长期看，如果 NyxID 端把 broker
   binding 也用标准 URI（如 `urn:ietf:params:oauth:token-type:jwt`，把
   binding_id 包成 JWT），客户端就能用标准 URI；这要 NyxID 先动。

## 参考

- [RFC 8693 — OAuth 2.0 Token Exchange](https://datatracker.ietf.org/doc/html/rfc8693)
- [RFC 8707 — Resource Indicators for OAuth 2.0](https://datatracker.ietf.org/doc/html/rfc8707)
- [RFC 6749 — The OAuth 2.0 Authorization Framework](https://datatracker.ietf.org/doc/html/rfc6749)
- ADR-0018 — `docs/adr/0018-per-user-nyxid-binding-via-oauth-broker.md`
- 对侧 NyxID 服务端 discussion — `<NyxID repo>/docs/RFC_8693_TOKEN_EXCHANGE_GAPS.md`
