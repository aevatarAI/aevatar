# Lark Channel-Bot Projection Recovery Runbook

This runbook covers the case where Lark messages reach
`/api/webhooks/nyxid-relay` and authenticate successfully, but Aevatar replies
with `401 Unauthorized` because the local channel registration read model is
missing or stale.

Refactor note (iter27/cluster-003-channel-registration-scope-backfill):
operations recovery no longer uses readmodel-derived backfill or live local
mirror repair. Use only `register_lark_via_nyx` and projection-only
`rebuild_projection`.

## Symptom Signature

In console-backend logs:

```text
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

## Recovery Path

### 1. Confirm The Environment

```bash
aevatar-cli env
aevatar-cli whoami
aevatar-cli api GET /api/channels/registrations
```

If the last command returns `[]`, continue.

### 2. Rebuild Projection

`rebuild_projection` is projection-only. It does not read the read model, infer
write candidates, or repair local mirror state.

```bash
aevatar-cli chat "Run channel_registrations action=rebuild_projection reason=lark-relay-readmodel-refresh"
```

Then verify:

```bash
aevatar-cli api GET /api/channels/registrations
```

If the registration appears with the expected `nyx_agent_api_key_id`, send a
Lark message and confirm the relay no longer returns 401.

### 3. Re-Provision If Authoritative State Is Missing

If `rebuild_projection` completes but the registrations list is still empty,
the authoritative `channel-bot-registration-store` state has no registration to
project. Do not repair it from the read side and do not reuse existing Nyx
resources through a local mirror repair surface.

Provision again through the supported path:

```bash
aevatar-cli chat "Run channel_registrations action=register_lark_via_nyx with:
- app_id=<lark app id>
- app_secret=<lark app secret>
- verification_token=<verification token when available>
- webhook_base_url=https://<aevatar-host>"
```

Configure the Lark developer console callback URL to the Nyx webhook URL
returned by the tool.

## What You Must Not Do

- Do not call or document retired local mirror repair surfaces; the HTTP
  endpoint, tool action, service method, and live repair command path are gone.
- Do not use readmodel contents to infer write candidates for scope repair.
- Do not delete Nyx api-keys, channel-bots, or routes to force a clean state
  unless you are intentionally re-provisioning and updating the Lark developer
  console webhook configuration.

## Verification

After either recovery path:

```bash
aevatar-cli api GET /api/channels/registrations
```

Then send a message to the Lark bot. Expected logs:

- Relay webhook returns `200`/`202`.
- `Resolved relay callback scope id from relay scope resolver` is emitted.
- The bot replies in Lark.
