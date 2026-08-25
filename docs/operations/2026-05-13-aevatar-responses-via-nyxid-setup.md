# Aevatar Responses / Messages 接入指南：用 nyxid CLI 签发 API Key 并配置客户端

面向终端用户：在 Codex、cc-switch、Claude Code 或任意支持 OpenAI Responses / Anthropic Messages 协议的客户端里，通过 NyxID 把流量打到 Aevatar。

当前状态：

- Responses 客户端走 `/v1/responses` 和 `/v1/models`，这是主入口。
- Claude Code 这类 Messages-only 客户端走 `/v1/messages`，Chat Completions-only 客户端走 `/v1/chat/completions`。
- `/v1/responses`、`/v1/messages`、`/v1/chat/completions` 共用 published profile snapshot、intent-selected exact catalog、proof 校验与 forwarded-tool 分类。普通问答不会默认注入 `workspace.default` 全量工具；web、Ornn skill、Aevatar invoke 等能力只有被本轮 profile/intent 选中时才进入小型 owned catalog，三条入口行为一致。
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

然后登录 Aevatar Backend Admin，进入 `/admin#/models`。当前 scope 页面可以继承平台默认，
也可以选择 NyxID `/api/v1/keys` inventory 中的 exact UserService 并显式填写模型 ID；
平台管理员还可以在“平台默认”页配置所有继承 scope 的通用目录。系统不会根据 URL 或服务名中
是否含 `llm` 自动识别，所以 `chrono-llm`、`chrono-llm-public` 也通过这里显式加入。

配置物化后，模型名使用 `/v1/models` 返回的完整 id，格式是：

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
- `/v1/messages` 与 `/v1/responses`、`/v1/chat/completions` 共享 exact catalog planner 和工具分类；chat-route tool set 只提供 ceiling，服务端只注入当前 profile/intent 选中的 exact tools。普通 skill intent 只有 `ornn_search_skills` + `use_skill`，authoring 工具需要显式 authoring profile。
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
| `/v1/models` 为空 | 当前 scope 配置了显式空替换，或 effective policy 没有模型 | 在 `/admin#/models` 添加来源和显式模型 ID，或切回继承平台默认 |
| 已列出的模型调用返回 NyxID `403/404` | policy 只控制发现；对应 binding、组织权限或 API key `allowed_service_ids` 不满足 | 用 `nyxid service list --output json` 核对 exact UserService，并调整 NyxID 授权后重试 |
| `/v1/models` 返回 `503 model_catalog_unavailable` | effective policy projection 暂不可读，尤其是尚未初始化平台默认时 | 平台管理员先保存平台默认配置；若已配置则检查 projection/read model 健康状态 |
| Aevatar 返回 `authentication_required` | 没有带 bearer，或绕过了 NyxID proxy | 确认 URL 是 `/api/v1/proxy/s/aevatar/v1/...`，并带 `Authorization: Bearer` |
| Claude Code 404 | `ANTHROPIC_BASE_URL` 拼错 | base URL 停在 `/api/v1/proxy/s/aevatar`，不要手写到 `/v1/messages` 两次 |
| Messages 返回 `invalid_max_tokens` | `max_tokens` 缺失或不是正整数 | 给 Messages 请求补 `max_tokens` |
| Messages 返回 `unsupported_parameter` | 使用了 `top_p`、`top_k`、`stop_sequences` 或 forced `tool_choice` | 删除这些参数；需要完整控制面时改用 Responses |
| Messages 图片没有进入模型 | 当前 Messages facade v1 会丢弃 image content | 先走文本；图片输入等后续协议补齐 |
| Ornn skill 工具没出现 | 本轮 profile/intent 没选择 skill runtime，或 exact catalog planner / profile read model 不可用 | 确认 route 绑定 reviewed profile、skill intent 命中，并检查 `IResponsesOwnedToolCatalogPlanner` 的 typed failure；不要用全局 provider 注入绕过 |
| Ornn skill 工具能出现但搜索/加载失败 | 受限 NyxID API key 没覆盖 Ornn API service，或用户没有 Ornn 权限 | 把 Ornn API 的 UserService id 加进 `--allowed-services`；确认用户能访问 `ornn-api` |

## 9. 相关文档

- `docs/canon/agent-turn-tool-catalog.md` — 每轮 exact owned catalog、proof、预算与 rollout 权威口径
- `docs/canon/nyxid-responses-direct.md` — NyxID Responses / Messages 直连权威口径
- `docs/canon/nyxid-llm-integration.md` — Aevatar 内部如何经 NyxID 调 LLM
- `docs/canon/chat-api.md` — Workflow Chat API 能力说明
- NyxID CLI 帮助：`nyxid api-key --help`、`nyxid service --help`
