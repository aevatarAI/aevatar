# Lark -> NyxID -> Aevatar Cutover Runbook

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

1. Deploy Aevatar with the Nyx relay ingress and reply path already live.
2. Provision or verify the Lark bot through the Nyx-backed registration flow.
3. In the Lark Developer Console, change the event callback URL to the returned Nyx `webhook_url`.
4. Observe:
   - Nyx -> Aevatar relay callback success
   - Aevatar -> Nyx `channel-relay/reply` success
   - `POST /api/channels/lark/callback/{registrationId}` returns `410 Gone`

## Expected Runtime Behavior

- New Lark provisioning goes through Nyx only.
- `POST /api/channels/registrations` no longer accepts direct Lark registrations.
- `channel_registrations action=register` no longer accepts `platform=lark`.
- `POST /api/channels/lark/callback/{registrationId}` returns `410 Gone`.
- Lark workflow approvals and `social_media` review steps are text-driven through `/approve`, `/reject`, and `/submit`; they do not rely on `card.action.trigger`.
- Nyx-backed Lark registrations must not use `update_token` or direct test-reply diagnostics.
- public `UserAgentCatalog` queries no longer expose `NyxApiKey`.
- host-side delivery paths that still need Nyx credentials must read them from the runtime-only `UserAgentCatalogNyxCredentialDocument` projection, not from public registration or catalog read models.
