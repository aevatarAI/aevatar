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

## Canonical Team Member Automation API

Team member automation is a member-owned resource beneath the canonical scope/team/member hierarchy. The canonical product route and its HTTP collection root are:

```text
/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
/api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
```

The Team detail tab may use a query string while choosing a member, but it must navigate to the member resource above once the owner is known. It is not an alternative owner or API route.

The Studio Host owns HTTP composition only. It maps the following operations and delegates business behavior to `IStudioMemberWorkflowSchedulePort`:

| Method | Relative path | Meaning |
| --- | --- | --- |
| `POST` | `/preflight` | Build the current typed authorization plan without provisioning a credential. |
| `GET` | collection | List projected automations owned by the exact member. |
| `POST` | collection | Start a create operation from a confirmed preflight plan. |
| `GET` | `/{scheduleId}` | Read one projected automation under the exact owner. |
| `PUT` | `/{scheduleId}` | Update schedule configuration after write-side authorization revalidation. |
| `POST` | `/{scheduleId}/reauthorize` | Start a dedicated credential replacement from a new confirmed plan. |
| `POST` | `/{scheduleId}/retry-revocation` | Retry an already committed NyxID/vault cleanup using the original operation identity and a fresh owner credential. |
| `DELETE` | `/{scheduleId}` | Commit deletion and credential-revocation intent. |
| `POST` | `/{scheduleId}/pause` | Disable firing without revoking the credential. |
| `POST` | `/{scheduleId}/resume` | Re-enable firing for a usable credential. |
| `POST` | `/{scheduleId}/run-now` | Admit an owner-scoped manual fire. |

`IStudioMemberWorkflowSchedulePort` resolves the member through the Studio read model, verifies that the path `teamId` contains that member, requires a bound workflow implementation, and derives `publishedServiceId` from the member summary. A browser cannot provide or substitute `workflowId`, `publishedServiceId`, grant identities, credential expiry, or credential material through this API.

## Stable Ownership And Generic Isolation

The persisted automation owner is exactly `TeamMemberAutomationOwner(scopeId, memberId)`. `teamId` is a containment guard checked against the member read model on every Studio operation; it is not a second mutable owner identity. Once a schedule is team-owned, its owner tuple cannot change. A `scheduleId` by itself is never sufficient authority, and a mismatched scope, team, or member is exposed as not found rather than leaking another owner's resource.

`ScheduledDispatchGAgent` is the sole authoritative owner of schedule and credential lifecycle facts. The Studio application validates member containment, composes authorization evidence, and invokes `IStudioScheduledCredentialMaterializer` only as a shared NyxID/vault effect adapter. The materializer neither owns nor advances lifecycle state: begin, completion, failure, replacement, and revocation intent are committed by the schedule actor. The committed-state projection owns the query replica. The frontend only consumes the canonical member API and never writes schedule actor state directly.

Generic schedules and Team automations are isolated in both directions:

- generic create/update/delete/get/list paths reject a Team automation owner and exclude team-owned documents;
- Team automation operations require the exact owner tuple and reject generic schedules;
- Team automations use `ScheduledDispatchScheduleKind.Workflow` and a server-derived workflow service target;
- `ScheduledDispatchScheduleKind.Generic` is not a fallback for Team automation, and the retired SkillRunner kind is not recreated.

## Admission Receipts And Projected State

Mutation endpoints return `202 Accepted` with a typed receipt containing `accepted`, `status`, `scheduleId`, `operationId`, and `commandId`. Receipt status `accepted` or `pending` describes command/effect admission only. It does not prove that credential provisioning or revocation finished, that the schedule is active, or that the read model has observed the new authoritative version.

After a mutation, clients must reread the canonical collection or detail endpoint. Only a projected row with a newer authoritative `stateVersion` can establish the durable lifecycle state. In particular, create and reauthorize receipts remain pending until the read model reports `active`; delete remains visible while revocation is unfinished. `enabled` controls firing and is separate from credential health, so an `active` credential may belong to a paused schedule.

The typed `authorizationStatus` values are:

| Status | Read-model meaning |
| --- | --- |
| `provisioning_pending` | Initial dedicated credential provisioning has started but no active generation has been committed. |
| `active` | A dedicated credential generation is committed and usable; firing still depends on `enabled`. |
| `needs_authorization` | Current owner, service, node, policy, digest, expiry, or credential evidence is missing, stale, revoked, or otherwise cannot be revalidated. A new preflight and reauthorization are required. |
| `replacement_pending` | Reauthorization has started but the replacement generation has not reached its terminal committed state. |
| `deleting` | The deletion tombstone and revocation workflow are being committed or executed. |
| `revocation_pending` | At least one credential revocation track remains incomplete and retryable. The row must remain visible. |
| `failed` | A credential lifecycle operation failed with a stable `lastAuthorizationErrorCode`. |

The same view exposes `credentialSourceKind`, `credentialExpiresAtUtc`, `credentialGeneration`, `revocationPending`, `lastAuthorizationErrorCode`, and `stateVersion`. These are read-model facts, not browser inputs. A missing or unresolvable fire-time credential transitions the owner-scoped automation to `needs_authorization` with a stable error code instead of remaining a generic fire failure. Revocation is never hidden by a successful admission receipt: `revocationPending = true` and `revocation_pending` remain query-visible until all required tracks complete.

## Run And Schedule Lifecycle

Channel-originated `agent_builder.run_agent` uses the catalog-admitted management path and calls `IScheduledDispatchApplicationService.RunNowAsync` for scheduled workflow agents.

Disable, enable, and delete actions use scheduled dispatch lifecycle contracts and catalog tombstones. Delete tools pass transient bearer authority in the tombstone command; the catalog actor commits the revocation intent and tombstone before invoking the dual-track executor. The bearer is not persisted. Failed tracks remain durable and bearer-bound sessions may retry them independently.

## Credential Lifecycle

Scheduled workflow creation does not let the request mapper write secrets and does not create a parallel Studio lifecycle. Before any external effect, the schedule actor commits the operation's stable identity, semantic mutation digest, exact credential owner, deterministic NyxID key name, and requested vault reference, then grants one caller a fenced effect attempt. That caller uses the shared materializer to issue/revoke NyxID and vault credentials, then reports completion or a stable failure back to the schedule actor. If initialization fails after materialization, compensation is an effect and the actor-owned locator plus failure/revocation intent remains the durable reconciliation fact.

The semantic mutation digest excludes bearer, raw key, vault payload, and generated credential identifiers. An exact `operationId/idempotencyKey` replay must carry the same normalized schedule definition and target identities; payload drift is a conflict rather than a second schedule or a replacement of the original operation. Once cleanup has been committed, clients retry through the identity-only `retry-revocation` action. They do not reconstruct an earlier reauthorization draft, and the Host supplies a fresh authenticated owner credential only for that retry call.

A vault track in `BLOCKED_MISSING_SECRET_REF` remains visible and cannot be cleared by attempt limits. Exact repair is a Host/Admin maintenance operation and is intentionally absent from ordinary scheduled agent tools, query ports, and the general catalog mutation interface.

Credential revocation read-model documents use the natural identity `(agent_id, api_key_id, secret_reference.ref)` encoded as an `scr1_` document key. The scheduled module owns the one-time migration from the former `agent_id` key: startup scans the revocation read model outside the query call stack, writes the same document and authoritative `state_version / last_event_id` under the canonical key, and deletes the legacy key only after the canonical write is accepted. Cursor exhaustion is the migration completion watermark; rejected writes fail startup and leave the legacy row available for a later retry.

The migration is idempotent and every subsequent committed-state projection also deletes the legacy key before writing the canonical row. During a rolling migration, caller-scoped queries collapse legacy and canonical copies by natural identity and prefer the higher authoritative state version, with the canonical key winning an equal-version tie. This path never reads or replays the event store and never primes projection from a query.

## Scheduled Invocation Authorization Facts

Workflow definition actors compile connector capability references, owner-LLM requirements, and literal `nyxid_proxy` service slugs during the bind turn. Both runtime-supported `slug` and `service` argument names preserve workflow step order and multiplicity. A dynamic or otherwise unresolvable service identity commits a required-grant policy without an invented identity, so authorization planning fails closed. Workflow service preparation copies the validated result into the immutable, artifact-hashed `WorkflowServiceDeploymentPlan`; mutable draft contents never substitute for an older prepared revision.

Authorization planning consumes the Studio member, the exact prepared artifact selected by `scopeId + publishedServiceId + workflowRevisionId`, and the owner-scoped NyxID catalog current-state replica. Connector and owner UserConfig evidence are read only when required by that revision. Scheduled workflow agents use their typed `ExecutionScopeId` to read the same owner UserConfig evidence, so their Ornn, channel delivery, failure notification, declared proxy, and effective LLM surfaces are covered by one plan. An absent UserConfig document contributes state version `0` and the Host default rather than hiding that route. An empty, `auto`, or `gateway` user preference resolves through the same Host-composed `Aevatar:NyxId:DefaultRoute` used by `NyxIdLLMProvider`, so the normal `chrono-llm-public` default still requires its exact `UserService.id` grant. Only an effective bare `/api/v1/llm/gateway/v1` route contributes no user-service grant.

The planner is the only producer of the canonical Protobuf digest. Write-side revalidation rereads current sources and returns a private cloned validated plan only when target, owner, schema, policy, versions, and digest still match. The NyxID issuer verifies that digest again immediately before its HTTP effect. Scheduled tool composition requires both planner and revalidator and fails during Host dependency resolution if either is missing; there is no runtime fallback planner. Browser requests never provide grants, secrets, credential identities, or expiry; the server fixes both allow-all flags to `false` and applies its 90-day UTC policy.

NyxID publishes the service inventory and API-key scope-plan contracts used by scheduled authorization. For a personal owner, `NyxIdAuthorizationCatalogRefreshPort` reads active, grant-eligible `GET /api/v1/user-services` entries, requests `POST /api/v1/api-keys/scope-plan` for that exact full service set, and validates the returned authority, actor, owner, contract, policy, completeness, service, and node facts. The owner-scoped catalog actor commits typed per-service resource owners, node-grant requirements and node IDs, provider evaluation time, local observation and freshness times, and Aevatar's Protobuf content digest. Normal committed-state projection remains the only path into the catalog read model.

Catalog refresh completion is actor-owned. Before projection preparation, the refresh port reads the catalog replica's actor-issued `LifecycleFence`; one typed begin command carries that expected epoch and atomically activates and begins the refresh. Every accepted cleanup or invalidation advances the epoch and clears newest-refresh ordering watermarks, so a current-epoch recovery refresh is never rejected because its clock precedes an observation or refresh from the prior epoch. A stale epoch commits a correlated `Superseded` outcome without reactivating or repopulating a cleaned catalog. Within one epoch, the actor fences ownership by the ordinal tuple `(startedAt, refreshId)` and atomically commits `Superseded` for a displaced refresh when a newer begin wins. The losing refresh races that committed terminal outcome against provider work and cancels its linked provider I/O without any process-local ownership registry.

The refresh port constructs its deterministic Projection Pipeline lease before ensuring the runtime scope, then releases that lease on preparation failure, cancellation, and every terminal path. It waits for committed `Started` before calling NyxID and derives success, failure, access denial, instability, or supersession only from the matching committed terminal outcome. Dispatch admission is not completion, and committed completion is not durable replica visibility: an observed result carries the actor's committed `StateVersion`. The Application-owned visibility service performs exactly one read-model query and separately reports refresh outcome, visibility outcome, required version, and visible version; it never primes, polls, or replays projection. Explicit `/api/auth/nyxid/authorization-catalog:refresh` returns `200` only for a ready replica, `202 Accepted` with both versions while projection is behind, and `503` for a visible stale, invalidated, invalid, owner-mismatched, or unavailable replica. Login finalization remains `200` after successful authentication but reports `AuthorizationCatalogReady=false` and the separate refresh/visibility fields when the replica is not ready.

Mutation revalidation gets one opportunistic second read. If the catalog replica has not reached the committed refresh version, the typed `CatalogProjectionPending` result maps to retryable HTTP `503` with `requiredStateVersion`. If a concurrent refresh supersedes the mutation's refresh and the first observed replica version is already at or beyond that committed version, Studio returns a distinct retryable refresh-superseded `503`, never a permanent reauthorization conflict. Preflight remains one pure planner read with no refresh, projection priming, or polling.

Both GAgentService and standalone Studio call the idempotent `AddNyxIdAuthorizationCatalogHosting` composition entrypoint. It installs the catalog actor, authorization planner/revalidator, NyxID adapter, refresh observation session, committed-state projector, and read-model provider on the single shared GAgentService Projection Pipeline; repeated full or scheduled capability composition does not duplicate these registrations.

The full-catalog scope plan is durable planning evidence, not a reusable key-creation precondition. Its opaque `normalized_grant_digest` is selection-scoped and is deliberately not persisted in the catalog. It is distinct from both the catalog `ContentDigest` and the authorization plan `PermissionDigest`; a digest produced for all eligible services cannot authorize a workflow that selected only a subset.

Immediately before creating a dedicated key, `ScheduledAgentApiKeyIssuer` requests a new scope plan for the validated authorization plan's exact ordinal-sorted service IDs and passes `target_org_id` only for an already validated organization owner. The integrity-covered plan binds a personal owner to itself as `authenticated_actor`; an organization owner requires an explicit normalized NyxID personal administrator. The issuer requires the provider response's actor authority, kind, and ID to match that principal exactly, along with the intended owner, current contract and policy versions, freshness and completeness declarations, every per-service resource owner and node grant, and both flattened allowlists. Any mismatch returns `authorization_plan_changed`, provider timeout returns the stable sanitized `nyxid_scope_plan_provider_timed_out`, caller cancellation propagates, and key creation is not called.

Only a matching targeted response supplies the mutation fields: both allow-all flags remain `false`, `allowed_service_ids` and `allowed_node_ids` come from that response, and its exact `normalized_grant_digest` is sent as `scope_plan_digest`. NyxID revalidates that digest against current state during creation and fails closed on drift. Catalog activation remains personal-owner only; explicit organization targeting during a validated issuance effect does not create a shared organization catalog.
