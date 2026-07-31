---
title: "Admitted Agent Tool Execution"
status: accepted
owner: eanzhao
---

# ADR-0045: Admitted Agent Tool Execution

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

Approval continuation has only two modes: `None` and `ActorOwned`. A durable actor-owned grant
must match all of `ExecutionOwner`, `ApprovalRequestId`, `RequestId`, `ToolName`, `ToolCallId`,
and `ArgumentsSha256`. A required approval without an actor-owned continuation is denied. A
pending call is resumed only from the owning actor's persisted original arguments; clients
cannot replace the owner, tool name, or arguments in the approval response.

Admission proceeds in this order:

1. Validate the typed execution owner and stable request, call, and tool identities.
2. Classify the frozen arguments once.
3. Apply credential policy.
4. Validate the exact actor-owned grant or append `WAITING_APPROVAL` and yield.
5. Atomically create the typed admission fact, including the execution owner, in
   `IAgentToolAdmissionLedger`.
6. Append the observational `RUNNING` audit fact.
7. Invoke the raw terminal only when the ledger result is `Started`.
8. Append `TERMINAL` with the actual outcome.

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
state and actor-owned approval continuation. Mainnet owns `AgentToolAdmission:MaximumRequestLifetime`
and `AgentToolAdmission:MaximumFutureClockSkew`; their defaults are 24 hours and 5 minutes, and
the request lifetime defines the maximum legal replay window and cannot exceed 30 days. The ledger rejects missing, invalid, excessively future,
or expired issued times before storage. Its distributed compare-and-set key expires at the
request's remaining replay deadline rather than living forever. Expiration is therefore storage
compaction, not renewed permission: after key cleanup, the original fact remains outside its
immutable replay deadline and cannot obtain `Started` again. Development and test in-memory
ledgers apply the same deadline and cleanup semantics.

The public outcome is one of `Executed`, `ExecutedAuditIncomplete`, `ApprovalRequired`,
`Denied`, or `Failed`, with an explicit failure stage, `TerminalInvoked`, `Retryable`, and
`AuditCompleted`. These fields prevent callers from guessing whether a side effect happened.

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
