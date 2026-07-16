---
title: "Scheduled Workflow Dispatch"
status: active
owner: eanzhao
---

# Scheduled Workflow Dispatch

This canon fixes scheduled workflow/team trigger and ownership boundaries after the legacy scheduled runner runtime was retired.

`ScheduledDispatchGAgent` owns schedule facts. Workflow/team execution is invoked through the workflow/team service contracts. Generic skill loading remains owned by the AI/tool-provider path, including `UseSkillTool`, `SkillsAgentToolSource`, `IRemoteSkillFetcher`, `ChatRuntime`, `WorkflowSkillsEndpoints`, `UserSkillRunService`, and NyxidChat slash-skill recovery.

## Removed Runtime

`SkillRunnerGAgent` is not a supported scheduled automation runtime. Do not add new actor, command-port, query-port, projection, readmodel, host endpoint, tool, or test surfaces that route scheduled workflow/team automation through:

- `SkillRunnerGAgent`
- `ISkillRunnerCommandPort`
- `ISkillRunnerCronSchedulePort`
- `ISkillRunnerExecutionQueryPort`
- `InitializeSkillRunnerCommand`
- `TriggerSkillRunnerExecutionCommand`
- `AdmitSkillRunnerExternalTriggerCommand`
- `ScheduledDispatchScheduleKind.SkillRunner`

Historical persisted actor kind/type tokens such as `channel-runtime.skill-runner` and `skill_runner` may appear only in retired-actor cleanup code or tests that prove cleanup of old persisted state. They are not a creation, routing, scheduling, or query surface.

## External Trigger Admission

Scheduled workflow/team external triggers are not admitted through the retired runner model or runner delivery ledgers.

When external trigger support is required, use workflow/team-owned ingress such as `WorkflowWebhookIngressEndpoints` and its replay/admission store. That path owns stable delivery identity, payload-conflict handling, and durable dedupe. Scheduled workflow agent creation rejects `external_trigger_sources` explicitly instead of silently routing them to a retired runner.

## Run And Schedule Lifecycle

Channel-originated `agent_builder.run_agent` uses the catalog-admitted management path and calls `IScheduledDispatchApplicationService.RunNowAsync` for scheduled workflow agents.

Disable, enable, and delete actions use scheduled dispatch lifecycle contracts and catalog tombstones. Delete tools pass transient bearer authority in the tombstone command; the catalog actor commits the revocation intent and tombstone before invoking the dual-track executor. The bearer is not persisted. Failed tracks remain durable and bearer-bound sessions may retry them independently.

## Credential Lifecycle

Scheduled workflow creation does not let the request mapper write secrets. A single scheduled credential lifecycle provisions the requested vault reference and maps the typed reference into the scheduled workflow creation path. If initialization fails, the lifecycle submits compensation intent.

A vault track in `BLOCKED_MISSING_SECRET_REF` remains visible and cannot be cleared by attempt limits. Exact repair is a Host/Admin maintenance operation and is intentionally absent from ordinary scheduled agent tools, query ports, and the general catalog mutation interface.

Credential revocation read-model documents use the natural identity `(agent_id, api_key_id, secret_reference.ref)` encoded as an `scr1_` document key. The scheduled module owns the one-time migration from the former `agent_id` key: startup scans the revocation read model outside the query call stack, writes the same document and authoritative `state_version / last_event_id` under the canonical key, and deletes the legacy key only after the canonical write is accepted. Cursor exhaustion is the migration completion watermark; rejected writes fail startup and leave the legacy row available for a later retry.

The migration is idempotent and every subsequent committed-state projection also deletes the legacy key before writing the canonical row. During a rolling migration, caller-scoped queries collapse legacy and canonical copies by natural identity and prefer the higher authoritative state version, with the canonical key winning an equal-version tie. This path never reads or replays the event store and never primes projection from a query.

## Scheduled Invocation Authorization Facts

Workflow definition actors compile connector capability references, owner-LLM requirements, and literal `nyxid_proxy` service slugs during the bind turn. Both runtime-supported `slug` and `service` argument names preserve workflow step order and multiplicity. A dynamic or otherwise unresolvable service identity commits a required-grant policy without an invented identity, so authorization planning fails closed. Workflow service preparation copies the validated result into the immutable, artifact-hashed `WorkflowServiceDeploymentPlan`; mutable draft contents never substitute for an older prepared revision.

Authorization planning consumes the Studio member, the exact prepared artifact selected by `scopeId + publishedServiceId + workflowRevisionId`, and the owner-scoped NyxID catalog current-state replica. Connector and owner UserConfig evidence are read only when required by that revision. Scheduled workflow agents use their typed `ExecutionScopeId` to read the same owner UserConfig evidence, so their Ornn, channel delivery, failure notification, declared proxy, and effective LLM surfaces are covered by one plan. An absent UserConfig document contributes state version `0` and the Host default rather than hiding that route. An empty, `auto`, or `gateway` user preference resolves through the same Host-composed `Aevatar:NyxId:DefaultRoute` used by `NyxIdLLMProvider`, so the normal `chrono-llm-public` default still requires its exact `UserService.id` grant. Only an effective bare `/api/v1/llm/gateway/v1` route contributes no user-service grant.

The planner is the only producer of the canonical Protobuf digest. Write-side revalidation rereads current sources and returns a private cloned validated plan only when target, owner, schema, policy, versions, and digest still match. The NyxID issuer verifies that digest again immediately before its HTTP effect. Scheduled tool composition requires both planner and revalidator and fails during Host dependency resolution if either is missing; there is no runtime fallback planner. Browser requests never provide grants, secrets, credential identities, or expiry; the server fixes both allow-all flags to `false` and applies its 90-day UTC policy.

NyxID catalog activation currently supports personal owners only. Organization ownership and cross-owner node bindings fail closed because the published wire contract does not expose enough binding ownership and key-issuance authority to reconstruct them safely. A refresh performs exactly two complete reads of the personal service/node/binding graph and commits only when normalized content and the requested same-owner node set are identical. A mismatch returns `nyxid_catalog_unstable` without retrying or publishing a torn observation. Binding edges are normalized by published priority, retain repeated bindings, allow negative priorities, and reject a priority shared by different nodes because no stable tie order is exposed. `ExternalRevision` remains empty because the source does not publish one.
