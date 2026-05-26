# Aevatar Responses / Messages 接入指南：用 nyxid CLI 签发 API Key 并配置客户端

面向终端用户：在 Codex、cc-switch、Claude Code 或任意支持 OpenAI Responses / Anthropic Messages 协议的客户端里，通过 NyxID 把流量打到 Aevatar。

当前状态：

- Responses 客户端走 `/v1/responses` 和 `/v1/models`，这是主入口。
- Claude Code 这类 Messages-only 客户端走 `/v1/messages`，Chat Completions-only 客户端走 `/v1/chat/completions`。
- `/v1/responses`、`/v1/messages`、`/v1/chat/completions` 共用直连 tool-source plan 和工具分类；Ornn skill bridge 会在三条入口注入 `use_skill` 和 `ornn_search_skills`，Mainnet 会默认补 `workspace.default` route tool set，chat-route `ToolSetRef` 指定的专用 tool set 也会三条入口同样注入。`lark.self_notify`、`voice.realtime` 必须组合 `workspace.default`，所以 NyxID/Aevatar workspace tools 默认可见。
- 客户端只持有 NyxID API key，不直接接触任何 LLM 供应商凭据。

## 1. 链路速览

```text
client
   ↓ Authorization: Bearer nyx_...
   ↓ /v1/responses、/v1/messages 或 /v1/chat/completions
NyxID proxy plane
   https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1/...
   ↓ 校验 API key + allowed services
Aevatar /v1 API
   ↓ 用同一调用者 bearer 经 NyxID 访问目标 LLM service
NyxID LLM gateway / proxy
   ↓ chrono-llm / llm-anthropic / llm-deepseek / ...
真正的 LLM 上游
   ↓ SSE / JSON 回流
原路返回客户端
```

关键点：

- `base_url` 必须指向 NyxID proxy，不要直连 Aevatar Host 域名。
- API key 至少需要 `proxy` scope，因为 `/api/v1/proxy/s/aevatar/...` 是 NyxID REST proxy 路由。
- Aevatar 不存 LLM key；真实供应商凭据仍由 NyxID 注入。

## 2. 前置条件

- 已注册 NyxID 账号，本机安装好 `nyxid` CLI。
- NyxID 服务端：`https://nyx-api.chrono-ai.fun`
- 需要使用的 LLM service 已在你的 NyxID 账户里可用。

首次登录：

```bash
nyxid login --base-url https://nyx-api.chrono-ai.fun
nyxid whoami
```

## 3. 添加你想用的 LLM 服务

Aevatar 是 NyxID 里的默认服务，用户登录后自动开通，通常不需要手动执行 `nyxid service add aevatar`。

你需要添加下游 LLM service，让 Aevatar 能通过你的 NyxID 身份找到真实模型：

```bash
nyxid service add chrono-llm
nyxid service add llm-anthropic
nyxid service add llm-deepseek
nyxid service add llm-openai-codex

nyxid service list
```

之后模型名使用 `/v1/models` 返回的完整 id，格式是：

```text
<service-slug>/<model>
```

例如：

```text
chrono-llm/gpt-5.5
llm-anthropic/claude-haiku-4-5
```

## 4. 签发 NyxID API Key

最快路径，适合首次验证：

```bash
nyxid api-key create \
  --name "aevatar responses" \
  --scopes proxy \
  --platform codex \
  --allow-all-services \
  --expires-in-days 0
```

输出末尾会打印一次 `nyx_...` 明文 key，只显示这一次。

如果要收紧权限，先拿到目标服务的 UserService id：

```bash
nyxid service list --output json \
  | jq -r '.keys[] | select(.slug=="chrono-llm") | .id'
```

再签发受限 key：

```bash
nyxid api-key create \
  --name "aevatar responses scoped" \
  --scopes proxy \
  --platform codex \
  --allowed-services <chrono-llm-user-service-id>
```

注意：

- `--allowed-services` 必须用 `nyxid service list --output json` 里的 UserService id。
- 不要用 `nyxid catalog list` 里的 catalog id；填错通常会遇到 `api_key_scope_forbidden_legacy`。
- 如果受限 key 访问 `/proxy/s/aevatar/*` 也 403，把 aevatar 的 UserService id 一并加进 `--allowed-services`，或先用 `--allow-all-services` 验证链路。

查看与吊销：

```bash
nyxid api-key list
nyxid api-key delete <key-id>
```

## 5. 配置 Responses 客户端

适用于 Codex、cc-switch 的 Codex provider、OpenCode，以及任何支持 OpenAI Responses 协议的客户端。

cc-switch 的 Codex 标签里新建 Provider：

- Name：`Aevatar`
- `OPENAI_API_KEY`：第 4 步生成的 `nyx_...`
- Config：

```toml
model_provider = "custom"
model = "chrono-llm/gpt-5.5"
disable_response_storage = true

[model_providers]
[model_providers.custom]
name = "custom"
wire_api = "responses"
requires_openai_auth = true
base_url = "https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1"
```

要点：

- `wire_api = "responses"` 必填。
- `base_url` 停在 `/v1`，客户端会追加 `/responses`、`/models`。
- `model` 优先使用 `/v1/models` 返回的 `<service-slug>/<model>`。
- 如果要让直连入口调用 Ornn skills，NyxID API key 的 allowed services 还要覆盖 Ornn API service，默认 slug 是 `ornn-api`。

## 6. 配置 Claude Code / Messages 客户端

适用于只支持 Anthropic Messages 协议的客户端。

Claude Code / cc-switch 的 Claude provider 配置：

```bash
export ANTHROPIC_BASE_URL="https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar"
export ANTHROPIC_AUTH_TOKEN="nyx_xxxxxxxxxxxxxxxx"
export ANTHROPIC_MODEL="chrono-llm/gpt-5.5"
```

要点：

- 用 `ANTHROPIC_AUTH_TOKEN`，让客户端发 `Authorization: Bearer ...`。
- 不要用只会发 `x-api-key` 的配置项；NyxID proxy plane 识别 bearer。
- `ANTHROPIC_BASE_URL` 停在 `/aevatar`，Claude Code 会自行拼 `/v1/messages`。
- `/v1/messages` 是无状态协议门面，每次请求都是一轮新的 `LlmSession`；需要 `previous_response_id` continuation 时用 `/v1/responses`。
- `/v1/messages` 与 `/v1/responses`、`/v1/chat/completions` 共享直连 tool-source plan 和工具分类；服务端会注入 Ornn skill bridge，也会按 chat-route `ToolSetRef` 注入同一批 route tools。
- Messages 的 `max_tokens` 必填。

## 7. curl 冒烟测试

准备环境变量：

```bash
API_KEY="nyx_xxxxxxxxxxxxxxxx"
BASE="https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar/v1"
MODEL="chrono-llm/gpt-5.5"
```

列模型：

```bash
curl -sS "$BASE/models" \
  -H "Authorization: Bearer $API_KEY" \
  | jq '.data[0]'
```

Responses 非流式：

```bash
curl -sS "$BASE/responses" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$MODEL\",
    \"input\": \"ping\"
  }" | jq
```

Responses 流式：

```bash
curl -N "$BASE/responses" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$MODEL\",
    \"input\": \"ping\",
    \"stream\": true
  }"
```

Messages 非流式：

```bash
curl -sS "$BASE/messages" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$MODEL\",
    \"max_tokens\": 512,
    \"messages\": [
      {\"role\": \"user\", \"content\": \"ping\"}
    ]
  }" | jq
```

Messages 流式：

```bash
curl -N "$BASE/messages" \
  -H "Authorization: Bearer $API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"model\": \"$MODEL\",
    \"max_tokens\": 512,
    \"stream\": true,
    \"messages\": [
      {\"role\": \"user\", \"content\": \"ping\"}
    ]
  }"
```

## 8. 常见问题排查

| 现象 | 多半原因 | 解法 |
|---|---|---|
| `401 unauthorized` 来自 `nyx-api.chrono-ai.fun` | API key 错或被吊销 | `nyxid api-key list` 确认，必要时重发 |
| `403 api_key_scope_forbidden_legacy` | `--allowed-services` 填了 catalog id | 用 `nyxid service list --output json` 拿 UserService id 后重签 |
| `403` 访问 `/proxy/s/aevatar/*` | 受限 key 没覆盖 aevatar service | 把 aevatar 的 UserService id 也加入 `--allowed-services`，或先用 `--allow-all-services` 验证 |
| `/v1/models` 为空 | 没有可达的 LLM service，或 key service 权限太窄 | `nyxid service add <slug>`，或临时用 `--allow-all-services` |
| Aevatar 返回 `authentication_required` | 没有带 bearer，或绕过了 NyxID proxy | 确认 URL 是 `/api/v1/proxy/s/aevatar/v1/...`，并带 `Authorization: Bearer` |
| Claude Code 404 | `ANTHROPIC_BASE_URL` 拼错 | base URL 停在 `/api/v1/proxy/s/aevatar`，不要手写到 `/v1/messages` 两次 |
| Messages 返回 `invalid_max_tokens` | `max_tokens` 缺失或不是正整数 | 给 Messages 请求补 `max_tokens` |
| Messages 返回 `unsupported_parameter` | 使用了 `top_p`、`top_k`、`stop_sequences` 或 forced `tool_choice` | 删除这些参数；需要完整控制面时改用 Responses |
| Messages 图片没有进入模型 | 当前 Messages facade v1 会丢弃 image content | 先走文本；图片输入等后续协议补齐 |
| Ornn skill 工具没出现 | Mainnet host 没启用共享直连 tool-source plan / 工具分类或未注册 skill bridge | 确认 Mainnet host 已注册 `IResponsesDirectToolPlanService`、`IResponsesToolClassificationService` 与 `ResponsesUserSkillsToolProvider` |
| Ornn skill 工具能出现但搜索/加载失败 | 受限 NyxID API key 没覆盖 Ornn API service，或用户没有 Ornn 权限 | 把 Ornn API 的 UserService id 加进 `--allowed-services`；确认用户能访问 `ornn-api` |

## 9. 相关文档

- `docs/canon/nyxid-responses-direct.md` — NyxID Responses / Messages 直连权威口径
- `docs/canon/nyxid-llm-integration.md` — Aevatar 内部如何经 NyxID 调 LLM
- `docs/canon/chat-api.md` — Workflow Chat API 能力说明
- NyxID CLI 帮助：`nyxid api-key --help`、`nyxid service --help`
