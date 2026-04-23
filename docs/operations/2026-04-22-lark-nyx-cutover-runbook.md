# Lark -> NyxID -> Aevatar Cutover Runbook

This runbook reflects the post-`#308` production contract.

## Preflight

- This cutover is a hard contract cut. Do not perform an in-place rollout while retaining pre-ADR-0012 `ChannelBotRegistration` persisted state.
- Before deploying the `#308` schema/runtime cut, explicitly delete the persisted event stream and any snapshots for actor id `channel-bot-registration-store`.
- If any environment has ever run a version that persisted the pre-ADR-0012 `ChannelBotRegistrationEntry` / `ChannelBotRegisterCommand` / `ChannelBotRegistrationDocument` wire layout, either wipe that persisted state first or stop the rollout.
- The expected steady state for this runbook is:
  - greenfield environment with no legacy `channel-bot-registration-store` data, or
  - environment where that legacy state has been intentionally cleared before deployment

## Goal

Cut production Lark webhook ingress over to `Lark -> NyxID -> Aevatar` and retire the direct Aevatar Lark callback with `410 Gone`.

## Preconditions

- Aevatar relay ingress is deployed at:
  - `POST /api/webhooks/nyxid-relay`
- Nyx relay JWT validation is enabled in Aevatar.
- Lark turn replies already go through Nyx `channel-relay/reply`.
- Lark bot provisioning is done with:
  - `channel_registrations action=register_lark_via_nyx ...`

## Provisioning Output

The Nyx-backed provisioning flow returns:

- `registration_id`
- `nyx_channel_bot_id`
- `nyx_agent_api_key_id`
- `nyx_conversation_route_id`
- `relay_callback_url`
- `webhook_url`

Operationally:

- `relay_callback_url` is Aevatar's Nyx relay ingress.
- `webhook_url` is the Nyx Lark webhook URL that must be configured in the Lark Developer Console.

## Cutover Steps

1. Complete the preflight wipe/greenfield check for `channel-bot-registration-store`.
2. Deploy Aevatar with the Nyx relay ingress and reply path already live.
3. Provision or verify the Lark bot through the Nyx-backed registration flow.
4. In the Lark Developer Console, change the event callback URL to the returned Nyx `webhook_url`.
   - Enable `im.message.receive_v1`
   - Enable `card.action.trigger`
5. Observe:
   - Nyx -> Aevatar relay callback success
   - Aevatar -> Nyx `channel-relay/reply` success
   - `POST /api/channels/lark/callback/{registrationId}` returns `410 Gone`

## Expected Runtime Behavior

- New Lark provisioning goes through Nyx only.
- `POST /api/channels/registrations` no longer accepts direct Lark registrations.
- `channel_registrations action=register` no longer accepts `platform=lark`.
- `POST /api/channels/lark/callback/{registrationId}` returns `410 Gone`.
- direct platform callback/test-reply flows are retired from ChannelRuntime.
- `update_token` is retired; ChannelRuntime does not store or refresh channel credentials.
- ChannelRuntime registration queries return only non-secret routing/identity/status handles.
- ChannelRuntime no longer requires `ICredentialProvider` / `SecretsStoreCredentialProvider` composition for channel registration or reply delivery.
- Telegram is not part of the supported production contract until it can satisfy the same external credential-authority boundary.
- Lark workflow approvals and `social_media` review steps can use interactive cards through `card.action.trigger`; `/approve`, `/reject`, and `/submit` remain fallback commands.
- Nyx-backed Lark registrations must not use retired direct-callback diagnostics.
- public `UserAgentCatalog` queries no longer expose `NyxApiKey`.
