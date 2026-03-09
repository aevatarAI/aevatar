# Aevatar Telegram 账号通道（内建实现）

本文是 **账号通道最终版**：Aevatar 直接在进程内使用 Telegram MTProto 能力，不再依赖外部网关服务。

- 不需要自建 `http://127.0.0.1:8787` 这类中转服务
- 不需要第三方托管回调/桥接服务
- workflow 里仍然沿用 `TelegramUserBridgeGAgent`

## 1. 交互链路

`Workflow -> TelegramUserBridgeGAgent -> connector(type=telegram_user) -> Telegram`

OpenClaw 仍然在同一群里作为 bot 回复，Aevatar 侧用账号通道读取群消息并匹配结果。

## 2. 版本要求

确保你使用的是包含以下改动的代码版本：

- 新增内建 connector：`telegram_user`（type=`telegram_user`）
- `TelegramUserBridgeGAgent` 默认 connector 名：`telegram_user`
- demo workflow：`telegram_openclaw_bridge_chat`

## 3. connectors.json 配置（关键）

编辑 `~/.aevatar/connectors.json`：

```json
{
  "connectors": [
    {
      "name": "telegram_user",
      "type": "telegram_user",
      "enabled": true,
      "timeoutMs": 30000,
      "telegramUser": {
        "apiId": "12345678",
        "apiHash": "your_api_hash_from_my_telegram_org",
        "phoneNumber": "+6580102726",
        "sessionPath": "telegram-user/main.session",
        "allowedOperations": ["/sendMessage", "/getUpdates", "/ensureLogin"],
        "deviceModel": "AevatarHost",
        "systemVersion": "macOS",
        "appVersion": "aevatar-1.0",
        "systemLangCode": "zh-hans",
        "langCode": "zh"
      }
    }
  ]
}
```

字段说明：

- `type` 必须是 `telegram_user`
- `apiId` / `apiHash` 来自 [my.telegram.org/apps](https://my.telegram.org/apps)
- `sessionPath` 可以是相对 `~/.aevatar/` 的路径，也可以绝对路径
- `allowedOperations` 建议包含 `"/ensureLogin"`，用于登录握手步骤

### 3.1 首次登录验证码（两阶段，无需重启）

`telegram_openclaw_bridge_chat` 现在是两阶段登录：

1. 先执行 `/ensureLogin` 触发 Telegram 发验证码
2. 收到 `PHONE_CODE` 后，workflow 自动进入 `human_input` 让你输入验证码
3. 输入后再次 `/ensureLogin` 完成登录

如果账号开启 2FA（二次密码），workflow 会在 `SESSION_PASSWORD_NEEDED` 时继续进入 `human_input` 收集 2FA 密码。

> 关键点：验证码是“先触发发码，再输入”，不需要 `aevatar app restart`。

如果你希望手工兜底，仍可通过环境变量提供 2FA 密码：

```bash
export AEVATAR_TELEGRAM_USER_2FA_PASSWORD="your_2fa_password"
```

## 4. config.json 默认参数

编辑 `~/.aevatar/config.json`：

```json
{
  "WorkflowRuntimeDefaults": {
    "telegram.chat_id": "-1001234567890",
    "telegram.openclaw_bot_username": "openclaw_bot"
  }
}
```

也支持嵌套写法：

```json
{
  "WorkflowRuntimeDefaults": {
    "telegram": {
      "chat_id": "-1001234567890",
      "openclaw_bot_username": "openclaw_bot"
    }
  }
}
```

## 5. workflow 用法（无需改动 demo）

文件：`tools/Aevatar.Tools.Cli/workflows/telegram_openclaw_bridge_chat.yaml`

关键参数已经是账号通道模式：

- `agent_type: TelegramUserBridgeGAgent`
- `connector: telegram_user`
- 登录预热步骤：`/ensureLogin`（用于先触发发码）
- 人工步骤：`collect_telegram_verification_code` / `collect_telegram_2fa_password`（`human_input`）
- `operation: /sendMessage`（发送）
- `operation: /waitReply`（等待；bridge 内部会轮询 connector 的 `/getUpdates`）
- 不再要求“先设验证码环境变量再重启”

## 6. 启动与验证

```bash
aevatar app restart
```

然后在 app 里运行 `telegram_openclaw_bridge_chat`。

预期：

1. Aevatar 账号在群里发出 `@openclaw_bot` 请求
2. OpenClaw bot 在群里回复
3. `wait_openclaw_group_stream` 命中回复并收敛

## 7. 推荐超时参数

发送步骤：

- `telegram.timeout_ms: "12000"`
- `timeout_ms: "30000"`

等待步骤：

- `wait_timeout_ms: "120000"`
- `poll_timeout_sec: "8"`
- `start_from_latest: "true"`
- `collect_all_replies: "true"`（收集同一轮回复的多条消息）
- `settle_polls_after_match: "2"`（命中后再轮询 2 次，尽量收齐分段回复）
- `correlation_contains: "${telegram.correlation_contains}"`（可选；留空则按发送者匹配）

## 8. 常见问题

### 8.1 `telegram connector 'telegram_user' not found`

- `connectors.json` 里没配 `name: telegram_user`
- `type` 写错（必须 `telegram_user`）
- 改完配置没重启

### 8.2 `telegram_user init failed`

- `apiId` / `apiHash` 配置错误
- 首次登录缺少验证码或 2FA 密码
- 本机网络无法连 Telegram
- 报错 `main.session ... is being used by another process`：
  - 先确保同一时间只有一个 `aevatar app` 进程
  - 如果刚升级过代码，先重启 app 进程再试（旧进程可能仍占用旧句柄）

### 8.3 `PHONE_CODE_INVALID`

- 你提供的是过期或错误验证码（验证码一次性且有效期短）
- 推荐做法：
  1. 直接重新运行 workflow（会再次触发发码）
  2. 在 `human_input` 步骤输入**最新验证码**
- 不需要重启 `aevatar app`
- 兼容兜底：也可以临时用环境变量

```bash
export AEVATAR_TELEGRAM_USER_VERIFICATION_CODE="最新验证码"
```

- 验证码复制时如果带空格，系统现在会自动去空格；但旧验证码仍会失败

### 8.4 一直等不到 OpenClaw 回复

- `telegram.openclaw_bot_username` 写错（不要带 `@`）
- 如果配置了 `correlation_contains`，但 OpenClaw 回复里没带该标记，会被过滤
- `telegram.chat_id` 配错

### 8.5 发送失败：cannot resolve chat_id

- 当前账号未加入该群
- 群刚迁移/变更，`chat_id` 过期
- 用该账号先在客户端里打开目标群，再重试

## 9. 安全建议

- `apiHash`、2FA 密码、session 文件都不要入库
- session 文件放在 `~/.aevatar/telegram-user/` 并限制权限
- 建议为自动化账号单独准备 Telegram 账号，不与个人主号混用

## 10. 一次性自检

1. `connectors.json` 已有 `type: telegram_user`
2. `config.json` 已有 `telegram.chat_id` 与 `telegram.openclaw_bot_username`
3. 首次登录时：按 workflow 提示输入验证码（必要时再输入 2FA）（或 session 已存在）
4. `aevatar app restart` 后运行 demo
5. 群里看到 Aevatar 发言 + OpenClaw 回复 + workflow 收敛

通过以上 5 条，即账号通道打通。
