---
title: "Admitted Agent Tool Execution"
status: accepted
owner: eanzhao
---

# ADR-0046: Admitted Agent Tool Execution

## Context

Server-owned `IAgentTool` calls can produce local or remote side effects. Previously,
callers could reach `IAgentTool.ExecuteAsync` through several execution loops and could
independently assemble approval, credential, and audit middleware. The authorization check
was therefore not guaranteed to cover every call, and a caller could classify one argument
payload but execute another.

Approval is also a continuation problem. A decision made outside the owning actor is not
enough to authorize a later replay unless the actor durably owns the pending call and the
grant is bound to that exact call. Start-once admission therefore requires a separate durable
authority; audit append results describe the decision and outcome but never grant execution.

## Decision

`Aevatar.AI.Abstractions` exposes one `IAgentToolExecutionPort`. Every server-owned tool
execution surface calls this port. `Aevatar.AI.Core.AdmittedAgentToolExecutor` is its canonical
implementation and the only production type allowed to invoke the raw
`IAgentTool.ExecuteAsync` terminal.

The request contract is fixed to `Tool`, `ArgumentsJson`, `ExecutionContext`,
`ApprovalContinuationMode`, and `ApprovalGrant`. `ExecutionContext.ExecutionOwner` is a typed,
authoritative namespace such as an actor, workflow run, channel registration, connector, or
host service. Each host boundary supplies that owner explicitly; request and call ids remain
correlation identities and are never interpreted as resource ownership. Callers may run hooks
that rewrite arguments before constructing the request. Once the request enters the port, the
executor freezes the exact argument string, derives its SHA-256 digest, and invokes
`GetCallSafety` exactly once. Credential policy, approval, the admission ledger, audit
observation, and terminal execution all use that same frozen payload and classification.

Voice tool calls use the owning actor id as `ExecutionOwner`. Their request and idempotency
identities also include the stable voice session id before the provider call id. This lets two
actors or two sessions reuse a provider-local call id without sharing admission state, while a
redelivery inside the same actor session still resolves to the same start-once identity.

Approval continuation has only two modes: `None` and `ActorOwned`. A durable actor-owned grant
must match all of `ExecutionOwner`, `ApprovalRequestId`, `RequestId`, `ToolName`, `ToolCallId`,
and `ArgumentsSha256`. A required approval without an actor-owned continuation is denied. A
pending call is resumed only from the owning actor's persisted original arguments; clients
cannot replace the owner, tool name, or arguments in the approval response.

Role chat persists a generation-fenced recovery checkpoint before any admitted tool terminal
can run. The checkpoint advances through `MODEL_READY`, `TOOL_BATCH_PREPARED`,
`WAITING_APPROVAL`, and `CONTINUATION_PREPARED`. Each prepared operation owns a stable
`operation_id` derived from the session, checkpoint generation, round, batch index, and provider
call id. Its frozen arguments and committed result are stored behind actor/session/operation-bound
secret-vault references; the actor journal contains their digests and typed recovery context, not
the payloads or live credentials. A failed intent commit therefore has zero external calls, while
a committed completion is reused without another terminal invocation.
The deterministic result reference contains a protobuf result proof and is first-result authority.
If result storage succeeds but checkpoint append fails, every retry adopts that exact payload and
reference before any external invocation; it neither overwrites the result nor creates an alias.
An expired, corrupt, or permanently unresolvable committed payload finalizes the session as
`SESSION_OUTCOME_UNCERTAIN`; only transient vault infrastructure failures remain retryable.
Once the external terminal has returned, result-store or checkpoint-append failure is a typed
post-external recovery condition, never an LLM failure. The actor keeps the session incomplete and
redelivers recovery so a sealed first result can be adopted; permanent recovery-material failure
uses `SESSION_OUTCOME_UNCERTAIN`.

Recovery obeys the tool-owned `AgentToolReplayPolicy`. Read-only and explicitly idempotent
operations may retry with the same operation and admission identity. Reconcilable operations use
the tool's typed reconciliation contract. An incomplete non-replayable operation is terminalized
as `SESSION_OUTCOME_UNCERTAIN`; recovery never invents a fresh operation id to bypass that fact.
Checkpoint generation and stage are validated before persistence and again when a self
continuation is consumed, so stale activation or caller redelivery cannot advance the actor.

An approval-required receipt atomically commits `WAITING_APPROVAL` with the matching pending
approval. It does not write a placeholder terminal completion or occupy the deterministic result
reference. The approved `ActorRecovery` execution writes the real result once, then atomically
commits `CONTINUATION_PREPARED` and clears the pending approval before dispatching the actor self
continuation. If dispatch is lost, activation redelivers from that committed checkpoint. The LLM
resume receives the original user request plus typed assistant tool-call and tool-result messages;
tool output is never concatenated into a synthetic recovery instruction.
The recovery context persists credential kind and non-secret required-slot flags. A source-readable
primary bearer is restored to `NyxIdAccessToken`. Proxy delegation that also requires a distinct
source-readable bearer must have an independently sealed reference; without one recovery fails
closed instead of substituting the delegation token.

Admission proceeds in this order:

1. Validate the typed execution owner and stable request, call, and tool identities.
2. Classify the frozen arguments once.
3. Apply credential policy.
4. Validate the exact actor-owned grant or atomically commit `WAITING_APPROVAL` with its pending
   approval and yield.
5. Atomically create the typed admission fact, including the execution owner, in
   `IAgentToolAdmissionLedger`.
6. Append the observational `RUNNING` audit fact.
7. Invoke the raw terminal only when the ledger result is `Started`.
8. Append `TERMINAL` with the actual outcome.

For a bound channel sender, a mutation may use either that sender's source-readable bearer
or a non-empty credential explicitly typed as `ProxyDelegation`. The executor preserves a
proxy delegation as an opaque credential and never promotes it to `SenderNyxIdAccessToken`.
An untyped owner credential does not satisfy the bound-sender mutation policy.

Approval, admission, running-audit, and terminal-audit ids include the execution-owner kind
and owner id. Two owners may therefore use identical request, call, tool, and argument values
without sharing a durable identity. Admission-ledger `Duplicate` means that the exact call
within one owner namespace already obtained execution permission
and must not be replayed. `Conflict` also fails closed without invoking the terminal, while
`StoreUnavailable` fails closed and may be retried because no side effect has started. Audit
append status is observational and never changes that ledger decision. Once the terminal has
been invoked, running or terminal audit failure never makes the tool call retryable: the
outcome preserves the real tool result and reports `ExecutedAuditIncomplete` when execution
succeeded but the audit facts could not be durably recorded.

The typed request identity also carries `IssuedAtUnixMs`, which is preserved through protobuf
state and actor-owned approval continuation. Every host that enables server-owned tools owns
its `AgentToolAdmission:MaximumRequestLifetime`,
`AgentToolAdmission:MaximumFutureClockSkew`, and `AgentToolAdmission:KeyPrefix` configuration.
The lifetime defaults are 24 hours and 5 minutes, and the request lifetime defines the maximum
legal replay window and cannot exceed 30 days. Distributed hosts use a durable compare-and-set
store and a host-specific key namespace: Mainnet defaults to
`aevatar:mainnet:agent-tool-admission:v1:`, while Workflow defaults to
`aevatar:workflow:agent-tool-admission:v1:` and requires
`AgentToolAdmission:RedisConnectionString` outside Development and Testing. The ledger rejects
missing, invalid, excessively future, or expired issued times before storage. Its distributed
compare-and-set key expires at the request's remaining replay deadline rather than living forever.
Expiration is therefore storage compaction, not renewed permission: after key cleanup, the
original fact remains outside its immutable replay deadline and cannot obtain `Started` again.
Development and test in-memory ledgers apply the same deadline and cleanup semantics.

The public outcome is one of `Executed`, `ExecutedAuditIncomplete`, `ApprovalRequired`,
`Denied`, or `Failed`, with an explicit failure stage, `TerminalInvoked`, `Retryable`, and
`AuditCompleted`. These fields prevent callers from guessing whether a side effect happened.

Workflow direct `tool_call` execution is an actor-owned asynchronous continuation. The
workflow actor never awaits provider I/O in its turn. Before an off-turn dispatch, it freezes
the exact request as `ToolCallProtectedMaterial`, stores that Protobuf payload behind an
owner-bound runtime-secret reference, and persists only the reference, deterministic SHA-256
digest, call/execution identities, authored deadline, continuation token, attempt, callback
leases, and a typed execution phase. Raw arguments, input, file references, external invocation
specification, and idempotency key are absent from newly written actor state, committed state
events, projections, started events, and logs. Every approval resume, retry, or activation
recovery must resolve the same reference and verify its schema, owner, digest, and exact call
identity before dispatch. Missing, corrupt, mismatched, or unavailable protected material fails
closed before the terminal.

The pending phases have one control meaning each:

- `APPROVAL_PENDING`: the exact call awaits its actor-owned grant; execution is forbidden.
- `EXECUTION_PENDING`: the exact terminal may be running or may have crossed its start-once
  boundary; a deadline here yields `OUTCOME_UNCERTAIN`.
- `RETRY_PENDING`: a typed `TerminalInvoked=false, Retryable=true` result was accepted and no
  terminal is in flight; expiry here is a confirmed pre-terminal failure, not uncertainty.

`UNSPECIFIED` never authorizes dispatch. Phase is mutable actor control state and is deliberately
excluded from protected material and its digest, so approval, execution, and retry transitions
reuse one immutable request reference. A new execution follows `protect -> persist
EXECUTION_PENDING -> install and persist the authored-deadline watchdog -> dispatch`. Activation
recovers the watchdog and any retry callback from actor state. An `EXECUTION_PENDING` recovery
may redispatch only with the same call id, execution id, protected material, idempotency key, and
`ActorRecovery` attempt kind. It never mints a new physical identity; the admitted start-once
ledger remains the authority for whether the raw terminal may run.

Internal retry is bounded to the same call identity and is permitted only for the complete typed
classification `TerminalInvoked=false, Retryable=true`. The authored deadline is computed once
and is rechecked immediately before every initial, approval, retry, and activation dispatch; no
recovery extends it. Retry exhaustion publishes a terminal step failure with outer retry
forbidden, preventing the workflow kernel from bypassing admission with a new call id.

Provider completion returns as a typed self continuation. The actor accepts it only when the
self publisher, delivery operation id, run/step/call/execution identities, attempt, and
continuation token all match durable pending state. Publication first tries the actor inbox,
then a durable self callback. If neither transport accepts the result, a fixed small number of
immediate yield-and-retry attempts may run within the authored deadline; module-local delay and
backoff are forbidden. If those attempts also fail, the already-installed authored-deadline
watchdog remains authoritative and terminates the pending execution as outcome unknown instead
of polling actor state outside Runtime.Callbacks. The accepted result is saved to the existing
completion outbox before tool and step completion events are published. A timeout and completion
race is therefore decided by the first matching actor transition; stale and late signals cannot
overwrite it. A process-local cancellation registry may stop duplicate local
dispatch and propagates cancellation to the provider on timeout, terminal cleanup, deactivation,
or module replacement, but it is never authoritative state and cancellation alone never proves
that a remote side effect did not occur.

Every terminal path removes the actor reference and requests protected-material revocation;
the bounded secret TTL is cleanup defense, not execution authority. Completion publication and
the durable completion outbox contain only the admitted result/safe failure, never the protected
request material. The complete state machine and recovery races are specified in
[Workflow ToolCall Async Continuation Design](../superpowers/specs/2026-08-14-workflow-tool-call-async-continuation-design.md).

MCP connectors preserve those admitted outcome fields through `ConnectorResponse` and the
Protobuf workflow attempt-completion event. A workflow may physically retry an explicitly
classified failure only when `Retryable=true` and `TerminalInvoked=false`; it must never mint a
new physical call id to bypass the start-once ledger. Connectors outside this admitted boundary
may omit both optional fields and retain their declared at-least-once behavior. Supplying only
one field is an incomplete safety classification and fails closed without retry.
MCP protocol `IsError` results and adapter exceptions produce typed error receipts; connector
success additionally requires an explicit `Success` receipt. Unspecified, error, denied, and
authorization-required receipts fail closed even when the terminal invocation itself completed.

Human-interaction projection delivery uses the committed source event id as part of its stable
request identity, together with the actor, run, and step ids. The call identity also binds the
delivery kind and target. A redelivery of the same committed event therefore reaches the same
start-once admission fact, while a later event from the same workflow step remains isolated.
The exact admission duplicate outcome (`tool_execution_already_started`, admission stage,
non-terminal, non-retryable) confirms projector redelivery and is treated as completed; other
failed outcomes still surface to the projection retry policy.

Client-forwarded tools are not server-owned terminals. They are recorded for continuation
and returned to the client without entering `IAgentToolExecutionPort`; their local port call
count is always zero.

NyxID aggregate tools use one closed action parser for schema, safety classification, and
execution. Only a valid JSON object that omits `action` defaults to `list`. Blank input,
malformed JSON, non-object input, non-string or blank actions, and unknown actions classify
as approval-required destructive calls and return `invalid_action` without downstream I/O.
Every mutation requires approval. In particular, a denied credential rotation performs
neither its preparatory read nor its update.

`ssh_exec` and `codex_exec` are disabled by default and require explicit host opt-in. When
exposed, `ssh_exec` and the `codex_exec` `private_ssh` target always require a matching durable
actor-owned grant. The operator-managed, isolated `managed_sandbox` target is read-only with
respect to caller infrastructure and follows its argument-level policy without a durable grant.
There is no private SSH approval bypass.

The former next-style tool-call middleware, synchronous approval handlers, audit middleware
and receipt finalizer, Responses execution wrapper, silent null adapter, legacy audit wiring,
and SSH bypass option are removed. They are not compatibility surfaces.

## Consequences

- Tool admission is complete mediation: all server-owned callers share one abstraction and
  one raw terminal.
- Classification cannot drift from execution arguments, closing the argument TOCTOU gap.
- Credential denial, approval denial, stale grants, and non-started admission facts have zero
  downstream side effects.
- Durable admission and audit identities are owner-scoped; correlation-id reuse across actors,
  workflows, channels, connectors, or host services cannot collide.
- Admission-ledger availability is fail-closed before execution; audit completeness remains
  observational and honest after execution.
- Admission records have host-owned bounded retention, while stale request facts remain denied
  after physical key cleanup.
- Actor-owned approval can be resumed without trusting client-supplied execution payloads.
- Architecture tests can enforce the single raw terminal and the known port callers across
  the solution graph.

Related references:

- [Platform Audit Trail](../canon/audit-trail.md)
- [NyxID Connected-Service LLM Tools](../canon/nyxid-connected-service-tools.md)
- [NyxID Responses Direct](../canon/nyxid-responses-direct.md)
- [Workflow Primitives](../canon/workflow-primitives.md)
- [Workflow ToolCall Async Continuation Design](../superpowers/specs/2026-08-14-workflow-tool-call-async-continuation-design.md)
