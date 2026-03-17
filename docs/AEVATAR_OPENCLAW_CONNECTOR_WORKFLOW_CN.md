# AEVATAR 通过 Connector 调 OpenClaw 并在 Telegram 群回消息

目标：不改 AEVATAR 框架代码，只用 `connectors + workflow yaml` 把 OpenClaw 作为外部能力接入。
本文档按“可发版、可给第三方用户复用”设计，不依赖本地仓库绝对路径。

## 1. 已提供文件

- `tools/connectors/openclaw_connector.sh`
- `workflows/connectors/openclaw.telegram.connectors.json`
- `workflows/openclaw_group_reply.yaml`
- `workflows/openclaw_group_reply_two_step.yaml`

## 2. 发布版目录约定（推荐）

建议把 connector 脚本以固定文件名发布到宿主机，例如：

- Linux/macOS: `/opt/aevatar/bin/aevatar-openclaw-connector`

安装示例：

1. `sudo mkdir -p /opt/aevatar/bin`
2. `sudo cp tools/connectors/openclaw_connector.sh /opt/aevatar/bin/aevatar-openclaw-connector`
3. `sudo chmod +x /opt/aevatar/bin/aevatar-openclaw-connector`

> 如果你使用其他路径，只需要改 connector 配置里的 `AEVATAR_OPENCLAW_CONNECTOR_CMD`。

## 3. 最小接入步骤

1. 复制 connector 配置到运行用户目录：
   - `cp workflows/connectors/openclaw.telegram.connectors.json ~/.aevatar/connectors.json`
2. 修改 `~/.aevatar/connectors.json`：
   - `AEVATAR_OPENCLAW_CONNECTOR_CMD`：改成部署机真实路径
   - `OPENCLAW_TG_CHAT_ID`：改成目标 Telegram 群 `chat_id`
   - 可选：`OPENCLAW_AGENT_ID`（默认 `main`）
3. 启动/重启 AEVATAR Host，让 connector 重新加载。
4. 调用 workflow：`openclaw_group_reply`

## 4. 两个 workflow 的区别

- `openclaw_group_reply.yaml`：
  - 单步 connector
  - OpenClaw 直接处理并回 Telegram 群
- `openclaw_group_reply_two_step.yaml`：
  - 两步 connector
  - 第一步 OpenClaw 生成，第二步独立发送
  - 便于后续在 AEVATAR 内插入审核/过滤步骤

## 5. 输入格式

`openclaw_group_reply` 默认把当前 workflow 输入作为 prompt 转发给 OpenClaw。  
也支持 JSON 输入（可选字段）：

```json
{
  "prompt": "请总结这段文本",
  "chat_id": "-100xxxxxxxxxx",
  "prefix": "AEVATAR_STREAM_REPLY",
  "agent_id": "main",
  "timeout_seconds": 120
}
```

如果没传 `chat_id`，会使用 connector 环境变量里的 `OPENCLAW_TG_CHAT_ID`。

## 6. 给第三方用户发布时的注意事项

1. 把 `openclaw_connector.sh` 当作独立工件发布，不要求用户有 AEVATAR 源码目录。
2. 在部署文档中明确三项运行时依赖：
   - `openclaw` 命令可用
   - `bash` 可用
   - Connector 命令路径与 `AEVATAR_OPENCLAW_CONNECTOR_CMD` 一致
3. 不要在模板里写任何个人路径（例如 `/Users/<name>/...`）。
4. 如果多租户/多群场景，建议为每个租户生成一份 connectors 配置，隔离 `OPENCLAW_TG_CHAT_ID`。
