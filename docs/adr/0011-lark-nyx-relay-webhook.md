---
title: "Lark Nyx Relay Webhook Topology"
status: superseded
owner: eanzhao
---

# ADR-0011: Lark Nyx Relay Webhook Topology

Superseded by [ADR-0013](0013-unified-channel-inbound-backbone.md), which generalizes relay ingress from a Lark-only webhook topology to the unified channel inbound backbone.

## Context

Issue `#296` fixes the production webhook topology for Lark.

The older direct path

`Lark -> Aevatar`

made Aevatar own Lark-specific webhook concerns:

- `verification_token`
- `encrypt_key`
- callback signature validation
- AES-CBC payload decryption
- platform-specific webhook parsing

That conflicts with the desired ownership boundary:

- Nyx is the sole Lark credential authority
- Aevatar does not persist Lark credentials
- Aevatar does not perform direct Lark crypto or direct Lark API calls

At the same time, current Nyx already provides:

- the Lark webhook ingress
- route resolution to an agent callback URL
- callback-scoped relay JWTs
- `POST /api/v1/channel-relay/reply` for async replies

## Decision

For Lark, the target production webhook topology is:

`Lark -> NyxID -> Aevatar`

Concretely:

- Lark callback URL points to Nyx, not Aevatar
- Aevatar receives only Nyx relay callbacks at `/api/webhooks/nyxid-relay`
- Aevatar validates Nyx relay JWTs via OIDC discovery + JWKS
- inbound-triggered turn replies use `POST /api/v1/channel-relay/reply`

This ADR is **Lark only**. The broader ChannelRuntime credential-boundary cleanup
is tracked separately in ADR-0012 / issue `#308`.

## Contract Notes

- `#296` supersedes `#294` Deliverable A for Lark.
- The outbound delta is contract migration:
  - from `Nyx proxy + persisted session-token assumptions`
  - to `channel-relay/reply + callback-scoped relay JWT`
- `#295` is not part of the target end state for this Lark relay path.
  It only becomes obsolete once `#294` is re-scoped away from the older persisted-session-token outbound contract and proactive send remains out of scope.

## Current Nyx Boundaries

These are explicit current boundaries, not hidden assumptions:

- Lark webhook URL still must be configured manually in the Lark Developer Console
- Nyx forwards both `im.message.receive_v1` and `card.action.trigger`
- relay-triggered interactive cards are supported only for card-aware Aevatar flows; generic chat replies remain text unless the sender explicitly emits a card payload

## Cutover Order

The required order is:

1. Build and validate `/api/webhooks/nyxid-relay`
2. Build and validate `channel-relay/reply`
3. Switch the Lark console callback URL to Nyx
4. Remove the direct Aevatar Lark callback path from the supported runtime contract
5. Return `410 Gone` for `POST /api/channels/lark/callback/{registrationId}` or delete that endpoint entirely

## Consequences

- Aevatar steady state for Lark carries only non-secret Nyx identifiers and status fields
- public read models do not persist `NyxApiKey`; runtime-only Nyx credential material lives in a separate runtime projection used only by host-side delivery ports
- callback-edge cryptographic verification shifts to JWT-via-JWKS
- HMAC callback signature remains defense-in-depth only under the current zero-secret constraint
- Lark approval and `social_media` relay UX can use interactive cards again; `/approve`, `/reject`, and `/submit` remain fallback commands
- no rollback-window contract remains for direct `Lark -> Aevatar` ingress
- ADR-0012 extends the same ownership rule to the remaining ChannelRuntime surface:
  no local channel credential ownership, no `update_token`, and no direct-callback production contract
