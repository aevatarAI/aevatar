---
title: "NyxID LLM Provider 集成指南"
status: active
owner: eanzhao
---

# NyxID LLM Provider 集成指南

Aevatar 的 Agent 可以通过 NyxID LLM Gateway 使用用户在 NyxID 上配置的 LLM API Key（OpenAI、Anthropic、DeepSeek 等），无需在 Aevatar 端存储任何密钥。

## 原理

```
用户通过 NyxID 登录 Aevatar（Bearer Token）
        |
Agent 需要调用 LLM
        |
Aevatar 用用户的 Bearer Token 直接请求 NyxID LLM Gateway
        |
NyxID 验证 Token → 查找该用户的 API Key → 注入 → 转发给上游 Provider
        |
返回结果
```

用户的 API Key 始终加密存储在 NyxID 中，Aevatar 不接触明文密钥。旧调用方仍可让 Gateway 按裸 model 名做兼容路由（例如 `gpt-4o` → OpenAI，`claude-sonnet-4-5-20250929` → Anthropic），但新的 Responses 直连接入应优先使用 `/v1/models` 返回的 `<service-slug>/<model>`。

---

## Responses 直连路由

外部客户端通过 NyxID proxy 调 Aevatar `/v1/responses`、`/v1/messages` 或 `/v1/chat/completions` 时，调用者身份与下游委托凭据分开处理。`X-NyxID-Identity-Token` 若存在，Host 先用 NyxID JWKS 校验该 RS256 assertion；校验通过后用 `sub` 作为 caller scope，不再调用 `/me`。该 header 存在但校验失败时 fail closed。只有缺失 identity assertion 时，才使用 inbound bearer 调 NyxID `/me` fallback。`X-NyxID-Delegation-Token` 只作为 downstream delegated credential，永远不作为 caller identity 输入。

模型路由采用 OpenRouter 风格：

```text
<service-slug>/<model>
```

例如：

```text
chrono-llm/gpt-5.5
llm-anthropic/claude-haiku-4-5
```

`GET /v1/models` 会按调用者 bearer 从 NyxID service catalog 聚合可达服务，并把每个上游模型规范化成上述格式。创建请求时，Aevatar 会把 `<service-slug>/<model>` 拆成两部分：

1. `service-slug` 通过 NyxID catalog 解析成 `route_value`，写入本次 LLM request 的 `NyxIdRoutePreference`。
2. 裸 `model` 传给下游 LLM provider。

如果客户端传裸 model 名，Aevatar 仍会走默认 gateway fallback。这只是兼容路径，不是新文档推荐路径。

完整外部接入说明见 `docs/canon/nyxid-responses-direct.md`；终端配置步骤见 `docs/operations/2026-05-13-aevatar-responses-via-nyxid-setup.md`。

---

## Channel Route 选择

Lark bot 等 channel surface 通过 `/model`、`/models`、`/llm`、`/route` 暴露同一组 LLM route 命令：

- `/route`：列出当前 NyxID 绑定用户可作为 LLM provider 的 ready service
- `/route use <编号|service-name> [model-name]`：保存 service route，可同时指定 model
- `/model use <model-name>`：只覆盖当前 route 下的 model
- `/model preset <preset-id>`：按 NyxID 返回的 setup preset 使用或创建 service
- `/model reset`：清空用户偏好，回退到 bot 默认配置

这些命令不读取 Aevatar 内部密钥，也不使用独立的 `llm:status` scope。Aevatar 通过 per-user NyxID binding 做 broker token-exchange，请求 `proxy` scope 的短期 token，然后调用 NyxID LLM service catalog / route API。集群自举注册的 OAuth client 以及 `/oauth/authorize` 必须使用同一 canonical scope：

```text
openid urn:nyxid:scope:broker_binding proxy
```

如果旧 binding 对应的 OAuth client 未包含 `proxy`，NyxID 会在 token-exchange 返回 `invalid_scope`。用户可重新发送 `/init` 完成绑定刷新；Aevatar 不会降级到 bot-owner credential 或缓存 token。

---

## Console Settings 契约

Console 的 Settings、Chat composer、Studio workflow dry-run 必须共用后端 LLM Settings 视图，不再各自读取 NyxID catalog 后在前端推断 route、provider label 或 fallback。

Canonical endpoints：

- `GET /api/user-config/llm`：返回当前用户的 LLM settings view。
- `PUT /api/user-config/llm`：投递保存 `routeValue` 与 `model` 的命令，返回 `202 Accepted` receipt（`accepted`、`commandId`、`ackStage = "accepted"`、`actorId`、`correlationId`、`ackedAtUtc`）；该响应只承诺命令已进入 dispatch/inbox 边界，不承诺 actor 已 handled、event 已 committed 或 read model 已 observed。前端保存成功后必须重新 `GET /api/user-config/llm` 读取 canonical settings view。
- `GET /api/user-config/runtime`：返回 runtime mode、active runtime URL、local/remote URL 以及后端默认值。
- `GET /api/auth/me`：返回最小 typed `profile` / `session`，不得回显 access token、refresh token、raw JWT 或 raw claims。

`GET /api/user-config/llm` 是 Settings 闭环的唯一 route truth。响应必须至少表达：

- `savedRoute` / `savedRouteLabel`：用户保存的 route 及展示名。
- `effectiveRoute` / `effectiveRouteLabel`：本次实际可用的 route 及展示名；当 saved route 不可用时由后端选择 fallback。
- `routeFallbackActive` / `fallbackReason`：诚实暴露 saved route 与 effective route 是否分离。
- `routeOptions`：可选 route 列表，包含 `routeValue`、`label`、`source`、`status`、`allowed`、`ready`、`serviceId`、`serviceSlug`。
- `modelGroupsByRoute`：按 route 分组的模型集合；前端不得用 provider slug 或 model 前缀重新拼装。
- `catalogStatus` 与 `capabilities`：用于驱动禁用态、保存态与 retry 行为。
- `defaultModel`：当前保存的默认模型。

NyxID catalog 不可用时，后端返回 degraded view，而不是空列表：保留 `savedRoute`、`effectiveRoute`、`defaultModel`，设置 `catalogStatus = "unavailable"`，并通过 `capabilities` 禁止编辑和保存、允许 retry。前端只展示这个 degraded view，不做 query-time fallback 或本地补跑 catalog。

Gateway route 的稳定值是空字符串 `""`。Gateway 的展示名由后端 settings view 返回；前端只消费 `savedRouteLabel`、`effectiveRouteLabel`、`routeOptions[].label`，不得把 `NyxID Gateway` 当作 route display source 硬编码。

旧 Console surface 不再保留兼容入口：`/api/user-config/models`、`/api/user-config/llm/options`、`/api/user-config/llm/preference` 已被 canonical `/api/user-config/llm` 取代。

---

## NyxID 端配置（管理员）

### 1. 创建 LLM Provider

在 NyxID 管理后台 **Providers** > **Manage** 页面创建 Provider（以 OpenAI 为例）：

| 字段 | 值 |
|------|-----|
| Name | OpenAI |
| Slug | openai |
| Provider Type | api_key |
| API Key Instructions | 前往 https://platform.openai.com/api-keys 创建 |
| Is Active | true |

同理创建 Anthropic、DeepSeek 等。

---

## Aevatar 端配置（管理员）

在 `appsettings.json` 中配置 NyxID Authority 即可。系统会自动注册 NyxID LLM Provider：

```json
{
  "Aevatar": {
    "NyxId": {
      "Authority": "https://your-nyxid-domain"
    }
  }
}
```

Gateway Endpoint 自动推导为 `{Authority}/api/v1/llm/gateway/v1`。

---

## 用户使用流程

1. **在 NyxID 上连接 Provider** — 登录 NyxID → Providers 页面 → 点击 Connect → 输入 API Key
2. **在 Aevatar 上使用** — 通过 NyxID 登录后，Agent 调用 LLM 时自动使用该用户的 API Key，无需额外操作

---

## 常见问题

**Q: 用户没配 API Key 会怎样？**
LLM 调用失败，NyxID 返回错误提示用户需要先连接 Provider。

**Q: 支持多个 Provider 吗？**
支持。NyxID catalog 提供多个可用 route，Aevatar 后端把它们物化成 `routeOptions` 与 `modelGroupsByRoute`，Settings 中选择的 route 和 model 决定后续调用。

**Q: 本地开发能直接用 API Key 吗？**
可以。在 CLI Settings > LLM 页面配置 OpenAI/DeepSeek 等 Provider 并填入 API Key，与 NyxID Gateway 共存。
