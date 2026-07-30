# Channel Onboarding Recovery Design

## Problem

`/admin#/channels` and `/channels` currently implement two independent channel
onboarding experiences over the same Aevatar registration APIs. The embedded
admin implementation has drifted from the dedicated `/channels` surface and can
misrepresent browser-only state changes as completed external operations. In
particular, its permission-import and Lark-version-publish buttons do not call
Lark, and its unbind action removes only the local table row instead of deleting
the authoritative registration.

This becomes operationally dangerous after an Aevatar persistence loss. A user
can rebuild the NyxID bot, relay API key, default conversation route, Vault-backed
workflow reply credential, and Aevatar registration, but the new NyxID bot id
changes the Lark Event Subscriptions Request URL. Lark requires that URL to be
updated manually in its developer console. The current admin surface can imply
that this manual work has completed even while NyxID still reports
`pending_webhook`.

The dedicated `/channels` surface already has the correct owner-scoped command
paths, real delete and repair actions, honest status polling, Lark permission
JSON, and explicit manual-console guidance. It should remain the single product
surface instead of maintaining a second onboarding state machine in
`admin.html`.

## Findings

- `admin#/channels` locally marks permission import and Lark version publication
  complete without observing any Lark fact.
- Its unbind action mutates only an in-memory browser array.
- Its Lark link targets the Feishu domestic console even for a Lark app.
- It renders an Encrypt Key result without collecting or forwarding an Encrypt
  Key. A Lark app with encryption enabled therefore cannot be configured
  correctly through this page.
- The registration list response omits the committed `webhook_url`, so after a
  user leaves the registration result screen the exact recovery URL is no longer
  available from Aevatar's read model.
- Lark's Verification Token is described but not required by the dedicated
  `/channels` form, even though a verified inbound event cannot activate the bot
  without it.
- Re-entering registration from an existing registration must not leave a local
  mirror that points to a NyxID bot replaced during re-provisioning.

## Semantic Decision

`/channels` is the only authoritative channel onboarding and recovery UI. The
admin console embeds that same-origin page for its Channels module and deletes
its duplicate channel state machine. No redirect compatibility layer and no
second channel API are introduced.

Aevatar can provision NyxID resources and observe NyxID bot status. It cannot
change Lark Event Subscriptions, grant Lark permissions, or publish a Lark app
version. Those steps remain explicit user actions. The UI may acknowledge that
the user says they completed them, but it may declare the channel complete only
after the existing status endpoint observes a verified inbound event and NyxID
reports `active`.

The committed `ChannelBotRegistrationEntry.WebhookUrl` remains the source for the
exact NyxID Request URL. It is a non-secret external address and is returned by
the registration list API so recovery guidance survives navigation, reloads, and
process restarts.

## Goals

- Make `admin#/channels` render the authoritative `/channels` experience.
- Remove browser-only fake permission, publish, and unbind completion paths.
- Require the Lark Verification Token before registration.
- Support the optional Lark Encrypt Key end to end without persisting it in
  Aevatar state, events, read models, responses, or logs.
- Keep the exact committed `webhook_url` available in the owner registration
  list and manage view.
- Give a `pending_webhook` user a complete, actionable recovery checklist:
  Request URL, Verification Token/Encrypt Key consistency, permission JSON,
  `im.message.receive_v1`, version publication, and a test message.
- Ensure re-provisioning from an existing registration does not retain a stale
  Aevatar mirror.
- Preserve existing workflow-result-delivery repair behavior.

## Non-Goals

- Automate Lark developer-console mutations or browser login.
- Store Lark app credentials in the Aevatar actor or read model.
- Add a generic external-console automation framework.
- Change NyxID's `pending_webhook -> active` contract.
- Rebuild every account's missing registration automatically.
- Add a second channel projection, lifecycle, or query path.

## Selected Approach

### One UI trunk

The admin Channels module uses the existing same-origin `suiteFrame('/channels')`
pattern already used for other canonical console surfaces. The duplicate channel
catalog, wizard state, registration logic, manage view, polling timer, and click
handlers are removed from `admin.html`. Shared admin CSS may remain only when it
is used elsewhere; channel-only dead CSS is deleted when removal is mechanical
and low-risk.

The embedded page keeps its own authentication gate and shared configured token
storage. `embedTrim` hides the nested top bar, so the admin navigation remains
the visible shell without forking channel behavior.

### Typed credential transport

The owner-facing registration request gains optional `encrypt_key`. Host mapping
passes it through the existing typed Lark credential records into
`NyxLarkProvisioningService`. The NyxID adapter includes `encrypt_key` only when
non-empty. The value is method-local transport material: it is not copied into
Protobuf state, domain events, read models, HTTP responses, logs, or Vault. NyxID
remains the credential authority for the platform bot.

The Verification Token is required by `/channels` for Lark registration. The
backend continues validating trust-boundary inputs independently; the browser
validation is guidance, not the security boundary.

### Durable recovery information

`GET /api/channels/registrations` returns:

```json
{
  "webhook_url": "https://nyx.example/api/v1/webhooks/channel/lark/<bot-id>"
}
```

from `ChannelBotRegistrationEntry.WebhookUrl`. `callback_url` retains its current
meaning and is not reused. Cross-account list visibility remains unchanged; no
secret is exposed.

The `/channels` manage view shows the exact `webhook_url` for Lark registrations,
with a copy action. When status is `pending_webhook`, it presents one ordered
recovery checklist and links to `https://open.larksuite.com/app`. It states that
configuration is complete only after a verified inbound event changes status to
`active`. It does not claim that `nyxid channel-bot verify` or a local checkbox
changed Lark configuration.

### Re-provisioning

The manage action is named and described as a replacement, not an in-place bind.
After explicit confirmation, it deletes the existing owner registration through
the real DELETE endpoint before entering the registration wizard. The existing
deprovisioning service removes the NyxID route, bot, key, and local registration
through the authoritative command flow. A failed new registration therefore
leaves no stale mirror claiming that the deleted NyxID bot is active.

The existing 409 recovery remains for a NyxID bot that has no corresponding
Aevatar registration. Its copy continues to warn that replacement changes the
bot id and Request URL.

## User Flow

1. Open `/admin#/channels`; the admin shell embeds `/channels`.
2. Choose Lark and enter App ID, App Secret, Verification Token, optional Encrypt
   Key, and label.
3. Aevatar provisions the relay API key, NyxID channel bot, default route,
   workflow-result-delivery credential, and committed registration mirror.
4. The result shows the new bot id and exact Request URL.
5. The user opens Lark's developer console, configures Event Subscriptions with
   the Request URL and matching verification/encryption values, imports the
   supplied permission JSON, subscribes to `im.message.receive_v1`, and publishes
   an approved version.
6. The user sends a message to the bot.
7. `/channels` polls the existing status endpoint. Only a verified inbound event
   and `status=active` complete the flow.
8. If the user leaves early, the manage view still exposes the committed Request
   URL and the same recovery checklist.

## Error And Honesty Rules

- `accepted` registration is described as provisioned, not Lark-active.
- `pending_webhook` identifies incomplete Lark verification and always links back
  to recovery steps.
- Permission import and version publication are described as manual actions; no
  Aevatar button claims to have performed them.
- Failed status reads remain `unknown`; they do not fabricate `active`.
- The UI does not display a secret recovered from server state. Verification and
  Encrypt Key values are available only during the current credential-entry
  session and must be re-entered for replacement.
- Delete/replacement reports success only from the real endpoint response and
  subsequent read-model refresh.

## Testing

- `BackendConsoleStaticAssetEndpointTests` proves the admin Channels module embeds
  `/channels` and no longer contains the duplicate fake completion handlers.
- `ChannelsEndpointsTests` proves Lark requires a Verification Token, supports an
  optional Encrypt Key, forwards it in the request, exposes manual-boundary copy,
  and renders durable `pending_webhook` recovery guidance.
- `ChannelCallbackEndpointsTests` proves the list response exposes the committed
  `webhook_url` and registration maps `encrypt_key` into typed Lark credentials
  without returning it.
- `NyxLarkProvisioningServiceTests` proves `encrypt_key` is sent to NyxID when
  provided and omitted when blank.
- Existing delete, workflow delivery repair, status parsing, owner scoping, and
  secret-redaction tests remain green.
- Run `bash tools/ci/test_stability_guards.sh` because tests change. Run the
  architecture guards, documentation lint, relevant projects, full solution
  build, and full solution tests before pushing.

## Documentation And Rollout

Update the channel architecture/runbook documentation to state that `/channels`
is the canonical UI and `/admin#/channels` embeds it. Document that Lark recovery
requires updating the new Request URL after bot replacement and that only a
verified inbound event proves activation. No data migration is required because
the new list field reads the already committed `WebhookUrl`.

Rollout is backward compatible at the HTTP level: adding `webhook_url` to a JSON
response and accepting optional `encrypt_key` are additive. The internal admin
duplicate is deleted rather than retained as a hidden compatibility entry.
