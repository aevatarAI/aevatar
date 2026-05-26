---
title: "Channel Runtime Credential Boundary"
status: accepted
owner: eanzhao
---

# ADR-0012: Channel Runtime Credential Boundary

## Context

Issue `#308` extends the direction established by `#296`.

`#296` moved the production Lark ingress/reply path to:

`Lark -> NyxID -> Aevatar`

That removed Lark credential ownership from the primary production path, but
ChannelRuntime still carried older assumptions elsewhere:

- registration contracts still exposed direct-callback credential-bearing fields
- `update_token` semantics still implied that Aevatar owned channel credentials
- direct-callback and Telegram paths still kept the older local-credential model alive
- ChannelRuntime composition still had to tolerate channel-delivery designs built around local secret ownership

That conflicted with the intended boundary:

- channel credential authority lives outside Aevatar runtime
- ChannelRuntime stores routing, identity, and status handles only
- unsupported platforms must leave the production contract instead of forcing generic credential storage back into ChannelRuntime

## Decision

ChannelRuntime is not a channel credential authority.

The steady-state contract is:

- channel registration state and readmodels keep only non-secret routing, identity, and status fields
- ChannelRuntime does not persist, refresh, or update channel bot tokens
- `update_token` and similar direct credential-management APIs/tools are retired
- ChannelRuntime may keep opaque `credential_ref` handles for relay verification secrets, but the secret material itself lives behind `IAevatarSecretsStore` and never enters registration proto/readmodel/query payloads
- if a platform cannot satisfy this boundary yet, it is removed from the supported production contract instead of keeping channel-wide local credential storage alive

## Supported Production Contract

After `#308`, the supported production path is:

`Lark -> NyxID -> Aevatar`

Concretely:

- supported provisioning is `register_lark_via_nyx`
- supported inbound webhook contract is Nyx relay ingress
- supported reply contract is Nyx-backed reply delivery
- registration contracts keep non-secret Nyx handles such as channel bot / api-key / route identifiers

The following are no longer part of the supported production contract:

- direct callback platform registrations
- direct callback test-reply / token-update flows
- Telegram delivery paths that require ChannelRuntime-local credential ownership

## Consequences

- ChannelRuntime registration proto/state/readmodel schemas are compacted to non-secret fields only
- no backward-compatibility holes are preserved for removed credential fields; schema numbering follows the new compact contract
- because of that hard cut, rollout requires either a greenfield environment or an explicit preflight wipe of persisted `channel-bot-registration-store` events/snapshots before deployment
- public registration queries return routing/identity/status data only
- production support is explicitly narrower but architecturally honest
- any future non-Lark platform must first provide an external credential-authority contract before re-entering the production support surface
