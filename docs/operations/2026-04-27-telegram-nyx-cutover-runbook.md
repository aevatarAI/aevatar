# Telegram -> NyxID -> Aevatar Cutover Runbook

This runbook reflects the post-`#262` Telegram production contract; it is the
Telegram counterpart to `2026-04-22-lark-nyx-cutover-runbook.md` and assumes the
same ADR-0013 unified inbound backbone is already deployed.

## Preflight

- ADR-0012 disallows local Telegram credential ownership in ChannelRuntime; the
  earlier `Aevatar.GAgents.Channel.Telegram` direct adapter prototype is removed and
  must not be redeployed.
- This cut requires the same `channel-bot-registration-store` greenfield / wipe state
  as the Lark cutover: do not register Telegram bots into a store that still holds
  pre-ADR-0012 wire-shape entries.
- Confirm the Aevatar relay ingress (`POST /api/webhooks/nyxid-relay`) and the Nyx relay
  reply path are healthy before adding Telegram traffic.

## Goal

Bring Telegram bot ingress online through `Telegram -> NyxID -> Aevatar`. There is no
direct Telegram ingress on Aevatar — Aevatar exposes no webhook URL that BotFather can
target. The Telegram bot's `setWebhook` URL must point at Nyx, exactly as Lark's
Developer Console webhook does.

## Preconditions

- Aevatar relay ingress is deployed at:
  - `POST /api/webhooks/nyxid-relay`
- Nyx relay JWT validation is enabled in Aevatar.
- NyxID exposes the `api-telegram-bot` proxy slug for outbound Telegram Bot API calls
  (`sendMessage`, `getChat`).
- A real Telegram bot token has been issued by `@BotFather` and is in hand.

## Provisioning

Provisioning is dispatched through the same registration endpoint as Lark; the platform
discriminator is `telegram` and the only required secret is the bot token. Either of the
following two body shapes is accepted:

Shorthand (Telegram only):

```json
{
  "platform": "telegram",
  "label": "Ops Bot",
  "webhook_base_url": "https://aevatar.example.com",
  "bot_token": "1234567890:AA...REDACTED..."
}
```

Generic credentials map (forward-compatible for future platforms):

```json
{
  "platform": "telegram",
  "label": "Ops Bot",
  "webhook_base_url": "https://aevatar.example.com",
  "credentials": {
    "bot_token": "1234567890:AA...REDACTED..."
  }
}
```

The endpoint returns the standard provisioning payload:

- `registration_id`
- `nyx_channel_bot_id`
- `nyx_agent_api_key_id`
- `nyx_conversation_route_id`
- `relay_callback_url` — Aevatar's Nyx relay ingress
- `webhook_url` — the Nyx Telegram webhook URL: `https://<nyx>/api/v1/webhooks/channel/telegram/{nyx_channel_bot_id}`
- `nyx_provider_slug` — defaults to `api-telegram-bot`

## Cutover Steps

1. Complete the preflight wipe / greenfield check for `channel-bot-registration-store`.
2. Deploy Aevatar with `INyxChannelBotProvisioningService` discovery for Telegram
   active and the `Aevatar.GAgents.Platform.Telegram` composer registered (verify the
   ChannelRuntime DI bucket reports two `INyxChannelBotProvisioningService` entries —
   Lark and Telegram — and that `IChannelMessageComposerRegistry.Get(ChannelId.From("telegram"))`
   resolves to `TelegramMessageComposer`).
3. Provision the Telegram bot through the registration endpoint (either body shape).
4. Call Telegram's `setWebhook` with the returned Nyx `webhook_url`. Recommended
   parameters:
   - `url` = `webhook_url`
   - `allowed_updates` = `["message","edited_message","callback_query","channel_post"]`
   - `secret_token` = whatever Nyx documents for HMAC validation on its side
5. Observe:
   - Nyx -> Aevatar relay callback success on `/api/webhooks/nyxid-relay` for inbound
     Telegram messages
   - Aevatar -> Nyx `channel-relay/reply` success for outbound replies
   - Optional: agent-tool calls `telegram_messages_send` / `telegram_chats_lookup`
     succeed against `api-telegram-bot`
6. If you need to rotate the bot token:
   - Issue a new token through `@BotFather` (`/revoke` then `/token`).
   - Re-provision through the registration endpoint with the new token; this creates a
     new `nyx_channel_bot_id`.
   - Re-run `setWebhook` against the new Nyx `webhook_url`.

## Manual Cleanup On Partial Provisioning Failure

`NyxTelegramProvisioningService` rolls back any of the three Nyx resources it
created (`api_key` -> `channel_bot` -> `channel_route`) when an exception is
thrown **before** the local mirror dispatch is accepted. If the local mirror
dispatch itself fails after all three Nyx resources are live, the service
returns `error="local_mirror_accepted_remote_cleanup_skipped"` and **does not
delete the Nyx resources** — the caller is expected to clean up manually so a
later operator can correlate the orphaned IDs with the failed registration.

When you see that error, the response payload still carries the Nyx resource
identifiers (`nyx_channel_bot_id`, `nyx_agent_api_key_id`, and the conversation
route ID is logged on the server side). Reverse-order cleanup against Nyx:

1. Delete the conversation route — `DELETE /api/v1/channel-conversations/{route_id}`
2. Delete the channel bot — `DELETE /api/v1/channel-bots/{nyx_channel_bot_id}`
3. Delete the relay api-key — `DELETE /api/v1/api-keys/{nyx_agent_api_key_id}`

Then re-run the registration endpoint to provision a fresh set. The earlier
ADR-0012 contract still applies — there is no Aevatar-side cleanup needed
because Aevatar never persisted the bot token.

## Expected Runtime Behavior

- Inbound Telegram updates arrive at Aevatar through `POST /api/webhooks/nyxid-relay`
  carrying `payload.platform == "telegram"`. There is no separate `/api/channels/telegram/callback/...` path on Aevatar.
- `ConversationReference.Scope` for Telegram traffic is derived by
  `NyxIdRelayConversationTypeMap`:
  `private` -> `DirectMessage`, `group` / `supergroup` -> `Group`,
  `channel` -> `Channel`. Forum topics (`message_thread_id`) are not yet modeled.
- Reply text-only messages flow through `NyxIdRelayOutboundPort.SendAsync(platform="telegram", ...)`
  which dispatches via `TelegramChannelNativeMessageProducer` -> Nyx
  `channel-relay/reply` -> Telegram `sendMessage`.
- Cards in agent intents degrade into the rendered text body for Telegram (no native
  card UI). Action buttons render as a single-row `inline_keyboard` with
  `callback_data` truncated to the Bot API 64-byte limit; submissions arrive as
  `CardActionSubmission` activities exactly like Lark `card.action.trigger`.
- Aevatar persists no Telegram bot tokens. The token only exists in transit through
  the registration endpoint; revocation/rotation is handled at Telegram +
  re-provisioning time as documented in step 6.
- Telegram tools (`telegram_messages_send`, `telegram_chats_lookup`) require a
  per-call NyxID access token in the request metadata; without it they return
  `success=false, error="No NyxID access token available"` rather than calling Nyx.

## Known Gaps

- Telegram forum topics (`message_thread_id`) are not surfaced in
  `ConversationReference` yet; group threads collapse into the parent group conversation
  scope. Add a typed `ThreadId` field on `TransportExtras` if/when topic-scoped routing
  becomes a product requirement.
- File / photo / voice attachments are not in the chat-only scope. The Telegram
  composer reports `Unsupported` capability when an intent carries attachments;
  agents must avoid producing attachment intents for Telegram until the composer
  grows that branch.
