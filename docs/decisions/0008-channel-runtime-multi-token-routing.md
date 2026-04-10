---
title: "Channel Runtime Multi-Token Credential Routing"
status: active
owner: chronoai
---

# ADR-0008: Channel Runtime Multi-Token Credential Routing

## Context

Aevatar Channel Runtime lets companies share messaging bots (Telegram, Lark) across employees. Each employee's message arrives via the same bot webhook but may need different NyxID credentials:

- **Org services** (bot API, internal tools) → use the registration owner's token
- **Personal services** (Google Calendar, GitHub) → use the employee's own NyxID token

NyxID's service model is per-user with no org-level credential sharing. NyxID #209 (Org Model) is designed but implementation timeline is uncertain.

## Decision

Aevatar owns multi-token routing at the application layer. The `NyxIdProxyTool` resolves which token to use per service slug by checking the user's service list first and falling back to the org (registration) token.

### Architecture

```
Webhook → ChannelCallbackEndpoints (parse + 200 OK)
              ↓ (background)
         ChannelUserGAgent (per sender)
              ├─ Track identity (event-sourced)
              ├─ Resolve tokens: user (if bound) + org (from registration)
              ├─ Dispatch ChatRequestEvent → NyxIdChatGAgent
              │     metadata: NyxIdAccessToken + NyxIdOrgToken
              ├─ NyxIdProxyTool resolves per-slug token:
              │     user service list → user token
              │     org service list  → org token (fallback)
              ├─ Collect AI response
              └─ Send reply via bot API (always org token)
```

### Token lifecycle

| Token | Persistence | Source |
|-------|------------|--------|
| User's bound token | Event-sourced in ChannelUserGAgent state (never projected) | ChannelUserBoundEvent |
| Org registration token | Transient, per-request from ChannelInboundEvent | ChannelBotRegistrationStore |

### Service discovery cache

`IServiceDiscoveryCache` abstraction with `InMemoryServiceDiscoveryCache` implementation. TTL: 5 minutes. Keyed by SHA-256 hash of token. Eliminates N+1 HTTP calls to NyxID `/proxy/services`.

## Alternatives Considered

1. **NyxID Org Model (#209)**: NyxID handles credential fallback in the proxy layer. Correct long-term but timeline uncertain. This decision is designed to be replaceable: if #209 ships, delete ~100 LoC from NyxIdProxyTool and stop passing `NyxIdOrgToken`.

2. **Single registration token**: All requests use the bot owner's token. No per-user service access. Simpler but doesn't support the multi-user use case.

3. **NyxID Developer Apps**: Users install org's OAuth app on their own NyxID account. Works for OAuth services but not for org-level resources like bot tokens or API keys.

## Consequences

- **Reply path uses org token, not effective token.** Bot API credentials belong to the org. The user's personal token cannot send messages via the org's bot.
- **Cache staleness window:** Up to 5 minutes. Service list changes are eventually consistent.
- **Actor population growth:** One ChannelUserGAgent per (platform, registrationId, senderId). No cleanup mechanism yet (see aevatarAI/aevatar#149).
- **Replaceable boundary:** The dual-token merge logic in NyxIdProxyTool is the only code that would change if NyxID #209 ships. ChannelUserGAgent and per-sender identity tracking are permanent.

## Related

- NyxID #204: CEO decision placing identity mapping in Aevatar's business layer
- NyxID #209: Org Model design (Org = User pattern)
- NyxID #178: Service scope hierarchy (personal / organization / system)
- aevatarAI/aevatar#149: Actor lifecycle cleanup strategy
