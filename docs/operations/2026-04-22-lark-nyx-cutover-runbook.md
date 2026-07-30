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

Cut production Lark webhook ingress over to `Lark -> NyxID -> Aevatar` and remove the direct Aevatar Lark callback from the supported runtime surface.

## Preconditions

- Aevatar relay ingress is deployed at:
  - `POST /api/webhooks/nyxid-relay`
- Nyx relay JWT validation is enabled in Aevatar.
- Lark turn replies already go through Nyx `channel-relay/reply`.
- Lark bot provisioning is done with:
  - `channel_registrations action=register_channel_via_nyx platform=lark ...`

## Canonical Console

- `/channels` is the canonical onboarding and recovery surface. `/admin#/channels` embeds it and must not maintain a second registration workflow.
- App ID, App Secret, and Verification Token are required for Lark. Encrypt Key is optional. These secrets are request-only and must not appear in registration queries, read models, logs, or browser responses.
- An accepted provisioning request is not proof that Lark ingress works. Treat `pending_webhook` as incomplete until a verified inbound message is received and the registration becomes `active`.

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
- Use the persisted `webhook_url` returned by the registration query exactly as shown. `callback_url` has a separate meaning and must not be substituted or derived.

## Cutover Steps

1. Complete the preflight wipe/greenfield check for `channel-bot-registration-store`.
2. Deploy Aevatar with the Nyx relay ingress and reply path already live.
3. Provision or inspect the Lark bot in `/channels`.
4. While the registration is `pending_webhook`, complete every external action in the Lark Developer Console manually:
   - Paste the exact `webhook_url` into Event Subscriptions as the Request URL.
   - Ensure the Verification Token and optional Encrypt Key match the values used during provisioning.
   - Import the permission JSON shown by `/channels`.
   - Enable `im.message.receive_v1`; enable `card.action.trigger` when interactive approvals are required.
   - Create, publish, and obtain approval for the app version.
5. Send a test message to the bot and refresh `/channels` until the registration becomes `active`. Only a verified inbound message plus `active` proves activation.
6. Observe:
   - Nyx -> Aevatar relay callback success
   - Aevatar -> Nyx `channel-relay/reply` success
   - no direct Aevatar Lark callback route is registered

Aevatar and `nyxid channel-bot verify` do not configure Event Subscriptions, permissions, or publication in the Lark Developer Console. Operators must complete and verify those steps explicitly.

## Replacement Recovery

Use **Replace onboarding** only when the existing registration cannot be recovered. The console first deletes the current registration and waits for that DELETE to succeed, then starts a blank registration flow. Replacement changes both `nyx_channel_bot_id` and `webhook_url`; repeat the Lark Developer Console steps with the new Request URL. If deletion or provisioning fails, stop and keep the existing management view—do not assume the old or new registration is active.

## Backfill Notes

- Relay API keys created before `#323` were registered with `platform=lark`. New provisioning uses `platform=generic` because NyxID treats `lark` as the channel-bot platform identifier, not a relay platform name. Existing relay keys are not migrated automatically; if NyxID enforces the platform contract on relay use, rotate the existing relay key by re-running the Nyx-backed provisioning flow and updating the Lark Developer Console webhook URL to the new `webhook_url`.

## Expected Runtime Behavior

- New Lark provisioning goes through Nyx only.
- `accepted`, `pending_webhook`, and `active` are distinct states; no accepted response or locally completed checklist is promoted to `active`.
- Registration queries expose the committed `webhook_url` needed for recovery without exposing Lark secrets.
- `POST /api/channels/registrations` no longer accepts direct Lark registrations.
- `channel_registrations action=register` no longer accepts `platform=lark`.
- the direct Aevatar Lark callback path is no longer exposed.
- direct platform callback flows are retired from ChannelRuntime.
- `update_token` is retired; ChannelRuntime does not store or refresh channel credentials.
- ChannelRuntime registration queries return only non-secret routing/identity/status handles.
- ChannelRuntime no longer requires `ICredentialProvider` / `SecretsStoreCredentialProvider` composition for channel registration or reply delivery.
- Telegram is not part of the supported production contract until it can satisfy the same external credential-authority boundary.
- Lark workflow approvals and `social_media` review steps can use interactive cards through `card.action.trigger`; `/approve`, `/reject`, and `/submit` remain fallback commands.
- Nyx-backed Lark registrations must not use retired direct-callback diagnostics.
- public `UserAgentCatalog` queries no longer expose `NyxApiKey`.
