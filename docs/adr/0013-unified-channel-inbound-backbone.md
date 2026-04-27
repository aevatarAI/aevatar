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
