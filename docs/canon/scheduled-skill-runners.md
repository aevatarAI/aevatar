---
title: "Scheduled Skill Runners"
status: active
owner: eanzhao
---

# Scheduled Skill Runners

This canon fixes scheduled skill runner trigger and ownership boundaries. `SkillRunnerGAgent` owns runner configuration and execution-result facts for the legacy runner. It no longer owns external trigger admission for scheduled workflow/team automation.

## External Trigger Admission

Scheduled workflow/team external triggers are not admitted through `SkillRunnerGAgent`, `ISkillRunnerCommandPort`, or runner delivery ledgers. The former Mainnet `SkillRunner` delivery endpoint is retired.

When external trigger support is required, use workflow/team-owned ingress such as `WorkflowWebhookIngressEndpoints` and its replay/admission store. That path owns stable delivery identity, payload-conflict handling, and durable dedupe. Scheduled workflow agent creation rejects `external_trigger_sources` explicitly instead of silently routing them to `SkillRunnerGAgent`.

## Runner-Owned Source Facts

New scheduled workflow/team creation does not declare runner external trigger sources. Historical `SkillRunner` committed event types may still exist for old actor state until the legacy runner runtime is removed, but they are not a supported admission surface:

- `SkillRunnerExternalTriggerAdmittedEvent`
- `SkillRunnerExternalTriggerDispatchRequestedEvent`
- `SkillRunnerExternalTriggerRejectedEvent`
- `SkillRunnerExternalTriggerDuplicateIgnoredEvent`

Do not add new HTTP, command-port, tool, or test surfaces that send `AdmitSkillRunnerExternalTriggerCommand` to `SkillRunnerGAgent`.

## Identity And Ledger

`SkillRunnerExternalTriggerIdentity` and `recent_external_trigger_deliveries` are legacy runtime state shapes. They must not be used as the admission or dedupe owner for new scheduled workflow/team external triggers.

Workflow/team-owned ingress must define its own stable delivery identity and durable replay semantics instead of depending on runner state.

## Outbound Delivery Facts

Outbound delivery facts remain separate from inbound trigger admission. `SkillRunnerGAgent` may still publish execution delivery facts for legacy runner execution, but inbound external trigger admission is no longer its responsibility.

这些字段随 `SkillRunnerExecutionDocument` current-state read model 覆盖复制。查询侧只能读取该 read model；不得扫描 tool result、重放 event store 或在 query path 启动 projection priming 来推断是否已经回复用户。交互式 tool middleware 只收集当前 actor turn 内的 typed delivery signal，不持有跨 run bool 事实源。

## Wake And Recovery

Do not rely on `SkillRunnerExternalTriggerAdmittedEvent` or `SkillRunnerExternalTriggerDispatchRequestedEvent` as a wake/recovery protocol for new scheduled workflow/team external triggers. Use workflow/team-owned ingress, admission, replay, and dispatch contracts.

## Boundary With Creation And Ornn Execution

`scheduled_agent_creator` is the runner creation surface. It rejects `external_trigger_sources` for scheduled workflow agents and does not introduce a second runner creation tool. Ornn skill reference / workflow execution still follows the existing Ornn skill fetch and workflow dispatch path.

Channel-originated `agent_builder.run_agent` uses the catalog-admitted management trigger path and dispatches `TriggerSkillRunnerExecutionCommand`; it must not use external trigger admission.

## Scheduled credential lifecycle

Runner creation does not let the request mapper write secrets. A single scheduled credential lifecycle provisions the requested vault reference, maps the typed reference into `InitializeSkillRunnerCommand`, and submits compensation intent if initialization fails. Delete tools pass transient bearer authority in the tombstone command; the catalog actor commits the revocation intent and tombstone before invoking the dual-track executor. The bearer is not persisted. Failed tracks remain durable and bearer-bound sessions may retry them independently.

A vault track in `BLOCKED_MISSING_SECRET_REF` remains visible and cannot be cleared by attempt limits. Exact repair is a Host/Admin maintenance operation and is intentionally absent from ordinary runner tools, query ports, and the general catalog mutation interface.

Credential revocation read-model documents use the natural identity `(agent_id, api_key_id, secret_reference.ref)` encoded as an `scr1_` document key. The scheduled module owns the one-time migration from the former `agent_id` key: startup scans the revocation read model outside the query call stack, writes the same document and authoritative `state_version / last_event_id` under the canonical key, and deletes the legacy key only after the canonical write is accepted. Cursor exhaustion is the migration completion watermark; rejected writes fail startup and leave the legacy row available for a later retry. The migration is idempotent and every subsequent committed-state projection also deletes the legacy key before writing the canonical row. During a rolling migration, caller-scoped queries collapse legacy and canonical copies by natural identity and prefer the higher authoritative state version, with the canonical key winning an equal-version tie. This path never reads or replays the event store and never primes projection from a query.
