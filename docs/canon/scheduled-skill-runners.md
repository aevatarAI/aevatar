---
title: "Scheduled Skill Runners"
status: active
owner: eanzhao
---

# Scheduled Skill Runners

本文固化 scheduled skill runner 的触发与所有权边界。`SkillRunnerGAgent` 是 runner 配置、trigger source declaration、delivery ledger、执行结果事实的唯一权威 actor。

## External Trigger Admission

外部触发只通过 `ISkillRunnerCommandPort.AdmitExternalTriggerAsync` 进入已有 runner。该端口只检查 runner 是否存在并投递 `AdmitSkillRunnerExternalTriggerCommand`；它不创建 runner、不校验 source 是否声明或启用、不读 read model、不启动 projection priming，也不维护 delivery cache。

同步返回的 `SkillRunnerExternalTriggerAdmissionReceipt` 只表示 `accepted for dispatch`。它提供稳定 `command_id / correlation_id / admission_id / source_id / delivery_id`，不得暗示 source validation、execution committed 或 read model observed。

## Runner-Owned Source Facts

创建 runner 时，`InitializeSkillRunnerCommand.external_trigger_sources` 声明允许的 external trigger source。source 的启用状态、未知 source、disabled source、duplicate delivery 都由 `SkillRunnerGAgent` 在 actor turn 内判定，并通过 committed events 表达：

- `SkillRunnerExternalTriggerAdmittedEvent`
- `SkillRunnerExternalTriggerDispatchRequestedEvent`
- `SkillRunnerExternalTriggerRejectedEvent`
- `SkillRunnerExternalTriggerDuplicateIgnoredEvent`

已有 runner 的 unknown 或 disabled source 不是同步 HTTP `409`。Host/webhook caller 收到 `202 Accepted` 后，runner 自己提交 `SkillRunnerExternalTriggerRejectedEvent`，原因分别为 `unknown` 或 `disabled`。

## Identity And Ledger

每个 external delivery 使用 `SkillRunnerExternalTriggerIdentity` 表达 `source_id`、`delivery_id`、`admission_id`、`kind`、`received_at`、`payload_summary`、`payload_ref`。这些字段随 admission、self-dispatch command、terminal execution event 和 ledger record 传递，避免在多个消息形状里复制散落字段。

delivery ledger 保存在 runner state 的 `recent_external_trigger_deliveries`。默认只承诺 retained window 内去重：terminal delivery 保留最多 `1000` 条或 `30 days`；non-terminal delivery 不被窗口裁剪。

## Wake And Recovery

accepted delivery 先提交 `SkillRunnerExternalTriggerAdmittedEvent`，再通过 self-message 请求执行，随后提交 `SkillRunnerExternalTriggerDispatchRequestedEvent`。runner activation 会扫描未 terminal 的 admitted / dispatch-requested delivery，并按 bounded dispatch attempts 恢复 self-dispatch；超过上限后提交 terminal rejected event，避免无限恢复循环。

## Boundary With Creation And Ornn Execution

`scheduled_agent_creator` 是 runner creation surface；它可以声明 `external_trigger_sources`，但不引入第二套 runner 创建工具。Ornn skill reference / workflow execution 仍按 Ornn skill fetch 与 workflow dispatch 的既有链路执行；external trigger admission 只决定“何时请求已有 runner 执行”，不改变 Ornn repository、Ornn runtime 或外部仓库能力。

Channel-originated `agent_builder.run_agent` uses the same admission command instead of the manual trigger path when the typed tool context carries a stable channel message id. The channel source id is deterministic: `channel:<platform>:<registration_scope_id>`. Creation must declare that id with `kind=channel_inbound`; if it is missing or disabled, the runner accepts the delivery command and then commits `SkillRunnerExternalTriggerRejectedEvent`.
