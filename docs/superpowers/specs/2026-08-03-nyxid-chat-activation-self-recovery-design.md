# NyxID Chat Activation Self-Recovery Fix

**Date:** 2026-08-03
**Status:** Awaiting written review
**Scope:** Feature-preserving repair of NyxID Chat activation recovery

## Incident And Introducing Change

The activation deadlock was introduced by commit
`1458a5bdbda5aefd17f8b5a0c43efee1618970f1` (`Harden NyxIdChat recovery
semantics`). That commit added restart recovery to
`NyxIdChatConversationGAgent` and `NyxIdChatTurnGAgent`. During
`OnActivateAsync`, both actors build a typed `NyxIdChatRecoveryRequestedSignal`
and submit it to their own actor ID through `IActorDispatchPort`. Additional
conversation recovery paths use the same external dispatch port for pending
input, history-initialization, and history-terminal self-continuations.

The original feature is required and remains correct. It prevents unsafe work
replay after a process crash or actor passivation:

- an exact browser-action postcondition may be safely redispatched;
- an interrupted LLM operation is reconciled without automatic replay;
- an effect-capable tool becomes uncertain instead of being repeated;
- a completed result whose delivery was lost becomes an explicit delivery-loss
  failure;
- stale recovery signals are rejected using the full operation key and the
  expected committed state version; and
- credentials, raw outputs, and transient execution capabilities remain out of
  durable state and recovery messages.

The defect is the transport chosen for the self-continuation, not the recovery
state machine. Orleans `IActorDispatchPort` checks
`IRuntimeActorGrain.IsInitializedAsync()` before handing an envelope to the
actor stream. When an actor calls that port for itself from `OnActivateAsync`,
the check waits for the same activation that is waiting for the check. The
activation eventually times out and can stall persistent stream delivery and
startup projection activation.

Commit `03e33f7f` later made workflow startup observation strict, which made the
latent activation defect fail Pod startup visibly. The deployment commit
`e8fd2287` exposed the problem through reactivation but did not modify the
faulty paths.

## Required Invariants

- Activation restores state but never executes provider or tool work inline.
- Every recovery action re-enters the actor inbox and runs in a later actor
  turn.
- The recovery kind, full operation key, expected committed version, effect
  evidence, and retry/skip safety rules remain unchanged.
- Existing input, history initialization, history terminal, postcondition, LLM,
  tool uncertainty, and result-delivery-loss recovery features remain enabled.
- External actor dispatch keeps its current target-existence/activation
  behavior; cold actors must still be activated before their stream handoff is
  accepted.
- `/api/chat`, AGUI/SSE, protobuf, read-model, actor identity, and secret
  contracts do not change.
- No second transport, runtime-specific business helper, process-local
  registry, retry loop, or compatibility path is introduced.

## Considered Approaches

### 1. Use the existing actor self-publication path

Recovery code running inside an actor publishes to its own inbox through the
already injected `IEventPublisher` exposed by `GAgentBase`. Topology self
signals use `PublishAsync(..., TopologyAudience.Self)`. The existing direct
self input continuation uses `SendToAsync(Id, ...)`, preserving its direct
route.

This is selected. It follows the repository rule that current-actor
continuations use the actor publication path, while `IActorDispatchPort` is the
external envelope admission boundary. Both Local and Orleans publishers already
short-circuit self publication directly to the actor stream without querying
the actor's lifecycle through `IActorDispatchPort`.

### 2. Special-case self-dispatch inside the Orleans adapter

The Orleans adapter could inspect ambient grain context and skip
`IsInitializedAsync()` when the target matches the current activation. This
would keep the current call sites but make a runtime-neutral port depend on
runtime-incidental caller context. It would also leave business code using the
external port for self continuation and require a special Orleans-only rule.

This is rejected.

### 3. Remove the Orleans initialization check for every dispatch

Publishing every envelope immediately would remove the self-wait, but an
external message to a cold actor could be published before that actor restores
its stream subscription. It would also turn unknown-target rejection into
possible silent message loss.

This is rejected.

## Selected Design

Only recovery-related messages targeting the current NyxID actor change their
transport call:

- `NyxIdChatConversationGAgent.OnActivateAsync` publishes outstanding operation
  recovery to `TopologyAudience.Self`;
- `NyxIdChatTurnGAgent.OnActivateAsync` publishes turn reconciliation to
  `TopologyAudience.Self`;
- pending history initialization and history terminal continuations publish to
  `TopologyAudience.Self`; and
- pending input materialization uses `SendToAsync(Id, ...)` because its existing
  route is direct-to-self.

The typed payloads and their handlers remain unchanged. No provider/tool call,
state transition, retry classification, or committed event moves into
activation.

The old stable envelope identity is retained as
`EventEnvelopeDeliveryOptions.OperationId`, so retry lineage remains stable even
though the existing publisher owns the transport envelope ID. Existing
correlation IDs are copied through
`EventEnvelopePropagationOverrides.CorrelationId`. Business idempotency remains
owned by the typed payload fields already used by handlers: request/delivery
identity, attempt, full operation key, and expected state version.

Messages targeting another actor continue to use `IActorDispatchPort`. The
Orleans dispatch implementation and its initialization check are not changed.

## Verification

Implementation follows TDD. A real Orleans regression test first recreates the
failure with persisted NyxID state and actor reactivation. It must demonstrate
that the pre-fix actor activation cannot complete while it synchronously probes
itself. After the fix, the same test must prove:

1. reactivation completes within a bounded deterministic test deadline;
2. the typed self recovery message is processed only after activation;
3. the expected recovery terminal state is committed; and
4. provider/tool execution is not repeated.

Existing NyxID unit tests are adapted to observe the event publisher instead of
the external dispatch test double. They continue to assert every original
feature: safe postcondition redispatch, interrupted LLM reconciliation,
effectful-tool uncertainty, delivery loss, stale key/version rejection, blocked
browser-action behavior, byte-equivalent repeated activation, and secret-free
state/projection output.

Required verification before push:

- focused NyxID recovery and real Orleans regression tests;
- `bash tools/ci/nyxid_chat_semantics_guard.sh`;
- `bash tools/ci/test_stability_guards.sh`;
- `bash tools/ci/architecture_guards.sh`;
- `dotnet build aevatar.slnx --nologo`; and
- `dotnet test aevatar.slnx --nologo`.

The final branch is fetched and reconciled without force, pushed to
`origin/feature/integrate`, and the remote SHA is read back.

## Non-Goals

- No change to workflow definition startup mitigation in `4377b067`.
- No change to the accepted-only API receipt contract.
- No redesign of NyxID task execution, history, projection, or browser actions.
- No general-purpose recovery framework or new abstraction.
- No production restart, rollout, or mutation as part of the repository fix.
