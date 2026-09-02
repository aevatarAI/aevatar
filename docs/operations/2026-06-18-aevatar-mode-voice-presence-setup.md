# aevatar-mode Voice Presence Setup Runbook

This runbook is the operator path for running `voice-presence` with
`BRAIN_PROVIDER=aevatar`. The contract source of truth remains
`docs/canon/voice-presence-integration.md`; this page only assembles the
existing operational surfaces needed to bring up a verified session.

No runtime behavior is introduced here.

## Runtime Shape

```text
device audio
  -> voice-presence edge
  -> aevatar /ws/voice
  -> VoicePresenceModule on the selected RoleGAgent
  -> OpenAI Realtime via NyxID-minted ephemeral credential

edge tool call
  -> VoicePresenceModule
  -> NyxID connected-service proxy
  -> NyxID node outbound WebSocket
  -> voice-presence /edge-tools HTTP API

household event
  -> voice-presence device-event client
  -> aevatar /api/device-events/{registrationId}
  -> target actor through the device callback command facade
```

## Inputs

Use deployment-specific values for the placeholders below:

- `AEVATAR_BASE_URL`: public aevatar Mainnet Host URL.
- `NYXID_OIDC_AUTHORITY`: NyxID browser OAuth authority.
- `NYXID_API_BASE_URL`: NyxID backend API and RFC 8707 resource-server base URL.
- `NYXID_WS_URL`: NyxID node WebSocket URL, normally
  `wss://<nyxid-host>/api/v1/nodes/ws`.
- `NYXID_API_KEY`: caller key or token with `proxy` access to the aevatar
  service and the OpenAI realtime service.
- `SCOPE_ID`: aevatar scope that owns the target actor.
- `ACTOR_ID`: `RoleGAgent` actor that will own the voice session.
- `AGENT_KIND`: registered agent kind for that actor.
- `VOICE_MODULE_NAME`: normally `voice_presence` or `voice_presence_openai`.
- `VOICE_PRESENCE_EDGE_URL`: URL the NyxID node can use to reach the local
  edge HTTP API.
- `EDGE_SERVICE_SLUG`: NyxID custom-service slug for the edge `/edge-tools`
  API.
- `NODE_ID`: NyxID node id on the machine that can reach the edge HTTP API.
- `DEVICE_EVENT_REGISTRATION_ID`: aevatar registration id returned after the
  registration read model materializes.
- `DEVICE_EVENT_HMAC_KEY`: shared callback HMAC key configured on both
  voice-presence and the aevatar registration.

Do not put long-lived OpenAI provider keys in aevatar. The production voice
provider path expects NyxID to hold the upstream OpenAI credential and mint a
short-lived realtime credential for each attach.

## 1. Configure aevatar Host

Mainnet Host reads production configuration from `AEVATAR_`-prefixed
environment variables. These are the voice-specific values for the existing
OpenAI realtime broker path:

```bash
export AEVATAR_Aevatar__NyxId__Authority="$NYXID_OIDC_AUTHORITY"
export AEVATAR_Aevatar__NyxId__ApiBaseUrl="$NYXID_API_BASE_URL"
export AEVATAR_Aevatar__Authentication__Authority="$NYXID_OIDC_AUTHORITY"
export AEVATAR_Aevatar__Authentication__Audience="urn:aevatar:api"

export AEVATAR_Aevatar__VoicePresence__OpenAI__Nyxid__ServiceSlug="openai-realtime"
export AEVATAR_Aevatar__VoicePresence__OpenAI__Nyxid__MintPath="v1/realtime/client_secrets"
export AEVATAR_Aevatar__VoicePresence__OpenAI__Nyxid__Model="gpt-realtime"

export AEVATAR_Aevatar__VoicePresence__OpenAI__Model="gpt-realtime"
export AEVATAR_Aevatar__VoicePresence__OpenAI__Voice="alloy"
export AEVATAR_Aevatar__VoicePresence__OpenAI__Instructions="你是一个简短自然的语音助手。"
```

Every non-Development host with authentication enabled must set the external
JWT audience. Startup fails when this value is empty; the scope-service-token
audience is a separate setting and does not satisfy this requirement.

`ServiceSlug` defaults to `openai-realtime` in code, but set it explicitly in
production so redeploys are easy to audit. The NyxID service endpoint for that
slug must be `https://api.openai.com`, so the proxy request resolves to
`/v1/realtime/client_secrets`.

If device-event callback HMAC checking is needed, keep the default verification
enabled and leave the freshness window at the default 10 seconds unless the
deployment has measured clock skew:

```bash
export AEVATAR_Aevatar__DeviceEvents__SkipHmacVerification="false"
```

## 2. Register OpenAI Realtime in NyxID

Create or verify the NyxID service that stores the real OpenAI key:

```bash
nyxid service list --output json

nyxid service add --custom \
  --slug openai-realtime \
  --label "OpenAI Realtime" \
  --endpoint-url "https://api.openai.com" \
  --auth-method bearer \
  --auth-key-name "Authorization"
```

The caller that connects to `/ws/voice` must be able to proxy this service.
The aevatar host receives the caller bearer on the WebSocket request, uses it to
ask NyxID for an ephemeral `ek_...`, and then connects directly to OpenAI
Realtime. NyxID is not in the audio hot path.

## 3. Register the Edge Tool Service

On the LAN machine that can reach `voice-presence`, register and run a NyxID
node:

```bash
nyxid node register-token --name "home-voice-edge" --output json

nyxid node register \
  --token nyx_nreg_... \
  --url "$NYXID_WS_URL" \
  --keychain

nyxid node daemon install
nyxid node daemon start
nyxid node daemon status
```

From an operator machine with NyxID access, create a custom service routed
through that node:

```bash
nyxid service add --custom \
  --slug "$EDGE_SERVICE_SLUG" \
  --label "Home Voice Edge" \
  --endpoint-url "$VOICE_PRESENCE_EDGE_URL" \
  --auth-method bearer \
  --auth-key-name "Authorization" \
  --via-node "$NODE_ID"
```

Then store the local upstream credential on the node, if the edge API requires
one:

```bash
nyxid node credentials add \
  --service "$EDGE_SERVICE_SLUG" \
  --url "$VOICE_PRESENCE_EDGE_URL" \
  --header "Authorization" \
  --secret-format bearer
```

The edge OpenAPI document must expose only voice-safe operations with
`x-aevatar-tool`. Aevatar discovers those operations through NyxID live service
discovery; do not create a local service catalog in aevatar.

Verify node routing from outside the LAN:

```bash
curl -sf "$NYXID_API_BASE_URL/api/v1/proxy/s/$EDGE_SERVICE_SLUG/edge-tools/openapi.json" \
  -H "Authorization: Bearer $NYXID_API_KEY" \
  | jq '.openapi, .paths'
```

## 4. Register Device-Event Ingress

Register the callback target in aevatar:

```bash
curl -sS -X POST "$AEVATAR_BASE_URL/api/device-events/registrations" \
  -H "Authorization: Bearer $NYXID_API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"scope_id\": \"$SCOPE_ID\",
    \"hmac_key\": \"$DEVICE_EVENT_HMAC_KEY\",
    \"description\": \"voice-presence edge\",
    \"device_event_target_actor_id\": \"$ACTOR_ID\"
  }" | jq
```

The response is `202 Accepted`. It contains a command receipt, not the final
registration id. Poll the registration read model until the document appears:

```bash
curl -sS "$AEVATAR_BASE_URL/api/device-events/registrations" \
  -H "Authorization: Bearer $NYXID_API_KEY" \
  | jq '.[] | select(.description == "voice-presence edge")'
```

Use the returned `id` as `DEVICE_EVENT_REGISTRATION_ID`. The callback URL is:

```text
$AEVATAR_BASE_URL/api/device-events/$DEVICE_EVENT_REGISTRATION_ID
```

The callback body must be the NyxID relay callback shape whose
`content.text` is a JSON device event object. The HMAC signature covers the
raw request body and is sent as lowercase hex in `X-NyxID-Signature`. The
signed body timestamp, not `X-NyxID-Timestamp`, controls freshness.

Supported `event_type` values at this ingress are:

- `temperature_change`
- `person_detected`
- `scene_summary`
- `camera_scene`
- `motion_detected`
- `speech_detected`
- `doorbell_pressed`
- `smoke_detected`
- `water_leak_detected`
- `carbon_monoxide_detected`
- `glass_break_detected`
- `lock_tampered`
- `alarm_triggered`

## 5. Enable Voice Presence on the Actor

Enable the voice module on the selected actor:

```bash
curl -sS -X POST \
  "$AEVATAR_BASE_URL/api/scopes/$SCOPE_ID/gagent-actors/$ACTOR_ID/voice-presence/enable?agentKind=$AGENT_KIND" \
  -H "Authorization: Bearer $NYXID_API_KEY" \
  -H "Content-Type: application/json" \
  -d "{
    \"moduleName\": \"$VOICE_MODULE_NAME\",
    \"remoteAudioSupport\": \"VOICE_REMOTE_AUDIO_SUPPORT_SUPPORTED\"
  }" | jq
```

The expected response is `202 Accepted` with `stage = "accepted"`. This only
means the command reached the dispatch boundary. Re-attach after the
`voice-presence-capabilities` read model has observed the committed state.

There is no public voice-capability query endpoint today. Operationally, use
one of these existing surfaces:

- wait until `/ws/voice` stops returning `404`, `409`, or
  `not_initialized` for the target actor;
- inspect the configured projection store for document id
  `<ACTOR_ID>:<VOICE_MODULE_NAME>` in index `voice-presence-capabilities`;
- in local or test hosts that expose the projection reader, read
  `VoicePresenceCapabilityReadModel` through that host's existing inspection
  surface.

Do not trigger projection priming from the query path. If the read model is
stale, wait for the normal projection pipeline or repair the projection through
the deployment's projection operations path.

## 6. Configure voice-presence

On the edge host, set aevatar mode and point the edge at the aevatar brain:

```bash
export BRAIN_PROVIDER="aevatar"
export AEVATAR_BASE_URL="$AEVATAR_BASE_URL"
export AEVATAR_VOICE_WS_URL="${AEVATAR_BASE_URL/https:/wss:}/ws/voice"
export AEVATAR_ACCESS_TOKEN="$NYXID_API_KEY"
export AEVATAR_VOICE_MODULE_NAME="$VOICE_MODULE_NAME"
export AEVATAR_DEVICE_EVENT_CALLBACK_URL="$AEVATAR_BASE_URL/api/device-events/$DEVICE_EVENT_REGISTRATION_ID"
export AEVATAR_DEVICE_EVENT_HMAC_KEY="$DEVICE_EVENT_HMAC_KEY"
```

If the edge needs to pass channel or connected-service hints to aevatar, use
the existing `/ws/voice` query parameters that the host maps into
`VoiceToolExecutionContext`, such as `channel`, `sender_id`,
`registration_scope_id`, `connected_services_context`, and
`nyxid_route_preference`.

## 7. Smoke Test `/ws/voice`

Use a WebSocket client that can send binary PCM16 frames. A text-only upgrade
test is still useful because a successful session emits a `sessionAccepted`
control frame before audio starts.

```bash
websocat \
  -H="Authorization: Bearer $NYXID_API_KEY" \
  "${AEVATAR_BASE_URL/https:/wss:}/ws/voice?codec=pcm16&voice_module_name=$VOICE_MODULE_NAME"
```

Expected result:

- the connection upgrades;
- the first text frame is a JSON `sessionAccepted` `VoiceControlFrame`;
- binary downstream frames appear after valid upstream PCM16 input;
- realtime transcript and lifecycle events arrive as JSON `realtimeFrame`
  control frames;
- a normal close detaches the transport lease so the next attach does not
  return `409`.

Common failure signatures:

| Symptom | Meaning | Check |
|---|---|---|
| `503 voice_not_configured` | voice module services were not registered | aevatar host config, NyxID realtime broker slug, provider registration |
| `403` before upgrade | caller identity or route policy rejected the attach | bearer token, caller scope, chat route policy |
| `501 Voice ForwardToModel is not supported in v1.` | route policy resolved to ordinary model forwarding, not a typed voice attach target | chat route policy must carry `voice_attach_target` |
| `404` or `not_initialized` | target actor/module is not enabled or read model is not observed yet | `voice-presence/enable` receipt and capability read model |
| `409` | a transport lease is already attached | close stale edge session and wait for detach |
| `503 voice_credential_unavailable` | host could not issue the volatile voice tool credential | `IVoiceToolCredentialIssuer` registration and caller bearer |
| `503 voice_capability_not_ready` | the enable/lease event committed but its capability read model has not caught up | retry after `Retry-After`; the browser console retries a newly provisioned first connect once |
| WebSocket close `1008 voice_provider_credential_unavailable` | NyxID could not mint the per-session OpenAI Realtime credential | reauthorize Voice so its feature token includes the configured realtime resource; if it already does, verify the service holds a valid OpenAI credential |
| no LAN tools | connected-service tool set is not exposed to the actor/session | NyxID service OpenAPI `x-aevatar-tool`, route/actor tool configuration |
| device event `401` | HMAC signature rejected | shared key and raw-body signature bytes |
| device event `400` | stale timestamp, missing delivery id, or unsupported event type | body timestamp, `event_id`/`correlation_key`, allowlisted `event_type` |

## What Not To Do

- Do not configure `OPENAI_API_KEY` as the production voice credential path.
- Do not route realtime audio through NyxID proxy.
- Do not call the edge `/edge-tools` API directly from aevatar.
- Do not add an aevatar process-local catalog for NyxID services or edge
  tools.
- Do not treat `202 Accepted` from `voice-presence/enable` or device
  registration as committed/read-model-observed.
- Do not use `/ws/voice/{actorId}` for normal clients; it is the dev/admin
  bypass and is authorization gated.

## References

- `docs/canon/voice-presence-integration.md`
- `docs/canon/nyxid-connected-service-tools.md`
- `docs/adr/0025-voice-router-integration.md`
- `docs/adr/0031-voice-edge-local-tools.md`
- `docs/adr/0033-voice-provider-nyxid-ephemeral-broker.md`
