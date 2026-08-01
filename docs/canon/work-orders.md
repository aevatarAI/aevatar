---
title: "Work Orders"
status: active
owner: eanzhao
---

# Work Orders

A `WorkOrder` is a durable, Scope-owned, user-visible request to have one Team
member invoke one validated published service. It owns the user intent and the
coordination lifecycle around that request. It does not own the execution,
approval, Team binding, or result content referenced by the request.

`WorkOrderGAgent` is the only authority for WorkOrder state and lifecycle
transitions. Its committed Protobuf events and state are authoritative.
`WorkOrderCurrentStateDocument` is an actor-scoped current-state replica used
only for queries.

## Independent Product Requirement

A WorkOrder is not reducible to a dispatch receipt, schedule, or Run:

- it exists before any Run and does not require dispatch at creation time;
- it can be reassigned or cancelled before dispatch without creating a Run;
- it may omit a deadline and remain durable indefinitely;
- it recovers pending coordination from committed state after an Actor or Host
  restart; and
- its committed history remains after terminal completion.

A dispatch receipt answers whether one command was accepted. A Run answers what
happened during one execution. A schedule owns recurring trigger and automation
policy. None of those resources naturally owns the durable user request above.

## Authority Boundaries

| Authority | Owned facts |
|---|---|
| `WorkOrderGAgent` | Requester intent, typed input and declared-output references as intent, validated assignment snapshot, deterministic dispatch identities, reassignment, cancellation, optional timeout, dispatch coordination, WorkOrder lifecycle, accepted Run link, and validated Run outcome reference. |
| Team/member read models | Membership, Team ownership, exact `publishedServiceId`, binding revision, implementation kind, deployment readiness, and current callability. |
| Workflow/Run execution owner | Approval plan, approvers, suspended external-action continuation, approval decisions, execution state, start time, output, error, and terminal execution facts. |
| `ContentArtifactGAgent` | Actual result content, revisions, provenance, citations, retention, and redaction. |
| `ScheduledDispatchGAgent` | Recurring triggers, credentials, authorization refresh, automation policy, and scheduled invocation lifecycle. |
| `WorkOrderCurrentStateDocument` | Read-only projection of committed WorkOrder state; never an authority. |

The WorkOrder contract contains no permission plan, approver identity,
approve/deny decision, Run output, Run error, Run start timestamp, or actual
result artifact. Declared output references remain declarations of user intent;
they are never promoted into actual results.

## Identity Model

The following identities remain separate typed fields throughout commands,
Actor state, projection, and API contracts:

| Identity | Meaning |
|---|---|
| `workOrderId` | Stable logical WorkOrder identity derived from canonical `scopeId + dedupKey`. |
| requester principal | Authenticated principal that requested the work; not implicitly a Team member. |
| `teamId` | Team against which the assignment is validated. |
| `memberId` | Stable Team member assigned responsibility. |
| `workflowId` | Workflow definition identity for a workflow-backed member. |
| `publishedServiceId` | Exact callable service identity returned by the authoritative member read model. |
| `serviceRevisionId` | Service revision validated with the assignment. |
| `dispatchCommandId` | Stable identity for the authorized dispatch. |
| `requestedRunId` | Stable logical Run identity requested by the WorkOrder. |
| `runActorId` | Opaque Actor address returned by accepted execution. |
| `terminalDeliveryId` | Stable idempotency identity for Run outcome delivery. |
| artifact references | Typed input and declared-output identities owned as request intent only. |

Callers must not derive `publishedServiceId` from `memberId`, `workflowId`,
display names, Actor id text, or route structure. They must not treat a declared
artifact reference as proof that an actual artifact exists.

## Creation And Assignment Validation

`POST /api/scopes/{scopeId}/work-orders` accepts a typed intent, chat input,
optional artifact references, optional deadline, and `dedupKey`. The
application service validates the assignment against current read models before
dispatching the Actor command:

1. The Team exists in the requested Scope and is active.
2. The member exists in the same Scope and belongs to that Team.
3. The member read model names the exact requested `publishedServiceId`.
4. The member has an authoritative binding with a service revision.
5. Scope binding readiness proves the requested endpoint is callable at that
   revision.
6. Workflow-backed members have an explicit `workflowId`.

Missing, cross-Scope, cross-Team, stale, or non-callable relationships fail
closed. Reassignment repeats the same validation. Dispatch revalidates the
stored assignment immediately before invocation.

The create response is `202 Accepted` with stable `workOrderId`, `commandId`,
and `correlationId` values. Its stage is `dispatch_accepted`; it does not claim
that the Actor committed the command, a read model observed it, a Run started,
or execution completed.

## Lifecycle

The Actor owns and versions every transition:

| State | Meaning and allowed progression |
|---|---|
| `accepted` | The create event was committed. The same command records readiness without requiring a Run or approval resource. |
| `ready` | The WorkOrder can be reassigned, cancelled, or authorized for dispatch. |
| `dispatch_pending` | Stable dispatch, requested Run, and terminal-delivery identities were committed. An accepted Run link may exist, but no matching committed start observation has advanced the WorkOrder. |
| `running` | A matching authoritative Run-start notification advanced the WorkOrder lifecycle. WorkOrder does not copy the Run start timestamp. |
| `completed` | A matching authoritative Run outcome reported success. |
| `failed` | Dispatch failed, or a matching authoritative Run outcome reported failure. A Run error remains in the Run authority. |
| `stopped` | A matching authoritative Run outcome reported a stopped execution. |
| `cancelled` | The requester cancelled before dispatch authorization. |
| `timed_out` | A supplied WorkOrder deadline elapsed. This does not claim that a linked Run was cancelled. |

Reassign and cancel are eligible only before dispatch authorization. Every
mutable command carries `expectedLifecycleVersion`; stale concurrent commands
fail closed in the Actor even when an application-side read model is stale.

There is no WorkOrder `waiting_approval` or `denied` state. Execution-level
approval belongs to the Workflow/Run owner that holds the exact external action
and suspended continuation.

## Dispatch And Run References

Dispatch uses deterministic identities derived from `workOrderId`. Duplicate
create and dispatch deliveries therefore address the same WorkOrder and Run.
The Actor stores the original create request and rejects a different request
that reuses the same logical identity.

After committing `dispatch_pending`, the Actor sends an internal execute command
to itself. Activation redrives pending execution. The execution adapter
revalidates the authoritative assignment, invokes the exact
`publishedServiceId`, and requires the accepted receipt to preserve the
requested Run and dispatch command identities. Accepted and failed execution
continuations are trusted only when their envelope publisher is the canonical
WorkOrder execution worker and their WorkOrder, dispatch-command, and requested
Run identities match the pending dispatch.

Execute, retry, and timeout signals are internal WorkOrder continuations. The
Actor accepts a matching signal only when its envelope publisher is that same
WorkOrder Actor; correlation keys alone do not authorize external callers to
advance or retry the lifecycle.

The accepted receipt is stored only as `WorkOrderRunLink`:

- `runId`
- `runActorId`
- `commandId`
- `correlationId`
- `revisionId`
- `deploymentId`
- `acceptedAtUtc`

Run start and terminal notifications remain facts owned by their Run producers.
WorkOrder observes them only to advance its own lifecycle. A terminal outcome
may arrive before a separate start notification; the terminal lifecycle wins,
and a later start notification cannot rewrite it.

The only terminal observation stored by WorkOrder is
`WorkOrderRunOutcomeReference`, containing exactly:

- `deliveryId`
- `runId`
- `runActorId`
- `commandId`
- `correlationId`
- `outcome`
- `terminalAtUtc`

The WorkOrder accepts the reference only when delivery, Run, Actor, command, and
correlation identities all match the accepted Run link and the envelope
publisher is the authoritative Run producer. Duplicate identical references are
idempotent; conflicting references fail closed. A reference received after a
WorkOrder timeout is stored as `lateRunOutcome` without rewriting `timed_out`.

Consumers resolve the Run authority for start time, output, error, and detailed
execution status. They resolve `ContentArtifactGAgent` for actual result content
and provenance.

## Deadline And Recovery

`timeoutAtUtc` is optional. When supplied, it must be later than
`requestedAtUtc`, and the Actor schedules its durable timeout. When omitted, the
Actor stores no deadline and schedules no WorkOrder timeout.

The existing Workflow and ServiceRun completion-notification contracts require
a positive numeric expiration. At that transport boundary only, an omitted
WorkOrder deadline maps to `long.MaxValue`. That transport sentinel is not
persisted or exposed as a WorkOrder product deadline.

Dispatch retry uses the existing bounded exponential backoff. A supplied
deadline caps the retry delay and eventually drives `timed_out`. Without a
deadline, retries use the normal per-attempt delay without inventing product
expiry.

Workflow, Role, Script, and ServiceRun producers retain their actor-owned
durable delivery facts and stable delivery operation identities. WorkOrder does not
copy their payloads to gain recovery.

## Read Model And API

The Studio projection pipeline consumes committed `WorkOrderState` facts and
monotonically overwrites `WorkOrderCurrentStateDocument` by authoritative Actor
state-event version. `StateVersion` comes from the committed Actor stream.
`UpdatedAtUtc` comes from `WorkOrderState.UpdatedAtUtc`; projection observation
time remains a separate infrastructure field.

Queries read only the current-state document provider:

- `GET /api/scopes/{scopeId}/work-orders/{workOrderId}` returns one WorkOrder.
- `GET /api/scopes/{scopeId}/work-orders` supports cursor pagination and filters
  for status, requester, Team, member, published service, workflow, Run, and
  creation time window.

Mutation endpoints use `:reassign`, `:dispatch`, and `:cancel` suffixes. There
are no WorkOrder `:approve` or `:deny` endpoints. All mutation responses remain
accepted-only receipts; callers observe committed lifecycle changes through the
read model.

No process-local dictionary, query-time event replay, query-time projection
priming, compatibility endpoint, deprecated message, or parallel old/new
contract participates in the WorkOrder path.

ContentArtifact interoperability preserves the existing WorkOrder reference
shape. `artifact_kind="content-artifact"` identifies the resource family,
`artifact_id` and `revision_id` retain the exact ContentArtifact identities, and
the optional `uri` may use the same-Scope revision metadata path. WorkOrder does
not copy the ContentArtifact hash, content, provenance, citations, or lifecycle
into its own authority. See [Content Artifacts](content-artifacts.md).

## Non-Goals

A WorkOrder is not a project-management ticket, recurring schedule, approval
resource, Run mirror, or artifact store. It never infers completion from command
acceptance, approval, logs, display names, declared result references, or copied
terminal payloads.
