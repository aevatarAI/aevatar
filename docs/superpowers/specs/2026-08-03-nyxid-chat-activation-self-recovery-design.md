# NyxID Chat Activation Recovery Fix

**Date:** 2026-08-03
**Status:** Approved; implementation under final verification
**Scope:** Repair activation recovery without removing or weakening any NyxID Chat feature

## Incident And Introducing Change

Commit `1458a5bdbda5aefd17f8b5a0c43efee1618970f1`
(`Harden NyxIdChat recovery semantics`) introduced the failing path. Its product
goal remains required: after a restart, NyxID Chat must reconcile committed work
without repeating an LLM request or a potentially effectful tool call. It also
added safe postcondition redispatch, delivery-loss handling, full operation
key/version checks, and secret-free durable recovery messages.

The defect was transport reentrancy. During `OnActivateAsync`, a NyxID actor sent
its recovery envelope to its own ID through `IActorDispatchPort`. The Orleans
adapter called `IsInitializedAsync()` before stream handoff. For self-dispatch,
that probe waited for the same activation that was waiting for the probe:

```text
actor activation -> self dispatch -> IsInitializedAsync(self)
       ^                                      |
       +--------------------------------------+
```

The later commits `03e33f7f` and `e8fd2287` made the latent problem more visible
through strict startup observation and actor reactivation; they did not create
the self-wait.

## Final Design

There is one recovery protocol and two boundary protections.

1. The Orleans transport recognizes dispatch from the current grain to itself
   and skips only the impossible `IsInitializedAsync(self)` probe. Dispatch to a
   different actor still performs the initialization check. This is the shared
   runtime invariant added by `f5153c900` (`Allow actor self-dispatch during
   activation`).
2. `NyxIdChatConversationGAgent` does not perform history or operation recovery
   work inside activation. It schedules typed durable self callbacks. Pending
   history reservation is recovered first; only after it commits does operation
   recovery get scheduled. This preserves the ordering added by `d707b0c0f` and
   `d16945419`.
3. Existing current-actor continuations use the actor-owned event path:
   `PublishAsync(..., TopologyAudience.Self)` for topology self messages and
   `SendToAsync(Id, ...)` for the direct pending-input continuation. The input
   handler explicitly accepts self delivery. Messages to another actor continue
   through `IActorDispatchPort`.
4. `NyxIdChatTurnGAgent` publishes its typed recovery signal to self after state
   restoration. It never repeats provider or tool I/O during activation.
5. Transport changes do not erase tracing or idempotency lineage. Conversation
   operation, history-initialization, and history-terminal recovery envelopes
   retain their previous correlation ID and stable delivery operation ID through
   `EventEnvelopePublishOptions`. Callback IDs remain separately stable for
   durable scheduling.

## Preserved Product Invariants

- exact postconditions may be safely redispatched;
- interrupted LLM work is reconciled without automatic replay;
- effect-capable tool work becomes uncertain instead of being repeated;
- a committed result lost before delivery becomes an explicit delivery-loss
  failure;
- stale operation key or committed-version recovery signals are no-ops;
- history initialization, reservation, terminal delivery, pending input, and
  steering behavior remain enabled;
- activation does not execute provider, tool, or history side effects inline;
- credentials, raw tool output, and transient capabilities remain outside
  durable actor state and recovery messages; and
- `/api/chat`, AGUI/SSE, protobuf, read models, actor identities, and accepted
  receipt semantics are unchanged.

## Verification Contract

The regression suite must prove all of the following on the final rebased tree:

- a real Orleans reactivation completes within a bounded deadline;
- no LLM or tool execution is repeated during recovery;
- Conversation callback recovery is ordered after pending history reservation;
- Turn recovery re-enters the actor through the self-publication path;
- recovery kind, full key, expected committed version, correlation ID, and stable
  delivery operation ID survive transport changes;
- all original recovery and security cases remain green; and
- NyxID semantic, test-stability, architecture, build, and repository test gates
  are run before the non-force push.

## Non-Goals

- No new recovery framework, transport, registry, or dependency.
- No retry of an operation whose external outcome is uncertain.
- No API, protobuf, projection, workflow identity, or secret-contract redesign.
- No production restart or rollout is performed by this repository change.
