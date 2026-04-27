---
title: /daily Command Pipeline — Test Reference
status: draft
owner: eanzhao
---

# `/daily` 命令完整链路与测试参考

> 本文档面向 QA。它把 `/daily` 端到端链路所有可观察的步骤、契约、状态、错误模式都列出来，方便由这些可观察项反推具体测试用例。
>
> 读者假设：熟悉 Lark 自定义机器人、HTTP webhook、cron 表达式；不假设熟悉 aevatar 内部 actor / projection 模型——文档会把进入这些层时的"输入 / 输出 / 副作用"显式列出。
>
> 涉及外部系统（Lark Open Platform、NyxID、GitHub）的部分，重点描述 **aevatar 与它们交互的边界契约**，而不是它们各自的实现。

---

## 0. 触发场景与目标

用户行为：在 Lark 上以 **私聊（p2p）** 给绑定到 NyxID 的机器人发送消息：

```
/daily                       # 使用已保存的 GitHub 用户名（或弹出表单）
/daily <github_username>     # 显式绑定 + 立即执行
/daily <user> schedule_time=09:00 schedule_timezone=Asia/Singapore repositories=owner/repo,owner/repo run_immediately=false
```

当前可观察结果：
1. aevatar 会 best-effort 对原消息加 ✓ emoji 反应；该调用是 fire-and-forget，不等待成功，缺权限或 Lark 拒绝时只记录日志。
2. aevatar 内部创建一个 `SkillRunnerGAgent`（`skill-runner-{guid32}`），按 cron 计划执行 daily 报告。
3. 如果 `run_immediately=true`（默认），当前实现会在同一次 `AgentBuilderTool` 调用内直接触发首次执行：通过 NyxID proxy 调 GitHub Search API → 让 LLM 总结过去 24 小时活动 → 通过 NyxID proxy 把文本回写到原 Lark 私聊。
4. 首次执行尝试返回后，才格式化并发送 `/daily` 创建确认回复；因此用户通常先看到 ✓ reaction，然后看到 daily 报告和创建确认中的一个或两个，顺序取决于投递路径和耗时。
5. agent 状态写入 `UserAgentCatalogGAgent`（well-known），可通过 `/agents`、`/agent-status <id>` 查询。

---

## 1. 端到端链路总览

```
┌────────────┐      ┌───────────────┐      ┌──────────────────────────┐      ┌───────────────┐      ┌──────────┐
│  Lark App  │ ───▶ │  NyxID Relay  │ ───▶ │ aevatar /api/webhooks/   │ ───▶ │ NyxID Proxy   │ ───▶ │  GitHub  │
│ (用户发消息) │      │ (channel-bot) │      │   nyxid-relay (POST)     │      │ s/api-github  │      │ Search   │
└────────────┘      └───────────────┘      └──────────────────────────┘      └───────────────┘      └──────────┘
       ▲                                              │                                                    │
       │                                              ▼                                                    │
       │                                  ┌──────────────────────────┐                                    │
       │                                  │ ChannelConversationTurn  │                                    │
       │                                  │ Runner → AgentBuilderTool│ ◀──────── (LLM tool 调用) ──────────┘
       │                                  │ → SkillRunnerGAgent      │
       │                                  └──────────────────────────┘
       │                                              │
       │                                              ▼
       │                                  ┌──────────────────────────┐
       └─────────────────────────────────┤  NyxID Proxy s/api-lark-bot │
                                          │  POST /im/v1/messages     │
                                          └──────────────────────────┘
```

完整 7 段链路（与用户描述一致）：

| 段 | 方向 | 内容 |
|----|------|------|
| ① Lark → NyxID | 入站 | Lark 把 `im.message.receive_v1` 推到 NyxID 的 channel bot relay webhook |
| ② NyxID → aevatar | 入站 | NyxID 把规范化后的 payload + 签名 JWT 转发到 aevatar `/api/webhooks/nyxid-relay` |
| ③ aevatar 内部 | 处理 | 鉴权 → 解析 `/daily` → `AgentBuilderTool.CreateDailyReportAgentAsync` → 创建 `SkillRunnerGAgent` |
| ④ aevatar → NyxID | 出站（创建 API key + GitHub 预检） | `POST /api/v1/api-keys`、`GET /api/v1/proxy/s/api-github/...`（preflight） |
| ⑤ NyxID → GitHub | LLM 工具调用 | `nyxid_proxy` 工具 → NyxID 注入 GitHub OAuth token → GitHub Search API |
| ⑥ GitHub → aevatar | 工具响应 | JSON 结果回到 LLM；LLM 总结成一段文本 |
| ⑦ aevatar → NyxID → Lark | 出站回执 | `POST /api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages` 把文本投递到原私聊 |

---

## 2. 链路时序

```
Lark User      Lark App     NyxID Relay     aevatar(webhook)    SkillRunnerGAgent     NyxID(proxy)     GitHub      LLM
   │              │              │                │                      │                  │              │          │
   │── /daily ───▶│              │                │                      │                  │              │          │
   │              │── event ────▶│                │                      │                  │              │          │
   │              │              │── POST relay ─▶│                      │                  │              │          │
   │              │              │  +X-NyxID-     │                      │                  │              │          │
   │              │              │  Callback-Token│                      │                  │              │          │
   │              │              │                │── ✓ react ──────────────────────────▶ Lark             │          │
   │              │              │                │                      │                  │              │          │
   │              │              │                │── parse /daily       │                  │              │          │
   │              │              │                │── CreateApiKey ─────────────────────▶ NyxID             │          │
   │              │              │                │   (services=         │                  │              │          │
   │              │              │                │    api-github,       │                  │              │          │
   │              │              │                │    api-lark-bot)     │                  │              │          │
   │              │              │                │── preflight GitHub ─────────────────▶ NyxID ───────▶ GitHub      │
   │              │              │                │   (/rate_limit)      │                  │              │          │
   │              │              │                │── Initialize ───────▶│                  │              │          │
   │              │              │                │   SkillRunner        │                  │              │          │
   │              │              │                │── Trigger ──────────▶│                  │              │          │
   │              │              │                │   (run_immediately)  │                  │              │          │
   │              │              │                │                      │── ExecuteSkill ────────────────────────────▶│
   │              │              │                │                      │                  │              │          │
   │              │              │                │                      │                  │   (LLM 决定 tool 调用)   │
   │              │              │                │                      │◀── nyxid_proxy(GET /search/commits) ────────│
   │              │              │                │                      │── proxy call ───▶│              │          │
   │              │              │                │                      │                  │── injects ──▶│          │
   │              │              │                │                      │                  │   gh OAuth   │          │
   │              │              │                │                      │                  │              │── search ▶│
   │              │              │                │                      │                  │              │◀─ items ─│
   │              │              │                │                      │◀─ JSON ──────────│              │          │
   │              │              │                │                      │   (重复 commits/issues/comments) │          │
   │              │              │                │                      │── final text ─────────────────────────────▶│
   │              │              │                │                      │◀─ summary text ───────────────────────────│
   │              │              │                │                      │── SendOutput ───▶│              │          │
   │              │              │                │                      │   POST /im/v1/   │── deliver ──▶│          │
   │              │              │                │                      │   messages       │              │          │
   │◀──── daily 报告 ────────────────────────────────────────────────────────── Lark        │              │          │
   │◀──── 创建确认 / agent id ───────────────────────────────────────────────── Lark        │              │          │
```

注意几个时间窗：
- **webhook 返回窗口**：当前 `HandleRelayWebhookAsync` 会等待 `ConversationGAgent.HandleEventAsync` 返回。对 `/daily run_immediately=true` 来说，创建 agent、GitHub preflight、首次 SkillRunner 执行和创建确认回复都在这条调用链里完成；因此“webhook 必须 ≤3 秒返回”是目标约束，不是当前代码已经保证的行为。
- **✓ reaction**：`TrySendImmediateLarkReactionAsync()` 是 fire-and-forget，`RunInboundAsync` 不等待它完成；它可以独立失败，也不能证明后续 agent 创建成功。它还有静默 gate：只对 `ActivityType.Message`、`lark/feishu` 平台、存在 `NyxUserAccessToken` 与 `NyxProviderSlug`、且 `NyxPlatformMessageId` 以 `om_` 开头的消息尝试发送。
- **首次执行延迟**：现网首次执行通常由 LLM 推理 + GitHub 多次 search 主导，约几十秒。当前创建确认回复可能在首次报告之后才到达。
- **下一次定时执行**：UTC `0 9 * * *`（默认 09:00 UTC，可改 `schedule_time` / `schedule_timezone`）。

---

## 3. 阶段详解

### 阶段 ① Lark → NyxID（不在 aevatar 范围内，但 QA 要能区分）

NyxID 上每个 Lark 机器人对应一条 `channel_bot` 记录，含：
- `bot_id`（Lark App ID）
- `callback_url`（指向 aevatar 的 `/api/webhooks/nyxid-relay`）
- `scope_id`（aevatar 侧的 registration scope）
- `nyx_channel_bot_id` / `nyx_conversation_route_id`

QA 关注点：
- 如果 NyxID 这条记录 `callback_url` 错（指向旧域名 / 失活的 pod），aevatar 永远收不到 webhook。**症状**：用户发 `/daily`，无 emoji 反应、无回复，aevatar 日志里没有 `POST /api/webhooks/nyxid-relay`，只有 K8s liveness 探活日志。这是 issue #398 描述的故障模式。
- 多副本部署：从单 pod 看不到 webhook，可能是另一个 pod 收了；测试报 bug 前先确认是否部署了多副本。

### 阶段 ② NyxID → aevatar：`/api/webhooks/nyxid-relay`

**入口文件**：`agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs:28` `HandleRelayWebhookAsync`

**HTTP 契约**：
- Method: `POST`
- Headers: `X-NyxID-Callback-Token: <JWT>`（必填，签名校验）
- Body: NyxID 规范化后的 relay payload（含 platform、message_id、reply_token、agent.api_key_id 等）

**鉴权链**：`NyxIdRelayAuthValidator.ValidateAsync(http, bodyBytes, payload, ct)`
- 校验 JWT 签名（公钥来自 `NyxIdRelayOptions.TokenPublicKeyUri`）
- 校验 audience / issuer / expiry / nonce
- 把 `Principal` 注入 `http.User`，并提取 `ScopeId`、`UserAccessToken`

**Scope 解析**：`ResolveRelayScopeIdAsync(validation.ScopeId, payload, …)`
- 优先用 JWT 里的 `scope_id`
- 缺失时用 `payload.Agent.ApiKeyId` 反查

**响应码语义**：
| 状态 | 含义 | 测试关注 |
|------|------|----------|
| `202 Accepted` (ignored) | payload 合法但被透传层标记为忽略（如非聊天事件）；handler 永远只回 `202`，不返回 `200`（[NyxIdChatEndpoints.Relay.cs:87](../../agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs)） | 不应触发任何下游逻辑 |
| `400 invalid_relay_payload` | parse 失败 | 期望 NyxID 重试或上报 |
| `400 conversation_key_missing` | activity 解析后没有 canonical conversation key | 等价上 |
| `401 Unauthorized` | JWT 校验失败 / scope 解析失败 | 不应有任何业务副作用，日志含 `Relay callback authentication failed` |

**派发**：成功后构造 `NyxRelayInboundActivity`（含 reply token、user access token、normalized `ChatActivity`），包装成 `EventEnvelope` 后直接调用 `ConversationGAgent.HandleEventAsync`（actor id 由 conversation canonical key 推出）。

### 阶段 ③ aevatar 内部业务路由

调用顺序：

1. `ChannelConversationTurnRunner` 收到 `ChatActivity`
   - 文件：`agents/Aevatar.GAgents.ChannelRuntime/ChannelConversationTurnRunner.cs`
   - `TrySendImmediateLarkReactionAsync()`（line 58 附近）→ fire-and-forget 发 ✓ emoji，不等待成功；前置条件不满足时静默跳过
   - 路由到 `TryHandleAgentBuilderAsync()`

2. `NyxRelayAgentBuilderFlow.TryResolve(evt, out decision)`
   - 文件：`agents/Aevatar.GAgents.ChannelRuntime/NyxRelayAgentBuilderFlow.cs`
   - 校验：`evt.Text` 必须以 `/` 开头；`chat_type == "p2p"`（私聊）；命令必须在已知列表里
   - 已知命令：`/daily /social-media /create-social-media /templates /agents /agent-status /run-agent /disable-agent /enable-agent /delete-agent`
   - 不在白名单 → 直接回 `BuildUnknownCommandReply()` 文案（不走 LLM）
   - 非私聊 → 回 `BuildPrivateChatRestrictionReply()`，不创建 agent、不执行 tool

3. `TryResolveDailyReport(tokens, conversationId, out decision)` (NyxRelayAgentBuilderFlow.cs:142)
   - 解析参数（顺序）：
     - `github_username`：先看 `github_username=...`，再看第一个位置参数
     - `schedule_time` / `schedule_cron` / `schedule_timezone` → `TryResolveSchedule()`
     - `repositories`
     - `run_immediately`（默认 true）
   - **保存偏好策略**：`save_github_username_preference = (githubUsername is not null)`——只有用户**显式**给了 username 才落库
   - 输出：`AgentBuilderFlowDecision.ToolCall("create_daily_report", json)`
     - JSON 结构：`{action, template, github_username, save_github_username_preference, repositories, schedule_cron, schedule_timezone, run_immediately, conversation_id}`

4. `AgentBuilderTool.ExecuteAsync(argumentsJson, ct)` 派发到 `CreateDailyReportAgentAsync()`
   - 文件：`agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTool.cs:178`
   - 关键步骤（**每步都有"失败时返回 JSON `{error: ...}`"分支，且都是测试覆盖点**）：

| 步 | 行号 | 行为 | 失败分支 |
|----|------|------|----------|
| a | 186-187 | 解析 `scope_id`（来自 `AgentToolRequestContext`） | scope 缺失走默认 |
| b | 188-195 | `ResolveDailyReportGithubUsernameAsync`：CLI 参数 → 已存偏好 → GitHub `/user` 接口反查 | 返回 `{error: "..."}` JSON |
| c | 197-204 | `AgentBuilderTemplates.TryBuildDailyReportSpec` 拼 system prompt + execution prompt | `github_username is required` |
| d | 206-212 | `ChannelScheduleCalculator.TryGetNextOccurrence`：cron + tz → 下一次执行时间（UTC） | `Invalid schedule: ...` |
| e | 214-217 | `conversation_id` 从参数或 metadata 取 | `conversation_id is required` |
| f | 219-221 | `ResolveCurrentUserIdAsync` → NyxID `GET /api/v1/users/me` | `Could not resolve current NyxID user id` |
| g | 223-225 | `BuildGitHubAuthorizationResponseAsync` 检查用户是否绑了 GitHub | `Connect GitHub in NyxID, then run /daily again.` |
| h | 227-230 | `ResolveProxyServiceIdsAsync` 把 `["api-github","api-lark-bot"]` slugs 解析成 NyxID service ids | 返回 errorJson |
| i | 232-234 | 生成 `agentId = skill-runner-{guid32}`（除非外部传 `agent_id`） | — |
| j | 236-245 | `nyxClient.CreateApiKeyAsync(...)`：在 NyxID 创建 proxy-scoped API key | error payload / parse fail |
| k | 257-262 | `PreflightGitHubProxyAsync(apiKey, slug)`：用新 key 调一次 GitHub `/rate_limit`；401/403 时立即 `BestEffortRevokeApiKeyAsync` 撤销避免孤儿 key | 返回 preflight error JSON |
| l | 264-265 | 取 / 创建 `SkillRunnerGAgent` | — |
| m | 267 | 记录 `versionBefore = queryPort.GetStateVersionAsync(agentId)` | — |
| n | 272 | `EnsureUserAgentCatalogProjectionAsync`：投影预热（订阅 scope）必须在写入 catalog 之前 | — |
| o | 274 | `ResolveDeliveryTarget(conversationId, agentId)`：算出 `lark_receive_id` 主备对 | — |
| p | 275-302 | 构造 `InitializeSkillRunnerCommand`，直接调用 `actor.HandleEventAsync(...)` | — |
| q | 304-308 | 若 `run_immediately=true`，再直接调用 `actor.HandleEventAsync(TriggerSkillRunnerExecutionCommand{Reason="create_agent"})` | — |
| r | 310-317 | `WaitForCreatedAgentAsync` 轮询投影；`maxAttempts = run_immediately ? 20 : 10` | 返回 `status: "accepted"`（带 note：投影未确认） |
| s | 319-324 | `SaveGithubUsernamePreferenceIfRequestedAsync`：写 `UserConfigGAgent` | — |
| t | 326-339 | 返回成功 JSON：`{status, agent_id, agent_type, template, github_username, github_username_preference_saved, run_immediately_requested, next_scheduled_run, conversation_id, api_key_id, note}` | — |

5. `NyxRelayAgentBuilderFlow.FormatToolResult(decision, toolResultJson)`
   - 把 step (t) 的 JSON 渲染成 Lark 可接受的 `MessageContent`
   - `create_daily_report` 走 `FormatCreateDailyReportResult()` → `AgentBuilderCardContent.FormatDailyReportToolReply()`，输出文字或卡片

### 阶段 ④ aevatar → NyxID（API key + 预检）

**创建 API key**：`POST {NyxID}/api/v1/api-keys`
- Header: `Authorization: Bearer {user_access_token_from_relay_jwt}`
- Body：`BuildCreateApiKeyPayload(agentId, requiredServiceIds)`
  - `name: "aevatar-agent-{agentId}"`
  - `scopes: "proxy"`
  - `platform: "generic"`
  - `allowed_service_ids: ["<UserService.id-of-api-github>", "<UserService.id-of-api-lark-bot>"]`
  - `allow_all_services: false`
- 返回解析：优先读顶层 `{id, full_key}`，也兼容嵌套 `api_key.{id, full_key/token/value}`；工具最终把 `id` 作为 `api_key_id` 返回给调用方。
- **测试关注**：
  - 失败需检查 `IsErrorPayload()` 并直接返回原文 → 用户能看到结构化 error
  - 重试场景：每次 `/daily` 失败前若已分到 key，preflight 再失败时必须撤销，不允许产生孤儿 key（issue 历史 PR #418）

**Preflight**：`PreflightGitHubProxyAsync(nyxClient, apiKey, slug, ct)`
- 用刚拿到的 proxy API key 调 NyxID `GET /api/v1/proxy/s/api-github/rate_limit`
- 只有 401/403 被视为“新 key 无法访问 GitHub”并 fail-fast；rate limit、5xx、非 JSON 等不被这里判定为创建失败。
- 历史 bug：因 GitHub 强制 User-Agent header 缺失而 403，导致首次 `/daily` 永远失败（已修：`NyxIdApiClient.ProxyRequestAsync` 默认注入 `User-Agent: aevatar-agent-builder`）。

### 阶段 ⑤ SkillRunner 执行 → NyxID → GitHub

**触发**：
- 立即执行：阶段 ③.q 直接调用 `SkillRunnerGAgent.HandleEventAsync(TriggerSkillRunnerExecutionCommand{Reason="create_agent"})`
- 定时执行：`ChannelScheduleRunner.ScheduleNextRunAsync` → Orleans 持久化回调 → fire `TriggerSkillRunnerExecutionCommand{Reason="schedule"}`
- 手动：`/run-agent <agent_id>` → 同样的 trigger，`Reason="manual"`
- 重试：失败时 `ScheduleRetryAsync` → 30s 后再 fire `Reason="retry", RetryAttempt=N`

**Handler**：`SkillRunnerGAgent.HandleTriggerAsync` (SkillRunnerGAgent.cs:130)
```
if (!State.Enabled) return;                         // 禁用即跳过
try {
    var output = await ExecuteSkillAsync(now, ...);
    await SendOutputAsync(output, ...);             // 投递到 Lark
    PersistDomainEventAsync(SkillRunnerExecutionCompletedEvent { Output = output });
    CancelRetryLeaseAsync();
    Scheduler.ScheduleNextRunAsync(now);
    UpdateRegistryExecutionAsync(StatusRunning, lastRunAt=now, nextRunAt, errorCount=0, lastError="");
}
catch (Exception ex) {
    if (RetryAttempt < MaxRetryAttempts /*=1*/)
        return ScheduleRetryAsync(RetryAttempt+1);  // 30s 后再试一次
    PersistDomainEventAsync(SkillRunnerExecutionFailedEvent { Error = ex.Message });
    TrySendFailureAsync(ex.Message);
    Scheduler.ScheduleNextRunAsync(now);
    UpdateRegistryExecutionAsync(StatusError, ...);
}
```

**ExecuteSkillAsync** 内部：
- 用 `State.SkillContent`（system prompt）+ `State.ExecutionPrompt`（"Run the daily report for GitHub user `{u}` covering the last 24 hours."）启动 LLM 会话
- 工具：`nyxid_proxy`（来自 `Aevatar.AI.ToolProviders.NyxId`）
  - 输入：`slug`、`method`、`path`、`body`、`headers`
  - 调用：`NyxIdApiClient.ProxyRequestAsync(effectiveToken=State.OutboundConfig.NyxApiKey, slug, path, ...)`
  - **重要**：proxy API key（不是用户 OAuth token）作为 effective token；NyxID 服务侧根据这把 key 注入对应 service 的真实凭据（GitHub OAuth token）
- LLM 受 prompt 引导调三类查询：
  ```
  GET /search/commits?q=author:{username}+author-date:>={iso_date}
  GET /search/issues?q=author:{username}+updated:>={iso_date}
  GET /search/issues?q=commenter:{username}+updated:>={iso_date}
  ```
- LLM 决定何时停（受 `MaxToolRounds=20` 限制），最终输出按 prompt 要求格式化：
  ```
  <Title>
  - bullet 1
  - bullet 2
  ...
  No blockers. (or one-line blocker)
  ```

### 阶段 ⑥/⑦ 出站投递回 Lark

**SendOutputAsync** → `NyxIdApiClient.ProxyRequestAsync`
- Method: `POST {NyxID}/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type={primary_type}`
- Body: Lark `im/v1/messages` 标准 payload，`receive_id` = `State.OutboundConfig.LarkReceiveId`，`msg_type=text`，content `{text:"..."}`
- Auth: `Authorization: Bearer {State.OutboundConfig.NyxApiKey}`

**Fallback 逻辑**：
- 只有主投递返回 Lark 错误 `230002`（bot 不在该聊天）时，才尝试用 `LarkReceiveIdFallback` + `LarkReceiveIdTypeFallback` 再投递一次
- 主备对在创建时由 `ResolveDeliveryTarget(conversationId, agentId)` 决定：
  - 主：通常 `chat_id`（`oc_*`）
  - 备：通常 `union_id`（`on_*`，跨 app 也能找到用户）
- `99992361`（open_id cross app）和 `99992364`（union_id cross tenant）不会触发 fallback，会直接进入失败路径并给 `/agent-status` 留下带重建提示的 `last_error`。
- **已知短板**（issue #423 § C）：失败通知 `TrySendFailureAsync` 走的也是同一条 `s/api-lark-bot` proxy，主链路 99992361/99992364 时通知通常也会丢。

---

## 4. 数据契约（关键 proto 字段）

文件：`agents/Aevatar.GAgents.ChannelRuntime/channel_runtime_messages.proto`

### `ChannelInboundEvent`（入站规范化消息）
- `text`、`sender_id`、`sender_name`、`conversation_id`、`chat_type`、`platform`、`registration_token`、`nyx_provider_slug`、`registration_scope_id`
- **重点**：`sender_id` 实质是 Lark `open_id`（`ou_*`），**只在单个 Lark App 内唯一**——同一个真人在不同 Lark app 下会有不同 `open_id`，跨 app 不能直接拿来对账（这是 PR #409 引入 `union_id`/`on_*` 入站和 `chat_id`-first delivery fallback 的原因，详见 [LarkConversationTargets.cs:69-70](../../agents/Aevatar.GAgents.ChannelRuntime/LarkConversationTargets.cs)）。`registration_scope_id` 是 bot 维度。下面 issue #436/#437 的 cross-user leak bug 就源自只用 `registration_scope_id` 当 user-config key，丢了 `sender_id`。

### `SkillRunnerOutboundConfig`
```proto
string conversation_id = 1;
string nyx_provider_slug = 2;
string nyx_api_key = 3;            // proxy-scoped key
string owner_nyx_user_id = 4;
string api_key_id = 5;
string platform = 6;
string lark_receive_id = 7;        // 主投递目标
string lark_receive_id_type = 8;
string lark_receive_id_fallback = 9;
string lark_receive_id_type_fallback = 10;
```

### `SkillRunnerState`
- `skill_name="daily_report"`、`template_name="daily_report"`
- `skill_content` / `execution_prompt`：阶段 ③ 拼好后冻在 actor state，**不会再变**——QA 注意：用户改 GitHub 绑定后，已存活的 agent 不会自动重指向；这是 issue #436 acceptance criteria 第 5 条要保留的语义
- `schedule_cron` / `schedule_timezone`、`enabled`、`scope_id`
- `provider_name` / `model` / `temperature` / `max_tokens` / `max_tool_rounds=20` / `max_history_messages`
- 运行态：`last_run_at`、`next_run_at`、`error_count`、`last_error`、`last_output`

### `UserAgentCatalogEntry`（well-known 注册表条目）
- 关键字段：`agent_id`、`agent_type="skill_runner"`、`template_name="daily_report"`、`platform="lark"`、`conversation_id`、`scope_id`、`status`、`last_run_at`、`next_run_at`、`error_count`、`last_error`、`lark_receive_id*`
- `nyx_api_key` / `api_key_id`：actor state 内的 catalog entry 保留这两个字段；公开 `UserAgentCatalogDocument` 不再暴露 `nyx_api_key`，运行时出站读取单独的 `UserAgentCatalogNyxCredentialDocument`。

### 命令 / 事件
- 命令：`InitializeSkillRunnerCommand`、`TriggerSkillRunnerExecutionCommand{Reason, RetryAttempt}`、`DisableSkillRunnerCommand`、`EnableSkillRunnerCommand`、`UserAgentCatalogUpsertCommand`、`UserAgentCatalogExecutionUpdateCommand`、`UserAgentCatalogTombstoneCommand`
- 事件：`SkillRunnerInitializedEvent`、`SkillRunnerNextRunScheduledEvent`、`SkillRunnerExecutionCompletedEvent`、`SkillRunnerExecutionFailedEvent`、`SkillRunnerDisabledEvent`、`SkillRunnerEnabledEvent`、`UserAgentCatalogUpsertedEvent`、`UserAgentCatalogExecutionUpdatedEvent`、`UserAgentCatalogTombstonedEvent`

---

## 5. 鉴权 / 凭据模型

存在三类不同的凭据，**测试时不要混用**：

| 凭据 | 谁颁发 | 用在哪 | TTL | 失效行为 |
|------|--------|--------|------|----------|
| `X-NyxID-Callback-Token` (relay JWT) | NyxID 用 relay 私钥签 | 阶段 ② webhook 鉴权 | 短期（payload 内含 `exp`） | 401 Unauthorized |
| `user_access_token`（NyxID OAuth 用户 token） | NyxID 在 relay JWT 里捎带（`validation.UserAccessToken`） | 阶段 ④ 创建 API key、查 `/users/me`、查 GitHub provider 状态 | 用户 NyxID 会话级 | 401，提示用户重新登录 / 重连 |
| `proxy api key`（agent-scoped） | aevatar 在阶段 ④.j 让 NyxID 颁发 | 阶段 ⑤ `nyxid_proxy` 工具 + 阶段 ⑦ Lark 投递 | 长期（agent 删除时撤销） | 401 / 403，agent 进入 error 状态 |

**关键不变量**：proxy api key **不会**被 LLM 直接看见；它放在 `SkillRunnerOutboundConfig.NyxApiKey`，`nyxid_proxy` 工具实现从 `AgentToolRequestContext` 读取并作为 effective token 传给 NyxID。LLM 只看到 NyxID 反代后的 GitHub JSON。

---

## 6. 调度 & 重试

**默认值**：`agents/Aevatar.GAgents.ChannelRuntime/SkillRunnerDefaults.cs`
- `AgentType = "skill_runner"`
- `ActorIdPrefix = "skill-runner"`，actor id `skill-runner-{guid:N}`（32 hex）
- `DefaultMaxToolRounds = 20`
- `MaxRetryAttempts = 1`（即同一次执行最多重试 1 次，总 2 次）
- `RetryBackoff = 30s`
- `TriggerCallbackId = "skill-runner-next-fire"`
- `RetryCallbackId = "skill-runner-retry"`

**Cron 解析**：`ChannelScheduleCalculator.TryGetNextOccurrence(cron, tz, now, out nextUtc, out err)`
- 接受标准 5 段 cron
- `schedule_time=HH:MM` 是糖：会被 `TryResolveSchedule` 转成 `M H * * *`，分钟在前、小时在后，例如 `14:30` → `30 14 * * *`
- 时区合法性以 .NET `TimeZoneInfo` 为准（可用 IANA `Asia/Singapore` 或 Windows id）

**Status 字符串**（projector / `/agent-status` 都用）：`"running"`、`"disabled"`、`"error"`

---

## 7. 状态 / Projection / 查询

**事实源**：`SkillRunnerGAgent` actor state（每个 agent 一个 actor）+ `UserAgentCatalogGAgent`（well-known，全局唯一注册表 actor）

**Projection**：`UserAgentCatalogProjector` 消费 `UserAgentCatalogUpsertedEvent` / `UserAgentCatalogExecutionUpdatedEvent` / `UserAgentCatalogTombstonedEvent` → 物化到 `UserAgentCatalogDocument`

**查询端口**：`IUserAgentCatalogQueryPort`
- `GetStateVersionAsync(agentId)`：阶段 ③.r 轮询用
- `ListAsync(ownerId/scopeId)`：`/agents` 命令的数据源
- 单条查询：`/agent-status <id>`

**关键不变量 / 测试关注**：
- `UpsertRegistryAsync` 在 `HandleInitializeAsync` 末尾发；之后立即可能被 `HandleTriggerAsync` 的 `UpdateRegistryExecutionAsync` 跟上 → 见 issue #440 怀疑的 race（init 端 upsert 和 trigger 端 execution-update 排序）。
- `UserAgentCatalogGAgent.HandleExecutionUpdateAsync` 有 early-return guard：`State.Entries` 里没找到 agent_id 就 `LogWarning("Cannot update execution state for missing user agent catalog entry")` 并丢弃。如果丢的是首次执行的 update，`/agent-status` 永远看不到 `Last run`/`Next run`。

---

## 8. Outbound 投递行为

**主路径**：`POST {NyxID}/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type={primary_type}` Body: `{receive_id, msg_type:"text", content:"{\"text\":\"...\"}"}`

**Fallback**：主投返回 Lark `230002 bot_not_in_chat` 时，重试用 `lark_receive_id_fallback` + `lark_receive_id_type_fallback`。已观察到但不重试的身份错误：
- `99992361`：open_id cross app
- `99992364`：union_id cross tenant

**已知边界**（已记入 issues，QA 复测时要能判别）：
- `lark_receive_id*` 在 agent 创建时被冻结。如果用户从 chat A 创建 agent，后来 chat A 解散或机器人被踢，agent 投递就永远失败 → 必须 `/delete-agent` + 重建。
- 如果创建侧的 inbound bot 和 outbound bot 是不同 Lark App（同租户跨 app 部署），`chat_id` 可能在 outbound 侧不可用，需要 fallback 到 `union_id`。
- Lark 文本消息体上限约 30KB，富报告可能超限（issue #423 §C）。

---

## 9. 错误 / 失败模式分类

按"用户能不能看到"维度分：

### 9.1 用户看得到（直接回 Lark 的 JSON `{error:"..."}` 或文案）
- `No NyxID access token available. User must be authenticated.` —— NyxID 会话失效
- `Connect GitHub in NyxID, then run /daily again.` —— 没绑 GitHub provider
- `github_username is required for template=daily_report`
- `schedule_cron is required for create_agent`
- `Invalid schedule: {cronError}`
- `conversation_id is required when no current channel conversation is available`
- `Could not resolve current NyxID user id`
- `Unsupported template '{x}'.`
- 创建 API key / 解析 service id 失败时 NyxID 原始 error JSON 透传

### 9.2 用户看到，但语义可能错（关键 bug 区！）
- **Issue #439（silent failure）**：proxy 返回 4xx/5xx/7xxx 时 `nyxid_proxy` 工具把错误 JSON 原样返回，LLM 误判为"无活动"，输出空的 daily 报告 + `Status: running, error_count: 0`。**测试关键**：要能区分"GitHub 真无活动"和"工具失败被吞掉"。
- **Issue #436/#437（cross-user leak）**：在同一个机器人下，多个 Lark 用户各自的 GitHub username 互相覆盖（last writer wins）。**测试关键**：两个不同 sender_id 在同一 registration_scope_id 下，分别 `/daily a` 和 `/daily b`，第三步 user A 再 `/daily` 必须看到自己的 username `a`，不应是 `b`。

### 9.3 用户看不到（更隐蔽，需要查日志或 `/agent-status` 才能发现）
- **Issue #440**：首次执行成功后 `/agent-status` 的 `Last run` / `Next run` 一直 `n/a`。
- **Issue #398**：webhook 完全没到 aevatar——aevatar 日志里只有 K8s liveness 探活，无 `POST /api/webhooks/nyxid-relay`。
- 出站失败被 `TrySendFailureAsync` 通知，但通知本身走同一条 proxy → 通知也丢（issue #423 §C）。

### 9.4 重试相关
- 每次执行 fail，`MaxRetryAttempts=1`，30 秒后自动重试 1 次
- 两次都失败：`SkillRunnerExecutionFailedEvent` + `TrySendFailureAsync` + `UpdateRegistryExecutionAsync(StatusError, ...)` + 仍调度下一次定时

---

## 10. 命令参数与文案矩阵

完整解析逻辑见 `NyxRelayAgentBuilderFlow.TryResolveDailyReport()`：

| 输入 | github_username 来源 | save_pref | 副作用 |
|------|----------------------|-----------|--------|
| `/daily` | 已存偏好 → fallback：NyxID GitHub `/user` | false | 立即建 agent |
| `/daily alice` | `"alice"` | true | 立即建 agent + 落库 alice |
| `/daily github_username=alice` | `"alice"` | true | 同上 |
| `/daily alice schedule_time=14:30` | `"alice"` | true | cron `30 14 * * *` |
| `/daily alice schedule_timezone=Asia/Shanghai` | `"alice"` | true | tz 解析后给 ChannelScheduleCalculator |
| `/daily alice repositories=a/b,c/d` | `"alice"` | true | execution prompt 加 `Prioritize repositories: a/b, c/d.` |
| `/daily alice run_immediately=false` | `"alice"` | true | 不立即跑，只调度 |
| 群聊里发 `/daily ...` | — | — | 直接回 `BuildPrivateChatRestrictionReply()`，**不创建 agent** |
| `/daily?` 等未知形态 | — | — | `BuildUnknownCommandReply()` |

**用法提示文案**：`"/daily [github_username] schedule_time=09:00 repositories=owner/repo"`

---

## 11. 已知 bug 一览（与 milestone "Day One Enhancement" 对齐）

| Issue | 严重度 | 标题简述 | 影响层 | QA 复现要点 |
|-------|--------|---------|--------|-------------|
| #437 | 高（数据隔离） | `/daily` binding causes cross-user data leakage（用户视角） | UserConfigGAgent scope key | 同 bot 两个 Lark user 各自 `/daily X` / `/daily Y`，A 再 `/daily` 应得 X，实得 Y |
| #436 | 高（同上 #437 的工程分析） | GitHub username binding shared across all Lark users（last writer wins） | 同上 | 同上 |
| #439 | 高（语义错） | SkillRunner masks GitHub tool failures as silent "no activity" success | prompt + nyxid_proxy 工具 + runner 的"非空即成功"路径 | 强制 GitHub 接口返回 4xx/5xx，验证报告必须显式标错而不是出 `No X surfaced` |
| #440 | 中（运维可见性） | `/agent-status` 首次执行不刷新 `Last run`/`Next run` | UserAgentCatalogGAgent.HandleExecutionUpdateAsync early-return guard | `/daily X`（run_immediately）→ 30s 后 `/agent-status <id>` 看 `Last run` 应非 n/a |
| #423 | 中（增强 + 失败通知短板） | richer report content + progressive delivery；副带失败通知通道脆弱 | prompt + SendOutputAsync + TrySendFailureAsync | 当前一次性投递；除 ✓ reaction 外缺少进度反馈，创建确认也可能延迟到首次执行尝试之后；构造投递失败场景看通知是否能到 |
| #398 | 高（链路断） | Lark relay callbacks never reach aevatar | NyxID 侧 callback_url 配置 / 多副本 ingress / Lark 订阅状态 | 用户发消息无任何反应，aevatar 日志只有 K8s liveness |

每条 bug 在对应 issue 描述里都有完整 acceptance criteria，QA 用例可直接对齐。

---

## 12. 测试矩阵（按测试类型组织）

### 12.1 单元测试 — 命令解析层（已有底子）

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/NyxRelayAgentBuilderFlowTests.cs`

应覆盖：
- ✅ `/daily` 不带任何参数 → tool args `github_username=null`、`save_github_username_preference=false`
- ✅ `/daily alice` → `github_username="alice"`、`save_pref=true`
- ✅ `/daily github_username=alice`（命名形式）等价于上面
- ✅ `/daily alice schedule_time=14:30` → `schedule_cron="30 14 * * *"`
- ✅ `/daily alice schedule_timezone=Asia/Shanghai` → 透传 tz 字符串
- ✅ `/daily alice repositories=a/b,c/d` → 透传 `"a/b,c/d"`，由 `TryBuildDailyReportSpec` 拆
- ✅ `/daily alice run_immediately=false` → `run_immediately=false`
- ✅ 非私聊（`chat_type != "p2p"`）→ `BuildPrivateChatRestrictionReply`，**不**产生 ToolCall
- ✅ 未知 slash 命令 `/foo` → `BuildUnknownCommandReply`
- ❌ 边界：`/daily schedule_time=25:99` → `Invalid schedule` 错误文案
- ❌ 边界：`/daily schedule_timezone=Mars/Olympus` → 同上
- ❌ 边界：`/daily ""` 空位置参数

### 12.2 单元测试 — Agent 创建层

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/AgentBuilderToolTests.cs`

应覆盖：
- API key 创建路径：成功 → 进 preflight；失败 → 直接返回原 error JSON
- Preflight 失败 → 必须调 `BestEffortRevokeApiKeyAsync` 撤销新 key（issue 历史 PR #418 已加测）
- `BuildGitHubAuthorizationResponseAsync` 返回非空 → 直接返回，不创建 actor
- `ResolveCurrentUserIdAsync` 返回空 → `Could not resolve current NyxID user id`
- `WaitForCreatedAgentAsync` 超时 → 返回 `status:"accepted"` 带 note，**不应**返回 `error`
- `save_github_username_preference=true` 时落库；`false` 时不落库
- `agent_id` 显式指定 → 不生成新 id；不指定 → `skill-runner-{guid32}` 形式
- **#436 应加测**：两个不同 `sender_id` 在同一 `scope_id` 下分别保存 username，互不覆盖（fix 后）
- **#439 应加测**：mock `nyxid_proxy` 返回 error JSON 时，LLM 输出含错误标记 → runner 必须 `Failed`，不能 `Completed`

### 12.3 单元测试 — SkillRunner actor

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/SkillRunnerGAgentTests.cs`

应覆盖：
- `HandleInitializeAsync`：`SkillContent` 为空 → 直接返回不持久化（仅 LogWarning）
- `HandleInitializeAsync` 正常 → 持久化 `SkillRunnerInitializedEvent` + `Scheduler.ScheduleNextRunAsync` + `UpsertRegistryAsync`
- `HandleTriggerAsync`：`State.Enabled=false` → 跳过
- `HandleTriggerAsync` 成功 → `Completed` 事件 + 注册表 update + retry lease 取消 + 下次调度
- `HandleTriggerAsync` 失败：`RetryAttempt < 1` → `ScheduleRetryAsync(2)` 不发 `Failed`
- `HandleTriggerAsync` 失败：`RetryAttempt >= 1` → 持久化 `Failed` + `TrySendFailureAsync` + 下次调度（仍按 cron）+ status=error
- `Disable` → `Enabled=false`，下次 trigger 跳过
- `Enable` → `Enabled=true`，恢复执行
- 状态转换：每个事件类型 → `TransitionState` 应正确合并

### 12.4 单元测试 — 注册表

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/UserAgentCatalogGAgentTests.cs`、`UserAgentCatalogProjectorTests.cs`

应覆盖：
- `Upsert` → entry 进 state；同 agent 再次 `Upsert` → 覆盖且不重复
- `ExecutionUpdate` 找到 entry → 更新 `last_run_at` / `next_run_at` / `status` / `error_count` / `last_error`
- **#440 应加测**：`Upsert` 与 `ExecutionUpdate` 同一 activation 内连续派发，二者最终都体现在 state 上（不被 early-return guard 误丢）
- `Tombstone` → entry 标 `tombstoned=true`，`/agents` 列表里隐藏
- Projector：每种事件 → readmodel 对应字段被覆盖（projector 是单调覆盖语义，不累加）

### 12.5 单元测试 — Webhook 鉴权与 ingress

文件：`test/Aevatar.AI.Tests/NyxIdChatEndpointsCoverageTests.cs`、`NyxIdRelayTransportTests.cs`、`NyxIdRelayScopeResolverTests.cs`

应覆盖：
- 缺 `X-NyxID-Callback-Token` → 401
- JWT 签名错 / 过期 → 401，日志含 `Relay callback authentication failed`
- payload parse 失败 → 400 `invalid_relay_payload`
- payload `Ignored=true` → 202 `status:"ignored"`，不触发下游
- `conversation_key_missing` → 400
- 成功路径：`activity` 经过 normalize → 写入 conversation actor inbox

### 12.6 集成测试 — `/daily` 端到端（aevatar 内部）

需 mock 的边界：
- NyxID HTTP 客户端（`NyxIdApiClient`）：`/api-keys`、`/users/me`、`/proxy/s/api-github/...`、`/proxy/s/api-lark-bot/...` 全 mock
- LLM provider：可走 `Aevatar.AI.Infrastructure.Local` 的 fake provider，预设工具调用序列
- Orleans / actor runtime：单元测试用 in-proc `IActorRuntime`

用例：
1. 黄金路径：`/daily alice run_immediately=true` → 期望 ✓ emoji + `/im/v1/messages` 调用 + agent 在 catalog 出现 + state `running`
2. GitHub 未绑：`BuildGitHubAuthorizationResponseAsync` mock 返回非空 → 期望直接回错误文案、不创建 agent、不创建 key
3. Preflight 失败：mock NyxID `/proxy/s/api-github/rate_limit` 返回 403 → 期望 `BestEffortRevokeApiKeyAsync` 被调，**0 actor 创建**
4. `nyxid_proxy` 全失败（#439）：mock 三次 search 都返回 error JSON → 期望 runner 持久化 `Failed`（不是 `Completed`），`/agent-status` 显示 `error_count > 0`
5. `nyxid_proxy` 部分失败（#439）：1 成功 + 2 失败 → 期望最终输出含失败 endpoint 列表（修复后才能过）
6. 投递主失败 fallback 成功：mock 主 `/im/v1/messages` 返回 230002 → 验证用 fallback receive_id 重试 → 成功
7. 投递主失败 fallback 也失败：验证 `TrySendFailureAsync` 被调，状态 `error`
8. **#436 cross-user leak**：模拟两个 `sender_id` (A、B) 在同一 `registration_scope_id` 下：
   - A 发 `/daily alice` → preference 应仅落到 A 的 user-config 子键（修复后）
   - B 发 `/daily bob` → 仅落 B
   - A 再发 `/daily`（无 username）→ 拿到 `alice`，不是 `bob`
9. **#440 first-run race**：`/daily alice run_immediately=true` → 等 `WaitForCreatedAgentAsync` 完成 → 1 秒后查 `IUserAgentCatalogQueryPort.GetAsync(agentId)`，期望 `last_run_at` / `next_run_at` 已填
10. cron 排程：`schedule_time=14:30 schedule_timezone=Asia/Shanghai` → 验证 `next_scheduled_run` UTC 时间正确（按当前 mock 时钟换算）

**`social_media` 模板**（同一条 `/daily`-类入口，但走 `WorkflowAgent` 而非 `SkillRunnerGAgent`，由 `AgentBuilderTool.CreateSocialMediaAgentAsync` 处理；`AgentBuilderTemplates.TryBuildSocialMediaSpec` 拼模板）：
11. 黄金路径：`/social-media topic="Q4 launch" audience=devs schedule_time=10:00` → 期望 workflow agent 在 catalog 出现，schedule_cron 与 daily 一致计算
12. 缺 topic：`/social-media`（无参数）→ `BuildSocialMediaHelpText()` 文案，**不创建** workflow
13. 缺 scope_id：mock `AgentToolRequestContext.TryGet("scope_id")` 返空 → 期望 `{error:"scope_id is required for the social_media template"}`
14. workflow 命令端口缺失：DI 不注册 `IScopeWorkflowCommandPort` → 期望 `{error:"Scope workflow command port is not registered."}`
15. 共享路径回归：daily 与 social-media 公用 `ChannelScheduleCalculator`、cron 解析、`WaitForCreatedAgentAsync`、`api-lark-bot` 出站——任意 daily 用例（1/3/6/9/10）改成 social-media 应仍通过

### 12.7 契约测试 — NyxID 边界

需要：本机或 staging NyxID + GitHub 测试账号

用例：
1. NyxID `/api/v1/api-keys` 接受 `{name, scopes:"proxy", platform:"generic", allowed_service_ids:[…], allow_all_services:false}`，返回可解析的 `{id, full_key}` 或嵌套 `api_key` 形态
2. proxy `s/api-github/rate_limit` 用刚拿的 key 能 200（preflight）
3. proxy `s/api-github/user` 用用户 access token 能 200，且响应里有 `login` 字段（无显式 username 时的 fallback）
4. proxy `s/api-github/search/commits?q=author:...` 能 200 且返回正常 GitHub 结构
5. proxy `s/api-lark-bot/open-apis/im/v1/messages?receive_id_type=chat_id` 能投到指定 chat
6. 撤销 API key：`DELETE /api/v1/api-keys/{id}` → 之后用该 key 调 (2)(4)(5) 必 401
7. JWKS endpoint：`{TokenPublicKeyUri}` 返回有效 JWK set，覆盖当前签发 callback JWT 的 kid
8. relay callback JWT 中应能取到 `scope_id`、`user_access_token`

### 12.8 端到端冒烟（环境：staging Lark + staging NyxID + 开发分支 aevatar）

**前置**：
- 一个 Lark 测试租户、一个绑定到 staging NyxID 的机器人
- 测试用户已在 NyxID 完成 GitHub OAuth 授权
- staging aevatar 已部署、`/api/webhooks/nyxid-relay` 可达

**用例**：
| ID | 步骤 | 期望 |
|----|------|------|
| E1 | 私聊发 `/daily eanzhao` | 尽快出现 ✓ emoji（best-effort，不作为成功条件）；≤90s 收到含至少 1 条 bullet 的报告（用 GitHub 上确实有活动的账号）；创建确认可能在报告之后到达 |
| E2 | 私聊发 `/daily inactive_user_no_commits_24h` | 报告显式说"无活动"（不要伪造内容） |
| E3 | 私聊发 `/daily` 多次（已落 preference） | 第二次起无需 username，应直接用历史绑定 |
| E4 | 群聊发 `/daily eanzhao` | 机器人回 `BuildPrivateChatRestrictionReply` 文案，不创建 agent |
| E5 | `/agent-status <id>` （创建后立即 + 30s 后 + 1 分钟后） | 30s 内 `Last run` 应已填（#440 修复后） |
| E6 | `/agents` | 列表里能看到刚创建的 agent，`status:"running"` |
| E7 | `/run-agent <id>` | 立即跑一次，新报告到聊天；状态更新 |
| E8 | `/disable-agent <id>` 后等过 cron 时刻 | 不应执行；`/agent-status` `Status: disabled` |
| E9 | `/enable-agent <id>` 后等 cron 时刻 | 应执行 |
| E10 | `/delete-agent <id> confirm` | 注册表里消失；NyxID 上 api key 撤销 |
| E11 | 两台测试机分别用不同 Lark 账号在同 bot 下 `/daily a` / `/daily b`，A 再 `/daily` | A 必须拿回 `a`（#436/#437 修复后） |
| E12 | 触发 GitHub 接口失败（吊销 NyxID 上的 GitHub OAuth 后立即跑 `/run-agent`） | 报告应显式说"GitHub 工具失败 + 状态码"（#439 修复后），`/agent-status` `error_count` 增加 |
| E13 | 跨 app 部署：从 inbound bot 私聊发起，outbound bot 不在该 chat | 主投返回 230002 → fallback 用 union_id 投到用户单聊；如全失败应有失败通知（#423 §C） |
| E14 | 关掉 NyxID 上 callback_url 指向，发 `/daily` | aevatar 收不到 webhook（验日志），用户看不到任何回复（#398 复现） |

### 12.9 性能 / 容量（建议覆盖）

- 同一 bot 下并发 50 个用户同时发 `/daily`：当前实现应记录 webhook 返回耗时；若目标是 ≤3s ack，则这个用例用于暴露“agent 创建/首次执行未脱钩”的性能缺口
- 单 agent 多次手动 `/run-agent`：调度幂等，不出现并发执行同一 agent（actor 串行保证）
- LLM 工具循环上限：构造一个让 LLM 不断调 `nyxid_proxy` 的 prompt，验证 `MaxToolRounds=20` 起效
- Lark 文本上限：构造让 LLM 输出 >30KB 的内容，看是否被截断 / 报错（#423 §C 提到的 length cap 还没实现，可能是问题）

### 12.10 配置 / 部署回归

| 配置项 | 影响 | 测试 |
|--------|------|------|
| `NyxIdRelayOptions.TokenPublicKeyUri` | webhook 鉴权 | 改错→所有入站 401 |
| `NyxIdToolOptions.BaseUrl` | NyxID 调用 | 改错→所有 NyxID 调用失败 |
| `LarkToolOptions.ProviderSlug`（默认 `api-lark-bot`） | 出站 / API key services | 改错→投递 / 创建 key 错 |
| K8s 副本数 | 多副本 webhook 路由 | 多副本下复测 E1 / E14 |
| 时区（容器默认 UTC） | cron 解析 | tz 不为 UTC 时仍能正确换算 |

---

## 13. 测试桩 / 数据准备 / 环境约定

**aevatar staging**：
- Endpoint：见 `aevatar-console-backend-api.aevatar.ai`（生产；staging URL 走内部）
- Webhook 路径：`POST {host}/api/webhooks/nyxid-relay`
- Health：`GET /api/health`

**Lark 测试机器人**：
- 必须开启"接收消息"事件订阅
- 必须有 `im:message`、`im:message:send_as_bot` 等基础权限
- 必须把机器人加进测试聊天

**NyxID 准备**：
- 测试用户 OAuth 三个 provider：NyxID 自身、Lark、GitHub
- 一条 `channel_bot` 记录 `callback_url` 指向 staging aevatar webhook
- staging NyxID 的 JWKS 必须可被 aevatar 公网拉取

**GitHub 准备**：
- 用一个**有近 24h 活动**的账号（commits + PRs + issue comments）做 happy path
- 用一个**确实空闲**的账号做"真无活动"场景
- 用一个**已撤销 OAuth grant** 的账号做 #439 场景

**LLM provider**：
- 默认 `SkillRunnerDefaults.DefaultProviderName`（生产用 NyxID 路由；测试可 stub）

---

## 14. 观测点 / 日志关键字

aevatar 侧（grep 关键字）：
- `Relay callback authentication failed` — webhook 鉴权失败
- `Cannot update execution state for missing user agent catalog entry` — #440 当前症状
- `Skill runner {ActorId} initialization ignored because skill_content is empty`
- `Skill runner {ActorId} ignored trigger because it is disabled`
- `Skill runner {ActorId} execution failed (attempt={Attempt})`
- `Skill runner {ActorId} scheduled retry attempt {Attempt} in {Backoff}`
- `[nyxid_proxy] Approval response: code={Code} requestId={RequestId}` — NyxID approval 流转

NyxID 侧（QA 联调时让后端协助拿）：
- relay callback 出站日志：能否看到 `POST {aevatar_callback_url}` 的请求记录
- proxy 日志：`s/api-github` / `s/api-lark-bot` 转发的状态码

Lark 开发者后台：
- 事件订阅状态（是否被自动禁用）
- 历史投递成功率

---

## 15. 注意事项 / 测试时容易踩的坑

1. **加 emoji 反应**和**daily 报告**不是同一个 HTTP 请求；emoji 是 fire-and-forget 的 best-effort 反应，报告走 `SkillRunnerGAgent.SendOutputAsync` 的 proactive proxy 投递。两者可以独立失败。
2. **首次 `/daily` 当前通常会产生两条用户可见消息**：一条是 SkillRunner 实际执行后的报告，另一条是 `AgentBuilderTool` 返回的"agent 已注册/正在跑"短文案。由于 `run_immediately=true` 时首次执行在创建 tool 调用内被 await，报告可能先于创建确认到达。
3. **`run_immediately=true` 默认开启**，所以 `/daily alice` 会立刻执行一次。如果不想立刻跑，必须显式 `run_immediately=false`。
4. agent 创建后，**改 NyxID 上的 GitHub username 绑定不会回流**到已存在 agent 的 `OutboundConfig` / 报告 prompt。需要 `/delete-agent <id>` 后重建。
5. `MaxRetryAttempts=1` 意味着失败最多自动再试**一次**（30 秒后）；不是无限重试。两次都失败才会进 `Failed` 状态。
6. cron 默认时区是 UTC，不是用户所在时区。`/daily alice` 在中国用户视角看是"每天早上 5 点收报告"（09:00 UTC）。要写 `schedule_timezone=Asia/Shanghai` 才会按本地 09:00。
7. `/daily` 在群聊里会直接回复私聊限制文案，不创建 agent；这是产品决策，不是 bug。
8. actor state 与运行时凭据 readmodel 中有 proxy-scoped key，公开截图和日志导出时不要泄露；普通 `UserAgentCatalogDocument` 不暴露 `nyx_api_key`。

---

## 16. 关键文件路径汇总（QA 报 bug / 写测试时定位用）

| 关注点 | 文件 |
|--------|------|
| Webhook ingress | `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` |
| 命令解析与路由 | `agents/Aevatar.GAgents.ChannelRuntime/NyxRelayAgentBuilderFlow.cs` |
| `/daily` 流程主体 | `agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTool.cs` |
| Skill 模板（system prompt） | `agents/Aevatar.GAgents.ChannelRuntime/AgentBuilderTemplates.cs` |
| Skill 执行 actor | `agents/Aevatar.GAgents.ChannelRuntime/SkillRunnerGAgent.cs` |
| Skill 默认参数 | `agents/Aevatar.GAgents.ChannelRuntime/SkillRunnerDefaults.cs` |
| 注册表 actor | `agents/Aevatar.GAgents.ChannelRuntime/UserAgentCatalogGAgent.cs` |
| 注册表投影 | `agents/Aevatar.GAgents.ChannelRuntime/UserAgentCatalogProjector.cs` |
| 调度计算 | `agents/Aevatar.GAgents.ChannelRuntime/ChannelScheduleCalculator.cs` / `ChannelScheduleRunner.cs` |
| 投递目标解析 | `agents/Aevatar.GAgents.ChannelRuntime/AgentDeliveryTargetTool.cs` 与 AgentBuilderTool 内 `ResolveDeliveryTarget` |
| NyxID HTTP 客户端 | `src/Aevatar.AI.LLMProviders.NyxId/...`、`src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs` |
| 用户偏好（GitHub username） | `agents/Aevatar.GAgents.UserConfig/UserConfigGAgent.cs`、`src/Aevatar.Studio.Projection/CommandServices/ActorDispatchUserConfigCommandService.cs`、`src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs` |
| Proto 契约 | `agents/Aevatar.GAgents.ChannelRuntime/channel_runtime_messages.proto` |
| 现有测试目录 | `test/Aevatar.GAgents.ChannelRuntime.Tests/` |

---

## 17. 待办 / 明确的"现状≠目标"清单

为防止 QA 把已知未实现项当 bug 报，下表列出**当前实现没有但 issue 里已规划**的能力：

- 报告内容更丰富 + 渐进式投递 (#423)
- GitHub 工具失败需明确暴露给用户（#439 修复后）
- 多 Lark 用户独立 `github_username`（#436/#437 修复后）
- `/agent-status` 首次执行后秒级反映（#440 修复后）
- 失败通知通道与主投递解耦，避免一起死（#423 §C）
- 富 / 长报告超 Lark 30KB 体限的分段处理（#423 §C）
- 跨 app 部署的 `lark_receive_id` 自动更新（目前只能 `/delete-agent` 重建）

QA 对照本表与 issue 复现步骤即可在每个 PR landing 后系统性回归。

---

**文档维护原则**：本文档随 `agents/Aevatar.GAgents.ChannelRuntime/` 与 `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` 行为变更而更新；行为不变的纯重构不更新（重构只改文件路径行号时，QA 直接用 `git log -p` 跟踪）。
