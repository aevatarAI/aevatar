# Lark Channel-Bot Projection Recovery Runbook

This runbook covers the case where Lark messages reach
`/api/webhooks/nyxid-relay` and authenticate successfully, but Aevatar replies
with `401 Unauthorized` because the local channel registration read model is
missing or stale.

Refactor note (iter56/cluster-933-channel-registration-rebuild-narrow):
operations recovery no longer exposes a public projection rebuild command.
The registration projection refresh is internal startup maintenance only.

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

### 2. Re-Provision If Local State Is Missing

There is no online public rebuild command. If the registrations list is empty,
do not repair it from the read side and do not reuse existing Nyx resources
through a local mirror repair surface.

Provision again through the supported path:

```bash
aevatar-cli chat "Run channel_registrations action=register_channel_via_nyx platform=lark with:
- lark.app_id=<lark app id>
- lark.app_secret=<lark app secret>
- lark.verification_token=<verification token when available>
- webhook_base_url=https://<aevatar-host>"
```

Configure the Lark developer console callback URL to the Nyx webhook URL
returned by the tool.

## What You Must Not Do

- Do not call or document retired local mirror repair surfaces; the HTTP
  endpoint, tool action, service method, and live repair command path are gone.
- Do not call or document public channel registration projection rebuild
  surfaces; startup refresh is internal Runtime maintenance only.
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
