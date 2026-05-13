# Aevatar Responses 接入指南：用 nyxid CLI 签发 API Key 并配置 cc-switch

面向终端用户：在 cc-switch / Codex / 任意支持 OpenAI Responses 协议的客户端里，
通过 NyxID 把流量打到 Aevatar，由 Aevatar 完成补全后回复客户端。

> Aevatar 当前对外暴露的是 **OpenAI Responses 协议**（`/v1/responses`、`/v1/models`），
> 还未实现 Anthropic Messages 协议。所以：
> - ✅ Codex CLI、Cursor、OpenCode 等 Responses 客户端可以接入
> - ❌ Claude Code（仅支持 Messages）暂时无法直连 Aevatar

---

## 1. 链路速览

```
cc-switch (codex app)
   ↓ Authorization: Bearer nyx_xxx
   ↓ POST /v1/responses
NyxID proxy plane
   https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1/responses
   ↓ 校验 API Key + allowed_services
   ↓ 注入 X-NyxID-Delegation-Token（user 身份）
Aevatar Responses API
   https://aevatar-console-backend-api.aevatar.ai/v1/responses
   ↓ 用 delegation token 经 NyxID 再调用 LLM provider
NyxID LLM gateway / proxy
   ↓ chrono-llm / llm-anthropic / llm-deepseek …
真正的 LLM 上游
   ↓ SSE / JSON 回流
原路返回客户端
```

关键点：

- 客户端只持有 **一把 NyxID API Key**，不直接接触任何 LLM 供应商凭据。
- API Key 既用于校验 `nyx-api → aevatar` 这一跳，也会作为 delegation token 让 Aevatar
  在下游再次回到 NyxID 完成实际的 LLM 调用。
- Aevatar 自身不存任何 LLM key，所有计费、限流、撤销都集中在 NyxID 侧。

---

## 2. 前置条件

- 已注册 NyxID 账号，本机安装好 `nyxid` CLI（`which nyxid` 应返回路径）。
- NyxID 服务端：`https://nyx-api.chrono-ai.fun`
- 本机安装好 cc-switch。

登录（首次或换机时）：

```bash
nyxid login --base-url https://nyx-api.chrono-ai.fun
nyxid whoami
```

---

## 3. 添加你想用的 LLM 服务

> **Aevatar 已经是 NyxID 的默认服务，每个 NyxID 用户登录后自动开通**（`auto_connected: true`，
> 不出现在 `nyxid catalog list` 的 "Available Services" 里）。所以**不需要**
> `nyxid service add aevatar`。

你只需要再挂一个 **LLM provider**，让 Aevatar 下游能找到真实模型。任选其一，按需多加：

```bash
nyxid service add chrono-llm       # 团队共享网关，无需自带 key（最简单）
nyxid service add llm-anthropic    # 自带 Anthropic key
nyxid service add llm-deepseek
nyxid service add llm-openai-codex

# 确认已添加
nyxid service list
```

> 之后在 cc-switch 里发请求时，会用 `chrono-llm/gpt-5.5`、
> `llm-anthropic/claude-haiku-4-5` 这种 `<provider-slug>/<model>` 形式指定模型，
> Aevatar 的 `/v1/models` 会把你账户下能用的全部列出来。

---

## 4. 用 nyxid CLI 签发 API Key

最快路径（首次接入推荐，所有已添加的服务都放行）：

```bash
nyxid api-key create \
  --name "cc-switch aevatar" \
  --scopes proxy \
  --platform codex \
  --allow-all-services \
  --expires-in-days 0
```

输出末尾会打印一次 **`nyx_...`** 形式的明文 Key，**只显示这一次**，复制保存。

如果想最小权限收紧（推荐生产用）：先拿到所选 LLM 服务的 UserService.id，再用 `--allowed-services`：

```bash
# 取你要用的 LLM provider 的 UserService.id（aevatar 自动开通，不用列）
nyxid service list --output json \
  | jq -r '.keys[] | select(.slug=="chrono-llm") | .id'

# 用这个 UserService ID 签发受限 Key
nyxid api-key create \
  --name "cc-switch aevatar (scoped)" \
  --scopes proxy \
  --platform codex \
  --allowed-services <chrono-llm-user-service-id>
```

> ⚠️ 必须用 `nyxid service list` 里的 UserService.id，不是 `nyxid catalog list`
> 里的目录 id。两者不同；后者会得到 `api_key_scope_forbidden_legacy`。
>
> 如果收紧后访问 `/proxy/s/aevatar/*` 反而 403，把 aevatar 的 UserService.id
> 也加进 `--allowed-services` 兜底——auto_connected 服务理论上应被 proxy 默认放行，
> 但不同版本 NyxID 的行为可能不同。

查看与吊销：

```bash
nyxid api-key list
nyxid api-key delete <key-id>
```

---

## 5. 配置 cc-switch（codex / Responses 形态）

打开 cc-switch → **Codex** 标签 → 新建 Provider，填写如下字段：

- **Name**：`Aevatar`
- **OPENAI_API_KEY**：第 4 步生成的 `nyx_...`
- **Config (toml)**：

```toml
model_provider = "custom"
model = "chrono-llm/gpt-5.5"          # 改成你实际想用的 <provider-slug>/<model>
disable_response_storage = true

[model_providers]
[model_providers.custom]
name = "custom"
wire_api = "responses"
requires_openai_auth = true
base_url = "https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1"
```

要点：

- **`wire_api = "responses"`** 必填——Aevatar 只讲 Responses 协议。
- **`base_url`** 必须停在 `/v1`，cc-switch 会自动追加 `/responses`、`/models`。
- `model` 形如 `<nyxid-service-slug>/<model-name>`；可用清单见下一步。

保存并切到这个 Provider。

---

## 6. 端到端冒烟测试

不进 cc-switch 也能验证（直接 curl）：

```bash
API_KEY="nyx_xxxxxxxxxxxxxxxx"
BASE="https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1"

# 1) 列出当前账户在 Aevatar 视角下可用的模型
curl -sS "$BASE/models" \
  -H "Authorization: Bearer $API_KEY" | jq '.data[].id' | head -20

# 2) 发一次最简 Responses 请求
curl -sS "$BASE/responses" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "chrono-llm/gpt-5.5",
    "input": "ping"
  }' | jq
```

正常返回里应包含 `id`、`output[].content[].text` 等 Responses 标准字段。

---

## 7. 常见问题排查

| 现象 | 多半原因 | 解法 |
|---|---|---|
| `401 unauthorized` 来自 `nyx-api.chrono-ai.fun` | API Key 错或被吊销 | `nyxid api-key list` 确认，必要时重发 |
| `403 api_key_scope_forbidden_legacy` | `--allowed-services` 填的是 catalog id 而不是 UserService.id | 用 `nyxid service list --output json` 拿到的 id 重签 |
| `403` 访问 `/proxy/s/aevatar/*` | 罕见——理论上 aevatar `auto_connected=true` 默认放行；若 NyxID 该版本仍走严格 allowed_services 校验则会 403 | 把 aevatar 的 UserService.id 也加进 `--allowed-services`，或 `--allow-all-services` |
| `403` / 模型在 `/v1/models` 列表里看不到 | 想用的 LLM 服务还没加进你的 NyxID 账户 | `nyxid service add <slug>` 再列一次 |
| `401 authentication_required` 来自 Aevatar | Bearer 没被 NyxID proxy 转写、或绕过了 proxy 直连了 Aevatar | 确认 `base_url` 是 `/api/v1/proxy/s/aevatar/v1`，**不要**直接写 `aevatar-console-backend-api.aevatar.ai` |
| `wire_api` 报错 / Claude Code 接入失败 | 客户端只支持 Messages 协议 | 现阶段 Claude Code 不能用，等 Aevatar 接 Messages 后再来 |
| 模型清单为空 | API Key 没有 `--allow-all-services` 也没正确 `--allowed-services` | 先用 `--allow-all-services` 验证链路，再回头收紧 |

---

## 8. 相关文档

- `docs/canon/nyxid-llm-integration.md` — Aevatar 侧如何用 NyxID LLM Gateway
- `docs/canon/chat-api.md` — Aevatar Responses API 详细字段语义
- NyxID CLI 帮助：`nyxid api-key --help` / `nyxid proxy --help` / `nyxid service --help`
