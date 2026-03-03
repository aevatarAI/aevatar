# CLAW via OpenClawModule + Workflow (CLI-first)

本文档描述 OpenClaw × Aevatar 的主路径集成方式：**优先使用 `openclaw_call` 原语直连 OpenClaw CLI**，可选兼容 HTTP/CLI connector，不依赖 MCP。  
Aevatar 保持 workflow 编排主脑，OpenClaw 作为 computer-use 执行层与 channel。

部署与运维细节见：`docs/OPENCLAW_HTTP_CLI_INTEGRATION.md`。

## 设计原则

- Aevatar 聚焦编排：多 Agent 协作、流程控制、重试/降级/审计。
- OpenClaw 聚焦执行：桌面与浏览器真实动作。
- 统一通过 `openclaw_call` 执行 OpenClaw CLI，避免为每条命令维护 connector。
- `openclaw_call` 仅允许执行 OpenClaw CLI；若二进制不在默认 PATH，可通过环境变量 `AEVATAR_OPENCLAW_CLI_PATH` 显式指定安装路径。
- 仅在确有必要时使用 `connector_call`（如跨系统 HTTP 接入）。

## 目标链路

1. OpenClaw channel 收到用户任务。
2. 请求经 Bridge（HTTP）进入 Aevatar `/api/chat`。
3. Workflow 优先使用 `openclaw_call` 走确定性链路；HTTP connector 仅用于可选桥接能力。
4. Aevatar 以事件/回执方式回传阶段状态与结果。

## Connector Profile（可选兼容）

在 `openclaw_call` 模式下，`57-68` demo 不再强依赖 connector 配置。  
若你需要兼容旧工作流或做 HTTP 桥接，可继续使用 connector profile。

### 一键自动配置（兼容旧路径）

不建议要求用户手工编辑 `connectors.json`。仓库内提供一键脚本：

```bash
bash tools/openclaw/setup_openclaw_connectors.sh
```

脚本行为：

- 自动探测 OpenClaw gateway `baseUrl`（优先环境变量，其次 `openclaw gateway status --json`）。
- 自动探测 gateway token（优先 `OPENCLAW_GATEWAY_TOKEN`，其次 `~/.openclaw/openclaw.json`）。
- 幂等 upsert `openclaw_*` connectors，不删除用户已有其它 connectors。
- 默认对旧 `connectors.json` 做时间戳备份。

可选环境变量：

- `OPENCLAW_GATEWAY_BASE_URL`：显式指定网关地址（如 `http://127.0.0.1:18789`）。
- `OPENCLAW_GATEWAY_PORT`：仅指定端口（如 `18789`）。
- `OPENCLAW_GATEWAY_TOKEN`：显式指定 bearer token。
- `OPENCLAW_SCREENSHOT_OUTPUT_DIR`：设置截图落盘目录（默认 `~/.aevatar/screenshot`）。
- `ENABLE_OPENCLAW_HTTP_CHAT=true`：启用 `openclaw_http_chat` connector（默认禁用）。
- `BACKUP_CONNECTORS_JSON=false`：关闭备份。

如果已生成 `~/.aevatar/connectors.json`，也可直接修改（旧路径）：

- `openclaw_cli_media_save.cli.environment.AEVATAR_OPENCLAW_SCREENSHOT_DIR`

```json
{
  "connectors": [
    {
      "name": "openclaw_cli_gateway_status",
      "type": "cli",
      "enabled": true,
      "timeoutMs": 15000,
      "cli": {
        "command": "openclaw",
        "fixedArguments": ["gateway", "status", "--json"],
        "allowedOperations": [],
        "allowedInputKeys": []
      }
    },
    {
      "name": "openclaw_cli_agent",
      "type": "cli",
      "enabled": true,
      "timeoutMs": 20000,
      "cli": {
        "command": "openclaw",
        "fixedArguments": ["agent"],
        "allowedOperations": ["status", "run", "resume"],
        "allowedInputKeys": []
      }
    },
    {
      "name": "openclaw_http_tools",
      "type": "http",
      "enabled": true,
      "timeoutMs": 60000,
      "http": {
        "baseUrl": "http://127.0.0.1:3000",
        "allowedMethods": ["POST"],
        "allowedPaths": ["/tools/invoke"],
        "allowedInputKeys": [
          "tool",
          "arguments",
          "session_id",
          "channel_id",
          "user_id",
          "request_id"
        ],
        "defaultHeaders": {
          "Authorization": "Bearer <OPENCLAW_GATEWAY_TOKEN>",
          "Content-Type": "application/json"
        }
      }
    },
    {
      "name": "openclaw_http_chat",
      "type": "http",
      "enabled": false,
      "timeoutMs": 60000,
      "http": {
        "baseUrl": "http://127.0.0.1:3000",
        "allowedMethods": ["POST"],
        "allowedPaths": ["/v1/chat/completions"],
        "allowedInputKeys": ["model", "messages", "temperature", "max_tokens"],
        "defaultHeaders": {
          "Authorization": "Bearer <OPENCLAW_GATEWAY_TOKEN>",
          "Content-Type": "application/json"
        }
      }
    }
  ]
}
```

说明（旧路径）：

- browser 场景建议优先 `openclaw_cli_*`（`status/snapshot/screenshot` 等）避免依赖运行时 tool 名。
- `openclaw_http_chat` 默认关闭，避免误把 OpenClaw chat 当成主控制面。
- `allowedInputKeys`、`allowedPaths` 应按最小权限收敛，避免过宽权限。

## Demo Workflows（已切换到 openclaw_call）

位于 `demos/Aevatar.Demos.Workflow/workflows/`：

- `57_claw_setup.yaml`：快速 setup 探测（gateway status + health）。
- `58_claw_ota_loop.yaml`：确定性 readiness smoke（gateway/node/browser relay）。
- `59_claw_planner.yaml`：LLM 生成 URL 列表，逐行调用 screenshot 子流程。
- `60_claw_browser_task.yaml`：URL 打开 + snapshot/screenshot 状态检查流。
- `61_claw_screenshot_save.yaml`：单 URL 截图落盘（默认 `~/.aevatar/screenshot`）。
- `62_claw_preflight_report.yaml`：完整 preflight 报告（gateway/node/browser 汇总）。
- `63_claw_open_snapshot_url.yaml`：单 URL 打开并返回 snapshot JSON。
- `64_claw_screenshot_from_url.yaml`：单 URL 截图并落盘。
- `65_claw_batch_screenshot_foreach.yaml`：多 URL（换行分隔）批量截图。
- `66_claw_resilient_browser_open.yaml`：带 `retry+delay+checkpoint` 的韧性模板。
- `67_claw_human_approval_screenshot.yaml`：人工审批门控后执行截图。
- `68_claw_channel_entry.yaml`：channel 入口流（输入校验 + preflight + screenshot 子流 + lifecycle emit）。

说明（channel-safe）：

- `58/60/61/62/63/64/66/67` 已支持 `session_id` 驱动的 browser profile 隔离：`openclaw-${session_id}`（无 `session_id` 时回退 `openclaw`）。
- 截图落盘流（`61/64/67`）已支持 `session_id` 分目录：`~/.aevatar/screenshot/${session_id}`（无 `session_id` 时回退 `~/.aevatar/screenshot`）。
- `openclaw_call` 框架层已内建 profile 缺失自愈：若 `browser start/open/... --browser-profile <name>` 返回 profile not found，会自动执行 `browser create-profile --name <name> --json` 并重试一次原命令。
- `openclaw_call` 对历史 `browser open` 参数做了兼容：`browser open --browser-profile <name> <url> --json` 会自动归一为 `browser --browser-profile <name> --json open <url>`，避免 `too many arguments for 'open'`。

## 运行方式

```bash
cd demos/Aevatar.Demos.Workflow

# 1) connector 连通性探测
dotnet run -- 57_claw_setup

# 2) OTA 主循环
dotnet run -- 58_claw_ota_loop

# 3) Planner + 子流程（LLM）
dotnet run -- 59_claw_planner

# 4) 单 URL 截图落盘
dotnet run -- 61_claw_screenshot_save

# 5) 批量 URL 截图
dotnet run -- 65_claw_batch_screenshot_foreach

# 6) channel 入口流（推荐给 OpenClaw Hook）
dotnet run -- 68_claw_channel_entry
```

## LLM Provider Sync PoC（Aevatar 主导）

新增 CLI：

```bash
# dry-run 规划：双向同步，冲突以 Aevatar 为准
aevatar openclaw sync plan --mode bidirectional --precedence aevatar --dry-run

# 应用同步：写回 ~/.aevatar/secrets.json 与 ~/.openclaw/openclaw.json
aevatar openclaw sync apply --mode bidirectional --precedence aevatar
```

PoC 规则：

- 同名 provider 冲突字段（`providerType/model/endpoint/apiKey`）采用 Aevatar 值。
- OpenClaw 独有 provider 以“新增”方式导入到 Aevatar。
- 不做 destructive delete（仅 upsert，保留额外字段）。
- `LLMProviders:Default` 作为主默认源，应用阶段会回写到 OpenClaw 默认 provider。

## 角色侧授权建议（仅 connector 路径）

若你仍在使用 connector 路径，可为执行角色声明 connector 白名单：

```yaml
roles:
  - id: operator
    connectors:
      - openclaw_cli_gateway_health
      - openclaw_cli_node_status
      - openclaw_cli_browser_start
      - openclaw_cli_browser_status
      - openclaw_cli_browser_screenshot
```

## 与 `/api/chat` 的融合建议

- OpenClaw 通过 Hook Bridge 调 Aevatar `/api/chat`，Aevatar 返回 `actorId/commandId`。
- 运行中把 `RUN_STARTED/STEP_FINISHED/RUN_FINISHED` 等事件回传 OpenClaw channel。
- 会话绑定使用稳定映射键（如 `session_id + channel_id + user_id`），不要在中间层放进程内事实缓存。
- Bridge 会把 `session/channel/user/message/correlation/idempotency` 写入 workflow 运行 metadata，并映射为 workflow 变量（如 `${session_id}`、`${channel_id}`）。
- Bridge 启用幂等后，同 `idempotencyKey` 会返回已接受结果或进行中冲突，不重复启动 run。

## 安全与治理

- 强制 token 鉴权（Bridge 与 Gateway 都要开）。
- 高风险动作前置 `human_approval` 步骤。
- 对外部调用统一设置 `timeout_ms`、`retry`、`on_error`。
- callback 建议配置 host allowlist，并设置 `CallbackMaxAttempts/CallbackRetryDelayMs`。
- 审计字段建议贯通：`correlationId`、`idempotencyKey`、`sessionKey`、`channelId`、`userId`、`commandId`、`eventId`、`sequence`。
