---
title: "Unified Channel Inbound Backbone"
status: accepted
owner: eanzhao
---

# ADR-0013: Unified Channel Inbound Backbone

## Context

Issue `#328` exposed that Aevatar still had two inbound business trunks for the same conversation work:

- the actor-backed `ChatActivity -> ConversationGAgent -> IConversationTurnRunner` path
- the Nyx relay HTTP endpoint path that directly orchestrated `NyxIdChatGAgent`, subscriptions, reply accumulation, and error classification

That split violated the repository rules:

- API endpoint code carried business orchestration
- relay traffic bypassed the conversation actor fact boundary
- slash flow, workflow resume, agent builder routing, and dedup were not shared by production relay traffic
- relay-specific reply accumulation and direct actor wiring created a second system beside ChannelRuntime

At the same time, Nyx relay is platform-neutral. The payload already carries platform identity and normalized conversation type, so the transport boundary is not Lark-specific.

## Decision

Adopt one inbound backbone for channel traffic:

`transport adapter -> ChatActivity -> ConversationGAgent -> ChannelConversationTurnRunner -> committed events / outbound`

Concretely:

- `ChatActivity` is the only inbound contract crossing from transport parsing into conversation processing
- `ConversationGAgent` is the sole authoritative inbound fact owner for relay-originated traffic and direct callback compatibility traffic alike
- `ConversationReference.Scope` is the authoritative chat-type semantic; string chat-type values are derived only at runner/tool boundaries
- Nyx relay is modeled as `Aevatar.GAgents.Channel.NyxIdRelay`, a channel transport adapter, not as Lark-specific business logic
- Lark-specific code under `Aevatar.GAgents.Platform.Lark` is reduced to rendering/composition concerns; direct webhook parsing and direct outbound adapter code are retired
- HTTP relay endpoints are shims only: authenticate, parse, dispatch `ChatActivity`, return `202 Accepted`
- LLM fallback becomes event-driven through `NeedsLlmReplyEvent` and `LlmReplyReadyEvent`; the HTTP layer no longer waits on cross-actor reply generation

## Consequences

- relay traffic now shares dedup, workflow resume, slash routing, agent-builder routing, and conversation completion events with the rest of ChannelRuntime
- `TransportExtras` and `OutboundDeliveryContext` carry relay reply facts as typed contracts instead of opaque bags or raw-payload side channels
- `ChannelBotRegistration` gains Nyx identity lookup fields so relay-originated activities resolve registration state without depending on `activity.Bot`
- `NyxRelayAgentBuilderFlow` short-circuits unknown slash commands so `/unknown_command` no longer falls through to LLM hallucination
- direct `NyxIdChatGAgent` creation from relay/webhook code is forbidden by CI guard; relay-to-chat orchestration must flow through `ConversationGAgent`
- solution filters are split into `aevatar.channels.slnf` and `aevatar.platforms.slnf` so transport and rendering code are no longer hidden inside the Foundation slice

## Status Changes

- ADR-0011 is superseded: the production relay edge remains `Lark -> NyxID -> Aevatar`, but inbound ownership is no longer a Lark-only webhook design
- ADR-0012 remains in force: Aevatar still does not become the long-term credential authority for channel bots

## Telegram amendment (2026-04-27)

Telegram joins as the second platform to ride this backbone, replacing the earlier
direct-callback `Aevatar.GAgents.Channel.Telegram` adapter prototype that ADR-0012 had
already excluded from the supported production contract.

- transport: same `Aevatar.GAgents.Channel.NyxIdRelay`. The relay payload carries
  `platform="telegram"` so the transport's normalize / parse path needs no Telegram
  branch. `ConversationReference.Scope` is derived from
  `NyxIdRelayConversationTypeMap` (`private` -> `DirectMessage`,
  `group` / `supergroup` -> `Group`, `channel` -> `Channel`).
- rendering: new `Aevatar.GAgents.Platform.Telegram` package mirrors
  `Aevatar.GAgents.Platform.Lark` — `TelegramMessageComposer` + `TelegramChannelNativeMessageProducer`
  + `TelegramOutboundMessage` + `TelegramPayloadRedactor`. Telegram has no card layout
  primitive, so cards degrade into the rendered text body and action buttons render as a
  single-row `inline_keyboard` with `callback_data` truncated to the 64-byte Bot API limit.
- provisioning: new `NyxTelegramProvisioningService` parallels `NyxLarkProvisioningService`
  but registers `platform="telegram"` with a real `bot_token` (no Lark
  `__unused_for_lark__` placeholder) and no `app_id` / `app_secret` /
  `verification_token`. The default Nyx provider slug is `api-telegram-bot`.
- registration contract: `NyxChannelBotProvisioningRequest` gains an optional
  `Credentials` map so future platforms can carry their secret bag without growing the
  record's typed sub-messages. The Lark typed sub-message stays in place to keep the
  existing Lark provisioning unchanged. The HTTP `POST /api/channels/registrations`
  endpoint accepts a top-level `bot_token` shorthand for Telegram and a generic
  `credentials` JSON map for future platforms; the endpoint mirrors the legacy Lark
  fields into the typed sub-message and the Telegram `bot_token` into the
  `Credentials["bot_token"]` map.
- tools: new `Aevatar.AI.ToolProviders.Telegram` exposes the chat-only subset needed
  today — `telegram_messages_send` (Bot API `sendMessage`) and `telegram_chats_lookup`
  (Bot API `getChat`). Both go through `NyxIdApiClient.ProxyRequestAsync` against the
  `api-telegram-bot` provider slug; reply-in-turn keeps flowing through
  `NyxIdRelayOutboundPort`.
- credential boundary: ADR-0012 still applies — Aevatar holds no Telegram bot tokens.
  The bot token only crosses the registration endpoint on the way to Nyx, never persisted
  locally. Webhook subscription URL points at Nyx (`/api/v1/webhooks/channel/telegram/{channelBotId}`),
  and inbound traffic still flows through the same callback-JWT-validated
  `/api/webhooks/nyxid-relay` ingress.

The lessons that shaped this PR are captured in `docs/operations/2026-04-27-telegram-nyx-cutover-runbook.md`.
