---
title: "Scope Service 经 NyxID SA Downstream 绑定与 Skill 调用方案"
status: draft
owner: eanzhao
---

# Scope Service 经 NyxID SA Downstream 绑定与 Skill 调用方案

> 状态：方案已按双方代码逐点验证（文末有代码事实索引），**未做线上实测**。标注「未实测」的条目在落地时按第 5.4 节验证清单回归。

## 1. 背景与目标

aevatar 的 scope service（`/api/scopes/{scopeId}/services/{serviceId}/invoke/{endpointId}` 一族）目前只能由持有 NyxID session JWT 的用户直连调用。本方案把它注册为 NyxID 的 downstream service，使 ornn skill（运行于 SkillRunner / channel-bot agent run 中）能通过既有的 `nyxid_services` 发现 + `nyxid_proxy` 调用工具链访问 scope service 能力，不引入任何新工具。

选择 **SA（service account）+ token_exchange** 路线而非 `forward_access_token` 路线，原因是 skill 运行时的调用方凭证形态不可控：

| 调用场景 | 凭证形态 | forward_access_token 路线 | SA 路线 |
|---|---|---|---|
| chat 触发的 skill（relay 下发 sender token） | session JWT | ✅ 可通（调 sender 自己的 scope） | ✅ 可通 |
| scheduled / cron skill（minted key） | `nyx_*` opaque API key | ❌ aevatar JWT 验证 401 | ✅ 可通 |
| 外部 agent / CI 集成 | `nyx_*` / `sa_*` | ❌ 401 或 scope 不匹配 403 | ✅ 可通 |

代价：所有调用在 aevatar 侧折叠为同一身份（SA），落在同一 scope（SA 的 UUID）。准入控制整体由 NyxID 侧的 service 访问控制承担。两条路线互不排斥，可并存注册为两个 service。

## 2. 端到端链路

```mermaid
%%{init: {"maxTextSize": 100000, "sequence": {"actorMargin": 25, "messageMargin": 22, "boxMargin": 6, "mirrorActors": false}, "themeVariables": {"fontSize": "11px"}}}%%
sequenceDiagram
    participant S as "Skill LLM (SkillRunner run)"
    participant T as "nyxid_proxy 工具"
    participant N as "NyxID Proxy"
    participant O as "NyxID /oauth/token"
    participant A as "aevatar /api/scopes/&lt;SA_UUID&gt;"

    S->>T: "tool_call: slug + path + payloadJson"
    T->>N: "POST /api/v1/proxy/s/&lt;slug&gt;/services/... (Bearer caller token)"
    N->>N: "认证 caller + service 访问检查"
    N->>O: "token_exchange: client_credentials (缓存未过期则跳过)"
    O-->>N: "SA access token (JWT, expires_in)"
    N->>A: "POST services/.../invoke/&lt;endpointId&gt; (Bearer SA AT + X-NyxID-User-*)"
    A->>A: "JWT 验签 → scope_id=SA UUID → guard 通过"
    A-->>N: "202 receipt (runId + runs URL)"
    N-->>T: "透传响应"
    T-->>S: "JSON 结果"
    S->>T: "轮询 GET services/.../runs/&lt;runId&gt;"
```

## 3. 身份与授权模型（已验证事实）

- SA 经 `POST /oauth/token` (`grant_type=client_credentials`, form-encoded) 换出的 AT 是 **RS256 JWT**，与用户 token 同一 issuer、同一 JWKS、同一 `aud`（NyxID base_url），`sub` = SA 的 UUID，无 `uid`，附加 `sa: true`，TTL 1 小时无 refresh token。
- aevatar 认证层对 `sa` claim 无任何特殊处理；claims waterfall（`scope_id → uid → sub`）将 `sub` 映射为 `scope_id`，即 **scope_id = SA UUID**。
- scope guard 要求恰好一个 scope claim 且与 URL 中 scopeId **Ordinal 相等** → SA 只能访问 `/api/scopes/<SA_UUID>/` 下的资源。本方案把 base_url 钉死到该前缀，调用方无法触达其他 scope。
- **1 小时 TTL 由 NyxID token_exchange 机制原生解决**：downstream service 注册为 `auth_method=token_exchange`，NyxID 服务端按 `expires_in` 缓存并自动续换，存储的静态凭证是 SA 的 `client_id/client_secret`，不会过期。
- proxy 会剥除 caller 伪造的 `x-nyxid-*` 请求头，再注入服务端生成的 `X-NyxID-User-*` 身份头（需开启 `identity_propagation_mode=headers`），不可伪造。

## 4. 绑定 Runbook

### 4.0 前提

1. NyxID 上已存在 aevatar 的 service account（`client_id` 形如 `sa_…`），`allowed_scopes` 至少包含 `proxy llm:proxy`（run 内回调 NyxID LLM 路由需要）。
2. 取得 SA 的 UUID（即未来的 scopeId）：换一次 AT 并解码 JWT 的 `sub`：

```bash
curl -s -X POST https://<nyxid-host>/oauth/token \
  -d grant_type=client_credentials -d client_id=sa_xxx -d client_secret=*** \
  | jq -r .access_token | cut -d. -f2 | base64 -d | jq .sub
```

3. SA 作为「用户」需在 NyxID 配好 LLM provider 连接（若绑定的 scope service 是含 `llm_call` 的 workflow）——见 GAP-2。

### 4.1 在 SA scope 内发布 scope service

用 SA 的 AT 直连 aevatar（不经 proxy），把 workflow / script / gagent 绑定进 SA 的 scope 并激活 revision：

```bash
SA_AT=$(curl -s -X POST https://<nyxid-host>/oauth/token \
  -d grant_type=client_credentials -d client_id=sa_xxx -d client_secret=*** | jq -r .access_token)

curl -s -X PUT "https://<aevatar-host>/api/scopes/<SA_UUID>/binding" \
  -H "Authorization: Bearer $SA_AT" -H 'Content-Type: application/json' \
  -d '{ "implementationKind": "workflow", "workflow": { ... }, "displayName": "..." }'
```

**资源归属声明**：`<SA_UUID>` scope 下的全部 binding / revision / run 归 aevatar 平台运维所有，由运维负责清理与升级；不视为任何最终用户的工作空间。

### 4.2 在 NyxID 注册 downstream service（两步）

**第一步 create**（`POST /api/v1/services`）。`identity_propagation_mode` 与 `inject_delegation_token` 在 create 路径被硬编码为 `none`/`false`，必须二步 update 补设：

```jsonc
{
  "name": "aevatar Scope Services",
  "slug": "aevatar-scope-services",
  // base_url 直接含 SA scope 前缀（base_url 校验允许带 path）：
  //  1) skill 调用路径无需感知 scopeId；2) 暴露面钉死在单 scope。
  "base_url": "https://<aevatar-host>/api/scopes/<SA_UUID>",
  "service_type": "http",
  "visibility": "private",            // ⚠️ 默认 public，必须显式收紧
  "service_category": "internal",     // 共享 master 凭证，不要求 per-user credential
  "auth_method": "token_exchange",
  "token_exchange_config": {
    "endpoint": "https://<nyxid-host>/oauth/token",
    "request_encoding": "form",
    "request_template": {
      "grant_type": "client_credentials",
      "client_id": "$client_id",
      "client_secret": "$client_secret",
      "scope": "proxy llm:proxy"
    },
    "token_response_path": "access_token",
    "ttl_response_path": "expires_in",
    "default_ttl_secs": 3600,
    "injection": "bearer"
  },
  "credential": "{\"client_id\":\"sa_xxx\",\"client_secret\":\"***\"}",
  "forward_access_token": false,
  "streaming_supported": true,
  "openapi_spec_url": "https://<aevatar-host>/api/openapi.json",
  "description": "aevatar scope services：list → contract → invoke(payloadJson) → poll runs。路径以 services/ 开头。"
}
```

**第二步 update**（`PUT /api/v1/services/{id}`），为审计留痕：

```json
{ "identity_propagation_mode": "headers", "identity_include_user_id": true }
```

**第三步 org 共享**：将 service 共享给 ChronoAI org（org admin 操作），使 chat 场景的 sender token 与 scheduled 场景的 minted key 对该 service 可见可用。

### 4.3 端点暴露面

base_url 钉死后，经 `/api/v1/proxy/s/aevatar-scope-services/<rest>` 可达的就是该 scope 的全部端点，skill 主要使用：

| rest path | 用途 |
|---|---|
| `GET services` | 列出 scope 内服务（含 invoke readiness） |
| `GET services/{serviceId}/endpoints/{endpointId}/contract` | 取请求/响应 schema |
| `POST services/{serviceId}/invoke/{endpointId}` | 非流式调用（推荐） |
| `GET services/{serviceId}/runs/{runId}` | 轮询 run 终态 |
| `POST services/{serviceId}/invoke/{endpointId}:stream` | SSE 流式（skill 内禁用，见 GAP-4） |

### 4.4 验证清单（落地时按序回归）

1. token_exchange 烟测：经 proxy `GET services` 返回 200（验证 SA AT 注入 + scope guard 通过）。
2. `POST .../invoke/<endpointId>` 带 `payloadTypeUrl + payloadJson` 返回 receipt，轮询 runs 到终态。
3. 用非 owner 的 org 成员 token 重复步骤 1（验证 org 共享与 approval 准入实况——未实测项）。
4. 用 scheduled 场景的 minted key 重复步骤 1（预期 403，确认 GAP-1 现状）。
5. curl 直连 proxy 调 `:stream` 验证 SSE 透传（浏览器/curl 场景可用性，与 skill 无关）。
6. 在一个真实 skill 内走「发现 → contract → invoke → 轮询」全链。

## 5. Skill 调用约定

- **发现式，不硬编码 slug**：skill prompt 用 `nyxid_services` 列表按 label/description 关键字定位服务（与 ICP skill v2.3 同范式）；service 的 `description` 字段写明调用配方入口。
- **标准配方（三步）**：
  1. `nyxid_proxy { slug, path: "services", method: "GET" }` → 选 serviceId/endpointId；
  2. `nyxid_proxy { path: "services/<id>/endpoints/<ep>/contract", method: "GET" }` → 取请求 schema；
  3. `nyxid_proxy { path: "services/<id>/invoke/<ep>", method: "POST", body: { "payloadTypeUrl": "...", "payloadJson": "{...}" } }` → 从响应取 runId → 轮询 `services/<id>/runs/<runId>`。
- `payloadJson` 与 `payloadBase64` 互斥；`payloadJson` 由 aevatar 服务端按 revision schema pack 成 protobuf，LLM 只需产出普通 JSON。
- **禁止在 skill 内调 `:stream` 端点**：`nyxid_proxy` 工具把整个 HTTP 响应 buffer 成字符串返回，SSE 会挂到流结束或超时（GAP-4）。

## 6. Gap 清单

| # | Gap | 影响 | 严重度 | 归属 / 出路 |
|---|---|---|---|---|
| GAP-1 | scheduled / cron 场景的 minted key `allowed-services` 不含本 service 时，proxy 准入即 403（与 #1990 同源） | 定时 skill 调不通；chat 触发不受影响 | 高 | NyxID 侧 minting 策略或 aevatar 侧 mint 请求把本 service 纳入授权面；落地前按 4.4-4 实测确认 |
| GAP-2 | SA 作为「用户」需有 LLM provider 连接 / user-config，否则含 `llm_call` 的 scope service run 内回调 NyxID 必败 | run 启动后失败 | 高 | 运维前置项：为 SA 配置 LLM 连接（status probe 已有同型先例） |
| GAP-3 | 身份折叠：aevatar 审计 / run 归属只见 SA，真实调用者仅存在于 proxy 注入的 `X-NyxID-User-*` 头，aevatar 当前不读取 | 无法按真实用户审计、限流、计费 | 中 | aevatar 侧后续小改：读取身份头落审计字段（不影响认证链） |
| GAP-4 | `nyxid_proxy` 工具整体 buffer 响应，无流式消费能力 | skill 不能消费 `:stream`；只能 invoke + 轮询 | 中 | 已有 workaround（第 5 节配方）；流式消费需工具层新能力，暂不做 |
| GAP-5 | 注入的 SA AT 固定 1h：run 执行超 1h 后，run 内回调 NyxID 的 caller credential 过期 401 | 长 run 的 LLM/工具调用中断 | 中 | skill 场景 run 通常短；长 run 需 aevatar 侧凭证刷新机制（当前不存在，独立议题） |
| GAP-6 | skill → scope service（workflow）→ `llm_call` → `use_skill` 可再递归，无深度防护（#1902） | runaway 风险 | 中 | 绑定进 SA scope 的服务避免携带 `use_skill` / `aevatar_start_workflow` 工具面；根治等 #1902 |
| GAP-7 | member / team 端点对 SA 不可用（SA token 无 roles claim，member guard 不匹配） | 仅 scope 级 default / 具名 service 面可用 | 低 | 本方案不使用 member/team 面；如需要则属 aevatar 侧授权模型扩展 |
| GAP-8 | NyxID create API 不暴露 identity/delegation 字段，注册需 create + update 两步；CLI 向导未必覆盖 token_exchange 全部字段 | 操作繁琐、易漏步 | 低 | Runbook 已固化两步；建议直接走 REST API 而非向导 |
| GAP-9 | `visibility` 默认 `public`：漏设即把 SA scope 的调用面开放给全部 NyxID 用户 | 越权调用 | 低（已规避） | Runbook 钉死 `visibility=private` + org 共享；列入 4.4-3 回归 |
| GAP-10 | org 共享 service 的 approval 准入行为未实测（per-user 一次性 connect approval 是否拦首调） | 首次调用可能被审批卡住 | 低 | 4.4-3 实测后回填本文档 |

## 7. 代码事实索引

| 事实 | 位置 |
|---|---|
| SA AT 生成（sub=SA UUID、aud=base_url、sa:true、RS256） | NyxID `backend/src/crypto/jwt.rs:710-758` |
| client_credentials grant 入口 | NyxID `backend/src/handlers/oauth.rs:1573-1635` |
| ServiceAccount.id 即 JWT sub | NyxID `backend/src/models/service_account.rs:9-13` |
| TokenExchangeConfig（自带 OAuth client_credentials 模板、form 编码、expires_in TTL） | NyxID `backend/src/models/downstream_service.rs:43-83` |
| token_exchange 执行与凭证注入 | NyxID `backend/src/services/proxy_service.rs:2387-2501` |
| create 路径硬编码 identity=none / delegation=false；update 可设 | NyxID `backend/src/handlers/services.rs:988-994, 1321-1352` |
| base_url 校验仅 scheme/host/元数据黑名单（允许带 path） | NyxID `backend/src/services/url_validation.rs:187-211` |
| SSE 按响应 content-type 自动流式透传 | NyxID `backend/src/handlers/proxy.rs:1934, 2268` |
| caller 伪造 x-nyxid-* 头剥除 + 身份头注入 | NyxID `backend/src/handlers/proxy.rs:1568-1660` |
| scope guard：单 claim + Ordinal 匹配 | aevatar `src/Aevatar.Capabilities/AevatarScopeAccessGuard.cs:83-107` |
| claims waterfall scope_id→uid→sub | aevatar `src/Aevatar.Authentication.Providers.NyxId/NyxIdClaimsTransformer.cs:19-25` |
| payloadJson/payloadBase64 互斥、服务端 pack | aevatar `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:1990-2028` |
| contract / invoke / runs 端点注册 | aevatar `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:51-97` |
| nyxid_proxy 工具整体 buffer（`Task<string>`） | aevatar `src/Aevatar.AI.ToolProviders.NyxId/NyxIdApiClient.cs:171-188` |
| OpenAPI 文档匿名暴露 | aevatar `src/Aevatar.Bootstrap/Hosting/WebApplicationBuilderExtensions.cs:35, 178` |
