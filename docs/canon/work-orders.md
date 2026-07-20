---
title: "Work Orders"
status: active
owner: eanzhao
---

# Work Orders

A `WorkOrder` is the durable, scope-owned intent to have one Team member invoke one validated published service. It connects the requester, assignment, permission plan, approval, Run, and terminal evidence without making any of those identities aliases of another.

`WorkOrderGAgent` is the only authority for a WorkOrder lifecycle. Its committed Protobuf events and state are authoritative; `WorkOrderCurrentStateDocument` is an actor-scoped current-state replica used only for queries.

## Identity Model

The following identities remain separate typed fields throughout the command, actor state, projection, and API contracts:

| Identity | Meaning |
|---|---|
| `workOrderId` | Stable logical WorkOrder identity derived from canonical `scopeId + dedupKey`. |
| requester principal | The authenticated principal that requested the work. It is not implicitly a Team member. |
| `teamId` | Team that owns the assignment. |
| `memberId` | Stable Team member responsible for the work. |
| `workflowId` | Optional workflow definition identity for workflow-backed members. |
| `publishedServiceId` | Exact callable service identity returned by the authoritative member read model. |
| `serviceRevisionId` | Service revision validated when the assignment is accepted. |
| `approvalId` and `decisionId` | Approval resource and decision identities. |
| `dispatchCommandId` | Stable command identity for the one authorized dispatch. |
| `requestedRunId` | Stable logical Run identity requested by the WorkOrder. |
| `runActorId` | Opaque actor address returned by accepted execution. |
| `terminalDeliveryId` | Stable idempotency identity for terminal evidence delivery. |
| artifact references | Typed input and declared-result artifact identities; they do not become Run or WorkOrder identities. |

Callers must not derive `publishedServiceId` from `memberId`, `workflowId`, display names, actor-id text, or route structure.

## Creation And Assignment Validation

`POST /api/scopes/{scopeId}/work-orders` accepts a typed intent, chat input, optional artifact references, permission plan, deadline, and `dedupKey`. The application service validates the assignment against current read models before dispatching the actor command:

1. The Team exists in the requested Scope and is active.
2. The member exists in the same Scope and belongs to that Team.
3. The member read model names the exact requested `publishedServiceId`.
4. The member has an authoritative binding with a service revision.
5. Scope binding readiness proves the requested endpoint is callable at that revision.
6. Workflow-backed members have an explicit `workflowId`.

Any missing, cross-Scope, cross-Team, stale, or non-callable relationship fails closed. Reassignment repeats the same validation, and dispatch revalidates the stored assignment immediately before invocation.

The create response is `202 Accepted` with a stable `workOrderId`, `commandId`, and `correlationId`. Its stage is `dispatch_accepted`; it does not claim that the actor committed the command, that the read model observed it, that a Run started, or that execution completed.

## Lifecycle

The Actor owns and versions every transition:

| State | Meaning and allowed progression |
|---|---|
| `accepted` | The create event was committed. Planning immediately determines whether approval is required. |
| `waiting_approval` | At least one typed permission requirement needs approval. An authorized decision advances to `ready` or terminal `denied`. |
| `ready` | Assignment and approval facts permit dispatch. Reassignment and requester cancellation remain eligible. |
| `dispatch_pending` | Stable dispatch, Run, and terminal-delivery identities were committed. An accepted Run receipt may already have supplied provenance, but no matching committed start evidence has been recorded. |
| `running` | Matching committed start evidence was recorded. An accepted dispatch receipt alone never advances a WorkOrder to this state. |
| `completed` | Matching authoritative Run evidence reported success. |
| `failed` | Dispatch failed or matching Run evidence reported failure. |
| `stopped` | Matching Run evidence reported a stopped execution. |
| `denied` | An authorized approver denied the permission plan. |
| `cancelled` | The requester cancelled before dispatch authorization. |
| `timed_out` | The WorkOrder deadline elapsed. This does not claim that a linked Run was cancelled. |

Reassign and cancel commands are eligible only in `accepted`, `waiting_approval`, or `ready`. Every mutable command carries `expectedLifecycleVersion`; stale concurrent commands fail closed in the Actor even when an application-side read model was stale.

## Permission And Approval Facts

Permission plans use typed external-action and permission-requirement records. Requirements identify their action and capability and explicitly state whether approval is required. Approval-requiring plans must name at least one approver principal id.

The Actor accepts an approval decision only while the WorkOrder is `waiting_approval`, only from a configured approver, and only at the expected lifecycle version. The approval id, decision id, principal, reason, and decision time remain committed facts and survive Actor restart.

## Dispatch And Run Evidence

Dispatch uses deterministic identities derived from `workOrderId`. Duplicate create and dispatch delivery therefore addresses the same WorkOrder and Run. The Actor stores the original create request and rejects a different request that reuses the same logical identity.

After committing `dispatch_pending`, the Actor sends an internal execute command to itself. Activation redrives that command when a restart recovers a pending dispatch. The execution adapter revalidates the authoritative assignment, invokes the exact `publishedServiceId`, and requires the accepted receipt to preserve both the requested Run id and dispatch command id. The accepted receipt records Run provenance without changing the lifecycle state. A matching committed workflow start notification advances the WorkOrder to `running`. Terminal evidence may legitimately arrive while the WorkOrder is still `dispatch_pending` when execution commits a terminal fact before a separate start fact is observed.

Terminal evidence has one authority per implementation:

- workflow services use committed start and terminal facts owned by `WorkflowRunGAgent`, delivered through its durable notification state;
- static services use `RoleGAgent`'s committed `RoleChatSessionCompletedEvent`, which is delivered to the registered `ServiceRunGAgent`;
- script services use `ScriptBehaviorGAgent`'s committed `ScriptRunOutcomeRecordedEvent`, which is delivered to the registered `ServiceRunGAgent`;
- `ServiceRunGAgent` validates implementation kind, source Actor, Run, command, correlation, and target identity before mapping a static or script terminal fact, then delivers the mapped terminal notification to the WorkOrder through its own durable outbox.

Accepted ServiceRun registration does not itself prove that execution started or guarantee a terminal result. Recovery depends on the implementation Actor replaying its committed but undispatched terminal fact and on `ServiceRunGAgent` replaying its prepared but undispatched WorkOrder notification.

The WorkOrder accepts terminal evidence only when `deliveryId`, `runId`, `runActorId`, `commandId`, and `correlationId` all match the committed accepted Run provenance. On runtime envelope paths, workflow evidence must be published by the matching workflow Run Actor, while static and script evidence must be published by the canonical ServiceRun Actor derived from the WorkOrder's authoritative scope, published service, and Run identities. Duplicate identical evidence is a no-op; conflicting evidence fails closed. A terminal notification received after WorkOrder timeout is recorded separately as late evidence and does not rewrite the `timed_out` outcome.

## Idempotency And Recovery

- `workOrderId` is a deterministic SHA-256-based identity over normalized Scope and dedup key.
- Create, dispatch, requested Run, and terminal delivery use distinct stable ids.
- Duplicate identical create commands do not reset reassignment or lifecycle state.
- Conflicting create requests at the same logical identity are rejected.
- Workflow exact-id provisioning ensures an existing Run has the same definition, Scope, origin, and inline definitions before reuse.
- A Workflow Run records its first command id; replay of that command after execution starts is a no-op, while a different command for the same Run fails closed.
- `dispatch_pending` is redriven on WorkOrder activation.
- Workflow, Role, Script, and ServiceRun notifications use actor-owned durable dispatch facts and stable delivery deduplication.

No process-local dictionary, query-time event replay, or query-time projection priming participates in these guarantees.

## Read Model And API

The Studio projection pipeline consumes committed `WorkOrderState` facts and overwrites `WorkOrderCurrentStateDocument` monotonically by the authoritative state-event version. `StateVersion` comes from the committed actor stream. `UpdatedAtUtc` comes from `WorkOrderState.UpdatedAtUtc`; projection observation time remains a separate infrastructure field.

Queries read only the current-state document provider:

- `GET /api/scopes/{scopeId}/work-orders/{workOrderId}` returns one WorkOrder.
- `GET /api/scopes/{scopeId}/work-orders` supports cursor pagination and filters for status, requester, Team, member, published service, workflow, Run, and creation time window.

Mutation endpoints use `:reassign`, `:approve`, `:deny`, `:dispatch`, and `:cancel` suffixes under the WorkOrder resource. All mutation responses remain accepted-only receipts; callers observe committed lifecycle changes through the read model.

## Boundaries

A WorkOrder does not replace Team membership, workflow definitions, published services, Runs, approvals, or artifact resources. It records typed references and coordination facts owned by its own lifecycle. It is not a general project-management or ticketing resource, and it never infers completion from command acceptance, approval, logs, or display names.
