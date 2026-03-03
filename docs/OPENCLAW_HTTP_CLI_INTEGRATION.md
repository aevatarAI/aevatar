# OpenClaw HTTP + CLI Integration Guide

本文档是 `docs/CLAW.md` 的实施补充，聚焦可部署细节：

- OpenClaw → Aevatar Hook Bridge
- 双向回执协议
- 降级策略矩阵
- 审计字段规范
- 验收清单

## 1. 部署前提

- OpenClaw CLI 已安装：`openclaw`
- OpenClaw Gateway 已启动（示例：`http://127.0.0.1:3000`）
- Aevatar Workflow Host API 已启动，暴露：
  - `POST /api/chat`
  - `POST /api/openclaw/hooks/agent`
  - `POST /hooks/agent`（同一 bridge 的短路径）

## 2. Aevatar 端 Bridge 配置

在 Host 配置中增加 `OpenClawBridge`：

```json
{
  "OpenClawBridge": {
    "Enabled": true,
    "RequireAuthToken": true,
    "AuthHeaderName": "X-OpenClaw-Bridge-Token",
    "AuthToken": "replace-with-strong-secret",
    "DefaultWorkflow": "68_claw_channel_entry",
    "EnableIdempotency": true,
    "IdempotencyTtlHours": 24,
    "CallbackTimeoutMs": 5000,
    "CallbackMaxAttempts": 2,
    "CallbackRetryDelayMs": 300,
    "CallbackAllowedHosts": ["openclaw.local"],
    "CallbackAuthHeaderName": "Authorization",
    "CallbackAuthScheme": "Bearer"
  }
}
```

## 3. OpenClaw Hook 入站请求（到 Aevatar）

请求示例：

```http
POST /hooks/agent
X-OpenClaw-Bridge-Token: replace-with-strong-secret
Content-Type: application/json
```

```json
{
  "prompt": "Open browser and summarize latest release notes.",
  "sessionId": "oc-session-001",
  "channelId": "slack#ops",
  "userId": "u-1001",
  "messageId": "m-20260226-001",
  "workflow": "59_claw_planner",
  "callbackUrl": "https://openclaw.local/bridge/callback",
  "callbackToken": "callback-secret",
  "metadata": {
    "tenant": "prod-cn",
    "source": "openclaw-hook"
  }
}
```

返回示例（202）：

```json
{
  "accepted": true,
  "actorId": "oc-4ed9f0f8d7f8b0b1a8fd0f2e6ac0",
  "commandId": "m-20260226-001",
  "workflow": "59_claw_planner",
  "correlationId": "m-20260226-001",
  "idempotencyKey": "slack#ops:u-1001:m-20260226-001",
  "sessionKey": "oc-session-001",
  "channelId": "slack#ops",
  "userId": "u-1001"
}
```

## 4. 双向回执协议

Bridge 会向 `callbackUrl` 回传事件。默认事件类型：

- `aevatar.workflow.started`
- `aevatar.workflow.frame`
- `aevatar.workflow.completed`
- `aevatar.workflow.failed`
- `aevatar.workflow.rejected`

回执结构：

```json
{
  "eventId": "slack#ops:u-1001:m-20260226-001:2",
  "sequence": 2,
  "type": "aevatar.workflow.frame",
  "timestamp": 1772050000000,
  "correlationId": "m-20260226-001",
  "idempotencyKey": "slack#ops:u-1001:m-20260226-001",
  "sessionKey": "oc-session-001",
  "channelId": "slack#ops",
  "userId": "u-1001",
  "messageId": "m-20260226-001",
  "actorId": "oc-4ed9f0f8d7f8b0b1a8fd0f2e6ac0",
  "commandId": "m-20260226-001",
  "workflowName": "59_claw_planner",
  "metadata": {
    "tenant": "prod-cn",
    "source": "openclaw-hook"
  },
  "payload": {
    "type": "STEP_FINISHED",
    "stepName": "execute_action",
    "code": null,
    "message": null
  }
}
```

字段约束（建议 OpenClaw callback 消费端按此处理）：

- `sequence`：同一次 hook 执行内严格递增（1,2,3...）。
- `eventId`：`${idempotencyKey}:${sequence}`，可直接用于去重。
- `actorId` / `commandId`：`started` 事件后持续透传到后续 `frame/completed/failed` 事件，保证链路连续性。
- `CallbackAllowedHosts` 非空时，仅允许向白名单 host 发送 callback。
- callback 发送支持 `CallbackMaxAttempts` + `CallbackRetryDelayMs` 重试，不阻断主流程。

## 4.1 幂等与重复请求策略

Bridge 会对同一 `idempotencyKey` 做治理：

- 首次请求：创建幂等记录并启动 run，返回 `202 accepted`。
- 重复请求（进行中）：返回 `409 IDEMPOTENCY_IN_PROGRESS`。
- 重复请求（已开始/已完成）：返回 `202 accepted`，`replayed=true`，并复用同一 `actorId/commandId`。
- 重复请求（历史失败）：返回 `409 IDEMPOTENCY_PREVIOUSLY_FAILED`。

说明：

- 幂等状态通过持久化抽象存储，不依赖中间层进程内事实字典。
- 幂等记录默认 TTL 由 `IdempotencyTtlHours` 控制。

## 5. 降级策略（CLI-first）

| 场景 | 主路径 | 降级路径 | 结果策略 |
|---|---|---|---|
| OpenClaw runtime 未就绪 | `openclaw_call`（gateway/node/browser） | `fallback_step: setup_guide` | 快速返回安装/配置指引 |
| 输入 URL 无效或打开失败 | `openclaw_call browser open ${input}` | `open_default_url`（`https://example.com`） | 流程继续并产出可观测输出 |
| Bridge 回执 callback 失败 | callback POST | 仅本地日志记录 | 不阻断 workflow 主执行 |

当前 demo 中的降级点（示例）：

- `58_claw_ota_loop.yaml`：`probe_gateway_health/probe_node_status/probe_browser_status` 失败 -> `browser_not_ready`
- `60_claw_browser_task.yaml`：`open_target_url` 失败 -> `open_default_url`
- `61_claw_screenshot_save.yaml`：`open_demo_page` 失败 -> `open_default_page`
- `63/64/66/67`：URL 打开失败均回退到 `open_default_url`
- `68_claw_channel_entry.yaml`：入口级校验 + preflight + screenshot 子流编排，失败回退 `setup_guide`

## 6. 审计字段规范（最小集合）

端到端统一字段建议：

- `correlation_id`：跨系统主追踪键（优先 messageId）
- `idempotency_key`：去重与重试幂等键
- `session_id/session_key`：会话绑定键
- `channel_id`：来源渠道
- `user_id`：操作者标识
- `actor_id`：Aevatar 执行 actor
- `command_id`：Aevatar 命令 ID
- `workflow_name`：执行流程名
- `event_id`：回执事件唯一键（`idempotency_key + sequence`）
- `sequence`：回执有序递增序号

其中 `correlation_id/idempotency_key/session_key/channel_id/user_id/actor_id/command_id/event_id/sequence` 已在 bridge callback 回执中携带。

此外，Bridge 已把 `session/channel/user/message/correlation/idempotency/callback_url` 透传到 workflow metadata，并可直接在 YAML 中通过变量使用（如 `${session_id}`、`${channel_id}`）。

## 7. 验收清单

- [ ] 不安装 MCP 组件，`57-68` 可按 CLI-first 路径运行
- [ ] OpenClaw 可通过 `/hooks/agent` 或 `/api/openclaw/hooks/agent` 触发 Aevatar 执行
- [ ] 未带或错误 token 时，Bridge 返回 `401 UNAUTHORIZED`
- [ ] 相同 `sessionId` 请求映射到稳定 `actorId`
- [ ] 相同 `idempotencyKey` 不会重复执行（返回 replay 或 conflict）
- [ ] callback 可收到 started/frame/completed（或 failed/rejected）回执
- [ ] HTTP 主链路失败时触发 CLI fallback，流程不中断或可控失败
- [ ] 日志/回执中可检索统一审计字段（至少 correlation/idempotency/session/channel/user）
