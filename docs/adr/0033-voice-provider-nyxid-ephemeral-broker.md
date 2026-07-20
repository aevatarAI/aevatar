---
title: "Voice Provider Credential via NyxID Ephemeral Broker"
status: proposed
owner: eanzhao
---

# ADR-0033: 语音 Provider 凭证经 NyxID 临时密钥经纪

## Context

`VoicePresence` 的 OpenAI Realtime provider 之前从宿主静态配置/环境变量读取
`OPENAI_API_KEY`（`Aevatar.Bootstrap.Extensions.AI.ServiceCollectionExtensions.BuildOpenAIVoiceProviderConfig`
读 `Aevatar:VoicePresence:OpenAI:ApiKey` / `OPENAI_API_KEY`），并以
`IsOpenAIVoiceConfigured = !empty(ApiKey)` 作为 host 启动期注册门槛。这与仓库两条
不变量冲突：

- **零长期 secret material（#375 / ADR-0018）**：aevatar 不应持有长期 provider 密钥。
- **NyxID 是唯一凭证经纪**：aevatar 的 tool / LLM 调用已经只走 per-request 的
  caller NyxID token（`AgentToolRequestContext.NyxIdAccessToken` + `NyxIdApiClient`，
  "token comes exclusively from per-request metadata, no local secrets fallback"）；
  唯独 voice provider 这条路径仍读裸 key。

后果：生产部署没有配置静态 key 时，voice 模块在启动期不注册，`/ws/voice` 落到
fail-closed 兜底返回 `503 voice_not_configured`（见 `PolicyAwareVoiceEndpoints` /
`MainnetHostBuilderExtensions`）。

约束（来自 CLAUDE.md「外部仓库无改动权」+ ADR-013）：

- **不得改 NyxID 代码**：只能用 NyxID 现有 proxy surface（加 service 属配置，非代码）。
- **NyxID 不进音频热路径（ADR-013）**：实时音频 WS 必须 aevatar 直连 provider，
  不能整条 WS 走 NyxID 代理。

## Decision

为 voice provider 引入 **per-session 凭证经纪**，取代启动期静态 key：

1. provider 凭证在 **connect 时** 由 `IRealtimeProviderCredentialResolver` 解析，而非
   启动期写死。无 resolver 时回退到静态 `VoiceProviderConfig.ApiKey`（本地/直连开发不变）。
2. 生产实现 `NyxIdRealtimeProviderCredentialResolver`：用 **caller 的 NyxID token**
   经 NyxID proxy `POST /api/v1/proxy/s/<slug>/v1/realtime/client_secrets` 让 NyxID
   注入真实 `sk-...` 并向 OpenAI 申请一个 **短期 ephemeral client secret（`ek_...`）**，
   aevatar 随后用该 ephemeral **直连** `wss://api.openai.com/v1/realtime`。
3. caller 的 NyxID token 由 host 在 `/ws/voice` 连接时从已验证的 bearer 取出，经
   `AgentToolContextScope` 注入 `AgentToolRequestContext`；因为 provider connect 在
   `VoiceVolatileMediaStreamPort.AttachAsync` 内 **同步 in-process** 发生，AsyncLocal
   随调用链流到 resolver，**token 不落 grain/proto state**。
4. 浏览器 `/voice` 使用独立的 feature OAuth session。它在路由写入和麦克风授权前，
   以 RFC 8707 `resource` 申请
   `[<NyxID>/proxy/s/aevatar, <NyxID>/proxy/s/<realtime-slug>]`；authorization-code
   exchange 与 refresh 必须重复同一集合。该 token 单独存储，不覆盖控制台基础登录
   token。用户拥有 service 不代表当前 bearer 已获准代理该 service。

```
/ws/voice handler  (已验证的 caller NyxID bearer, scope 含 proxy)
   │  AgentToolContextScope.Push(Credentials.NyxIdAccessToken = bearer)
   ▼
VoiceVolatileMediaStreamPort.AttachAsync  ──(同一 async 上下文, in-process)──┐
   ▼                                                                        │ AsyncLocal 流动
OpenAIRealtimeProvider.ConnectAsync                                          │
   │  resolver.ResolveApiKeyAsync(sessionKey, config)                       │
   │     callerToken = AgentToolRequestContext.NyxIdAccessToken  ◄──────────┘
   │     ephemeral  = NyxID proxy POST /v1/realtime/client_secrets  (NyxID 注入真实 sk-)
   │     config.ApiKey = ephemeral.value   (ek_…, ~60s TTL, 只在内存)
   ▼  RealtimeClient(ApiKeyCredential(ek_…)) → 直连 OpenAI
OpenAI Realtime WS  ◄──── 音频热路径不经 NyxID（ADR-013 ✓）
```

注册门槛改为 `IsOpenAIVoiceConfigured(config) || nyxIdRealtimeBrokerEnabled`，其中
`nyxIdRealtimeBrokerEnabled = !empty(Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug)`，
使纯经纪部署（无静态 key）也能注册 voice 模块、不再 503。

## 不变量对齐

- **零 secret material**：aevatar 不持有 `sk-...`；ephemeral `ek_...` 仅在 connect
  内存中短暂存在，不入 actor/projection/log；caller bearer 只走 AsyncLocal，不持久化。
- **ADR-013**：NyxID 只在「申请 ephemeral」这一次 HTTP 调用里出现，音频 WS aevatar 直连。
- **per-caller-token 模型**：复用 `AgentToolRequestContext.NyxIdAccessToken` +
  `NyxIdApiClient.ProxyRequestAsync(token, …)`，不新增任何 service-level NyxID secret。
- **最小授权面**：基础 console token 只保留 Aevatar resource；只有进入 Voice 功能时
  才增量申请 realtime resource，并校验 token response 确实覆盖全部请求资源。
- **不改 NyxID**：仅用现有 proxy；OpenAI service 在 NyxID 侧属配置（`nyxid service add`）。

## 实现

| 层 | 改动 |
|---|---|
| `Foundation.VoicePresence.Abstractions` | 新增 `IRealtimeProviderCredentialResolver` + `RealtimeProviderCredentialException` |
| `Foundation.VoicePresence.OpenAI` | `OpenAIRealtimeProvider` 可选注入 resolver；connect 时 resolve-then-validate，未配置时用静态 key |
| `Bootstrap.Extensions.AI` | `NyxIdRealtimeProviderCredentialResolver` + `NyxIdRealtimeProviderCredentialOptions`；门槛 `\|\| brokerEnabled`；注册 resolver；两处 provider 构造注入 resolver |
| `Mainnet.Host.Api/Voice` | `PolicyAwareVoiceEndpoints` 在 attach 前 `AgentToolContextScope.Push(caller bearer)`；`/voice` 在 route/mic 前取得独立的 feature-scoped token |
| `Mainnet.Host.Api/BackendConsole` | 共享 PKCE callback 从 pending state 读取 resources 完成 code exchange，只把白名单 `voice-realtime` token 写入独立 storage key |

依赖：经纪模式要求宿主已配置 NyxID tools（`INyxIdApiClientFactory`）。

OpenAI ephemeral 响应解析兼容 GA（`{ "value": "ek_…" }`）与旧 beta
（`{ "client_secret": { "value": "ek_…" } }`）两种形状。

## Configuration

```
Aevatar:VoicePresence:OpenAI:Nyxid:ServiceSlug = openai-realtime   # 启用经纪
Aevatar:VoicePresence:OpenAI:Nyxid:MintPath    = v1/realtime/client_secrets  # 默认
Aevatar:VoicePresence:OpenAI:Nyxid:Model       = gpt-realtime               # 默认/回退
```

NyxID 侧（运维一次性，凭证只存 NyxID）：

```
nyxid service add --custom --slug openai-realtime --label "OpenAI Realtime" \
  --endpoint-url https://api.openai.com --auth-method bearer
# 凭证填真实 sk-...；service 归属/共享给连接 /ws/voice 的 caller 身份，
# 使其 proxy-scoped token 可访问该 service。
```

`endpoint-url` 必须是裸 host `https://api.openai.com`，使 proxy 路径解析为
`…/v1/realtime/client_secrets`。

Voice OAuth resource URI 使用同一个 `ServiceSlug`：

```
<same base as the injected Aevatar resource>/api/v1/proxy/s/openai-realtime
```

不得把该可选 resource 全局追加到所有 console 登录。未连接此服务的用户会在 authorize
阶段得到 `invalid_target`；正确路径是 `/voice` 按功能增量授权。NyxID proxy 返回
`403 api_key_scope_forbidden` 时，应先重新授权当前 Voice token，而不是提示用户重复连接
一个已经存在的 service。

## Consequences

- 生产无需也不应配置静态 `OPENAI_API_KEY`；密钥只在 NyxID。
- NyxID 成为 voice provider 凭证的单一 control point（限流/审计/吊销）。
- Voice 的 OAuth grant 独立于基础 console session；授权失败不会清除或扩大基础 token。
- ephemeral TTL 极短（~60s），仅用于开连接；泄漏窗口最小。
- 静态 key 路径保留，本地/直连开发与单测不受影响。
- 后续（单独 PR）：voice **工具** 调用在 actor turn 上执行，AsyncLocal 不跨 actor 边界，
  仍未拿到 caller NyxID 凭证；需用 volatile per-lease 旁路（仿 `IVoiceVolatileMediaStreamPort`）
  补齐，超出本 ADR 范围。

## Related

- ADR-013 — NyxID pure passthrough（不进音频热路径）
- ADR-0018 — Per-User NyxID Binding via OAuth Broker（零 secret material / 经纪范式）
