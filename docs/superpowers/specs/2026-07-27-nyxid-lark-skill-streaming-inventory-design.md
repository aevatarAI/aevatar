# NyxID Lark Skill-Streaming Inventory Design

## Status

Approved as option A on July 27, 2026. The user explicitly requires the normal
skill-driven AgentRun path and CardKit streaming; a keyword-matched fixed reply
is not acceptable.

## Product Mismatch

The current channel UI says `/whoami` has found a NyxID binding, but a natural
language request such as “我在 NyxID 上有什么服务” is intercepted before the
conversation run and may answer that the session has no usable authorization.
The implementation therefore implies that binding existence, runtime-route
readiness, and connected-service inventory authority are the same fact, while
the user expects the bot to use the binding for the narrow question they asked.

This is both a runtime and ownership mismatch:

- natural-language understanding and answer composition belong to AgentRun and
  its loaded skill;
- the connected-service inventory belongs to the current channel sender's
  exact NyxID binding;
- full Aevatar runtime readiness is a separate, stronger capability contract;
- Lark reply presentation belongs to the existing CardKit streaming pipeline.

## Root Cause

The first repair introduced a parallel execution path in
`ChannelConversationTurnRunner`:

1. a process-local phrase matcher recognizes a small set of inventory wording;
2. the turn runner invokes a query adapter directly;
3. a fixed renderer constructs the final text;
4. the turn returns without creating an AgentRun.

That path bypasses `use_skill`, LLM tool rounds, streamed model output, and the
CardKit create/stream/finalize lifecycle. It also forced one transport handler
to own natural-language intent, NyxID querying, and response composition.

The original `UNAUTHENTICATED` symptom had a different cause. A sandboxed
`nyxid service list` command runs under process-local CLI login state and does
not possess the current Lark sender's bearer token. NyxID's authoritative
`GET /api/v1/keys` endpoint is user-scoped, so an ambient sandbox or bot-owner
credential cannot answer the sender's inventory question.

## Decision

Natural-language connected-service inventory requests use one authoritative
conversation path:

```text
Lark inbound text
  -> ChannelConversationTurnRunner
  -> LlmReplyRequested
  -> AgentRunGAgent
  -> ChatStreamAsync
  -> use_skill(skill="nyxid")
  -> nyxid_service_inventory
  -> sender-scoped GET /api/v1/keys
  -> streamed model answer
  -> Lark CardKit create / stream / finalize
```

Slash commands such as `/init` and `/whoami` remain deterministic channel
commands. They are identity/configuration controls, not ordinary natural
language conversation turns.

## Authority and Typed Context

The inbound binding lookup produces two separate typed facts:

- `AgentToolSenderBindingContext.BindingId` identifies the exact binding;
- `AgentToolNyxIdAuthorityContext` carries the exact external subject
  (`platform`, `tenant`, and `external_user_id`).

Both facts must be placed on `NeedsLlmReplyEvent.ToolContext` and survive the
deferred AgentRun credential strip. Bearer tokens remain transient and are not
persisted.

`ChannelNyxIdConnectedServiceInventoryToolSource` remains a channel-only
`IAgentToolSource`. Discovery only exposes a list-only lazy tool and performs no
capability issuance or HTTP. When that tool executes, it follows this authority
order:

1. reuse a verified sender runtime token when one is available;
2. otherwise issue a narrow inventory capability from the exact typed external
   subject plus `bindingId`;
3. fail closed if either typed identity fact is missing;
4. never substitute a bot-owner token, channel registration token, guessed
   subject, or sandbox CLI login.

The narrow inventory capability proves only that the binding may read its own
connected-service inventory. It does not claim that the binding covers the
configured LLM, Ornn, sandbox, or other Aevatar runtime resources.

## Skill and Tool Behavior

The system prompt and built-in overlay instruct the model to load
`use_skill(skill="nyxid")` before handling a NyxID inventory request, then call
`nyxid_service_inventory`. The loaded skill supplies current NyxID semantics;
the typed tool supplies sender authority and live data.

The model must not use `code_execute`, invoke a sandbox CLI, or run
`nyxid service list` for this question. The process-local CLI has no authority
relationship to the Lark sender.

The implementation does not force a fixed answer or phrase-level routing. The
model may summarize, group, and explain the typed result, but it may not invent
connections that are absent from the result.

## Error Semantics

Inventory failures are data-access failures, not proof that the sender is
unbound. The typed inventory tool returns a stable, sanitized error envelope.
The model must say that the connected-service list could not be read on this
attempt and may suggest retrying.

The reply must not:

- expose raw bearer tokens or the upstream `UNAUTHENTICATED` response;
- claim that an existing binding is absent merely because inventory failed;
- recommend `/init` unless the binding is actually missing/revoked or the user
  explicitly asks to renew service authorization;
- silently fall back to the bot owner's connected services.

If NyxID explicitly reports the binding revoked, the existing actor-owned
reconciliation path may remove the stale binding. A general runtime service
scope mismatch does not revoke it.

## Removed Parallel Path

The following direct-query artifacts have no remaining product owner and are
deleted:

- `NyxIdConnectedServiceInventoryIntent`;
- `INyxIdConnectedServiceInventoryQuery` and its query result enum;
- `NyxIdConnectedServiceInventoryReplyRenderer`;
- the inventory branch and dependency in `ChannelConversationTurnRunner`.

`ChannelNyxIdConnectedServiceInventoryToolSource` retains only its typed tool
source role. It stays outside the global `IAgentToolSource` collection and
`workspace.default`; `ResolveChannelToolSources` adds the single channel-local
instance to the NyxID chat reply generator to avoid tool-name collisions on
non-channel surfaces.

## Streaming Contract

No new reply transport is introduced. AgentRun continues to use
`ChatStreamAsync` as the sole interactive LLM execution entry. Visible text is
emitted through `TurnStreamingReplySink`; with CardKit enabled, the existing
actor-owned delivery state drives card creation, interim stream writes, final
text synchronization, and streaming-mode closure.

Tool-call narration and tool results are not rendered as a separate fixed
message. The user sees the model's streamed final answer in the same card
lifecycle as other NyxID chat turns.

## Tests

Regression coverage must prove the behavior at observable boundaries:

1. a bound natural-language inventory request returns `LlmReplyRequested` and
   sends no direct platform reply;
2. its typed tool context contains distinct binding and external-subject facts;
3. a streamed multi-round provider calls `use_skill("nyxid")`, then
   `nyxid_service_inventory`, then emits the final answer;
4. neither `code_execute` nor a CLI command is offered or invoked as part of
   the inventory flow;
5. tool discovery issues no capability and performs no HTTP; tool execution
   calls `GET /api/v1/keys` with the sender token and never the bot-owner token;
6. inventory failure produces a sanitized tool result and no unconditional
   `/init` guidance;
7. CardKit create, stream, and finalize behavior remains covered by the
   existing streaming actor and renderer tests;
8. `/init` authorization URLs retain the exact external-subject fields and do
   not send `binding_grant_id`.

Tests use different identifiers for the binding and external subject so an
identity swap cannot pass accidentally.

## Documentation and Rollout

The connected-service canon and OAuth broker ADR are updated to describe the
single AgentRun path and the narrow capability boundary. Prompt text is checked
for stale claims that inventory bypasses skills or the LLM.

After local tests, build, architecture guards, stability guards, and docs lint
pass, the branch is merged with the latest `origin/feature/integrate` and pushed
without force. Production acceptance requires the deployed immutable image to
show, for a real Lark message, AgentRun activity, `use_skill`, the typed
inventory tool, `GET /api/v1/keys`, and CardKit streaming. Deployment alone is
not acceptance.
