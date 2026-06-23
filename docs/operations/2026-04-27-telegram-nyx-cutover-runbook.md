# Telegram -> NyxID -> Aevatar Cutover Runbook

This runbook reflects the post-`#262` Telegram production contract, updated for
ADR-0037 (aevatar no longer self-registers channel bots; inbound scope comes from
the NyxID callback JWT). It is the Telegram counterpart to
`2026-04-22-lark-nyx-cutover-runbook.md` and assumes the same ADR-0013 unified
inbound backbone is already deployed.

## Preflight

- ADR-0012 disallows local Telegram credential ownership in ChannelRuntime; ADR-0037
  removes the aevatar-side registration mirror entirely. The earlier
  `Aevatar.GAgents.Channel.Telegram` direct adapter prototype and the
  `NyxTelegramProvisioningService` / `/api/channels/registrations` provisioning facade
  are removed and must not be redeployed.
- The Telegram channel-bot, relay api-key, and conversation route are registered
  **directly on NyxID**. If any environment still holds aevatar-side
  `channel-bot-registration-store` state, clear it (the retired-actor startup-cleanup
  spec destroys it automatically).
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
- Nyx relay JWT validation is enabled in Aevatar; the validated JWT carries the
  authoritative scope claim (`scope_id ?? sub ?? NameIdentifier`).
- NyxID exposes the `api-telegram-bot` proxy slug for outbound Telegram Bot API calls
  (`sendMessage`, `getChat`).
- A real Telegram bot token has been issued by `@BotFather` and is in hand.

## NyxID-side Provisioning

Provisioning is a **NyxID-direct operation** (use `nyxid_channel_bots` /
`use_skill(skill="nyxid")`); aevatar exposes no registration endpoint. Register the
Telegram channel-bot on NyxID with `platform="telegram"` and the bot token, then a
relay api-key whose callback points at Aevatar's `/api/webhooks/nyxid-relay`, then the
conversation route via the `api-telegram-bot` proxy slug. The NyxID side yields:

- the Nyx channel-bot id
- the relay api-key id (its callback is Aevatar's relay ingress)
- the conversation route id
- the Nyx Telegram `webhook_url`: `https://<nyx>/api/v1/webhooks/channel/telegram/{nyx_channel_bot_id}`
- the outbound provider slug (`api-telegram-bot`)

Aevatar stores none of these; the relay callback JWT carries the scope per turn.

## Cutover Steps

1. Confirm no aevatar-side `channel-bot-registration-store` state remains (greenfield,
   or cleared by the retired-actor cleanup spec).
2. Deploy Aevatar with the `Aevatar.GAgents.Platform.Telegram` composer registered
   (verify `IChannelMessageComposerRegistry.Get(ChannelId.From("telegram"))` resolves to
   `TelegramMessageComposer`).
3. Register the Telegram bot **directly on NyxID**. NyxID's `POST /api/v1/channel-bots`
   calls Telegram's `setWebhook` server-side using a NyxID-managed `secret_token`;
   **do not call `setWebhook` yourself** — overwriting NyxID's secret breaks
   `x-telegram-bot-api-secret-token` verification and may also drop `allowed_updates`
   types Aevatar expects.
4. Observe:
   - Nyx -> Aevatar relay callback success on `/api/webhooks/nyxid-relay` for inbound
     Telegram messages, with a non-empty scope claim in the validated JWT (NyxID
     currently subscribes to `message`, `edited_message`, `channel_post` only —
     `callback_query` button clicks do not round-trip yet, so the Telegram composer
     degrades action buttons to a plain-text bullet list)
   - Aevatar -> Nyx `channel-relay/reply` success for outbound replies (NyxID sends
     these with `parse_mode="Markdown"`; the composer escapes `_`, `*`, `[`, `` ` ``
     so model output cannot accidentally trip `can't parse entities`)
   - Optional: agent-tool calls `telegram_messages_send` / `telegram_chats_lookup`
     succeed against `api-telegram-bot`
5. If you need to rotate the bot token:
   - Issue a new token through `@BotFather` (`/revoke` then `/token`).
   - Re-register on the NyxID side with the new token; this creates a new
     `nyx_channel_bot_id` and triggers NyxID to re-register the webhook.
   - Do **not** call `setWebhook` manually as part of rotation either.

## Manual Cleanup On Partial Provisioning Failure

Provisioning is now NyxID-direct, so there is no aevatar-side rollback to coordinate —
aevatar never persisted any registration state or bot token. If a NyxID-side
registration partially fails and leaves orphaned resources, clean them up directly on
NyxID in reverse order:

1. Delete the conversation route — `DELETE /api/v1/channel-conversations/{route_id}`
2. Delete the channel bot — `DELETE /api/v1/channel-bots/{nyx_channel_bot_id}`
3. Delete the relay api-key — `DELETE /api/v1/api-keys/{nyx_agent_api_key_id}`

Then re-register a fresh set on NyxID.

## Expected Runtime Behavior

- Inbound Telegram updates arrive at Aevatar through `POST /api/webhooks/nyxid-relay`
  carrying `payload.platform == "telegram"`. There is no separate direct Telegram callback path on Aevatar.
- `ConversationReference.Scope` for Telegram traffic is derived by
  `NyxIdRelayConversationTypeMap`:
  `private` -> `DirectMessage`, `group` / `supergroup` -> `Group`,
  `channel` -> `Channel`. Forum topics (`message_thread_id`) are not yet modeled.
- Reply text-only messages flow through `NyxIdRelayOutboundPort.SendAsync(platform="telegram", ...)`
  which dispatches via `TelegramChannelNativeMessageProducer` -> Nyx
  `channel-relay/reply` -> Telegram `sendMessage`.
- Cards in agent intents degrade into the rendered text body for Telegram (no native
  card UI). Action buttons also degrade into a plain-text bullet list of labels
  rather than `inline_keyboard` callback buttons: NyxID's Telegram channel adapter
  does not subscribe to `callback_query` updates today, so any `inline_keyboard`
  click would never round-trip back to Aevatar. The composer's
  `SupportsActionButtons=false` advertises this honestly so callers can plan around
  it; once NyxID grows the `callback_query` subscribe + parse + forward contract
  end-to-end, flip this back and revisit the runbook.
- Aevatar persists no Telegram bot tokens. The token is registered directly on NyxID
  and never crosses aevatar; revocation/rotation is handled at Telegram + NyxID-side
  re-registration time as documented in step 5.
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
