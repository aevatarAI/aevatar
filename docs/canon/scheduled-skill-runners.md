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

Team member automation is a member-owned schedule beneath the canonical scope/team/member hierarchy. The product owner route is:

```text
/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations
```

The Team detail tab may use a query string while choosing a member, but it must navigate to the member resource above once the owner is known. It is not an alternative owner route.

The only nested Studio Host HTTP operation is preflight:

```text
POST /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight
```

Preflight builds the current typed authorization plan without provisioning a credential. `IStudioMemberWorkflowSchedulePort` resolves the member through the Studio read model, verifies that the path `teamId` contains that member, requires a bound workflow implementation, and derives `publishedServiceId` from the member summary. A browser cannot provide or substitute `workflowId`, `publishedServiceId`, grant identities, credential expiry, or credential material through this API.

Schedule lifecycle operations use the canonical owner-aware schedule API. Callers pass the typed Studio member automation owner on `/api/schedules` create/update/action requests or as owner query parameters on reads and actions:

```text
ownerKind=studio_member_automation
ownerScopeId={scopeId}
ownerTeamId={teamId}
ownerMemberId={memberId}
```

`/api/schedules` is the canonical HTTP surface for listing, reading, creating, updating, enabling, disabling, deleting, and run-now admission. The nested Studio member automation route is not a CRUD/action route.

Deletion and any replay while credential revocation remains pending use the same canonical request:

```http
DELETE /api/schedules/{scheduleId}
Content-Type: application/json

{
  "reason": "scheduled_agent_key_canary_cleanup",
  "operationId": "delete-operation-...",
  "idempotencyKey": "delete-idempotency-...",
  "owner": {
    "kind": "studio_member_automation",
    "scopeId": "scope-...",
    "teamId": "team-...",
    "memberId": "m-..."
  }
}
```

The exact same normalized owner, `operationId`, `idempotencyKey`, and reason are replayed while revocation is pending. The Host derives a fresh authenticated bearer on each request; bearer authority is never supplied in the body. There is no nested delete or public `retry-revocation` route. A `202 Accepted` receipt is admission only. Callers reread the canonical owner-aware detail until both revocation tracks are terminal and the row becomes not found.

## Stable Ownership And Generic Isolation

The persisted automation owner is exactly `TeamMemberAutomationOwner(scopeId, memberId, teamId)`. The `scopeId`, `teamId`, and `memberId` tuple is the stable owner identity for Studio member automation schedules. Once a schedule is team-owned, its owner tuple cannot change. A `scheduleId` by itself is never sufficient authority, and a mismatched scope, team, or member is exposed as not found rather than leaking another owner's resource.

`ScheduledDispatchGAgent` is the sole authoritative owner of schedule and credential lifecycle facts. The Studio application validates member containment during preflight, composes authorization evidence, and invokes `IStudioScheduledCredentialMaterializer` only as a shared NyxID/vault effect adapter when a dedicated credential lifecycle is present. The materializer neither owns nor advances lifecycle state: begin, completion, failure, replacement, and revocation intent are committed by the schedule actor. The committed-state projection owns the query replica. Clients consume the canonical owner-aware schedule API and never write schedule actor state directly.

Generic schedules and Team automations are isolated in both directions:

- generic create/update/delete/get/list paths reject or hide schedules with a Team automation owner;
- owner-aware Team automation operations require the exact owner tuple and reject generic schedules;
- Team automations use `ScheduledDispatchScheduleKind.Workflow` and a server-derived workflow service target;
- `ScheduledDispatchScheduleKind.Generic` is not a fallback for Team automation, and the retired SkillRunner kind is not recreated.

## Public Schedule Target Boundary

The public `/api/schedules` contract accepts only a catalog-resolved typed
`serviceInvocation` target. The server resolves that target from the service
and revision catalogs; a browser or other external caller cannot supply an
actor address, an `EventEnvelope`, or an equivalent raw dispatch payload.

The authenticated request scope must equal the resolved target tenant. A
target in another tenant is rejected before Application admission, even when
the caller can name that service. `actorId` is an opaque runtime address and
raw `EventEnvelope` is an internal transport shape; the public schedule target
input does not accept either as a caller-supplied value.

Legacy persisted envelope schedules remain readable only by the actor and
projection paths needed to retire them. Application hides those rows from
public get/list results and treats public lifecycle mutations as not found.
When activation encounters an envelope schedule without the required marker,
the actor durably disables it and purges its scheduled callbacks before it can
fire. The Protobuf `TrustedInternal` marker is a strongly typed, actor-only
contract for the narrow internal envelope protocol. Hosting and Application do
not expose it, and it does not create an administrator or public
raw-envelope-scheduling escape hatch.

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

The same view exposes `credentialSourceKind`, `credentialExpiresAtUtc`, `credentialGeneration`, `revocationPending`, `nyxIdRevocationStatus`, `vaultRevocationStatus`, `lastAuthorizationErrorCode`, and `stateVersion`. These are read-model facts, not browser inputs. The two track fields use the exact projected wire values `NotRequired`, `Pending`, `Completed`, and `Failed`; they are not lifecycle-status aliases. A missing or unresolvable fire-time credential transitions the owner-scoped automation to `needs_authorization` with a stable error code instead of remaining a generic fire failure. Revocation is never hidden by a successful admission receipt: `revocationPending = true` and `revocation_pending` remain query-visible until all required tracks complete. Deleting an automation with an existing Agent Key requires both track values to reach `Completed` before the deleted row may become not found.

## Run And Schedule Lifecycle

Channel-originated `agent_builder.run_agent` uses the catalog-admitted management path and calls `IScheduledDispatchApplicationService.RunNowAsync` for scheduled workflow agents.

Disable, enable, and delete actions use scheduled dispatch lifecycle contracts and catalog tombstones. Deletion always uses the canonical owner-aware `DELETE` request above. The Host passes its freshly derived transient bearer authority in the tombstone command; the catalog actor commits the revocation intent and tombstone before invoking the dual-track executor. The bearer is not persisted. Failed tracks remain durable, and an exact delete replay re-enters only unfinished revocation work under the same actor-owned operation identity.

## Credential Lifecycle

Scheduled workflow creation does not let the request mapper write secrets and does not create a parallel Studio lifecycle. Before any external effect, the schedule actor commits the operation's stable identity, semantic mutation digest, exact credential owner, deterministic NyxID key name, and requested vault reference, then grants one caller a fenced effect attempt. That caller uses the shared materializer to issue/revoke NyxID and vault credentials, then reports completion or a stable failure back to the schedule actor. If initialization fails after materialization, compensation is an effect and the actor-owned locator plus failure/revocation intent remains the durable reconciliation fact.

The semantic mutation digest excludes bearer, raw key, vault payload, and generated credential identifiers. An exact `operationId/idempotencyKey` replay must carry the same normalized schedule definition and target identities; payload drift is a conflict rather than a second schedule or a replacement of the original operation. Delete replay keeps the original normalized owner, reason, and operation identities, does not reconstruct an earlier reauthorization draft, and relies on the Host to derive fresh authenticated bearer authority for that canonical request.

A vault track in `BLOCKED_MISSING_SECRET_REF` remains visible and cannot be cleared by attempt limits. Exact repair is a Host/Admin maintenance operation and is intentionally absent from ordinary scheduled agent tools, query ports, and the general catalog mutation interface.

Credential revocation read-model documents use the natural identity `(agent_id, api_key_id, secret_reference.ref)` encoded as an `scr1_` document key. The scheduled module owns the one-time migration from the former `agent_id` key: startup scans the revocation read model outside the query call stack, writes the same document and authoritative `state_version / last_event_id` under the canonical key, and deletes the legacy key only after the canonical write is accepted. Cursor exhaustion is the migration completion watermark; rejected writes fail startup and leave the legacy row available for a later retry.

The migration is idempotent and every subsequent committed-state projection also deletes the legacy key before writing the canonical row. During a rolling migration, caller-scoped queries collapse legacy and canonical copies by natural identity and prefer the higher authoritative state version, with the canonical key winning an equal-version tie. This path never reads or replays the event store and never primes projection from a query.

## Scheduled Invocation Authorization Facts

An owner-LLM-dependent Team automation follows one integrity-bound chain:

```text
committed typed UserConfig selection
  -> digest-covered authorization plan
  -> constrained NyxID Agent Key + Vault reference
  -> actor-owned authorization fact + persisted ChatRequestEvent.LlmControl
  -> runtime caller/payload/fact cross-check
  -> workflow inbox
```

The committed UserConfig selection, exact service grant, credential locator,
caller authority, and runtime route/model are parts of one decision. The
permission digest covers the typed owner LLM selection. Create, reauthorize,
and update copy that validated selection into the actor-owned authorization
fact and derive persisted `ChatRequestEvent.LlmControl` from the same plan or
fact. They never accept an operator-supplied route, model, service grant,
credential identifier, or caller binding.

Before dispatch reaches the workflow inbox, an Agent Key workflow must have a
complete verified caller binding, a valid fact selection, payload route/model
equal to that selection, and the selected UserService in the fact's exact
grants. Missing authority or any mismatch fails with a typed scheduled
authorization code, moves the automation toward `needs_authorization`, and
cancels future fire leases. The fire path must not query UserConfig, fill from
a Host default, infer identity from a legacy slug/model prefix, accept a
missing binding, or treat a v1 permission digest as v2-compatible.

Caller authority is deliberately absent from projection and public API
contracts. A successful create emits one non-projected operational event in
category `Aevatar.Studio.MemberAutomation`, EventId
`6201/StudioMemberAutomationCreateAccepted`, with exactly the six application
fields `ScopeId`, `TeamId`, `MemberId`, `ScheduleId`, `OperationId`, and
`BindingId`. This event is acceptance correlation, not a read model or
completion fact, and it must contain no permission digest or credential
material.

An accepted committed revocation outcome with both pending flags false emits
`6202/StudioMemberAutomationRevocationCompleted` in the same category with
exactly `ScopeId`, `TeamId`, `MemberId`, `ScheduleId`, `OperationId`,
`NyxIdRevocationStatus`, `VaultRevocationStatus`, `StateVersion`, and
`ObservedAtUtc`; both status values are exactly `Completed`. The repository
tool `tools/schedules/query_member_automation_audit.sh` is the canonical
allowlisted query for the `create` and `revocation` operational events.

Workflow definition actors compile typed `ExternalWorkflowCapabilityRef` values and owner-LLM requirements during the bind turn. A Connector dependency is `connector_capability_ref + operation_id + contract_digest`; a NyxID dependency is `user_service_id + service_slug_snapshot + endpoint_id + method + path_template + contract_digest`. Slug-only, `service` alias, dynamic identity, incomplete endpoint tuple, contract drift, and sensitive headers fail before the definition is committed. Workflow service preparation copies the validated refs and admission digest into the immutable, artifact-hashed `WorkflowServiceDeploymentPlan`; mutable draft contents never substitute for an older prepared revision.

Authorization planning consumes the Studio member, the exact prepared artifact selected by `scopeId + publishedServiceId + workflowRevisionId`, and the owner-scoped NyxID authorization catalog current-state replica. Connector and owner UserConfig evidence are read only when required by that revision. The planner accepts only exact `user_service_id` evidence from the typed refs; `service_slug_snapshot` is a route/display integrity check and is never resolved back into an id. Two equal slugs with different ids remain two grants. Scheduled workflow agents use their typed `ExecutionScopeId` to read the same committed typed UserConfig selection, so their Ornn, channel delivery, failure notification, declared proxy, and LLM surfaces are covered by one plan. An absent UserConfig document contributes state version `0` and an `Unspecified` selection; it never manufactures Gateway or the Host default. Explicit `Gateway` and `NyxIdUserService` selections must each carry a valid canonical model, and only the latter contributes its exact `UserService.id` grant.

Durable external-capability readiness uses that catalog replica as evidence rather than treating NyxID durable mode as permanently unavailable. The readiness source performs one pure owner-scoped read-model query for the verified caller and never refreshes, activates, polls, replays, or primes projection. It accepts only an active, non-cleaned snapshot with a positive authoritative version, complete lifecycle facts, the exact ordinal `nyxid` resource-owner authority, and a content digest recomputed from the typed owner and services. A durable NyxID `READY` proof binds the exact capability identity to a `DURABLE_AUTHORIZATION_CATALOG` source stamp containing the actor id, authoritative version, freshness window, and content digest. Unified admission rejects a proof evaluated for another execution mode or capability and rejects both new and existing durable plans when this stamp is absent or invalid.

Workflow capability admission has distinct live and persisted contracts. Live admission derives `nyxid/personal/<subject>` only from the authenticated caller and may use transient bearer credentials to inspect `/api/v1/mcp/config` readiness. The resulting `external-capability-admission.v4` plan seals that typed durable owner into `admission_digest`. After the plan is committed into a service revision or Studio member binding run, prepare, publish, replay, and handoff use credential-free persisted revalidation: they reparse the bound definition, validate schema/capabilities/source freshness/digest, require the caller-owned expected execution mode to match the plan exactly, and require the owner-derived catalog actor id to equal the sealed catalog source id. V2 and v3 plans require an explicit rebind before old authoring is parsed. The expected mode is never read back from the plan being validated. These paths never reconstruct a caller from `appId`, `serviceId`, route position, or an empty-identity convention.

The planner is the only producer of the canonical Protobuf digest. Write-side revalidation rereads current sources and returns a private cloned validated plan only when target, owner, schema, policy, versions, and digest still match. The NyxID issuer verifies that digest again immediately before its HTTP effect. Scheduled tool composition requires both planner and revalidator and fails during Host dependency resolution if either is missing; there is no runtime fallback planner. Browser requests never provide grants, secrets, credential values, or expiry; the server fixes both allow-all flags to `false` and applies its 90-day UTC policy. LLM authoring uses `required_nyx_services[]` entries containing exact `user_service_id` plus `service_slug_snapshot`, and `nyx_user_service_id` for the exact outbound provider; the removed `required_service_slugs` field is rejected.

NyxID publishes the service inventory and API-key scope-plan contracts used by scheduled authorization. For a personal owner, `NyxIdAuthorizationCatalogRefreshPort` reads active, grant-eligible `GET /api/v1/user-services` entries and validates `POST /api/v1/api-keys/scope-plan` authority, actor, owner, contract, policy, completeness, service, and node facts. Owner-level repair and explicit refresh requests use the full active grant-eligible service set and commit a full-owner catalog observation. Schedule mutation recovery uses the planner-resolved required `UserService.id` set for that invocation, so unrelated owner services do not enter the scope-plan request or poison the schedule's authorization evidence. A schedule-scoped refresh commits a typed required-service-subset observation that names exactly the covered user-service IDs; the owner-scoped catalog actor merges those service rows into its existing owner catalog and recomputes the catalog digest from the merged typed state instead of replacing unrelated services. Authorization, rate-limit, transport, transient, malformed, route-unresolved, and catalog-mismatch failures for the requested service set remain fail-closed and do not commit partial evidence. The owner-scoped catalog actor commits typed per-service resource owners, node-grant requirements and node IDs, provider evaluation time, local observation and freshness times captured after provider work completes, and Aevatar's Protobuf content digest. Normal committed-state projection remains the only path into the catalog read model.

Catalog refresh completion is actor-owned. Before projection preparation, the refresh port reads the catalog replica's actor-issued `LifecycleFence`; one typed begin command carries that expected epoch and atomically activates and begins the refresh. Every committed observed, failed, invalidated, or cleaned terminal transition advances the epoch and clears active refresh ownership. A new terminal event remains exactly one epoch ahead; during replay each terminal advances to at least `current fence + 1`, while activation never reduces a migrated fence. Historical observed events with the old fence and failed events without the optional fence therefore cannot collapse later lifecycles into the same epoch. State and lifecycle events carry a typed fence-semantics version. After replay or snapshot restore, any persisted legacy state commits a one-time migration barrier before commands are served, clears and supersedes restored active ownership, and projects the migrated fence through the standard committed-state path; a fresh empty actor writes no migration. The actor retains no newest-refresh wall-clock watermark across terminal epochs, so a legitimate recovery refresh can start after clock rollback while a delayed prior-epoch begin still commits a correlated `Superseded` outcome. While a refresh remains active in one epoch, the actor fences contenders by the ordinal tuple `(startedAt, refreshId)` and atomically commits `Superseded` for a displaced refresh when a newer begin wins. The losing refresh races that committed terminal outcome against provider work and requests cancellation through an independent provider-owned cancellation source without any process-local ownership registry. If the provider ignores cancellation, the refresh releases its observation resources and returns immediately; a sanitized nonblocking continuation retains that source until provider completion and observes any eventual fault without logging provider or bearer details.

The refresh port constructs its deterministic Projection Pipeline lease before ensuring the runtime scope, then releases every owned resource on preparation failure, cancellation, and every terminal path without replacing the original operation result. It waits for committed `Started` before calling NyxID and observes supersession concurrently with provider work. The terminal observation deadline starts only after provider work has dispatched its terminal command; a provider-owned timeout commits a typed `Failed` outcome, while caller cancellation remains cancellation. Success, failure, access denial, instability, or supersession derive only from the matching committed terminal outcome. Dispatch admission is not completion, and committed completion is not durable replica visibility: an observed result carries the actor's committed `StateVersion`. The Application-owned visibility service performs exactly one read-model query and separately reports refresh outcome, visibility outcome, required version, and visible version; it never primes, polls, or replays projection. Explicit `/api/auth/nyxid/authorization-catalog:refresh` returns `200` only for a ready replica, `202 Accepted` with both versions while projection is behind, and `503` for a visible stale, invalidated, invalid, owner-mismatched, or unavailable replica. Login finalization remains `200` after successful authentication but reports `AuthorizationCatalogReady=false` and the separate refresh/visibility fields when the replica is not ready.

Create, reauthorize, and update mutations share one refresh-aware revalidation path with at most one opportunistic second read. Every second-read result, including authorization success, is gated by the committed refresh version. If the catalog replica has not reached that version, the typed `CatalogProjectionPending` result maps to retryable HTTP `503` with `requiredStateVersion`. Transient provider, observation-timeout, and refresh-infrastructure failures map to a sanitized retryable `503`; HTTP `409` remains reserved for actual plan drift or reauthorization semantics. If a concurrent refresh supersedes the mutation's refresh and the first observed replica version is already at or beyond that committed version, Studio returns a distinct retryable refresh-superseded `503`. Preflight remains one pure planner read with no refresh, projection priming, or polling.

Both GAgentService and standalone Studio call the idempotent `AddNyxIdAuthorizationCatalogHosting` composition entrypoint. It installs the catalog actor, authorization planner/revalidator, NyxID adapter, refresh observation session, committed-state projector, and read-model provider on the single shared GAgentService Projection Pipeline; repeated full or scheduled capability composition does not duplicate these registrations.

## Workflow admission rejection and rollout

Scheduled workflow compatibility is decided before workflow actor lifecycle and before service-run registration. The invocation adapter maps the bounded workflow admission outcome into a schedule-owned typed failure. `ScheduledDispatchGAgent` commits one failed fire, increments `fireCount` and `failureCount` once, stores the safe message in `lastError`, stores the stable code in `lastErrorCode` and the fire record, and keeps the schedule enabled for operator repair. It creates zero Run artifacts and adds no second failure store.

| Stable code | Operator meaning | Remediation |
| --- | --- | --- |
| `WORKFLOW_DEFINITION_INVALID` | Root or inline workflow structure is invalid. | Update the definition and rebind. |
| `NYXID_OPERATION_AUTHORING_MIGRATION_REQUIRED` | The workflow uses a retired NyxID authoring contract. | Replace the authoring contract and rebind. |
| `CAPABILITY_ADMISSION_REBIND_REQUIRED` | The persisted plan is absent, legacy, mismatched, or has the wrong `ExpectedExecutionMode`. | Rebuild admission and rebind. |
| `scheduled_dispatch_failed` | Dispatch failed outside the bounded workflow admission contract. | Inspect the sanitized schedule failure and retry only after repair. |

Deployment order is: shared protobuf contracts; actor validation and state; projectors and query mapping; catalog composition; UserConfig/settings/channel atomic writers; durable planner/runtime exact-match enforcement; workflow preflight; then scheduled fires. Deploy these as one compatible release before enabling unattended execution.

Rollout begins with a read-only audit of active UserConfig selections, authorization catalog evidence, workflow artifacts, and schedules. There is no automatic production migration, rerun, pause, delete, repair, replay, or backfill. Operators explicitly reselect unavailable LLM targets, rebuild incompatible admission plans, and reauthorize affected schedules. The rollout rejects empty-list-as-open-catalog, accepted-ACK-as-active, silent Gateway fallback, query-time catalog reads, and invocation-time `RevalidatePersistedAsync`.

## Catalog Projection Version-Regression Recovery

NyxID authorization catalog version-regression repair is a platform-admin
incident-recovery operation only. It is never part of normal scheduled Agent
Key preflight, planner query, readiness evaluation, login finalization, or
fire-time authorization. Those paths remain read-only with respect to
projection lifecycle and must not delete, refresh, activate, replay, poll, or
prime a catalog in order to answer a request.

The guarded Mainnet route
`POST /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog` first
inspects the exact owner-scoped actor/document fingerprint. Apply is permitted
only when the document version is greater than the positive authoritative
source version and the request repeats the exact actor ID, both versions, last
event ID, repair request ID, and operator reason. Any changed fingerprint is a
conflict that requires a new inspection; generic replica deletion is not a
catalog capability.

After the guarded conditional delete, authorization evidence is rebuilt only
through a fresh NyxID observation using the same elevated bearer's verified
personal owner subject. The repair must not republish empty actor state, copy
catalog contents out of Elasticsearch, or hydrate the actor from the read
model. A refresh result of `observed` proves the actor committed a terminal
refresh outcome; it is distinct from read-model `ready`. Until visibility
reports `ready` at the required authoritative version, automation mutation,
Agent Key creation, and canary execution must remain stopped. After the
non-credential workflow/Team/member/published-service scaffold exists, the
canonical Team automation preflight may be used as a bounded, pure read-only
readiness probe: it must observe the required catalog actor state version and
the exact expected non-wildcard service grant. It must not refresh, apply
repair again, create a schedule, or provision a credential.

The full-catalog scope plan is durable planning evidence, not a reusable key-creation precondition. Its opaque `normalized_grant_digest` is selection-scoped and is deliberately not persisted in the catalog. It is distinct from both the catalog `ContentDigest` and the authorization plan `PermissionDigest`; a digest produced for all eligible services cannot authorize a workflow that selected only a subset.

Immediately before creating a dedicated key, `ScheduledAgentApiKeyIssuer` requests a new scope plan for the validated authorization plan's exact ordinal-sorted service IDs and passes `target_org_id` only for an already validated organization owner. The integrity-covered plan binds a personal owner to itself as `authenticated_actor`; an organization owner requires an explicit normalized NyxID personal administrator. The issuer requires the provider response's actor authority, kind, and ID to match that principal exactly, along with the intended owner, current contract and policy versions, freshness and completeness declarations, every per-service resource owner and node grant, and both flattened allowlists. Any mismatch returns `authorization_plan_changed`, provider timeout returns the stable sanitized `nyxid_scope_plan_provider_timed_out`, caller cancellation propagates, and key creation is not called.

Only a matching targeted response supplies the mutation fields: both allow-all flags remain `false`, `allowed_service_ids` and `allowed_node_ids` come from that response, and its exact `normalized_grant_digest` is sent as `scope_plan_digest`. NyxID revalidates that digest against current state during creation and fails closed on drift. Catalog activation remains personal-owner only; explicit organization targeting during a validated issuance effect does not create a shared organization catalog.
