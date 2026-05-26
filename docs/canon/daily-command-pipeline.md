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
>
> 操作侧（NyxID provider 配置、GitHub OAuth App 注册、单用户 OAuth 连接、`pending_auth` 故障排查）见 [`docs/operations/2026-04-29-daily-card-github-oauth-setup.md`](../operations/2026-04-29-daily-card-github-oauth-setup.md)。

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
2. `/daily` slash shortcut 不走本地 agent-builder 创建逻辑；`ChannelConversationTurnRunner` 把它改写为一次 LLM turn，要求模型先调用 `use_skill(skill="chrono-ai-daily", args="<slash args>")`。
3. `use_skill` 通过 `IRemoteSkillFetcher` / `OrnnSkillClient` 从 Ornn 拉取 skill 指令；Ornn API 访问经 NyxID proxy（默认 slug 可由 `Aevatar:Ornn:NyxIdSlug` 覆盖）。
4. daily 报告由当前 conversation reply 链路返回到原 Lark 私聊。`202 Accepted`、✓ reaction、`use_skill` 成功、报告投递成功是不同观察点。
5. `/agents`、`/agent-status <id>`、`/run-agent`、`/disable-agent`、`/enable-agent`、`/delete-agent` 管理的是 catalog 中已有的 scheduled agents；这些命令走 accepted-only command ports，状态通过后续 query/readmodel 观察。

---

## 1. 端到端链路总览

```
┌────────────┐      ┌───────────────┐      ┌──────────────────────────┐      ┌───────────────┐
│  Lark App  │ ───▶ │  NyxID Relay  │ ───▶ │ aevatar /api/webhooks/   │ ───▶ │ Conversation  │
│ (用户发消息) │      │ (channel-bot) │      │   nyxid-relay (POST)     │      │ Turn Runner   │
└────────────┘      └───────────────┘      └──────────────────────────┘      └───────┬───────┘
       ▲                                                                            │
       │                                                                            ▼
       │                                      ┌──────────────────────────┐    ┌──────────────┐
       │                                      │ use_skill / Ornn skill   │◀──▶│ NyxID Proxy  │
       │                                      │ chrono-ai-daily          │    │ s/ornn       │
       │                                      └─────────────┬────────────┘    └──────────────┘
       │                                                    ▼
       │                                      ┌──────────────────────────┐    ┌──────────────┐
       └──────────────────────────────────────│ LLM + nyxid_proxy tools  │◀──▶│ GitHub/Lark  │
                                              └──────────────────────────┘    └──────────────┘
```

完整 7 段链路（与用户描述一致）：

| 段 | 方向 | 内容 |
|----|------|------|
| ① Lark → NyxID | 入站 | Lark 把 `im.message.receive_v1` 推到 NyxID 的 channel bot relay webhook |
| ② NyxID → aevatar | 入站 | NyxID 把规范化后的 payload + 签名 JWT 转发到 aevatar `/api/webhooks/nyxid-relay` |
| ③ aevatar 内部 | 处理 | 鉴权 → 解析 `/daily` → 改写为 `use_skill("chrono-ai-daily")` LLM turn |
| ④ aevatar → NyxID → Ornn | 技能加载 | `OrnnSkillClient` 经 NyxID proxy 拉取 `chrono-ai-daily` skill JSON |
| ⑤ aevatar → NyxID → GitHub | LLM / skill 工具调用 | skill 指令驱动工具调用；GitHub 访问仍经 NyxID proxy 注入用户凭据 |
| ⑥ GitHub → aevatar | 工具响应 | JSON 结果回到 LLM；LLM 总结成 daily 文本 |
| ⑦ aevatar → NyxID → Lark | 出站回执 | conversation reply 链路把文本投递到原私聊 |

---

## 2. 链路时序

```
Lark User      Lark App     NyxID Relay     aevatar(webhook)    Ornn/use_skill     NyxID(proxy)     GitHub      LLM
   │              │              │                │                      │                  │              │          │
   │── /daily ───▶│              │                │                      │                  │              │          │
   │              │── event ────▶│                │                      │                  │              │          │
   │              │              │── POST relay ─▶│                      │                  │              │          │
   │              │              │  +X-NyxID-     │                      │                  │              │          │
   │              │              │  Callback-Token│                      │                  │              │          │
   │              │              │                │── ✓ react ──────────────────────────▶ Lark             │          │
   │              │              │                │                      │                  │              │          │
   │              │              │                │── parse /daily       │                  │              │          │
   │              │              │                │── build LLM prompt: use_skill("chrono-ai-daily") ───────▶│
   │              │              │                │                      │                  │              │          │
   │              │              │                │                      │◀── use_skill ──────────────────────────────│
   │              │              │                │                      │── get skill ───▶ NyxID ───────▶ Ornn       │
   │              │              │                │                      │◀─ SKILL.md ───── NyxID ◀────── Ornn       │
   │              │              │                │                      │                  │              │          │
   │              │              │                │                      │◀── nyxid_proxy(GET /search/commits) ───────│
   │              │              │                │                      │── proxy call ───▶│              │          │
   │              │              │                │                      │                  │── injects ──▶│          │
   │              │              │                │                      │                  │   gh OAuth   │          │
   │              │              │                │                      │                  │              │── search ▶│
   │              │              │                │                      │                  │              │◀─ items ─│
   │              │              │                │                      │◀─ JSON ──────────│              │          │
   │              │              │                │                      │── final text ─────────────────────────────▶│
   │              │              │                │                      │◀─ summary text ───────────────────────────│
   │◀──── daily 报告 ────────────────────────────────────────────────────────── Lark        │              │          │
```

注意几个时间窗：
- **webhook 返回窗口**：`HandleRelayWebhookAsync` 只做鉴权、规范化与 `IActorDispatchPort.DispatchAsync` 派发；`202 Accepted` 表示 activity 已进入 `ConversationGAgent` inbox，不表示 `/daily` 创建、首次执行或 readmodel 已完成。
- **✓ reaction**：`TrySendImmediateLarkReactionAsync()` 是 fire-and-forget，`RunInboundAsync` 不等待它完成；它可以独立失败，也不能证明后续 agent 创建成功。它还有静默 gate：只对 `ActivityType.Message`、`lark/feishu` 平台、存在 `NyxUserAccessToken` 与 `NyxProviderSlug`、且 `NyxPlatformMessageId` 以 `om_` 开头的消息尝试发送。
- **首次执行延迟**：现网首次执行通常由 Ornn skill load、LLM 推理和 GitHub 多次 search 主导，约几十秒。用户只应把最终报告或错误说明当作 `/daily` 业务结果。
- **下一次定时执行**：若 skill 或已有 scheduled agent 建立计划，调度语义由其强类型状态和后续 readmodel/query 观察，不由 `/daily` webhook ACK 承诺。

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

**响应码语义**（handler 永远只回 `202` / `400` / `401` / `499` / `500`，**不返回 `200`**——成功与忽略路径都是 `202`，靠 body 里的 `status` 字段区分；见 [NyxIdChatEndpoints.Relay.cs:87,147](../../agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs)）：

| 状态 | body | 含义 | 测试关注 |
|------|------|------|----------|
| `202 Accepted` | `{status:"accepted", message_id, actor_id}` | **正常成功路径**：activity 已派发到 `ConversationGAgent` inbox | QA 看到 `202` 不能默认是"忽略"，必须读 body `status` 字段 |
| `202 Accepted` | `{status:"ignored", reason, detail}` | payload 合法但被透传层标记为忽略（如非聊天事件） | 不应触发任何下游逻辑 |
| `400 Bad Request` | `{error:"invalid_relay_payload", detail}` | parse 失败 | 期望 NyxID 重试或上报 |
| `400 Bad Request` | `{error:"conversation_key_missing", detail}` | activity 解析后没有 canonical conversation key | 等价上 |
| `401 Unauthorized` | — | JWT 校验失败 / scope 解析失败 | 不应有任何业务副作用，日志含 `Relay callback authentication failed` |
| `499` | — | 客户端取消 | — |
| `500 Internal Server Error` | — | handler 未捕获异常 | 日志含 `Relay handler unexpected error` |

**派发**：成功后构造 `NyxRelayInboundActivity`（含 reply token、user access token、normalized `ChatActivity`），包装成 `EventEnvelope` 后通过 `IActorDispatchPort.DispatchAsync` 投递到 `ConversationGAgent`（actor id 由 conversation canonical key + scope 推出）。

### 阶段 ③ aevatar 内部业务路由

调用顺序：

1. `ChannelConversationTurnRunner` 收到 `ChatActivity`
   - 文件：`agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs`
   - `TrySendImmediateLarkReactionAsync()`（line 58 附近）→ fire-and-forget 发 ✓ emoji，不等待成功；前置条件不满足时静默跳过
   - 路由到 `TryHandleAgentBuilderAsync()`

2. `NyxRelayAgentBuilderFlow.TryResolve(evt, out decision)`
   - 文件：`agents/Aevatar.GAgents.Authoring.Lark/NyxRelayAgentBuilderFlow.cs`
   - 校验：`evt.Text` 必须以 `/` 开头；`chat_type == "p2p"`（私聊）；命令必须在已知列表里
   - 已知命令：`/agents /agent-status /run-agent /disable-agent /enable-agent /delete-agent`
   - `/daily` 与其他未知 slash（如 `/goal`）是 Ornn skill shortcut：本路由放行给 LLM reply path，不走 `agent_builder`
   - 不在白名单 → fall through；由 `BuildLlmRequestActivity(...)` 强制走 Ornn skill 搜索/加载，而不是本地 Unknown command 回复
   - 非私聊 → 回 `BuildPrivateChatRestrictionReply()`，不创建 agent、不执行 tool

3. `ChannelConversationTurnRunner.BuildLlmRequestActivity(...)`
   - 文件：`agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs`
   - `TryBuildSkillInvocationPrompt()` 识别 `/daily` 或 `/daily ...`
   - 输出 LLM prompt：要求先调用 `use_skill`，`skill="chrono-ai-daily"`，`args` 为 `/daily` 后面的原始参数文本
   - 其他非本地 slash（如 `/goal`）输出 LLM prompt：要求先调用 `ornn_search_skills(query="<command>")`，再 `use_skill` 最匹配的 skill，并把 slash 后面的原始参数作为 `args`
   - 原始命令文本保留在 prompt 中，便于 skill 按自己的契约解析参数

4. `NyxIdConversationReplyGenerator.GenerateReplyAsync(...)`
   - 文件：`agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs`
   - 构造 `ChatRuntime.ChatStreamAsync` 主链；`use_skill` 与 `ornn_search_skills` 作为工具注入
   - `UseSkillTool` 从本地 `LocalSkillCatalog` 或远程 `IRemoteSkillFetcher` 加载 skill；远程路径由 `OrnnRemoteSkillFetcher` / `OrnnSkillClient` 通过 NyxID proxy 访问 Ornn
   - skill 指令负责 GitHub daily 的后续工具调用、格式与错误文案；aevatar 本地不再复制一套 daily 创建/调度语义

5. `AgentBuilderTool.ExecuteAsync(argumentsJson, ct)` 只管理 catalog 中已有 agents
   - 文件：`agents/Aevatar.GAgents.Authoring.Lark/AgentBuilderTool.cs`
   - 关键步骤（**每步都有"失败时返回 JSON `{error: ...}`"分支，且都是测试覆盖点**）：

| 步 | 行为 | 失败分支 |
|----|------|----------|
| a | 解析 caller scope、NyxID token、query port、runner command port、catalog command port | 必要服务缺失返回 `{error: ...}` |
| b | `list_agents` / `agent_status` 走 `IUserAgentCatalogQueryPort` 读取 readmodel | 未找到 agent 返回 `{error: ...}` |
| c | `run_agent` / `disable_agent` / `enable_agent` 先从 readmodel 校验 caller 可见 agent，再通过 `ISkillRunnerCommandPort` 派发 lifecycle command | 不支持 managed lifecycle 或 command dispatch failure |
| d | `delete_agent` 先要求 `confirm=true`，再 disable runner、撤销 NyxID API key、通过 `IUserAgentCatalogCommandPort.TombstoneAsync` 派发 tombstone | 未确认 / agent 不存在 / command dispatch failure |
| e | 所有 lifecycle/delete command ACK 都是 accepted-only；状态变化、删除可见性与执行结果通过后续 `/agent-status`、`/agents` 或推送观察 | — |

6. `NyxRelayAgentBuilderFlow.FormatToolResult(...)` / `AgentBuilderCardFlow.FormatToolResult(...)`
   - 把 agent management tool JSON 渲染成 Lark 可接受的 `MessageContent`
   - lifecycle 文案明确使用 accepted / propagating 语义，不承诺 readmodel 已刷新

### 阶段 ④ aevatar → NyxID → Ornn（skill 加载）

**Skill 加载**：
- `UseSkillTool` 参数：`skill="chrono-ai-daily"`，`args` 为 `/daily` 后面的原始参数文本。
- 本地 `LocalSkillCatalog` 未命中时，`UseSkillTool` 每次按当前 NyxID token 调用 `OrnnRemoteSkillFetcher.FetchSkillAsync()`，再由 `OrnnSkillClient.GetSkillJsonAsync(token, "chrono-ai-daily")` 经 NyxID proxy 拉取远程 skill；远程 skill 不写入进程级缓存。
- `OrnnSkillClient` 使用当前 NyxID access token，经 `NyxIdApiClient.ProxyRequestAsync` 访问生产 NyxID `ornn-api` service 当前暴露的 Ornn API 路由（`/api/v1/skill-search` 与 `/api/v1/skills/{idOrName}/json`）；默认 NyxID service slug 来自 Ornn options，可由 `Aevatar:Ornn:NyxIdSlug` 覆盖。
- 单次 Ornn 拉取有 30s per-call timeout；timeout 或 proxy error 会返回 skill not found / loading failure，让 LLM 走错误说明路径。外层 reply generation 不再用固定 120s 之类的硬超时截断长 skill workflow。
- `../chrono-ornn` 不在本 worktree 同级目录时，本文只描述 aevatar 可验证的 skill bridge 契约，不复制 Ornn skill 内部实现。

### 阶段 ⑤ SkillRunner 执行 → NyxID → GitHub

本节描述 catalog 中已有 scheduled agents 的执行路径，用于 `/run-agent`、`/agent-status`、历史 `SkillRunnerGAgent` 回归与投影 QA；当前 `/daily` shortcut 本身不再创建新的 local runner。

**触发**：
- 立即执行：阶段 ③.j 由 `ISkillRunnerCommandPort.InitializeAsync(..., runImmediately:true)` 派发 `TriggerSkillRunnerExecutionCommand{Reason="create_agent"}`
- 定时执行：`ChannelScheduleRunner.ScheduleNextRunAsync` → Orleans 持久化回调 → fire `TriggerSkillRunnerExecutionCommand{Reason="schedule"}`
- 手动：`/run-agent <agent_id>` → `ISkillRunnerCommandPort.TriggerAsync(...)` 派发同样的 trigger，`Reason="manual"`
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
    // runner committed state is projected into SkillRunnerExecutionDocument
}
catch (Exception ex) {
    if (RetryAttempt < MaxRetryAttempts /*=1*/)
        return ScheduleRetryAsync(RetryAttempt+1);  // 30s 后再试一次
    PersistDomainEventAsync(SkillRunnerExecutionFailedEvent { Error = ex.Message });
    TrySendFailureAsync(ex.Message);
    Scheduler.ScheduleNextRunAsync(now);
    // failure facts are owned by the runner and projected into the execution document
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
- `99992361`（open_id cross app）和 `99992364`（union_id cross tenant）不会触发 receive_id fallback，会直接进入失败路径并给 `/agent-status` 留下带重建提示的 `last_error`。
- **失败通知通道**（issue #423 § C，已落地）：`TrySendFailureAsync` 优先走 `OutboundConfig.FailureNotificationProviderSlug`（创建 agent 时从入站 channel-bot 的 `nyx_provider_slug` 抓住的旁路 proxy）。当主投递因 99992361/99992364 在 `s/api-lark-bot` 拒绝时，这条旁路 slug 是用户最近一次成功消息的 bot——按定义可达。仅当 (a) 入站 slug 与主 slug 相同（同一个 proxy 没有恢复价值），或 (b) 入站 slug 不在用户 `UserService` 列表里（API key 无法授权 routing），才回退到原本的"和主投递走同一 proxy"的单次尝试。失败通知本身吞所有异常，不会盖掉 `SkillRunnerExecutionFailedEvent` 持久化。

---

## 4. 数据契约（关键 proto 字段）

文件：`agents/Aevatar.GAgents.Channel.Runtime/protos/channel_bot_registration.proto`、`agents/Aevatar.GAgents.Scheduled/protos/skill_runner.proto`、`agents/Aevatar.GAgents.Scheduled/protos/user_agent_catalog.proto`

### `ChannelInboundEvent`（入站规范化消息）
- `text`、`sender_id`、`sender_name`、`conversation_id`、`chat_type`、`platform`、`registration_token`、`nyx_provider_slug`、`registration_scope_id`
- **重点**：`sender_id` 实质是 Lark `open_id`（`ou_*`），**只在单个 Lark App 内唯一**——同一个真人在不同 Lark app 下会有不同 `open_id`，跨 app 不能直接拿来对账（这是 PR #409 引入 `union_id`/`on_*` 入站和 `chat_id`-first delivery fallback 的原因，详见 [LarkConversationTargets.cs](../../agents/platforms/Aevatar.GAgents.Platform.Lark/LarkConversationTargets.cs)）。`registration_scope_id` 是 bot 维度。下面 issue #436/#437 的 cross-user leak bug 就源自只用 `registration_scope_id` 当 user-config key，丢了 `sender_id`。

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
OwnerScope owner_scope = 11;
string failure_notification_provider_slug = 12;  // §C 旁路 proxy slug（入站 channel-bot），失败通知用
```

### `SkillRunnerState`
- `skill_name="daily"`、`template_name="daily"`
- `skill_content` / `execution_prompt`：阶段 ③ 拼好后冻在 actor state，**不会再变**——QA 注意：用户改 GitHub 绑定后，已存活的 agent 不会自动重指向；这是 issue #436 acceptance criteria 第 5 条要保留的语义
- `schedule_cron` / `schedule_timezone`、`enabled`、`scope_id`
- `provider_name` / `model` / `temperature` / `max_tokens` / `max_tool_rounds=20` / `max_history_messages`
- 运行态：`last_run_at`、`next_run_at`、`error_count`、`last_error`、`last_output`

### `UserAgentCatalogEntry`（well-known 注册表条目）
- 关键字段：`agent_id`、`agent_type="skill_runner"`、`template_name="daily"`、`platform="lark"`、`conversation_id`、`scope_id`、`lark_receive_id*`
- 不承载执行事实：`status`、`last_run_at`、`next_run_at`、`error_count`、`last_error` 由 `SkillRunnerState` 拥有，并由 `SkillRunnerExecutionProjector` 从 runner committed state 物化进 `SkillRunnerExecutionDocument`。
- `nyx_api_key` / `api_key_id`：actor state 内的 catalog entry 保留这两个字段；公开 `UserAgentCatalogDocument` 不再暴露 `nyx_api_key`，运行时出站读取单独的 `UserAgentCatalogNyxCredentialDocument`。

### 命令 / 事件
- 命令：`InitializeSkillRunnerCommand`、`TriggerSkillRunnerExecutionCommand{Reason, RetryAttempt}`、`DisableSkillRunnerCommand`、`EnableSkillRunnerCommand`、`UserAgentCatalogUpsertCommand`、`UserAgentCatalogTombstoneCommand`
- 事件：`SkillRunnerInitializedEvent`、`SkillRunnerNextRunScheduledEvent`、`SkillRunnerExecutionCompletedEvent`、`SkillRunnerExecutionFailedEvent`、`SkillRunnerDisabledEvent`、`SkillRunnerEnabledEvent`、`UserAgentCatalogUpsertedEvent`、`UserAgentCatalogTombstonedEvent`

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

**默认值**：`agents/Aevatar.GAgents.Scheduled/SkillRunnerDefaults.cs`
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

**事实源**：`SkillRunnerGAgent` actor state（每个 agent 一个 actor，拥有执行事实）+ `UserAgentCatalogGAgent`（well-known，全局唯一注册表 actor，只拥有成员集合与静态属性）

**Projection**：`UserAgentCatalogProjector` 只消费 catalog committed state → 物化 catalog membership-only `UserAgentCatalogDocument`，`StateVersion` 来自 `UserAgentCatalogGAgent` committed version。`SkillRunnerExecutionProjector` 只消费 runner committed state → 物化 runner-owned `SkillRunnerExecutionDocument`，`StateVersion` 来自对应 `SkillRunnerGAgent` committed version。`/agents` 与 `/agent-status` 在 query/consumer 层 join 两个 readmodel，并暴露 catalog/runner 双水位，不合成单一版本。

**Presentation join 约束**：`/agents` 与 `/agent-status` 的 catalog + execution join 只是对外 presentation response 装配，用于展示 caller 可见 agent 的执行快照。它不得作为内部 lifecycle command 准入事实源，不得形成可复用 aggregate query contract，也不得反向声明 catalog/execution 的统一业务状态。`run_agent` / `disable_agent` / `enable_agent` 同步准入只依赖 catalog authority（caller visible、agent exists、agent type supports managed lifecycle）；runner `Enabled/Disabled` 只在 `SkillRunnerGAgent` 自身 turn 内判定，拒绝执行时发布 runner-owned state event，再由 `/agent-status` 或 `/agents` 观察。

**查询端口**：`IUserAgentCatalogQueryPort`
- `QueryByCallerAsync(owner_scope)`：`/agents` 命令的数据源
- `GetForCallerAsync(agentId, owner_scope)`：`/agent-status <id>` 单条查询

**关键不变量 / 测试关注**：
- `UpsertRegistryAsync` 在 `HandleInitializeAsync` 末尾只注册 membership；它不写执行字段。
- runner 执行完成、失败、启停后的 committed state 是 `/agent-status` 的执行事实来源；projection 必须从 runner state 物化 `status` / `last_run_at` / `next_run_at` / `error_count` / `last_error` 到 `SkillRunnerExecutionDocument`。
- 创建、启停、删除与手动运行命令的同步结果只承诺 accepted；readmodel 是否已经反映，需要通过后续 `/agent-status`、`/agents` 或推送事件观察。

---

## 8. Outbound 投递行为

**默认路径（streaming-edit）**：SkillRunner 在 LLM 流式输出过程中，第一条非空 delta 走 `POST {NyxID}/api/v1/proxy/s/api-lark-bot/open-apis/im/v1/messages?receive_id_type={primary_type}`（`{receive_id, msg_type:"text", content:"{\"text\":\"...\"}"}`），从响应 `data.message_id` 捕获平台消息 id；之后每条 delta 走 `PUT .../open-apis/im/v1/messages/{message_id}`（`{msg_type:"text", content:"{\"text\":\"...\"}"}`）。Lark 编辑接口按 `msg_type` 分动词：**PUT** 编辑 text/post (rich text)，**PATCH** 编辑 interactive card——发 text 必须用 PUT，否则 Lark 会拒绝每一次编辑，streaming-edit 在占位之后无法增长（参考 [Lark Edit message 文档](https://open.feishu.cn/document/server-docs/im-v1/message/update)）。Throttle 默认 300ms（`SkillRunnerDefaults.StreamingEditThrottle`）：throttle 窗口内多条 delta 折叠到最新文本，单 turn 平均编辑速率 ≤3.3/s 安全在 Lark 限速以下。Finalize 阶段强制再编辑一次最终文本以保证一致性，跳过 throttle。详见 [SkillRunnerStreamingReplySink.cs](../../agents/Aevatar.GAgents.Scheduled/SkillRunnerStreamingReplySink.cs)。

**Fallback（仅作用于初次 POST）**：初次 POST 返回 Lark `230002 bot_not_in_chat` 时，用 `lark_receive_id_fallback` + `lark_receive_id_type_fallback` 重试一次以拿到可编辑的 `message_id`。一旦初次 POST 成功捕获 `message_id`，后续 PUT 不再 fallback——同一目标内 retry 即可。已观察到但不触发 fallback 的身份错误（直接进入失败路径并给 `/agent-status` 留下重建提示）：
- `99992361`：open_id cross app
- `99992364`：union_id cross tenant

**失败语义**：
- 初次 POST 在流式中段失败：日志告警，**下一条 delta 重试**（流式失败不等于 turn 失败，LLM 仍在出 token）。Finalize 时若 POST 仍失败 → 抛 `InvalidOperationException`，主链路 catch 进 `SkillRunnerExecutionFailedEvent` 持久化路径。
- 中段编辑（PUT）失败：日志告警，下一条 delta 用最新累积文本重试（latest-wins 折叠保证旧文本不会卡住）。
- Finalize 阶段的编辑失败：抛异常，同上。

**One-shot 兜底（无 streaming sink 时）**：当 `NyxIdApiClient` 未注入或 `OutboundConfig` 缺关键字段（`NyxApiKey`/`NyxProviderSlug`/`ConversationId`），`ExecuteSkillAsync` 会回退到原本的一次性 `SendOutputAsync(POST)` 路径——同步发整段文本，沿用同样的 230002 fallback 重试。失败通知 `TrySendFailureAsync` 始终走这条 one-shot 路径（无需 streaming，且失败文案本来就短）。

**长度上限 / 分段投递**（issue #423 §C，已落地）：流入 sink 的累积文本超过 `SkillRunnerStreamingReplySink.MaxLarkTextLength=30000` 字符时仍会在 sink 内截断（运行时安全网）。但 `ExecuteSkillAsync` 在 stream 结束后会用 `SkillRunnerOutputChunker.Split()` 按段落（`\n\n`）边界把整段输出切成 ≤30K 的若干 chunk：chunk[0] 经流式编辑落地（用户看到的那条消息），chunk[1..N] 各自通过 `SendOutputAsync` 走主 `nyx_provider_slug` 投递成新消息。每个非首/末段附 `[part k/N • continued ↑/continues ↓]` 标记，无段落边界（病态长段落）的输入退化为字符级硬切——结果仍可投递，只是切点没有段落对齐。任一段失败抛 `InvalidOperationException`，主链路 catch 进失败持久化路径；先落地的段保留在用户聊天里，是有意的部分可见——Lark 没有事务式多消息投递。

**已知边界**（已记入 issues，QA 复测时要能判别）：
- `lark_receive_id*` 在 agent 创建时被冻结。如果用户从 chat A 创建 agent，后来 chat A 解散或机器人被踢，agent 投递就永远失败 → 必须 `/delete-agent` + 重建。
- 如果创建侧的 inbound bot 和 outbound bot 是不同 Lark App（同租户跨 app 部署），`chat_id` 可能在 outbound 侧不可用，需要 fallback 到 `union_id`。
- Lark 编辑接口对**已删除消息**返回错误，sink 在中段会日志告警并继续；finalize 时仍 throw（极端 edge case：用户在 LLM 输出过程中手动删除了在编辑的消息）。

---

## 9. 错误 / 失败模式分类

按"用户能不能看到"维度分：

### 9.1 用户看得到（直接回 Lark 的 JSON `{error:"..."}` 或文案）
- `No NyxID access token available. User must be authenticated.` —— NyxID 会话失效
- `Connect GitHub in NyxID, then run /daily again.` —— 没绑 GitHub provider
- `github_username is required for template=daily`
- `schedule_cron is required for create_agent`
- `Invalid schedule: {cronError}`
- `conversation_id is required when no current channel conversation is available`
- `Could not resolve current NyxID user id`
- `Unsupported template '{x}'.`
- 创建 API key / 解析 service id 失败时 NyxID 原始 error JSON 透传

### 9.2 用户看到，但语义可能错（关键 bug 区！）
- **Issue #439（silent failure）**：proxy 返回 4xx/5xx/7xxx 时 `nyxid_proxy` 工具把错误 JSON 原样返回，LLM 误判为"无活动"，输出空的 daily 报告 + `Status: running, error_count: 0`。**测试关键**：要能区分"GitHub 真无活动"和"工具失败被吞掉"。
- **Issue #436/#437（cross-user leak）** ✅ 已由 [#438](https://github.com/aevatarAI/aevatar/pull/438) 修复——composite scope `{regScope}:lark:{senderId}` 让 user-config 按 Lark 用户隔离。**回归测试关键**：两个不同 sender_id 在同一 registration_scope_id 下，分别 `/daily a` 和 `/daily b`，第三步 user A 再 `/daily` 必须看到自己的 username `a`，不应是 `b`。

### 9.3 用户看不到（更隐蔽，需要查日志或 `/agent-status` 才能发现）
- **Issue #440**：首次执行成功后 `/agent-status` 的 `Last run` / `Next run` 一直 `n/a`。
- **Issue #398**：webhook 完全没到 aevatar——aevatar 日志里只有 K8s liveness 探活，无 `POST /api/webhooks/nyxid-relay`。
- 出站主投递失败时 `TrySendFailureAsync` 优先走 `OutboundConfig.FailureNotificationProviderSlug`（入站 channel-bot 抓住的旁路 proxy，issue #423 §C）。仅当未捕获到旁路 slug、或入站与主 slug 相同（同一 proxy 没有恢复价值）、或两路都拒绝时，用户才完全看不到失败——剩余的可观测路径是 `/agent-status` 的 `last_error` 文案。

### 9.4 重试相关
- 每次执行 fail，`MaxRetryAttempts=1`，30 秒后自动重试 1 次
- 两次都失败：`SkillRunnerExecutionFailedEvent` + `TrySendFailureAsync` + 仍调度下一次定时；`/agent-status` 的 error 状态来自 runner committed state 的投影。

---

## 10. 命令参数与文案矩阵

`/daily` 参数解析属于 Ornn `chrono-ai-daily` skill 的契约；aevatar 本地只把 `/daily` 后面的原始文本作为 `use_skill.args` 透传。下表用于 QA 描述用户意图，不表示本仓库有本地 daily 创建 parser。

| 输入 | aevatar 本地处理 | 下游语义 |
|------|----------------------|----------|
| `/daily` | `use_skill.args=""` | 已存偏好 / GitHub fallback 由 `chrono-ai-daily` skill 解释 |
| `/daily alice` | `use_skill.args="alice"` | username、是否保存偏好由 skill 契约解释 |
| `/daily github_username=alice` | 原样透传 args | 命名参数由 skill 契约解释 |
| `/daily alice schedule_time=14:30` | 原样透传 args | cron / timezone 由 skill 契约解释 |
| `/daily alice schedule_timezone=Asia/Shanghai` | 原样透传 args | 同上 |
| `/daily alice repositories=a/b,c/d` | 原样透传 args | 仓库过滤由 skill 契约解释 |
| `/daily alice run_immediately=false` | 原样透传 args | 是否调度 / 是否立即执行由 skill 契约解释 |
| 群聊里发 `/daily ...` | 不创建本地 runner | 按当前 slash/LLM 路由约束处理 |
| `/daily?` 等未知形态 | 不匹配 `/daily` shortcut 时按普通 slash / LLM 路由处理 | — |

**用法提示文案**：`"/daily [github_username] schedule_time=09:00 repositories=owner/repo"`

---

## 11. 已知 bug 一览（与 milestone "Day One Enhancement" 对齐）

| Issue | 严重度 | 标题简述 | 影响层 | QA 复现要点 |
|-------|--------|---------|--------|-------------|
| ~~#437~~ ✅ | 高（数据隔离） | `/daily` binding causes cross-user data leakage（用户视角） | UserConfigGAgent scope key | **已由 [#438](https://github.com/aevatarAI/aevatar/pull/438) 修复**（composite scope `{regScope}:lark:{senderId}`）；下表 12.6 #8 / 12.8 E11 转为回归测试 |
| ~~#436~~ ✅ | 高（同上 #437 的工程分析） | GitHub username binding shared across all Lark users（last writer wins） | 同上 | 同上 |
| #439 | 高（语义错） | SkillRunner masks GitHub tool failures as silent "no activity" success | prompt + nyxid_proxy 工具 + runner 的"非空即成功"路径 | 强制 GitHub 接口返回 4xx/5xx，验证报告必须显式标错而不是出 `No X surfaced` |
| #440 | 中（运维可见性） | `/agent-status` 首次执行不刷新 `Last run`/`Next run` | runner committed state → `SkillRunnerExecutionProjector` execution readmodel 路径 | `/daily X`（run_immediately）→ 30s 后 `/agent-status <id>` 看 `Last run` 应非 n/a |
| ~~#423~~ ✅ | 中（增强 + 失败通知短板） | richer report content + progressive delivery + chunked + 失败通知旁路 | prompt（§A，#458 已合）+ streaming-edit（§B，#469 已合）+ chunked + failure-notification slug（§C，本 PR） | 已落地：`/daily` 报告流式编辑、>30K 自动分段、出站失败时优先经入站 channel-bot 投递失败通知 |
| #398 | 高（链路断） | Lark relay callbacks never reach aevatar | NyxID 侧 callback_url 配置 / 多副本 ingress / Lark 订阅状态 | 用户发消息无任何反应，aevatar 日志只有 K8s liveness |

每条 bug 在对应 issue 描述里都有完整 acceptance criteria，QA 用例可直接对齐。

---

## 12. 测试矩阵（按测试类型组织）

### 12.1 单元测试 — 命令解析层（已有底子）

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/NyxRelayAgentBuilderFlowTests.cs`

应覆盖：
- ✅ `/daily` 不带任何参数 → agent-builder router fall through，由 LLM reply path 处理 Ornn skill shortcut
- ✅ `/daily alice` / `/DAILY alice schedule_time=09:00` → agent-builder router fall through
- ✅ `ChannelConversationTurnRunner` 把 `/daily alice` 改写成包含 `use_skill`、`chrono-ai-daily`、`alice`、原始命令文本的 LLM request
- ✅ 未知 slash 命令 `/goal ...` → agent-builder router fall through；`ChannelConversationTurnRunner` 改写成先 `ornn_search_skills` 再 `use_skill`
- ✅ 非私聊（`chat_type != "p2p"`）→ `BuildPrivateChatRestrictionReply`，**不**产生 ToolCall
- ❌ 边界：Ornn skill load 失败 → 用户看到 skill loading / unavailable 说明，不创建本地 runner
- ❌ 边界：`/daily` 参数非法 → 由 `chrono-ai-daily` skill 返回参数错误文案

### 12.2 单元测试 — Agent management 层

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/AgentBuilderToolTests.cs`

应覆盖：
- `list_agents` / `agent_status` 只读 caller-scoped catalog readmodel
- `run_agent` → `ISkillRunnerCommandPort.TriggerAsync`，返回 `status:"accepted"`，不等待 execution readmodel 刷新
- `disable_agent` / `enable_agent` → accepted + propagating note，后续 `/agent-status` 观察状态
- `delete_agent` 未带 `confirm=true` → 返回确认提示；确认后先 disable、撤销 API key、再 tombstone catalog membership
- tombstone 结果为 accepted-only；删除可见性通过后续 `/agents` 观察
- 不支持 managed lifecycle 的 agent type 返回明确 error，不派发 runner command

### 12.3 单元测试 — SkillRunner actor

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/SkillRunnerGAgentTests.cs`

应覆盖：
- `HandleInitializeAsync`：`SkillContent` 为空 → 直接返回不持久化（仅 LogWarning）
- `HandleInitializeAsync` 正常 → 持久化 `SkillRunnerInitializedEvent` + `Scheduler.ScheduleNextRunAsync` + `UpsertRegistryAsync`
- `HandleTriggerAsync`：`State.Enabled=false` → 跳过
- `HandleTriggerAsync` 成功 → `Completed` 事件 + retry lease 取消 + 下次调度；执行字段由 runner committed state 投影到 `SkillRunnerExecutionDocument`
- `HandleTriggerAsync` 失败：`RetryAttempt < 1` → `ScheduleRetryAsync(2)` 不发 `Failed`
- `HandleTriggerAsync` 失败：`RetryAttempt >= 1` → 持久化 `Failed` + `TrySendFailureAsync` + 下次调度（仍按 cron）+ status=error
- `Disable` → `Enabled=false`，下次 trigger 跳过
- `Enable` → `Enabled=true`，恢复执行
- 状态转换：每个事件类型 → `TransitionState` 应正确合并

### 12.4 单元测试 — 注册表

文件：`test/Aevatar.GAgents.ChannelRuntime.Tests/UserAgentCatalogGAgentTests.cs`、`UserAgentCatalogProjectorTests.cs`

应覆盖：
- `Upsert` → entry 进 state；同 agent 再次 `Upsert` → 覆盖且不重复
- `SkillRunnerExecutionCompletedEvent` / `SkillRunnerExecutionFailedEvent` → execution projector 物化 `last_run_at` / `next_run_at` / `status` / `error_count` / `last_error`
- **#440 应加测**：membership upsert 与 runner execution committed state 分别物化到 `UserAgentCatalogDocument` / `SkillRunnerExecutionDocument`，查询层 join 后共同体现在 `/agent-status` DTO 上。
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
1. 黄金路径：`/daily alice` → 期望 ✓ emoji best-effort；conversation LLM request 包含 `use_skill` / `chrono-ai-daily` / `alice`；最终报告经 reply 链路投递到原 Lark 私聊
2. Ornn skill load 失败：mock `OrnnSkillClient.GetSkillJsonAsync` 返回 null / timeout → 期望错误说明投递给用户，不创建本地 runner
3. GitHub 未绑：由 `chrono-ai-daily` skill / NyxID proxy 返回授权提示；aevatar 本地不创建 API key 或 runner
4. GitHub proxy 全失败（#439）：mock GitHub search 返回 error JSON → 期望报告显式暴露工具失败，不伪装成“无活动”
5. GitHub proxy 部分失败（#439）：1 成功 + 2 失败 → 期望最终输出含失败 endpoint 列表（修复后才能过）
6. 投递主失败 fallback 成功：mock 主 Lark reply 返回 230002 → 验证用 fallback receive_id 重试 → 成功
7. 投递主失败 fallback 也失败：验证失败通知 / error path 可观察
8. **#436 cross-user leak**：模拟两个 `sender_id` (A、B) 在同一 `registration_scope_id` 下：
   - A 发 `/daily alice` → preference 应仅落到 A 的 user-config 子键（修复后）
   - B 发 `/daily bob` → 仅落 B
   - A 再发 `/daily`（无 username）→ 拿到 `alice`，不是 `bob`
9. Scheduled agent readmodel observation：对已有 `skill_runner` 发 `/run-agent <id>` → 收到 accepted 后，通过后续 `IUserAgentCatalogQueryPort.GetForCallerAsync(agentId, owner_scope)` 或 `/agent-status <id>` 观察，期望 `last_run_at` / `next_run_at` 在 projection catch up 后已填
10. cron 排程：对已有 scheduled agent 验证 `next_scheduled_run` UTC 时间正确（按当前 mock 时钟换算）

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
| E1 | 私聊发 `/daily eanzhao` | 尽快出现 ✓ emoji（best-effort，不作为成功条件）；≤90s 收到含至少 1 条 bullet 的报告（用 GitHub 上确实有活动的账号）；不应出现本地 agent id 回执 |
| E2 | 私聊发 `/daily inactive_user_no_commits_24h` | 报告显式说"无活动"（不要伪造内容） |
| E3 | 私聊发 `/daily` 多次（已落 preference） | 第二次起无需 username，应直接用历史绑定 |
| E4 | 群聊发 `/daily eanzhao` | 按当前 slash/LLM 路由返回私聊限制或不创建 agent；不得创建本地 runner |
| E5 | `/agent-status <id>`（针对已有 scheduled agent） | 返回 readmodel 当前快照；新鲜度通过后续查询观察 |
| E6 | `/agents` | 列表展示已有 agents，状态来自 catalog readmodel |
| E7 | `/run-agent <id>` | 返回 accepted；新报告到聊天后，状态通过后续 `/agent-status` 更新 |
| E8 | `/disable-agent <id>` 后等过 cron 时刻 | 不应执行；`/agent-status` `Status: disabled` |
| E9 | `/enable-agent <id>` 后等 cron 时刻 | 应执行 |
| E10 | `/delete-agent <id> confirm` | 注册表里消失；NyxID 上 api key 撤销 |
| E11 | 两台测试机分别用不同 Lark 账号在同 bot 下 `/daily a` / `/daily b`，A 再 `/daily` | A 必须拿回 `a`（#438 已修，此用例转回归） |
| E12 | 触发 GitHub 接口失败（吊销 NyxID 上的 GitHub OAuth 后立即跑 `/run-agent`） | 报告应显式说"GitHub 工具失败 + 状态码"（#439 修复后），`/agent-status` `error_count` 增加 |
| E13 | 跨 app 部署：从 inbound bot 私聊发起，outbound bot 不在该 chat | 主投返回 230002 → fallback 用 union_id 投到用户单聊；如全失败应有失败通知（#423 §C） |
| E14 | 关掉 NyxID 上 callback_url 指向，发 `/daily` | aevatar 收不到 webhook（验日志），用户看不到任何回复（#398 复现） |

### 12.9 性能 / 容量（建议覆盖）

- 同一 bot 下并发 50 个用户同时发 `/daily`：当前实现应记录 webhook 返回耗时；该用例用于暴露 Ornn skill load、LLM 与 proxy 工具调用对 conversation turn 的影响。
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

1. **加 emoji 反应**和**daily 报告**不是同一个 HTTP 请求；emoji 是 fire-and-forget 的 best-effort 反应，当前 `/daily` 报告走 conversation reply / Ornn skill 链路。两者可以独立失败。
2. **首次 `/daily` 当前是 Ornn skill turn**：用户看到的是 skill 生成的 daily 报告或错误说明，不应期待本地 agent id 回执。
3. **`run_immediately` 是 skill 参数语义**，不是本地 command ACK 语义；本仓库只透传 `/daily` 后面的原始参数给 `chrono-ai-daily`。
4. 已存在 scheduled agent 的 `OutboundConfig` / 报告 prompt 不会因 NyxID 上的 GitHub username 绑定变化自动回流。需要 `/delete-agent <id>` 后重建该 agent。
5. `MaxRetryAttempts=1` 意味着失败最多自动再试**一次**（30 秒后）；不是无限重试。两次都失败才会进 `Failed` 状态。
6. cron 默认时区是 UTC，不是用户所在时区。scheduled agent 若使用 `0 9 * * *`，中国用户视角是每天 17:00；要写 `schedule_timezone=Asia/Shanghai` 才会按本地语义换算。
7. `/daily` 不创建本地 agent；如 Ornn skill 决定创建或调度外部资源，按该 skill 的契约验证。
8. actor state 与运行时凭据 readmodel 中有 proxy-scoped key，公开截图和日志导出时不要泄露；普通 `UserAgentCatalogDocument` 不暴露 `nyx_api_key`。

---

## 16. 关键文件路径汇总（QA 报 bug / 写测试时定位用）

| 关注点 | 文件 |
|--------|------|
| Webhook ingress | `agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Relay.cs` |
| 命令解析与路由 | `agents/Aevatar.GAgents.Authoring.Lark/NyxRelayAgentBuilderFlow.cs` |
| `/daily` shortcut 改写 | `agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs` |
| Conversation LLM reply | `agents/Aevatar.GAgents.NyxidChat/ConversationReplyGenerator.cs` |
| Ornn skill bridge | `src/Aevatar.AI.ToolProviders.Ornn/`、`src/Aevatar.AI.ToolProviders.Skills/UseSkillTool.cs` |
| Agent management tool | `agents/Aevatar.GAgents.Authoring.Lark/AgentBuilderTool.cs` |
| Skill 执行 actor（已有 scheduled agents） | `agents/Aevatar.GAgents.Scheduled/SkillRunnerGAgent.cs` |
| Skill 默认参数 | `agents/Aevatar.GAgents.Scheduled/SkillRunnerDefaults.cs` |
| 注册表 actor | `agents/Aevatar.GAgents.Scheduled/UserAgentCatalogGAgent.cs` |
| 注册表投影 | `agents/Aevatar.GAgents.Scheduled/UserAgentCatalogProjector.cs` |
| 调度计算 | `agents/Aevatar.GAgents.Scheduled/ChannelScheduleCalculator.cs` / `ChannelScheduleRunner.cs` |
| 投递目标解析 | `src/Aevatar.AI.ToolProviders.AgentCatalog/AgentDeliveryTargetTool.cs` |
| NyxID HTTP 客户端 | `src/Aevatar.AI.LLMProviders.NyxId/...`、`src/Aevatar.AI.ToolProviders.NyxId/Tools/NyxIdProxyTool.cs` |
| 用户偏好（GitHub username） | `agents/Aevatar.GAgents.UserConfig/UserConfigGAgent.cs`、`src/Aevatar.Studio.Projection/CommandServices/ActorDispatchUserConfigCommandService.cs`、`src/Aevatar.Studio.Projection/QueryPorts/ProjectionUserConfigQueryPort.cs` |
| Proto 契约 | `agents/Aevatar.GAgents.Channel.Runtime/protos/channel_bot_registration.proto`、`agents/Aevatar.GAgents.Scheduled/protos/skill_runner.proto`、`agents/Aevatar.GAgents.Scheduled/protos/user_agent_catalog.proto` |
| 现有测试目录 | `test/Aevatar.GAgents.ChannelRuntime.Tests/` |

---

## 17. 待办 / 明确的"现状≠目标"清单

为防止 QA 把已知未实现项当 bug 报，下表列出**当前实现没有但 issue 里已规划**的能力：

- ~~报告内容更丰富~~ ✅ #423 §A 已由 [#458](https://github.com/aevatarAI/aevatar/pull/458) 实现（结构化 9 段、omit-if-empty、source-health footer）
- ~~渐进式投递（streaming-edit）~~ ✅ #423 §B 已实现（`SkillRunnerStreamingReplySink`：POST 占位 + PUT 增量编辑，详见 §8）
- GitHub 工具失败需明确暴露给用户（#439 修复后）
- ~~多 Lark 用户独立 `github_username`~~ ✅ 已由 [#438](https://github.com/aevatarAI/aevatar/pull/438) 修复（composite scope）；结构性升级到 `LarkUserGAgent` 仍是未来选项
- `/agent-status` 首次执行后秒级反映（#440 修复后）
- ~~失败通知通道与主投递解耦~~ ✅ #423 §C 已实现（`OutboundConfig.FailureNotificationProviderSlug` 抓住入站 channel-bot 的 slug，`TrySendFailureAsync` 优先走旁路 proxy；详见 §3 阶段⑥/⑦）
- ~~富 / 长报告超 Lark 30KB 体限的**分段**处理~~ ✅ #423 §C 已实现（`SkillRunnerOutputChunker.Split()` 按 `\n\n` 段落边界切，每段 ≤30K，`[part k/N]` 标记）
- 跨 app 部署的 `lark_receive_id` 自动更新（目前只能 `/delete-agent` 重建）

QA 对照本表与 issue 复现步骤即可在每个 PR landing 后系统性回归。

---

**文档维护原则**：本文档随 `agents/Aevatar.GAgents.NyxidChat/`、`agents/Aevatar.GAgents.Authoring.Lark/`、`agents/Aevatar.GAgents.Scheduled/` 与 Ornn skill bridge 行为变更而更新；行为不变的纯重构不更新（重构只改文件路径行号时，QA 直接用 `git log -p` 跟踪）。
