---
title: "Lark Nyx Relay Webhook Topology"
status: accepted
owner: eanzhao
---

# ADR-0011: Lark Nyx Relay Webhook Topology

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

This ADR is **Lark only**. Telegram migration is a separate follow-up.

## Contract Notes

- `#296` supersedes `#294` Deliverable A for Lark.
- The outbound delta is contract migration:
  - from `Nyx proxy + persisted session-token assumptions`
  - to `channel-relay/reply + callback-scoped relay JWT`
- `#295` is not part of the target end state for this Lark relay path.
  It only becomes obsolete once `#294` is re-scoped away from the older persisted-session-token outbound contract and proactive send remains out of scope.

## Current Nyx Boundaries

These are explicit current limitations, not hidden assumptions:

- Lark webhook URL still must be configured manually in the Lark Developer Console
- Nyx forwards `im.message.receive_v1`, but not `card.action.trigger`

Because of that:

- `social_media` and approval-style card-action flows must migrate off `card.action.trigger`
- supported interactive pattern is `open_url` / deep-link or text-driven interaction

## Cutover Order

Cutover is not a one-shot URL flip.

The required order is:

1. Build and validate `/api/webhooks/nyxid-relay`
2. Build and validate `channel-relay/reply`
3. Switch the Lark console callback URL to Nyx
4. Keep the legacy direct Aevatar callback path during an explicit rollback window
5. Only after Nyx ingress and relay reply are stable, return `410` or delete the legacy direct callback path

## Consequences

- Aevatar steady state for Lark carries only non-secret Nyx identifiers and status fields
- public read models do not persist `NyxApiKey`; runtime-only Nyx credential material lives in a separate runtime projection used only by host-side delivery ports
- callback-edge cryptographic verification shifts to JWT-via-JWKS
- HMAC callback signature remains defense-in-depth only under the current zero-secret constraint
- card-action-dependent Lark UX must be redesigned before final cutover
