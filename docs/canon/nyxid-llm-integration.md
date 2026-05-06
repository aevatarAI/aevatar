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

用户的 API Key 始终加密存储在 NyxID 中，Aevatar 不接触明文密钥。Gateway 按 model 名自动路由（`gpt-4o` → OpenAI，`claude-sonnet-4-5-20250929` → Anthropic）。

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
支持。model 名决定路由。用户可在 NyxID 上同时连接多个 Provider。

**Q: 本地开发能直接用 API Key 吗？**
可以。在 CLI Settings > LLM 页面配置 OpenAI/DeepSeek 等 Provider 并填入 API Key，与 NyxID Gateway 共存。
