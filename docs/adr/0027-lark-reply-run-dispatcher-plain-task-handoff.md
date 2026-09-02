---
title: Lark Reply Run Dispatcher Plain Task Handoff
status: Accepted
owner: eanzhao
supersedes: ADR-0021 dispatcher return value sections
---

# ADR-0027: Lark Reply Run Dispatcher Plain Task Handoff

## Context

ADR-0021 made Lark reply-chain completion stages explicit, but its dispatcher section introduced a `DispatchOutcome` / `DispatchPhase` return shape for `IChannelLlmReplyRunDispatcher.DispatchAsync`.

The implementation now follows actor-owned run admission: stale detection, duplicate absorption, reply production, dropped, and failed outcomes are facts owned by `AgentRunGAgent`, not by the dispatcher adapter. Keeping a dispatcher result type would invite callers to treat admission decisions as synchronously known before the run actor has processed its inbox.

## Decision

`IChannelLlmReplyRunDispatcher.DispatchAsync` returns plain `Task`.

A normal return means only:

- the request was normalized
- the run actor id was derived from typed `run_id`
- the run actor exists or was created
- the start envelope was accepted by `IActorDispatchPort` for actor inbox handoff

It does not mean:

- the run actor has admitted the request
- stale or duplicate checks have completed
- the LLM has started
- a reply has been produced, handed off, delivered, or finalized

Transport failure to hand the message to the dispatch port may still surface as an exception. Downstream execution failure must surface through `AgentRunReplyProducedEvent`, `AgentRunDroppedEvent`, `AgentRunFailedEvent`, and readmodel/projection observation.

For Nyx relay turns, `ConversationGAgent` persists only typed, TTL-bound
`RuntimeSecretReference` values for the relay reply token and NyxID user access
token. The raw credentials remain encrypted in `IRuntimeSecretStore` and stay out
of actor state, events, projections, and logs. If the initial inbox handoff throws,
the durable retry resolves those references, restores the credentials only on the
dispatch clone, and retries the same typed `run_id`. An unavailable or expired
reference terminates honestly as `missing_runtime_reply_token`; it must not trigger
query-time reconstruction or a process-local credential registry.

## Superseded ADR-0021 Sections

This ADR supersedes ADR-0021 section 4, "Dispatcher 返回值显式化", and the related rows in section 5 / consequences that require `Task<DispatchOutcome>`, `DispatchOutcome`, or `DispatchPhase`.

ADR-0021 remains the source of record for the four reply-chain stages, `AgentRunStatus.REPLY_HANDED_OFF`, delivery tracking intent, streaming closeout bridge, and terminal idempotency bridge.

Detailed implementation guidance lives in [docs/canon/lark-reply-completion-semantics.md](../canon/lark-reply-completion-semantics.md).

## Consequences

- `IChannelLlmReplyRunDispatcher.DispatchAsync` has no receipt/result type.
- `AgentRunDispatcher` creates the run actor and hands off an `AgentRunStartRequested` envelope through `IActorDispatchPort`.
- Nyx relay handoff retries recover ephemeral credentials through distributed `IRuntimeSecretStore` references; raw tokens never enter event-sourced state.
- Stale and duplicate admission tests belong to `AgentRunGAgent`.
- Callers must not branch on dispatcher-local accepted/rejected phases.
- Orleans `IActorDispatchPort` uses actor stream handoff and does not couple the ACK to `_agent.HandleEventAsync` execution.
