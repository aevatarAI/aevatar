# Lark -> NyxID -> Aevatar Cutover Runbook

This runbook reflects the post-`#308` production contract, updated for ADR-0037 (aevatar no longer self-registers channel bots; inbound scope comes from the NyxID callback JWT).

## Preflight

- The bot is registered **directly on NyxID**, not on aevatar. Aevatar holds no local registration mirror.
- If any environment has ever run a version that persisted the aevatar-side `channel-bot-registration-store` (the `ChannelBotRegistration` GAgent / readmodel / projector, removed in ADR-0037), delete that persisted event stream and any snapshots for actor id `channel-bot-registration-store` before or during rollout. The retired-actor startup-cleanup spec (`2026-04-28-retired-actor-startup-cleanup-runbook.md`) destroys it automatically on startup.
- The expected steady state is an environment with no aevatar-side registration state at all — bot/route/api-key facts live solely on NyxID.

## Goal

Cut production Lark webhook ingress over to `Lark -> NyxID -> Aevatar` and keep the supported runtime surface to the relay ingress + reply path only.

## Preconditions

- Aevatar relay ingress is deployed at:
  - `POST /api/webhooks/nyxid-relay`
- Nyx relay JWT validation is enabled in Aevatar; the validated JWT carries the authoritative scope claim (`scope_id ?? sub ?? NameIdentifier`).
- Lark turn replies already go through the `api-lark-bot` proxy + relay reply token (`channel-relay/reply`).
- The Lark channel-bot, the relay api-key (callback pointing at `/api/webhooks/nyxid-relay`), and the conversation route are provisioned **directly on NyxID** (use `nyxid_channel_bots` / `use_skill(skill="nyxid")`).

## NyxID-side Provisioning Output

NyxID-direct provisioning yields the facts aevatar needs at the boundary:

- the Nyx channel-bot id
- the relay api-key id (its `callback_url` is Aevatar's relay ingress)
- the conversation route id
- the Nyx Lark `webhook_url` that must be configured in the Lark Developer Console

Aevatar does not store any of these; the relay callback JWT carries the scope per turn.

## Cutover Steps

1. Confirm no aevatar-side `channel-bot-registration-store` state remains (greenfield, or cleared by the retired-actor cleanup spec).
2. Deploy Aevatar with the Nyx relay ingress and reply path already live.
3. Register or verify the Lark bot + relay api-key + route **directly on NyxID**.
4. In the Lark Developer Console, change the event callback URL to the Nyx `webhook_url`.
   - Enable `im.message.receive_v1`
   - Enable `card.action.trigger`
5. Observe:
   - Nyx -> Aevatar relay callback success, with a non-empty scope claim in the validated JWT
   - Aevatar -> Nyx `channel-relay/reply` success via the `api-lark-bot` proxy
   - no direct Aevatar Lark callback route is registered

## Backfill Notes

- Relay API keys created before `#323` were registered with `platform=lark`. NyxID treats `lark` as the channel-bot platform identifier, not a relay platform name, so relay api-keys should use `platform=generic`. Existing relay keys are not migrated automatically; if NyxID enforces the platform contract on relay use, rotate the existing relay key on the NyxID side and update the Lark Developer Console webhook URL to the new `webhook_url`.

## Expected Runtime Behavior

- Lark bot/route/api-key registration is a NyxID-direct operation; aevatar exposes no `/api/channels/registrations` endpoint and no `register_lark_via_nyx` tool.
- Inbound relay scope is derived per-turn from the validated callback JWT; aevatar performs no registration lookup. A callback JWT with no scope claim is rejected with `401` at the relay ingress.
- the direct Aevatar Lark callback path is not exposed; direct platform callback flows are retired from ChannelRuntime.
- `update_token` is retired; ChannelRuntime does not store or refresh channel credentials, and holds no channel registration state.
- ChannelRuntime no longer requires `ICredentialProvider` / `SecretsStoreCredentialProvider` composition for channel reply delivery.
- Telegram is not part of the supported production contract until it can satisfy the same external credential-authority boundary.
- Lark workflow approvals and `social_media` review steps can use interactive cards through `card.action.trigger`; `/approve`, `/reject`, and `/submit` remain fallback commands.
- public `UserAgentCatalog` queries no longer expose `NyxApiKey`.
