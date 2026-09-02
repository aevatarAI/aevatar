---
title: "定时任务 Agent Key 凭证引用补充决策"
status: proposed
owner: eanzhao
supersedes: "ADR-0037 Decision item 5 and Cutover Order Phase 0 wording"
---

# ADR-0041: 定时任务 Agent Key 凭证引用补充决策

> 本 ADR 只 supersede ADR-0037 的两处措辞：Decision item 5 的 create/update validation 适用范围，以及 Cutover Order Phase 0 的新写 tag 列表。ADR-0037 仍是定时任务调用凭证权威来源模型的基础决策。

## Context

ADR-0037 已在 Required Contract 中声明 `scheduled_invocation_agent_key = 7` 与 `ScheduledInvocationAgentKeyCredentialReferenceState`，并锁定 raw key material 只能通过 vault purpose `scheduled.invocation-agent-key` 存取，state/readmodel/log/API response 只保留 typed reference 与过期时间。

但 ADR-0037 的 Decision item 5 使用了未限定的“create/update 期只做无副作用 validation”，Cutover Order Phase 0 只写了新写 `5/6`。在 scheduled invocation agent key reference 已进入同一 `oneof source` 的前提下，这两处容易让实现者误读为：

- agent key reference 也要套用 NyxID source 的 binding 预检描述；
- tag `4` 可以被重新解释或新写列表里没有 tag `7`；
- 后续实现可以直接修改 ADR-0037 来修正文案。

仓库规则要求 `docs/adr/` 只追加新决策，不改写历史决策；因此本补充用新的 ADR 承接该修正。

## Decision

1. ADR-0037 的 create/update 无副作用 validation 约束在该条上下文中只限定 `NyxIdCredentialSource`：不得用“真 mint”做 NyxID source 的预检，create/update 只能读取既有 binding 与 scope 归属事实。
2. `ScheduledInvocationAgentKeyCredentialReferenceState` 是同一 `oneof source` 下的 typed reference。create/update 只能校验引用形态、归属、用途与过期时间等本仓库可见事实，不得持久化 raw key material，也不得把预检实现成可直接使用的 token 签发。
3. `legacy_durable_sender_bearer_blocked = 4` 保留为旧 raw durable 的 fail-closed 哨兵，不得复用为新来源字段。
4. ADR-0037 的 Phase 0 新写 source tag 集合补充为 `5/6/7`：`nyx_id = 5`、`durable = 6`、`scheduled_invocation_agent_key = 7`。旧 tag `1/2/3` 仍只作为迁移期 deprecated 双读输入。
5. 通用 HTTP `/api/schedules` 不因本补充获得 durable 或 agent key raw material 写入能力。scheduled invocation agent key reference 只能由 trusted internal provisioning 写入，并只以 typed reference 与过期时间进入 state、readmodel、log 或 API response。

## Consequences

- 后续实现可以在不改写 ADR-0037 的前提下，按 tag `7` 落地 scheduled invocation agent key reference。
- NyxID source 的无副作用 validation、durable reference 的 internal-only 约束、agent key reference 的 vault-backed 引用语义保持边界清楚。
- 旧 raw durable 的 fail-closed 哨兵继续固定在 tag `4`，避免 wire-safe migration 期间出现字段复用歧义。
- 本补充不新增外部仓库依赖；如果 NyxID 或 vault surface 不足，实现必须在 aevatar 内 fail closed 或延后行为阶段。
