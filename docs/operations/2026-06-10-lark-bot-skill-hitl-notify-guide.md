# Lark Bot Skill 卡片交互 HITL 与 Notify 写法

本文说明 Ornn skill 如何在 Aevatar 的 Lark/飞书 bot 里完成两类交互：

- 轻量卡片交互：skill 直接调用 `reply_with_interaction`，让用户在当前对话里点按钮或提交表单。
- 正式 workflow 交互：skill 携带 workflow YAML，通过 `human_approval` / `human_input` / `secure_input` 和 `notify` 把卡片投递到 Lark，并由运行时负责恢复流程或发送通知。

优先选正式 workflow 路线做生产 HITL；轻量卡片适合一次性确认、补信息、演示或低风险对话。

## 基本前提

Lark 侧必须已配置事件：

- `im.message.receive_v1`
- `card.action.trigger`

Aevatar 侧有两类配置要分清：

- `channel_registrations`：配置 inbound bot 回调，让用户消息和卡片点击进 Aevatar。
- `agent_delivery_targets`：配置 outbound 投递目标，让 workflow 的 HITL / Notify 卡片能主动发到某个 Lark 会话。

workflow HITL 和 Notify 使用 `delivery_target_id=<agent_id>`，该 `agent_id` 必须能通过 `agent_delivery_targets` 解析到真实 Lark 会话。

## Skill 包结构

推荐 Ornn skill 使用 mixed 类型：

```markdown
---
name: release-gate
description: "Use from Lark bot to run a release gate with human approval and notification cards."
metadata:
  category: mixed
  tag:
    - lark
    - hitl
    - notify
    - approval
  output-type: text
  runtime:
    - "python"
  tool-list:
    - "reply_with_interaction"
    - "aevatar_start_workflow"
    - "code_execute"
version: "1.0"
---

# Release Gate

Use when the user says deploy, release, approve rollout, 上线, 部署, or /deploy.

Follow the workflow route when a scope/workflow runtime is available. Use the lightweight card route only for one-turn ad hoc confirmation.
```

关键点：

- `description` 写清触发场景，Lark bot 靠它判断是否加载 skill。
- `tool-list` 写出会用到的工具。轻量卡片需要 `reply_with_interaction`；正式流程需要 `aevatar_start_workflow`；检查脚本可用 `code_execute`。
- 如果有 workflow YAML，通过 `ornn_publish_skill.workflow_yamls` 发布；包内路径会归一到 `workflows/{workflowId}.yaml`。这些 YAML 是模板/导入源，不是 scope 内已发布的可运行 workflow；需要先通过 Scope Workflow 命令链路挂载/导入，再启动运行。`assets/*.yaml` 只作为历史包读取兼容，不作为新发布路径。

## 轻量卡片交互

轻量路线就是：skill 先做检查，然后调用 `reply_with_interaction` 发卡片。卡片点击会作为下一条 `card_action` 入站消息继续对话。

示例：

```markdown
## Lightweight Lark approval

1. Run preflight with `code_execute`.
2. Call `reply_with_interaction`:
   - title: "🚀 确认部署到 staging？"
   - body: preflight output
   - fields:
     - "Build" -> "✅"
     - "Tests" -> "✅"
     - "Breaking Changes" -> "None"
   - actions:
     - action_id: "deploy-approve", label: "✅ 确认部署", value: "staging", style: "primary"
     - action_id: "deploy-reject", label: "❌ 取消", value: "staging", style: "danger"

When the next user message is `[card_action] deploy-approve: staging`, run the deploy step.
When the next user message is `[card_action] deploy-reject: staging`, reply "❌ Deployment cancelled."
```

`reply_with_interaction` 参数会被映射成 Lark 2.0 interactive card：

- `title`：卡片 header。
- `body`：正文 markdown。
- `fields`：正文里的字段列表。
- `actions`：按钮、选择框、输入框或表单提交。
- `style=primary`：主按钮。
- `style=danger`：危险按钮。

按钮点击后的普通续跑约定：

- 普通 button 会变成类似：`[card_action] deploy-approve: staging`
- 没有 `value` 的 button 会变成：`[card_action] deploy-reject`
- form submit 会把字段变成多行文本，例如：

```text
environment: staging
reason: hotfix validated
```

所以 skill 必须显式写“收到这些 card_action 文本后下一步做什么”。不要假设按钮本身绑定了后端代码。

轻量路线的限制：

- 它依赖下一轮 LLM 继续理解上下文，不是持久化 workflow gate。
- 长时间等待、跨会话恢复、审计、超时处理不稳。
- 不适合高风险生产审批。

## 正式 HITL Workflow

正式 HITL 适合审批、人工输入、敏感输入、长流程暂停恢复。skill 可以携带 workflow YAML 模板，但模板必须先通过 Scope Workflow 命令链路挂载/导入，之后再按 `workflow_id` 启动已挂载的 scope workflow。

启动已挂载 workflow 的工具参数形状：

```json
{
  "workflow_id": "release-gate",
  "inputs": {
    "json": "{\"environment\":\"staging\",\"delivery_target_id\":\"agent-release-ops\"}"
  },
  "wait": "stream"
}
```

只有在 Scope Workflow 挂载/导入不可用的显式降级场景，才把 inline `workflow_yamls` 作为临时导入/草稿运行输入；不要把 Ornn 包内 YAML 当成已发布、页面可见、可长期复用的 scope workflow 身份。

`human_approval` 示例：

```yaml
name: release-gate
description: Staging release gate with Lark approval

steps:
  - id: preflight
    type: tool_call
    parameters:
      tool: code_execute
      language: python
      code: |
        from datetime import datetime
        print(f"Preflight check: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
        print("Build passed")
        print("Tests green")

  - id: approval
    type: human_approval
    parameters:
      prompt: "确认部署到 staging？"
      timeout: "1800"
      on_reject: "fail"
      delivery_target_id: "${input.delivery_target_id}"
      interaction_spec:
        title: "🚀 确认部署到 staging？"
        body: "Preflight 已通过，请确认是否继续。"
        fields:
          - title: "Environment"
            text: "staging"
          - title: "Build"
            text: "✅"
          - title: "Tests"
            text: "✅"
        actions:
          - kind: form_submit
            action_id: approve
            label: "✅ 确认部署"
            style: primary
            approval_decision: approve
          - kind: form_submit
            action_id: reject
            label: "❌ 取消"
            style: danger
            approval_decision: reject

  - id: deploy
    type: tool_call
    parameters:
      tool: code_execute
      language: python
      code: |
        print("Deploying to staging...")
        print("Deployment complete.")
```

`human_input` 示例：

```yaml
steps:
  - id: ask_release_note
    type: human_input
    parameters:
      prompt: "请输入发布说明"
      variable: release_note
      timeout: "1800"
      on_timeout: "fail"
      delivery_target_id: "${input.delivery_target_id}"
      interaction_spec:
        title: "📝 发布说明"
        body: "请填写本次发布说明。"
        actions:
          - kind: text_input
            action_id: user_input
            label: "发布说明"
            placeholder: "例如：修复登录回调问题"
          - kind: form_submit
            action_id: submit
            label: "提交"
            style: primary
```

正式 workflow 的点击恢复逻辑由运行时处理：

- 卡片按钮携带 `actor_id + run_id + step_id + approved/user_input/...` 等强类型恢复载荷。
- Lark `card.action.trigger` 经 NyxID relay 回到 Aevatar。
- Aevatar 解析为 `CardActionSubmission`，命中 workflow resume 路由后恢复对应 workflow run。
- 不需要 skill 自己解析 `[card_action] ...`。

## Notify Workflow

`notify` 用于主动发一张通知卡，不等待用户恢复流程。它要求：

- 必须有 `delivery_target_id`。
- 必须且只能提供一个 payload：`interaction_spec` 或 `interaction_template_spec`。
- 发送后步骤会以 `notification_status=accepted` 完成；它只表示通知已被接受投递，不表示用户已读。

普通卡片通知：

```yaml
steps:
  - id: notify_release_started
    type: notify
    parameters:
      delivery_target_id: "${input.delivery_target_id}"
      interaction_spec:
        title: "📣 staging 部署已开始"
        body: "Release gate 已通过，部署流程开始执行。"
        fields:
          - title: "Environment"
            text: "staging"
          - title: "Operator"
            text: "${input.operator}"
        actions:
          - kind: link
            action_id: open_dashboard
            label: "打开监控"
            value: "https://dashboard.aevatar.ai"
```

Lark 模板卡通知：

```yaml
steps:
  - id: notify_with_template
    type: notify
    parameters:
      delivery_target_id: "${input.delivery_target_id}"
      interaction_template_spec:
        template_id: "AAq2X..."
        template_variable:
          environment: "staging"
          status: "started"
```

Notify 适合：

- 流程开始、完成、失败通知。
- 审批通过后广播状态。
- 给监控链接、报告链接、下一步入口。

Notify 不适合：

- 等待用户决定。
- 收集表单输入。
- 需要超时、拒绝、恢复语义的流程。

这些场景用 `human_approval` / `human_input`。

## 写 Skill 时的推荐模板

在 `SKILL.md` 里把流程写成明确分支：

```markdown
## Execution

1. If a workflow-capable Aevatar scope is available, mount/import the bundled workflow template through the Scope Workflow command path, then start the mounted scope workflow after the accepted receipt/readmodel propagation contract allows it.
   - Use inline `workflow_yamls` only as an explicit fallback when mounting is unavailable.
   - Require `delivery_target_id`; if missing, ask for it with `reply_with_interaction`.
2. If no workflow scope is available, use lightweight `reply_with_interaction`.
3. For lightweight card callbacks:
   - `[card_action] deploy-approve: staging` -> run deployment.
   - `[card_action] deploy-reject: staging` -> cancel.
4. Do not call `lark_messages_reply` for the current inbound Lark relay turn. Return final text or use `reply_with_interaction`; the channel runtime sends it through the relay token.
```

如果 skill 需要先补信息，先发一个最小卡片：

```json
{
  "title": "部署参数",
  "body": "请选择环境并填写原因。",
  "actions": [
    {
      "kind": "select",
      "action_id": "environment",
      "label": "环境",
      "options": [
        { "label": "staging", "value": "staging" },
        { "label": "prod", "value": "prod" }
      ]
    },
    {
      "kind": "text_input",
      "action_id": "reason",
      "label": "原因",
      "placeholder": "为什么要部署？"
    },
    {
      "kind": "form_submit",
      "action_id": "submit_deploy_params",
      "label": "提交",
      "style": "primary"
    }
  ]
}
```

## 常见坑

- 不要把卡片 JSON 当普通文本发出去。skill 应调用 `reply_with_interaction` 或走 workflow `interaction_spec`。
- 不要在当前 Lark relay 入站回合里调用 `lark_messages_reply`。普通回答直接给 final text；交互卡用 `reply_with_interaction`。
- 不要把高风险审批只写成轻量按钮。生产审批优先 workflow `human_approval`。
- `notify` 不是 HITL；它不会等待用户点击。
- `delivery_target_id` 不是 Lark chat id。它是 Aevatar 的投递目标 ID，需要先通过 `agent_delivery_targets` 绑定到 Lark 会话。
- `human_input` / `human_approval` 不使用 `interaction_template_spec`；模板卡通知只给 `notify` 用。
- 轻量按钮点击后只是下一轮 LLM 输入，必须在 skill 中写清 `[card_action] action_id` 的处理规则。
- 若卡片要收集输入或下拉选择，必须使用 `form_submit`，否则表单字段不会作为完整提交进入下一轮。
