# Durable Studio Workflow Schedule Provisioning

## Problem

The one-call Studio provisioning path used to bind a workflow revision and then
write its schedule in the same HTTP or tool request. Schedule authorization reads
workflow evidence from a projection. A newly admitted revision is not immediately
visible there, so preflight returned
`workflow_authorization_evidence_not_found`.

Retrying the whole request did not converge. Admission incorporated current
catalog evidence into the revision identity, each retry minted another revision,
and schedule preflight always chased a revision that had not yet projected. The
result was a deterministic livelock rather than ordinary transient failure.

## Decision

The request path admits and binds exactly once, then submits a secret-free schedule
provisioning intent to `StudioMemberGAgent`. That actor owns the continuation and
waits until its authoritative state observes the exact
`publishedServiceId + revisionId` binding pair.

After binding readiness:

1. The actor resolves a one-shot UTC fire time once, when applicable, and commits it.
2. The actor starts a numbered background execution attempt.
3. The executor performs authorization preflight against projected evidence.
4. Projection lag produces a typed retryable continuation, not a new admission.
5. A durable self-timeout starts the next attempt with the same revision and timing.
6. Success or terminal failure returns as a typed message to the actor inbox.

The HTTP and tool response is an honest accepted acknowledgement. It contains
`scheduleProvisioningId` and `scheduleProvisioningStatus`; `scheduleId` is absent
until the actor records successful schedule creation or replacement.

## Durable Skill Confirmation Entry Point

`POST /api/workflow/skills/{guid}/schedule` is the production acceptance entry
point and has two distinct phases:

1. The first call omits `workflowConfirmationToken`. It resolves the single root
   workflow and calls `ISkillWorkflowConfirmationPort` with execution mode
   `Durable`. The endpoint returns HTTP 200 with `confirmation_required`, a fresh
   token, and the typed workflow previews. It must not start provisioning.
2. The caller submits the same request with that fresh token. The confirmation
   result is converted to typed explicit-request confirmations and the
   `WorkflowCapabilityAdmissionContext` continues to use execution mode
   `Durable`. The provisioning service binds the reviewed contracts to the exact
   workflow and revision identities before accepting the actor-owned intent.
3. Accepted provisioning returns HTTP 202. While pending, `scheduleId` is null,
   the receipt includes `bindingRunId`, `scheduleProvisioningId`, and
   `scheduleProvisioningStatus`, and `Location` points to the member read model.
   If the schedule is already visible, `Location` points to
   `/api/schedules/{scheduleId}`.

Neither confirmation, HTTP 202, nor a pending member read model proves schedule
creation. Success requires a committed member read model with provisioning status
`succeeded`, non-empty schedule and operation ids, and observation of a real
scheduled workflow run.

## Sequence

```mermaid
%%{init: {"maxTextSize": 100000, "flowchart": {"useMaxWidth": false, "nodeSpacing": 10, "rankSpacing": 50}, "themeVariables": {"fontSize": "10px"}}}%%
sequenceDiagram
    participant C as "HTTP or tool caller"
    participant A as "Provisioning application service"
    participant M as "StudioMemberGAgent"
    participant B as "Binding pipeline"
    participant E as "Schedule executor"
    participant P as "Projection and schedule ports"

    C->>A: "Provision workflow"
    A->>A: "Admit once; derive revision"
    A->>B: "Dispatch exact binding"
    A->>M: "Persist secret-free schedule intent"
    A-->>C: "202 accepted + provisioning identity"
    B-->>M: "Committed exact binding revision"
    M->>M: "Persist one-shot time and attempt"
    M->>E: "Execute out of turn"
    E->>P: "Preflight projected evidence"
    alt "Projection pending"
        P-->>E: "Retryable typed result"
        E-->>M: "Retry deferred(attempt)"
        M->>M: "Durable self-timeout"
    else "Evidence visible"
        P-->>E: "Authorization plan"
        E->>P: "Create or replace pinned schedule"
        E-->>M: "Succeeded(scheduleId, operationId, attempt)"
    end
    M-->>P: "Committed member state projection"
```

## Authority And Retry Rules

- `StudioMemberGAgent` is the sole owner of provisioning status, attempt count,
  resolved one-shot timing, terminal result, and failure detail.
- The executor is stateless. It returns `success`, `retryable`, or `terminal failure`
  and never mutates member state from its callback.
- Durable timeout payloads carry `provisioningId + observedAttempt`. Stale watchdogs
  and late completions cannot advance a newer attempt.
- A failed or rejected target binding terminates provisioning without calling the
  schedule port.
- Projection lag retries the same admitted revision. It never re-enters workflow
  admission and therefore cannot mint a moving revision target.
- A live deterministic schedule is replaced and pinned to the new revision. An
  explicit delete tombstone advances the id from `provision-{serviceId}` to `.2`,
  `.3`, and so on.

## Credential Boundary

Raw bearer tokens are request-bound and must not enter actor state. The durable
intent stores only authenticated owner identity, subject reference, and
`VerifiedBindingId`. During a background attempt, the schedule port uses that
binding to issue a short-lived provisioning token and materialize the dedicated
scheduled invocation credential.

## Read Model

`StudioMemberCurrentStateDocument` projects the actor-owned status as:

- provisioning id and status;
- target revision id;
- schedule id and operation id after success;
- attempt count;
- failure code and message;
- authoritative actor state version and update time.

Queries only read this materialized model. They do not poll binding actors, replay
events, prime projections, or infer completion from the HTTP acknowledgement.

## Verification

Coverage must include:

- first-call Durable confirmation without provisioning;
- second-call Durable admission with the reviewed confirmation token;
- HTTP 202 pending receipt and member read-model Location;
- waiting for the exact binding before execution;
- reuse of one-shot timing and revision across retries;
- stale attempt completion rejection;
- binding failure termination;
- projection-pending retry classification;
- create, live replacement, and tombstone generation behavior;
- HTTP, tool receipt, projector, and query response semantics.
