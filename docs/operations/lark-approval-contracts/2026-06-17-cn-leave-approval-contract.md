# 中国区请假审批 Lark 契约

日期：2026-06-17
状态：pending-live-values
范围：仅作为 GitHub issue #2178 的契约文档。

## 目的

本文档记录 Aevatar 通过现有 NyxID 代理创建中国区请假审批实例之前所需的 Lark 后端契约。本文档不引入 workflow、registry、新 tool、proto、actor 或 NyxID 变更。审批实例创建继续使用当前 NyxID 已连接服务入口：

```text
provider_slug=api-lark-bot
POST /open-apis/approval/v4/instances?user_id_type=open_id
```

2026-06-17 的本地 NyxID 服务发现已确认存在可用的 `api-lark-bot` 绑定。只读审批定义探测仍返回 Lark 授权错误，因此下方审批定义取值必须由 Lark 租户管理员或应用所有者确认应用权限，从 Lark 管理后台或开发者控制台读取真实审批定义，并在目标租户内执行创建实例的 smoke。

## 运行时边界

| 项目 | 契约 |
|---|---|
| 凭据所有者 | NyxID 拥有 Lark 应用凭据与租户 token 交换。 |
| Aevatar 传输方式 | 通过 NyxID 代理调用 Lark，使用 `provider_slug=api-lark-bot`。 |
| Lark 端点 | `/open-apis/approval/v4/instances?user_id_type=open_id`。 |
| 调用方身份类型 | 使用 `open_id`；不得切换为 user ID、union ID、email 或名称查找。 |
| 审批定义来源 | Lark 审批后端或管理端定义，不是 Aevatar fixture 或生成卡片。 |
| 当前 Aevatar 范围 | 仅限契约文档与 smoke 证据。 |

## 待补充线上值

以下字段有意不进行臆造。只能从真实 Lark 审批定义与 smoke 运行结果中填写。

| 字段 | 状态 | 取值 / 说明 |
|---|---|---|
| `approval_code` | pending-live-values | TODO(lark-admin)：从中国区请假审批定义复制。 |
| 租户 / 应用 | pending-live-values | TODO(lark-admin)：记录脱敏后的租户或应用标签，不记录密钥。 |
| 申请人 `open_id` | pending-live-values | TODO(lark-admin)：使用允许创建请假申请的测试员工。 |
| 请假类型 widget id | pending-live-values | TODO(lark-admin)：从审批定义复制精确 widget id。 |
| 请假类型 option id | pending-live-values | TODO(lark-admin)：复制 smoke 使用的精确 option id。 |
| 开始日期 / 时间 widget id | pending-live-values | TODO(lark-admin)：复制精确 widget id。 |
| 结束日期 / 时间 widget id | pending-live-values | TODO(lark-admin)：复制精确 widget id。 |
| 时长 widget id | pending-live-values | TODO(lark-admin)：复制精确 widget id 与单位语义。 |
| 事由 widget id | pending-live-values | TODO(lark-admin)：复制精确 widget id。 |
| 附件 widget id | pending-live-values | TODO(lark-admin)：仅在审批定义要求附件时记录。 |
| 直属上级路由节点 | pending-live-values | TODO(lark-admin)：确认审批流使用 Lark 直属上级自动路由，或记录精确节点配置。 |
| 其他路由节点 | pending-live-values | TODO(lark-admin)：如存在 HR、行政、财务或兜底节点则记录。 |
| 事件订阅 | pending-live-values | TODO(lark-admin)：记录已启用的审批事件与接收系统。 |
| 脱敏 `instance_code` | pending-live-values | TODO(lark-admin)：在创建实例 smoke 成功后填写。 |

## 预期创建实例请求形态

待补充线上值可用后，使用以下内容作为 smoke 模板。将每个 `TODO(lark-admin)` 值替换为精确的 Lark 定义值。实际请求中不得保留占位符。

```json
{
  "approval_code": "TODO(lark-admin)",
  "user_id": "TODO(lark-admin-open-id)",
  "form": [
    {
      "id": "TODO(lark-admin-leave-type-widget-id)",
      "value": "TODO(lark-admin-leave-type-option-id)"
    },
    {
      "id": "TODO(lark-admin-start-widget-id)",
      "value": "TODO(lark-admin-start-value)"
    },
    {
      "id": "TODO(lark-admin-end-widget-id)",
      "value": "TODO(lark-admin-end-value)"
    },
    {
      "id": "TODO(lark-admin-duration-widget-id)",
      "value": "TODO(lark-admin-duration-value)"
    },
    {
      "id": "TODO(lark-admin-reason-widget-id)",
      "value": "Aevatar contract smoke for China leave approval"
    }
  ]
}
```

预期代理命令形态：

```bash
nyxid proxy request api-lark-bot \
  "/open-apis/approval/v4/instances?user_id_type=open_id" \
  -m POST \
  -H "Content-Type: application/json; charset=utf-8" \
  -d @/path/to/redacted-cn-leave-approval-smoke.json
```

预期成功证据：

| 证据 | 状态 | 说明 |
|---|---|---|
| HTTP status | pending-live-values | TODO(lark-admin)：记录不含密钥的成功响应。 |
| `instance_code` | pending-live-values | TODO(lark-admin)：脱敏中间字符，例如 `2026********1234`。 |
| 申请人 open id | pending-live-values | TODO(lark-admin)：脱敏中间字符。 |
| Approval code | pending-live-values | TODO(lark-admin)：如非密钥则记录精确值；否则按一致规则脱敏。 |
| Timestamp | pending-live-values | TODO(lark-admin)：记录 ISO-8601 时间戳与时区。 |

## 事件订阅契约

生产调用方依赖该审批之前，Lark 应用必须为负责下游观察的接收方启用审批生命周期事件。请在此处记录精确启用的事件与接收方：

| 事件 / 接收方 | 状态 | 取值 / 说明 |
|---|---|---|
| 实例发起事件 | pending-live-values | TODO(lark-admin)。 |
| 实例通过事件 | pending-live-values | TODO(lark-admin)。 |
| 实例拒绝事件 | pending-live-values | TODO(lark-admin)。 |
| 实例撤销事件 | pending-live-values | TODO(lark-admin)。 |
| 事件接收方 | pending-live-values | TODO(lark-admin)：NyxID channel relay、Aevatar webhook，或其他具名接收方。 |

如果目标 Lark 应用无法向受控接收方发送生命周期事件，则 Aevatar 内部不得将该审批视为可观察完成。

## Smoke 结果

状态：blocked

原因：当前 worktree 存在可用的 `api-lark-bot` 代理入口，但本次实现没有真实 Lark 审批定义值：`approval_code`、表单 widget ids、option ids、直属上级路由节点详情、事件订阅接收方，以及安全的测试申请人 `open_id`。在缺少这些值的情况下创建审批实例，要么会被 Lark 拒绝，要么会生成不可审阅的线上审批。

只读探测：

```bash
nyxid proxy request api-lark-bot \
  "/open-apis/approval/v4/approvals?page_size=100&locale=zh-CN" \
  -m GET
```

结果：Lark 返回 `code=99991663`，`msg="Invalid access token for authorization. Please make a request with token attached."`。通过 `--via-service` 固定已发现的组织级 `api-lark-bot` 服务时，结果相同。未创建任何审批实例。

因此本文档标记为 `pending-live-values`，线上创建实例 smoke 在 Lark 管理员补齐缺失契约事实并确认 Lark 应用拥有审批 API 权限之前保持 blocked。

## 验收门禁

仅当以下条件全部满足后，本契约才可以标记为 ready：

1. 本文档中移除 `pending-live-values`。
2. 从线上 Lark 后端填写 `approval_code`、widget ids、option ids、路由节点、事件订阅与脱敏 smoke 证据。
3. smoke 已通过 `api-lark-bot` 使用 `user_id_type=open_id` 创建一个实例。
4. 脱敏后的 `instance_code` 已记录在本文档中。
5. 满足本 issue 不需要任何 Aevatar workflow、tool、registry、actor、proto 或 NyxID 变更。
