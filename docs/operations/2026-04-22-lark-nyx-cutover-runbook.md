# Lark -> NyxID -> Aevatar Cutover Runbook

## Goal

Cut production Lark webhook ingress over to `Lark -> NyxID -> Aevatar`, keep the old direct Aevatar callback only during an explicit rollback window, then retire it with `410 Gone`.

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
4. During the rollback window only, enable legacy direct Lark callback acceptance with:

```json
{
  "ChannelRuntime": {
    "LarkDirectWebhookCutover": {
      "AllowLegacyDirectCallback": true,
      "RollbackWindowEndsUtc": "2026-04-29T00:00:00Z"
    }
  }
}
```

5. Observe:
   - Nyx -> Aevatar relay callback success
   - Aevatar -> Nyx `channel-relay/reply` success
   - no new traffic depending on `/api/channels/lark/callback/{registrationId}`
6. After the rollback window closes, remove or disable `AllowLegacyDirectCallback`.

## Expected Runtime Behavior

- New Lark provisioning goes through Nyx only.
- `POST /api/channels/registrations` no longer accepts direct Lark registrations.
- `channel_registrations action=register` no longer accepts `platform=lark`.
- With rollback disabled, `POST /api/channels/lark/callback/{registrationId}` returns `410 Gone`.
- Nyx-backed Lark registrations must not use `update_token` or direct test-reply diagnostics.
- public `UserAgentCatalog` queries no longer expose `NyxApiKey`.
- host-side delivery paths that still need Nyx credentials must read them from the runtime-only `UserAgentCatalogNyxCredentialDocument` projection, not from public registration or catalog read models.

## Rollback

If the Nyx relay path is unhealthy during the rollback window:

1. Revert the Lark Developer Console callback URL back to the old Aevatar direct callback URL for the affected legacy registration.
2. Keep `AllowLegacyDirectCallback=true` until the issue is resolved.
3. Fix Nyx relay ingress or reply behavior.
4. Re-run the cutover sequence.

After the rollback window closes, rollback is no longer supported by configuration; the direct Lark callback path is intentionally retired.
