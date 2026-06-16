---
title: "Workflow Saga / Compensation Protocol"
status: proposed
owner: eanzhao
---

# ADR-0034: Workflow Saga / Compensation Protocol

## Context

The workflow engine today is **forward-only**. A run advances step by step; when
a step fails it can retry (`StepRetryPolicy`), branch forward (`on_error: skip /
fallback`), or — at the run level — fork and re-execute from the failed step
(`WorkflowRunForkRequestedEvent`, see `WorkflowRunGAgent.cs`). What it never does
is **undo the side effects of steps that already succeeded**. If step 2 created
an order and step 3 (charge payment) fails terminally, the order stays created.

This is a deliberate gap, not an oversight:

- [ADR-0017 §Q5](0017-studio-team-first-class-aggregate.md) evaluated a
  "Saga (request-accept-revert)" protocol for Studio archive and **rejected it**
  as two-phase-commit complexity unnecessary for that feature, but explicitly
  left the door open: *"If a hard rejection is later required … it can be added
  as a separate ADR introducing the saga protocol — explicitly, not implicitly."*
- [ADR-0006 §2-B](0006-multi-agent-evolution.md) sketched, as an **optional**
  Phase-2 item, a `CompensationRequestEvent` that drives a parent workflow's
  fallback step when a sub-workflow fails and is not retryable. It was never
  implemented.
- [ADR-0002](0002-mainnet-architecture.md) states the governance principle that
  workflows containing non-deterministic LLM calls *"应通过幂等键、重试策略、补偿
  机制治理"* (govern via idempotency keys, retry policies, **and compensation**).
  Idempotency-via-dedup and retry exist; compensation does not.

A code audit confirms the gap concretely:

- **No compensation declaration.** `StepDefinition` (`Primitives/StepDefinition.cs`)
  carries retry and `on_error` policy but no notion of "how to undo this step".
- **No completed-step ledger.** `WorkflowExecutionKernelState` /
  `WorkflowRunState` (`workflow_state.proto`) persist `current_step_id`,
  `retry_attempts_by_step_id`, `fork_attempt`, and `final_error` — but nothing
  records *which steps already committed side effects in what order*, which is
  the minimum needed to compensate in reverse.
- **No compensation event.** `workflow_execution_messages.proto` defines
  `StepCompletedEvent`, `WorkflowCompletedEvent`, `WorkflowRunForkRequestedEvent`,
  etc. A repo-wide grep for `compensat|saga` in `src/workflow` returns nothing.
- **At-least-once side effects.** `docs/canon/aevatar-channel-architecture.md`
  already admits outbound side effects are at-least-once and rely on
  platform-level idempotency keys; the workflow side has no first-class
  idempotency key threaded into `tool_call` / `connector_call` dispatch, so a
  fork-retry can re-execute a side effect.

This ADR is the explicit saga ADR that ADR-0017 and ADR-0006 anticipated. It
defines a **compensation-based saga** owned by the existing run actor — not a new
distributed-transaction subsystem.

## Constraints (must honor)

From CLAUDE.md and prior ADRs:

- **Actor 即业务实体 / 单一权威拥有者.** A run is already a business entity owned by
  `WorkflowRunGAgent`. Compensation is part of a run's termination lifecycle, so
  it must be owned by that same actor — **not** split into a separate "saga
  coordinator" actor (that would fracture one entity into two).
- **self-continuation 事件化 / 延迟超时事件化.** Compensation steps must be driven
  through the actor's own inbox via self-messages and the existing durable
  callback/retry machinery (`IActorRuntimeCallbackScheduler`,
  `WorkflowStepRetryBackoffFiredEvent`). No inline loops, no callback-thread
  state mutation.
- **显式对账.** Every compensation trigger carries `run_id + step_id` and is
  reconciled against live run state; stale events are rejected
  (`StaleStepCompletionRejectedEvent` pattern).
- **序列化统一 Protobuf.** New events, ledger entries, and state additions are
  proto-first.
- **强类型内核.** Compensation is a typed declaration on the step contract, not a
  string convention in a generic bag.
- **projection 只消费 committed 事实.** Compensation status surfaced for querying
  goes through the normal projection pipeline / read model, never query-time
  replay.
- **读写分离.** "Is this saga compensating / dead-lettered?" is answered by a read
  model, not by a synchronous query into the run actor.

## Open Questions and Recommended Decisions

### Q1. Who owns the saga — a new coordinator actor or the run actor?

**Recommendation: the existing `WorkflowRunGAgent`.** A run is the saga. Its
state already holds the execution context, variables, and sub-workflow bindings.
Introducing a separate coordinator would violate "Actor 即业务实体" and create a
cross-actor consistency problem (who is authoritative for run termination?) that
does not exist today. Compensation is a terminal phase of the run's own state
machine:
`WORKFLOW_SAGA_STATUS_UNSPECIFIED → WORKFLOW_SAGA_STATUS_COMPENSATING → WORKFLOW_SAGA_STATUS_COMPENSATED_FAILED | WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER`.

### Q2. Choreography or orchestration?

**Recommendation: orchestration, reverse-order, explicit per-step declaration.**
The run actor walks its completed-step ledger backward and dispatches each step's
declared compensation. We do **not** broadcast compensation events for actors to
react to independently (choreography) — that reintroduces the generic
event-bus-as-coordinator anti-pattern. We do **not** auto-infer compensations
(there is no safe automatic inverse for an arbitrary `tool_call`).

### Q3. What triggers compensation?

**Recommendation: terminal run failure with a non-empty compensable ledger.**
After per-step retry, `on_error`, and run-level `fork_from_failed_step` are
exhausted and the run would emit `WorkflowCompletedEvent { success=false }`, the
run enters the compensation phase **iff** at least one already-completed step
declared a `compensation`. `on_error: skip / fallback` remain forward recovery
and do **not** trigger compensation — compensation is the all-or-nothing path a
workflow opts into, not a replacement for graceful degradation. A workflow with
no compensation declarations behaves exactly as today (fully backward
compatible).

### Q4. How does a compensation action execute?

**Recommendation: as a normal step, dispatched via self-continuation.** Each
compensation is itself a workflow step (referenced by `step_id` or declared
inline) re-using `StepRequestEvent` dispatch, the retry/backoff machinery, and
step timeouts. The original step's captured output + idempotency key are made
available to its compensation so the undo is deterministic. Compensations run
**sequentially in reverse ledger order** (compensation can have ordering
dependencies; parallel undo is out of scope for v1).

### Q5. How are side effects made safe to retry and to compensate?

**Recommendation: a first-class idempotency key on side-effecting steps.**
`tool_call` and `connector_call` steps get an `idempotency_key` (author-supplied
expression, else deterministic `runId:stepId:attempt`) threaded into the
invocation, so fork-retry and compensation re-delivery collapse to one effect at
the boundary. This is **at-least-once + idempotent**, not exactly-once — we
explicitly inherit the FLP boundary already acknowledged in
`docs/canon/aevatar-channel-architecture.md`. The key is advisory to the callee;
the engine guarantees a stable key, the connector/tool/platform guarantees dedup.

### Q6. How do sub-workflows participate?

**Recommendation: a child run compensates itself, then reports outcome; no
distributed two-phase commit across parent and child.** When a child run fails
terminally, it runs its own compensation ledger first, then surfaces
`SubWorkflowInvocationCompletedEvent { success=false }` with a `compensated`
flag. To the parent, `workflow_call` is just another completed/failed step: if
the parent declared a compensation for that `workflow_call` step, it runs as part
of the parent's reverse walk. There is no global saga log spanning the run tree;
each run is authoritative for its own compensation.

### Q7. What happens when a compensation itself fails?

**Recommendation: bounded retry, then a durable dead-letter — never log-and-drop.**
A compensation step that exhausts its retries does not silently disappear (the
current failure-handling weakness). The run transitions to a terminal
`compensation_dead_letter` status and emits a durable
`WorkflowCompensationFailedEvent` carrying the run, the failed compensation
step, and the still-uncompensated remainder of the ledger. This terminal state
is projected to a read model for operator visibility and is the explicit
"needs human intervention" signal.

### Q8. What is explicitly out of scope?

- Distributed two-phase commit / XA / global locks across actors.
- Cross-run or global sagas spanning unrelated runs (each run is its own saga).
- Exactly-once side effects (we provide at-least-once + idempotency key only).
- Automatic inference of a step's inverse (compensation is always author-declared).
- Compensating read-only steps (steps with no declared compensation are skipped
  during the reverse walk).
- Parallel/concurrent compensation (v1 is strictly sequential reverse order).

## Decision

Introduce an **orchestrated, compensation-based saga** as a terminal phase of the
workflow run lifecycle, owned by `WorkflowRunGAgent`:

1. Steps may declare a typed `compensation` (a reference to a step that undoes
   them) and side-effecting steps may declare an `idempotency_key`.
2. The run actor maintains an ordered, persisted **completed-step ledger** of
   successfully-committed steps that have a compensation, plus the captured
   output and idempotency key each compensation needs.
3. On terminal failure with a non-empty compensable ledger, the run enters a
   `compensating` phase and dispatches each declared compensation in reverse
   order via self-continuation, reusing existing retry/timeout machinery.
4. A successful reverse walk ends the run as `compensated_failed`
   (`WorkflowCompletedEvent { success=false }` + `WorkflowCompensationCompletedEvent`).
5. A compensation that exhausts retries ends the run as
   `compensation_dead_letter` (`WorkflowCompensationFailedEvent`), surfaced via
   read model for operators.

Workflows that declare no compensations are unaffected — this is a pure,
opt-in extension of the existing forward-only model.

## Locked Rules

1. **Single owner.** `WorkflowRunGAgent` is the sole authority for a run's
   compensation. No separate saga-coordinator actor is introduced.
2. **Opt-in.** Compensation is driven only by author-declared `compensation`
   references. Absent declarations ⇒ today's behavior, byte-for-byte.
3. **Reverse, sequential.** Compensations execute in strict reverse order of the
   completed-step ledger, one at a time. No parallel undo in v1.
4. **Event-sourced phase.** The compensation phase is a sequence of committed
   events (`CompensationRequestEvent`, `CompensationStepCompletedEvent`,
   `WorkflowCompensationCompletedEvent` / `WorkflowCompensationFailedEvent`) on
   the run's own stream — replayable, not a transient in-memory loop.
5. **Self-continuation only.** Each compensation step is dispatched through the
   actor inbox and the existing durable callback/retry path. No callback-thread
   state mutation, no `Task.Run` advancing business state.
6. **Explicit reconciliation.** Every compensation event carries `run_id +
   step_id (+ attempt)`; the run rejects stale/duplicate compensation completions
   the same way `StaleStepCompletionRejectedEvent` guards forward steps.
7. **Idempotency key is stable, dedup is the callee's job.** The engine
   guarantees a deterministic `idempotency_key` per side-effecting attempt; it
   does not itself dedup. No exactly-once claim is made or implied.
8. **No silent drop.** A compensation that exhausts retries must reach the
   durable `compensation_dead_letter` terminal + `WorkflowCompensationFailedEvent`.
   Logging a failure and continuing is forbidden.
9. **Read-side honesty.** Saga/compensation status is exposed only through the
   projection pipeline read model, carrying the authoritative run state version.
   No synchronous run-actor query, no query-time replay.

## Required Contract

### Authoring (`StepDefinition` — `Primitives/StepDefinition.cs`, + YAML schema)

```yaml
steps:
  - id: create_order
    type: tool_call
    tool: orders.create
    idempotency_key: "${run_id}:${step_id}"   # optional; defaults to runId:stepId:attempt
    compensation: cancel_order                  # step id that undoes this step
  - id: charge_payment
    type: tool_call
    tool: payments.charge
    compensation: refund_payment
  # ... compensation steps are ordinary steps, referenced above, skipped on the forward path
  - id: cancel_order
    type: tool_call
    tool: orders.cancel
  - id: refund_payment
    type: tool_call
    tool: payments.refund
```

- `compensation` is a typed optional reference resolved at compile time against
  the same workflow's step set (validation error if the target step id does not
  exist).
- A compensation target step is not auto-inserted into the forward path; it runs
  only during the reverse walk.

### Proto — state (`workflow_state.proto`)

```proto
// One entry per successfully-committed step that declared a compensation,
// in commit order. The reverse walk consumes this back-to-front.
message CompletedStepLedgerEntry {
  string step_id            = 1;
  string compensation_step_id = 2;
  string idempotency_key    = 3;
  string captured_output    = 4;   // the step output the compensation may need
  int64  committed_at_unix_ms = 5;
}

// Added to WorkflowRunState (run-owned, durable):
//   repeated CompletedStepLedgerEntry compensable_ledger = N;
//   int32  compensation_cursor = N+1;   // index into ledger, walked downward
//   WorkflowSagaStatus saga_status = N+2; // UNSPECIFIED for non-compensating runs
//
enum WorkflowSagaStatus {
  WORKFLOW_SAGA_STATUS_UNSPECIFIED = 0;
  WORKFLOW_SAGA_STATUS_RUNNING = 1;
  WORKFLOW_SAGA_STATUS_COMPENSATING = 2;
  WORKFLOW_SAGA_STATUS_COMPENSATED_FAILED = 3;
  WORKFLOW_SAGA_STATUS_COMPENSATION_DEAD_LETTER = 4;
}
```

### Proto — events (`workflow_execution_messages.proto`)

```proto
message CompensationRequestEvent {
  string run_id              = 1;
  string failed_step_id      = 2;   // the terminal failure that triggered the saga
  string compensation_step_id = 3;  // the compensation to run now (reverse cursor)
  string idempotency_key     = 4;
  string captured_output     = 5;
}

message CompensationStepCompletedEvent {
  string run_id              = 1;
  string compensation_step_id = 2;
  bool   success             = 3;
  string error               = 4;
}

message WorkflowCompensationCompletedEvent {
  string run_id              = 1;
  int32  compensated_steps   = 2;   // count successfully undone
}

message WorkflowCompensationFailedEvent {
  string run_id              = 1;
  string failed_compensation_step_id = 2;
  int32  remaining_uncompensated     = 3;  // ledger entries left unwound
  string error               = 4;
}
```

### Side-effect idempotency

`tool_call` / `connector_call` dispatch threads `idempotency_key` into the
invocation envelope (alongside the existing `execution_id` on
`PendingConnectorCallState` / tool dispatch). The key is deterministic across
fork-retry and compensation re-delivery.

### Read model + guards

- A `WorkflowRunSagaStatusDocument` (or a field on the existing run read model)
  projects `saga_status` + dead-letter detail from committed run events.
- CI guards:
  - terminal failure with a non-empty compensable ledger **must** route through
    the compensation phase, not directly to `WorkflowCompletedEvent`;
  - compensation dispatch **must** go through self-continuation (no inline);
  - a `compensation` declaration **must** resolve to an existing step id
    (compile-time);
  - compensation exhaustion **must** emit `WorkflowCompensationFailedEvent`
    (no log-and-drop).

## Consequences

- Workflows gain opt-in all-or-nothing semantics without a distributed-transaction
  subsystem; the run actor stays the single authority.
- Run state grows by the compensable ledger (bounded by the number of
  side-effecting steps with declared compensations, not total steps).
- Authors take on the responsibility of writing correct inverse steps and stable
  idempotency keys — the engine guarantees ordering, durability, and at-least-once
  delivery, not business correctness of the undo.
- Failure is now honest: a stuck saga has a durable terminal state and an operator
  surface instead of a logged-and-dropped error.
- Sub-workflows compose without a global saga log; each run compensates itself.
- No change to existing workflows that declare no compensations.

## Cutover Order

1. Land this ADR (proposed → accepted).
2. **Contract first**: add the `compensation` / `idempotency_key` authoring
   fields (`StepDefinition` + YAML schema + compile-time validation), the
   `CompletedStepLedgerEntry` + saga fields on run state, and the four
   compensation proto events. Build + proto regen + parse/validate tests.
3. **Orchestration**: implement the reverse-walk compensation phase in
   `WorkflowRunGAgent` / `WorkflowExecutionKernel`, dispatching each compensation
   via self-continuation and reconciling `CompensationStepCompletedEvent` against
   the cursor. Tests for reverse order, idempotent re-compensation, stale-event
   rejection.
4. **Idempotency keys**: thread `idempotency_key` through `tool_call` /
   `connector_call` dispatch.
5. **Dead-letter + read model**: `WorkflowCompensationFailedEvent`, the terminal
   `compensation_dead_letter` status, and the saga-status projection + read model.
6. **Guards + docs**: the CI guards above; update `docs/canon/workflow-*`; add a
   superseding note to ADR-0006 §2-B that the "optional" compensation protocol is
   now specified here.

Each step is gated by build + targeted tests + the relevant workflow CI guards.

## Non-Goals

- Distributed two-phase commit, XA, or cross-actor locks.
- Global/cross-run sagas (each run is its own saga).
- Exactly-once side effects.
- Automatic inference of step inverses.
- Parallel compensation.
- Retrofitting compensation onto existing workflows that do not declare it.

## Outcome

After this ADR is accepted and implemented, a workflow author can declare
`compensation` on side-effecting steps and rely on the run actor to unwind
committed effects in reverse order on terminal failure, with idempotent
re-delivery, a durable dead-letter for stuck sagas, and read-model visibility —
closing the "幂等键 ✅ 重试 ✅ 补偿 ❌" gap named in ADR-0002 and realizing the saga
protocol that ADR-0017 and ADR-0006 deferred to a dedicated ADR.
