---
title: "Channel bots register directly on NyxID; inbound relay scope from callback JWT"
status: accepted
owner: eanzhao
---

# ADR-0037: Channel bots register directly on NyxID; inbound relay scope from callback JWT

> Supersedes (by reference, no in-place edit of the prior ADRs):
> - ADR-0012 §"Supported Production Contract" — the `supported provisioning is register_lark_via_nyx` clause and the premise that ChannelRuntime keeps a registration state/readmodel as the routing/identity store.
> - ADR-0013 §Decision line 45 (`ChannelBotRegistration` gains Nyx identity lookup fields so relay-originated activities resolve registration state) and the §"Telegram amendment (2026-04-27)" self-provisioning facade (`NyxTelegramProvisioningService` / `NyxLarkProvisioningService` / `NyxChannelBotProvisioningRequest` / the `POST /api/channels/registrations` endpoint).
>
> ADR-0008 (already superseded), ADR-0009, ADR-0011 are unaffected and remain historical record. The inbound backbone shape of ADR-0013 (`transport adapter -> ChatActivity -> ConversationGAgent -> ChannelConversationTurnRunner`) stays in force; only the registration-lookup scope-resolution and the aevatar-side provisioning facade are superseded.

## Context

ADR-0012 narrowed the supported channel production contract to `Lark -> NyxID -> Aevatar` and modeled `register_lark_via_nyx` as the supported provisioning operation. ADR-0013 made `ConversationGAgent` the sole inbound fact owner and gave `ChannelBotRegistration` Nyx identity lookup fields so a relay-originated activity could resolve registration state without depending on `activity.Bot`. The Telegram amendment extended the same self-provisioning facade to a second platform.

In practice this left aevatar carrying a **redundant self-registration surface** that duplicated NyxID-owned facts:

- `POST/GET/DELETE /api/channels/registrations` endpoints and the `NyxLark/TelegramProvisioningService` provisioning saga.
- the `ChannelBotRegistration` GAgent + its readmodel + projector + `channel-bot-registration-store` durable scope (a local mirror of routing/identity facts that NyxID already owns authoritatively).
- the `NyxIdRelayScopeResolver` and the `ChannelAdmin` registration tool, used to look registration state up by api-key / Nyx identity at inbound time.

This violated the repository's authority rules: the bot, route, and relay api-key are NyxID-owned facts, and every production relay callback already arrives as a JWT validated against NyxID that carries the authoritative scope claim. The registration lookup at inbound time was a dead fallback layered over a fact NyxID already asserts — a second source of truth for the same routing/identity facts (FI-004), and a "缺失即创建" mirror whose ownership and cleanup were never cleanly defined.

## Decision

**A Lark/Feishu channel bot is registered directly on NyxID, not on aevatar.** The channel-bot, a relay api-key whose `callback_url` points at aevatar's `/api/webhooks/nyxid-relay`, and the conversation route (via the `api-lark-bot` proxy) are all NyxID-direct operations. Aevatar holds **no local registration mirror**.

Concretely:

- **Inbound scope comes solely from the validated NyxID relay callback JWT.** The relay endpoint resolves scope as `scope_id ?? sub ?? NameIdentifier` from the validated token and places it on `activity.TransportExtras.NyxRegistrationScopeId`. There is no registration lookup, no api-key→scope resolver, and no Nyx-identity registration fallback. The empty-scope guard (HTTP 401 when the JWT carries no scope claim) is preserved as the trust boundary.
- **Inbound durable fact** is `ChannelInboundEvent` (the only surviving channel-runtime inbound proto, relocated to `agents/Aevatar.GAgents.Channel.Runtime/protos/channel_inbound.proto`). It carries the JWT-derived scope; it does not reference a registration entry.
- **Outbound reply** goes through the `api-lark-bot` proxy plus the relay reply token. The provider slug for lark/feishu is the `api-lark-bot` platform constant; aevatar holds no bot token and reads no `transport_binding.credential_ref` from a registration readmodel.
- **The self-registration surface is removed.** `ChannelBotRegistration` GAgent + readmodel + projector + `channel-bot-registration-store`, `NyxLark/TelegramProvisioningService` (+ `INyxChannelBotProvisioningService` and request/result records), the `/api/channels/registrations` endpoints, the registration query ports, `NyxIdRelayScopeResolver`, the `ChannelAdmin` registration tool, and the `ChannelBotRegistrationFreshnessSource` status probe are all deleted.

## Consequences

- aevatar no longer holds a second source of truth for channel-bot routing/identity; NyxID is the sole owner of bot/route/api-key facts (FI-004). Setting up a bot is a NyxID-direct operation (`nyxid_channel_bots` + `use_skill(skill="nyxid")`), not an aevatar provisioning call.
- The 401-reply failure mode caused by a stale/missing local registration readmodel disappears, because scope is derived per-turn from the JWT rather than from a materialized mirror. The `2026-04-29-lark-mirror-recovery-runbook.md` failure mode no longer exists.
- The `channel-bot-registrations` / `channel-bot-runtime` `/status` freshness probe is removed; the status dashboard no longer points at a deleted readmodel.
- The `ChannelTransportBinding` / `credential_ref` abstraction described in `docs/canon/aevatar-channel-architecture.md` §9.6 is now strictly forward-looking design for a hypothetical self-hosted adapter; it no longer hangs off a live registration entry.
- ADR-0012's credential boundary (aevatar is not a channel credential authority) is **strengthened**, not weakened: aevatar now holds no channel registration state at all, not merely no secret material.
- The retired-actor startup-cleanup spec gains the registration actor (`ChannelBotRegistrationGAgent`) and its `channel-bot-registration-store` / `projection.durable.scope:channel-bot-registration` durable scopes, so leftover persisted state from prior deployments is destroyed on startup.
