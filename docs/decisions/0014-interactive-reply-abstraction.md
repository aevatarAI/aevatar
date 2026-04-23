---
title: "Channel Interactive Reply Abstraction"
status: accepted
owner: eanzhao
---

# ADR-0014: Channel Interactive Reply Abstraction

## Context

Issue `#350` identifies that the Aevatar relay outbound path could not send rich
interactive replies (cards, buttons) even though:

- `MessageContent` (proto) already modelled `text`, `actions`, `cards`, `card_action`
  as platform-neutral outbound intent.
- `IMessageComposer<TNativePayload>` already existed as the intent-to-native translator.
- `LarkMessageComposer` already upgraded to `msg_type=interactive` whenever
  `intent.Actions` / `intent.Cards` was non-empty.

The gap was concentrated in the relay outbound segment:

| File | Prior behaviour | Problem |
|---|---|---|
| `NyxIdRelayReplies.FinalizeReplyAsync` | Directly called `SendChannelRelayTextReplyAsync` | Could only send text; `IMessageComposer` was fully bypassed. |
| `NyxIdApiClient.SendChannelRelayTextReplyAsync` | Hardcoded body `{ message_id, reply: { text } }` | No `reply.metadata.card` branch. |
| `NyxIdChatEndpoints.Relay.FinalizeDayOneBridgeReplyAsync` | Same text-only path | Bridge path also text-only. |
| `FeishuCardHumanInteractionPort` | Hardcoded Lark 2.0 card JSON directly via `ProxyRequestAsync` | Lark-specific, bypasses composer, violates open-closed. |

LLM tools that wanted to send cards, and composers that already knew how to render them,
could not meet: the relay finalize path always stripped the result down to plain text.

## Decision

Introduce a channel-neutral interactive reply pipeline. A per-turn collector captures a
channel-agnostic `MessageContent` intent produced by an LLM tool; a registry looks up a
per-channel composer; a dispatcher composes the native payload and forwards it over the
relay transport. Composers remain pure translators. Transports remain transport-agnostic.
Adding a platform's card support is an additive change: implement that platform's
composer + producer, register it, done.

### Shape

```
┌─ LLM turn ──────────────────────────────────────────────────┐
│                                                              │
│  reply_with_interaction(title?, body?, actions[],           │
│                         fields[], cards[])                  │
│         ↓ emits neutral MessageContent                      │
│  IInteractiveReplyCollector  (AsyncLocal, turn-scoped)      │
│                                                              │
└──────────────────────────────────────────────────────────────┘
         ↓ turn ends
┌─ FinalizeReplyAsync ────────────────────────────────────────┐
│                                                              │
│  collector.TryTake() != null?                              │
│    ├─ yes → IInteractiveReplyDispatcher.DispatchAsync       │
│    │       ├─ registry.GetNativeProducer(channel).Produce   │
│    │       │     → ChannelNativeMessage                     │
│    │       └─ NyxIdApiClient.SendChannelRelayReplyAsync     │
│    │           body: { text?, metadata: { card? } }         │
│    └─ no  → SendChannelRelayTextReplyAsync (legacy path)    │
│                                                              │
└──────────────────────────────────────────────────────────────┘
         ↓ POST /api/v1/channel-relay/reply
                      NyxID → per-platform adapter
```

### Key abstractions

| Type | Project | Role |
|---|---|---|
| `ChannelNativeMessage` | `Aevatar.GAgents.Channel.Abstractions` | Unified DTO produced by composers: `Text?`, `CardPayload?`, `MessageType?`, `Capability`. |
| `IChannelNativeMessageProducer` | `Aevatar.GAgents.Channel.Abstractions` | Per-channel producer wrapping an `IMessageComposer` and projecting its output onto `ChannelNativeMessage`. |
| `IChannelMessageComposerRegistry` | `Aevatar.GAgents.Channel.Abstractions` | Registry indexed by `ChannelId` returning the composer and the native producer. |
| `IInteractiveReplyCollector` | `Aevatar.GAgents.Channel.Abstractions` | Turn-scoped buffer (AsyncLocal default) capturing the intent from a tool call. |
| `IInteractiveReplyDispatcher` | `Aevatar.GAgents.Channel.Abstractions` | Transport-aware dispatcher: resolves producer, composes, posts to relay. |
| `NyxIdRelayInteractiveReplyDispatcher` | `Aevatar.GAgents.ChannelRuntime` | Default dispatcher implementation targeting NyxID channel-relay/reply. |
| `ChannelRelayReplyBody` | `Aevatar.AI.ToolProviders.NyxId` | Body DTO for `POST /api/v1/channel-relay/reply` with `text?`, `metadata.card?`. |
| `ReplyWithInteractionTool` | `Aevatar.AI.ToolProviders.Channel` (new project) | LLM-facing tool; neutral params (title/body/actions/fields/cards); writes into collector. |

### Why `ChannelNativeMessage` lives in abstractions

The registry is consumed by the transport-level dispatcher. For the dispatcher to never
see "Lark-shaped payload" vs "Telegram-shaped payload" as runtime branches, every
producer agrees on a single DTO. Put the DTO in abstractions; `LarkOutboundMessage` stays
as a Lark-local record used by `LarkChannelAdapter`'s direct-send path and is projected
into `ChannelNativeMessage` by `LarkChannelNativeMessageProducer`.

### Why the tool lives in `Aevatar.AI.ToolProviders.Channel`

The tool expresses intent; it does not know about the NyxID relay transport. Housing it
next to the transport-bound NyxID tools would couple tool discovery to that transport.
A separate project keeps transport and intent orthogonal, so a future direct-webhook
path can use the same tool without refactoring.

### Why AsyncLocal for the collector

Tool executions run on the ambient `AsyncLocal` context already carried by
`AgentToolRequestContext`. The collector defaults to `AsyncLocal` so no additional turn
plumbing is required. Alternate scope carriers (for example, explicit
`ITurnContext`-backed implementations) can register their own `IInteractiveReplyCollector`
and replace the default via standard DI precedence.

### Capability degradation

- No producer registered for the channel → dispatcher sends plain text with the intent's
  `Text`. UX stays acceptable; composer is not required for capability.
- `ComposeCapability.Unsupported` → dispatcher logs, degrades to plain text.
- `ComposeCapability.Degraded` → dispatcher composes anyway and forwards whatever the
  producer returned (text-only `ChannelNativeMessage`).
- `ComposeCapability.Exact` → full card payload forwarded in `metadata.card`.

### Feature flag

`Aevatar:NyxId:Relay:InteractiveRepliesEnabled` (default `true`) controls whether the
finalize path drains the collector. Setting it to `false` restores the legacy text-only
behaviour without redeploying.

## Scope

### In scope (this PR)

- New abstractions (`ChannelNativeMessage`, `IChannelMessageComposerRegistry`,
  `IInteractiveReplyCollector`, `IChannelNativeMessageProducer`,
  `IInteractiveReplyDispatcher`).
- `NyxIdApiClient.SendChannelRelayReplyAsync` rich overload; existing text overload
  becomes a thin wrapper.
- `LarkChannelNativeMessageProducer` wrapping the existing `LarkMessageComposer`.
- `NyxIdRelayInteractiveReplyDispatcher` default implementation.
- New project `Aevatar.AI.ToolProviders.Channel` with `ReplyWithInteractionTool` and
  its `IAgentToolSource`.
- Relay finalize integration with the `Aevatar:NyxId:Relay:InteractiveRepliesEnabled`
  feature flag.

### Out of scope / follow-ups

- **Day One bridge card replies.** The bridge handler returns plain-text today; the
  finalize path is prepared to dispatch an interactive reply but the bridge has no hook
  to populate the collector. A follow-up can surface an interactive reply API on
  `INyxRelayDayOneBridge`.
- **`FeishuCardHumanInteractionPort` migration.** The approval card relies on Feishu
  `tag=input` form fields plus a form-submit button. The neutral `MessageContent` /
  `ActionElement` schemas do not yet cover text-input form controls, so a proto
  extension must land first before the port can be migrated. Tracked as a follow-up
  to avoid a premature lossy abstraction.
- **Platform coverage.** NyxID today ships card support only for Lark / Feishu. When
  NyxID adds card support for Telegram / Slack / Discord, the corresponding Aevatar
  composer + `IChannelNativeMessageProducer` implementations become additive.
- **CI guard.** A guard forbidding `msg_type = "interactive"` / raw `elements` /
  `"tag":"button"` string literals inside `ChannelRuntime` / `NyxidChat` will be added
  once `FeishuCardHumanInteractionPort` is migrated (the guard would trip on its current
  legacy Lark JSON).

## Related

- Issue `#350` — this ADR's driving issue.
- Issue `#328` — unified inbound trunk; this ADR's outbound abstraction slots into the
  same `IChannelOutboundPort` surface once #328 lands.
- PR `#324` — inbound `card.action.trigger` processing; this ADR is its outbound dual.
- ADR-0011 (`docs/decisions/0011-lark-nyx-relay-webhook.md`) — relay webhook topology;
  not superseded, outbound completeness augments it.
