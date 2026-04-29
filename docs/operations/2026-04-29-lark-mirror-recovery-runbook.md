# Lark Channel-Bot Local Mirror Recovery Runbook

This runbook covers the case where Lark messages reach
`/api/webhooks/nyxid-relay` and authenticate successfully, but Aevatar replies
with `401 Unauthorized` because the local `ChannelBotRegistrationDocument` for
the bot's NyxID api-key is missing. The Nyx-side bot, route, and api-key all
still exist and are working; only the Aevatar mirror was lost.

This is the recovery path used during the 2026-04-28 incident
(see issue #502 for the underlying drivers).

## Symptom signature (grep this exactly)

In console-backend logs:

```
warn: Aevatar.NyxId.Chat.Relay[0]
      Relay callback authentication succeeded but did not resolve a canonical scope id:
      message=<uuid>, apiKeyId=<uuid>
HTTP/1.1 POST .../api/webhooks/nyxid-relay - 401
```

And the local registrations list comes back empty:

```bash
aevatar-cli api GET /api/channels/registrations
# []
```

If both lines hold, this runbook is the right one.

## What is NOT this runbook

- `RegisterAsStreamProducer failed` / `InconsistentStateException` for a
  `projection.durable.scope:*` actor — that's the Orleans pub/sub stale-state
  issue covered by PR #501.
- `EventStoreOptimisticConcurrencyException: expected N, actual N+1` — that's
  the version-key drift issue covered by issue #502. After PR #503 lands,
  `EventSourcingBehavior.ConfirmEventsAsync` self-heals on conflict by
  refreshing `_currentVersion` and dropping the rejected batch from
  `_pending` (handler re-execution replays it on the runtime envelope retry,
  no duplicates). On replay, drift is fatal by default (throws
  `EventStoreVersionDriftException`); projection scope actors opt in via
  `EventSourcingRuntimeOptions.ShouldRecoverFromVersionDriftOnReplay` (wired
  in `Aevatar.Foundation.Runtime.Hosting` to recover only
  `projection.{durable,session}.scope:*` ids). Domain GAgents still throw —
  see the runbook for #502 for the manual repair procedure if you hit drift
  on a non-projection actor.
- `Optimistic concurrency conflict` followed by relay 401 in the SAME silo
  start — that's a chained failure where the projection scope is stuck.
  Projection scope drift is now self-recovered on activation; if it still
  wedges, capture the `EventStoreVersionDriftException` (or the
  `Event sourcing replay recovering from version drift` warning) for triage,
  then come back here if registrations are still `[]`.

## Why the data went missing

The `channel-bot-registration-store` actor lives at one stable id under
`Aevatar.GAgents.Channel.Runtime`. Past namespace migrations (e.g.
`ChannelRuntime` → `Channel.Runtime`) routed retired-actor cleanup through
`RetiredActorCleanupHostedService`, which destroyed the old actor + reset its
event stream. The migration replaces the actor binding but does not migrate
the registration entries into a new actor — `state.Registrations` on the
new-namespace actor is empty, so the query-side
`ChannelBotRegistrationDocument` index has nothing to project.

Anything that triggered a destroy+reset of `channel-bot-registration-store`
without re-mirroring from Nyx (manual cleanup, retired-cleanup, accidental
key wipe in Garnet) lands you here.

## Prerequisites

- `aevatar-cli` installed and authenticated against the affected environment.
- `nyxid` CLI installed and logged in to the NyxID account that owns the bot.
- The NyxID account must have admin/list access to the channel-bot in
  question. For personal scopes this is the same user that originally
  provisioned the bot.

## Recovery steps

### 1. Confirm the diagnosis

```bash
aevatar-cli env                        # confirm env is the one you expect
aevatar-cli whoami
aevatar-cli api GET /api/channels/registrations
```

If the last command returns `[]`, the local mirror is empty as expected.

### 2. Find the Nyx-side identifiers

`repair_lark_mirror` needs four pieces of data:
`nyx_channel_bot_id`, `nyx_agent_api_key_id`, `nyx_conversation_route_id`,
and `scope_id`. The relay 401 log line gives you `apiKeyId` for free; the
others come from the NyxID CLI.

```bash
# The Lark bot itself.
nyxid channel-bot list --output json
# Find the lark bot whose label/api-key match the failing webhook.
# nyx_channel_bot_id = bots[i].id

# Conversation route attached to the bot.
nyxid channel-bot route list --bot-id <nyx_channel_bot_id> --output json
# nyx_conversation_route_id = conversations[0].id

# Sanity-check the api-key matches the apiKeyId in the relay 401 log.
nyxid api-key show <nyx_agent_api_key_id> --output json
# Confirm:
#   - callback_url ends in /api/webhooks/nyxid-relay on the right host
#   - is_active = true
```

If the api-key is inactive or the channel-bot is not present in Nyx, this
runbook does not apply — the bot needs to be re-provisioned from scratch
through `channel_registrations action=register_lark_via_nyx` (see the cutover
runbook). Stop here.

### 3. (Optional) Recover the original `registration_id`

If you want the rebuilt mirror to keep the pre-incident registration id
(useful when external systems already reference it), the id can be recovered
from two prefixes that surface in the Nyx-side resources:

- The bot label is `Aevatar Lark Bot {registrationId[..8]}`.
- The api-key name is `aevatar-lark-relay-{registrationId[..12]}`.

So the bot label `Aevatar Lark Bot 4c829032` plus api-key name
`aevatar-lark-relay-4c829032a027` together expose 12 hex characters of the
original 32-char registration id (`4c829032a027...`). If a historical
projection-store delete log is still around, the FULL id is in the
Elasticsearch delete trace:

```
Projection read-model delete completed.
   readModelType=Aevatar.GAgents.Channel.Runtime.ChannelBotRegistrationDocument
   key=4c829032a02746cbb85f3ab871a2c4d6  result=Applied
```

Otherwise it's safe to let `repair_lark_mirror` mint a new registration id —
the apiKey-based relay routing does not depend on it.

### 4. Find your Aevatar `scope_id`

```bash
aevatar-cli api GET /api/auth/me
# scopeId = ... (this is what `repair_lark_mirror` needs)
```

For personal scopes this is your NyxID `sub`. Pin this scope as active so
chat works:

```bash
aevatar-cli scopes use <scope_id>
```

### 5. Trigger the repair

There are two equivalent ways to trigger `repair_lark_mirror`. Pick the
direct HTTP endpoint when the silo is healthy enough to serve requests; fall
back to the LLM tool path if it isn't (e.g. NyxidChat agent is the only
known-working interface).

#### 5a. Direct HTTP endpoint (preferred)

`POST /api/channels/registrations/repair-lark-mirror` is the authenticated
direct equivalent of the LLM tool. It validates Nyx-side resources and
dispatches the local `ChannelBotRegisterCommand` exactly like the tool does,
without needing a chat session or a scope-bound NyxidChat agent.

```bash
aevatar-cli api POST /api/channels/registrations/repair-lark-mirror --json '{
  "registration_id": "<from step 3, optional>",
  "scope_id": "<from step 4>",
  "nyx_provider_slug": "api-lark-bot",
  "webhook_base_url": "https://<aevatar-host>",
  "nyx_channel_bot_id": "<from step 2>",
  "nyx_agent_api_key_id": "<from step 2>",
  "nyx_conversation_route_id": "<from step 2>"
}'
```

Successful response is `202 Accepted` with the registration id and the IDs
echoed back. Failures map to the same status codes used by `POST
/api/channels/registrations`:

- `400` — missing required field, scope mismatch, malformed JSON
- `401` — no Authorization bearer token
- `502` — Nyx-side resource lookup failed (api-key inactive, channel-bot
  deleted, route mismatch)
- `500` — `nyx_base_url_not_configured` (host misconfiguration, escalate)

#### 5b. LLM-tool fallback (`channel_registrations action=repair_lark_mirror`)

Same operation, routed through the `NyxidChat` agent's tool surface. Use
this if the direct endpoint is unavailable on the deployed version.

```bash
aevatar-cli chat new --title "repair-lark-mirror"
aevatar-cli chat "Run channel_registrations action=repair_lark_mirror with:
- nyx_channel_bot_id=<from step 2>
- nyx_agent_api_key_id=<from step 2>
- nyx_conversation_route_id=<from step 2>
- registration_id=<from step 3, or omit to mint a new one>
- scope_id=<from step 4>
- nyx_provider_slug=api-lark-bot
- webhook_base_url=<aevatar host>

The local Aevatar mirror is missing for this Lark bot; Nyx already has all
the resources. Just call repair_lark_mirror to rebuild the local mirror."
```

The `aevatar-cli chat` rendering may print `[unknown frame: message]` lines
while the SSE stream is in flight. That's a known cosmetic gap — the call
still completes.

#### Verify either path

```bash
aevatar-cli api GET /api/channels/registrations
```

The bot should now appear with the correct `nyx_agent_api_key_id`.

### 6. Verify Lark replies

Send a message to the Lark bot. Watch the console-backend logs:

- Relay webhook returns `200`/`202` (no more 401 on the canonical-scope-id
  branch).
- `Resolved relay callback scope id from relay scope resolver` info log fires.
- The bot replies in Lark.

If the relay is still 401 at this point, the registration is in the local
mirror but the projection write hasn't reached Elasticsearch yet —
`ChannelBotRegistrationProjector` writes asynchronously through the
projection scope. Wait ~5–10 seconds and retry; if it persists, check
projection scope health (issue #502 territory).

## What you must NOT do as a shortcut

- **Do not call `POST /api/channels/registrations`** to "re-register" the
  bot. That endpoint goes through `INyxChannelBotProvisioningService.ProvisionAsync`,
  which is **not idempotent** and creates a new Nyx api-key + channel-bot +
  route every time. The original Nyx resources stay alive but orphaned, and
  the Lark Developer Console webhook URL no longer matches the new bot.
- **Do not delete the Nyx api-key/bot/route to "force a clean state"**.
  Lark is configured to deliver to the Nyx webhook URL tied to the existing
  Nyx channel-bot id. Deleting it requires reconfiguring Lark.

## When to stop using this runbook

The version-key drift symptoms covered in the "What is NOT this runbook"
section are now self-healing for projection scope actors only:
`EventSourcingBehavior.ConfirmEventsAsync` recovers from the conflict loop
universally, while `ReplayAsync` recovers from drift only for actor ids
matching the `projection.{durable,session}.scope:` prefixes (see
`Aevatar.Foundation.Runtime.Hosting`'s
`ShouldRecoverFromVersionDriftOnReplay` wiring). Step 5 should rarely be
preceded by a manual Garnet reset for projection scope actors; for any
other actor that hits drift, the activation throws
`EventStoreVersionDriftException` and the operator must repair the store
manually. Keep this runbook as long as the data-loss path through
retired-actor cleanup is still possible — i.e. as long as
`RetiredActorCleanupHostedService` can destroy
`channel-bot-registration-store` and migrate to a new actor type without a
parallel data-migration path. If that gap closes, this runbook becomes
obsolete.
