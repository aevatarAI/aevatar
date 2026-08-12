---
name: aevatar-local-nyxid-chat-schedule
version: "0.2"
description: Debug local Aevatar Mainnet Host against online NyxID for /api/chat Studio schedule provisioning, frontend-equivalent PKCE finalization, broker-capable OAuth clients, and scheduled-dispatch fire failures.
metadata:
  project: aevatar
  category: plain
---

# Aevatar Local NyxID Chat Schedule

Use this skill when local `/api/chat` must behave like the frontend login flow against online NyxID, especially for `aevatar_provision_workflow_schedule` and later scheduled workflow fires.

This skill is for local debugging only. Do not use it against production hosts, and do not persist local OAuth tokens, access tokens, refresh tokens, PKCE verifiers, or authorization codes in the repo.

## What This Validates

Validate these layers separately:

- local `Aevatar.Mainnet.Host.Api` starts with online NyxID and local Orleans/Garnet settings;
- local `/api/auth/nyxid/config` returns the intended online NyxID authority and OAuth client id;
- frontend-equivalent PKCE login finalizes through `/api/auth/nyxid/finalize`;
- local `/api/chat` accepts the returned local access token;
- `/api/chat` can call the real `aevatar_provision_workflow_schedule` tool and produce a success receipt;
- the resulting scheduled dispatch fires, or fails with evidence that distinguishes local auth, NyxID broker binding, and workflow/runtime issues.

## Important Conclusions From Prior Debugging

The browser redirect route:

```text
/api/oauth/nyxid-callback
```

is also the Lark/broker callback endpoint. When a normal PKCE browser flow lands there with a random frontend state, this response is expected:

```json
{"error":"state_malformed","detail":"绑定链接已过期或无效,请回到 Lark 重新发送 /init"}
```

Do not fix local `/api/chat` by threading a new authority or changing the main chat flow. Production frontend login works because it takes the callback `code` and calls:

```text
POST /api/auth/nyxid/finalize
```

with the original PKCE verifier. Local testing should mirror that flow.

A successful `/api/chat` turn does not prove scheduled execution will succeed. The chat turn can use the local session access token directly; scheduled fires later need Aevatar to exchange a durable NyxID broker binding for a short-lived token. If the later fire logs:

```text
NyxID binding was revoked for the scheduled subject.
```

then `/api/chat -> tool` worked, but durable broker binding reuse did not.

## NyxID OAuth Client Requirements

Use a broker-capable OAuth client that the current NyxID account can manage.

Required client properties:

```text
is_active: true
broker_capability_enabled: true
redirect_uri: http://127.0.0.1:5094/api/oauth/nyxid-callback
allowed_scopes: openid profile email offline_access urn:nyxid:scope:broker_binding proxy
delegation_scopes: proxy:*
default_service_catalog_slugs: aevatar, chrono-llm-public, ornn-api, chrono-sandbox
```

The four default services are required by the Mainnet host path:

```text
aevatar
chrono-llm-public
ornn-api
chrono-sandbox
```

Check the client:

```bash
nyxid developer-app show <client-id> --output json \
  | jq '{id, is_active, broker_capability_enabled, default_service_catalog_slugs, redirect_uris, allowed_scopes, delegation_scopes}'
```

If the current user cannot update an existing client and `developer-app update` returns 404, create a new local smoke client instead of changing app code.

## Start Local Host

Before starting, stop stale Aevatar hosts on the port you will use. Do not kill unrelated hosts in other worktrees or on other ports.

This flow uses local port `5094` and online NyxID API authority:

```bash
AUDIT_KEY="$(openssl rand -base64 32)" \
ASPNETCORE_ENVIRONMENT=PersistentLocal \
ASPNETCORE_URLS=http://127.0.0.1:5094 \
HOST_BACKEND_CONSOLE_OIDC_CLIENT_ID=<client-id> \
AEVATAR_NYXID_AUTHORITY=https://nyx-api.chrono-ai.fun \
AEVATAR_OAUTH_REDIRECT_BASE_URL=http://127.0.0.1:5094 \
ActorRuntime__OrleansStreamBackend=InMemory \
ActorRuntime__OrleansStreamProviderName=AevatarOrleansStreamProvider \
ActorRuntime__OrleansActorEventNamespace=aevatar.actor.events \
ActorRuntime__SecretStoreKeyringPath="$HOME/.aevatar/secret-store-keyring.json" \
ActorRuntime__OrleansPersistenceBackend=Garnet \
ActorRuntime__OrleansGarnetConnectionString=localhost:6379 \
ActorRuntime__SecretStoreBackend=Garnet \
ActorRuntime__SecretStoreVaultPrefix=aevatar:local:secret-vault \
ActorRuntime__SecretStoreRuntimePrefix=aevatar:local:runtime-secrets \
Orleans__ClusteringMode=Localhost \
Orleans__SiloHost=127.0.0.1 \
Orleans__SiloPort=11111 \
Orleans__GatewayPort=30000 \
Projection__Document__Providers__Elasticsearch__Enabled=false \
Projection__Document__Providers__InMemory__Enabled=true \
Projection__Graph__Providers__Neo4j__Enabled=false \
Projection__Graph__Providers__InMemory__Enabled=true \
Projection__Policies__Environment=Development \
Projection__Policies__DenyInMemoryDocumentReadStore=false \
Projection__Policies__DenyInMemoryGraphFactStore=false \
Audit__ActorIdentityHasher__ActiveKeyId=local-dev \
Audit__ActorIdentityHasher__Keys__0__KeyId=local-dev \
Audit__ActorIdentityHasher__Keys__0__KeyBase64="$AUDIT_KEY" \
Aevatar__AdminAccess__AllowedEmails__0=<your-email> \
Cli__App__NyxId__Authority=https://nyx-api.chrono-ai.fun \
Cli__App__NyxId__ApiBase=https://nyx-api.chrono-ai.fun \
dotnet run --project src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
```

Expected config:

```bash
curl -sS http://127.0.0.1:5094/api/auth/nyxid/config \
  | jq '{baseUrl, clientId, scope}'
```

Expected authority:

```text
https://nyx-api.chrono-ai.fun
```

Check Aevatar's local OAuth client projection:

```bash
curl -sS http://127.0.0.1:5094/api/oauth/aevatar-client/status \
  | jq '{status, client_id, nyxid_authority, redirect_uri_registered, broker_capability_observed, oauth_scope_drifted}'
```

`broker_capability_observed: false` does not by itself prove the NyxID developer app lacks broker capability. It is Aevatar's own observed flag and should be interpreted together with callback/finalize and scheduled fire behavior.

## Generate PKCE Authorize URL

Prefer `nyx-api` for the authorize URL. During prior debugging, `https://nyx.chrono-ai.fun/oauth/authorize` returned a browser `502 Bad gateway`, while `https://nyx-api.chrono-ai.fun/oauth/authorize` redirected correctly to the consent UI.

Generate a local authorize URL without printing tokens:

```bash
node - <<'NODE'
const crypto = require('crypto');
function b64url(buf) { return buf.toString('base64').replace(/=/g, '').replace(/\+/g, '-').replace(/\//g, '_'); }
const verifier = b64url(crypto.randomBytes(48));
const challenge = b64url(crypto.createHash('sha256').update(verifier).digest());
const state = b64url(crypto.randomBytes(24));
const url = new URL('https://nyx-api.chrono-ai.fun/oauth/authorize');
url.searchParams.set('response_type', 'code');
url.searchParams.set('client_id', '<client-id>');
url.searchParams.set('redirect_uri', 'http://127.0.0.1:5094/api/oauth/nyxid-callback');
url.searchParams.set('scope', 'openid profile email offline_access urn:nyxid:scope:broker_binding proxy');
url.searchParams.set('code_challenge', challenge);
url.searchParams.set('code_challenge_method', 'S256');
url.searchParams.set('state', state);
require('fs').writeFileSync('/tmp/aevatar-local-pkce-verifier.txt', verifier, { mode: 0o600 });
require('fs').writeFileSync('/tmp/aevatar-local-authorize-url.txt', url.toString(), { mode: 0o600 });
console.log(JSON.stringify({state, authorizeUrlFile:'/tmp/aevatar-local-authorize-url.txt'}));
NODE
open "$(< /tmp/aevatar-local-authorize-url.txt)"
```

For forced service-access review, add `prompt=consent` and resource parameters for all required services:

```text
resource=https://nyx-api.chrono-ai.fun/api/v1/proxy/s/aevatar
resource=https://nyx-api.chrono-ai.fun/api/v1/proxy/s/chrono-llm-public
resource=https://nyx-api.chrono-ai.fun/api/v1/proxy/s/ornn-api
resource=https://nyx-api.chrono-ai.fun/api/v1/proxy/s/chrono-sandbox
```

If the consent page opens and cannot be clicked by automation, use the default-service flow after confirming `default_service_catalog_slugs` already includes the four services.

## Finalize Local Login

After the browser redirects to:

```text
http://127.0.0.1:5094/api/oauth/nyxid-callback?code=<code>&state=<state>
```

ignore the `state_malformed` page body and use the URL's `code` with the saved verifier:

```bash
umask 077
CODE='<code-from-callback-url>'
VERIFIER="$(< /tmp/aevatar-local-pkce-verifier.txt)"
curl -sS -o /tmp/aevatar-local-finalize.json -w '%{http_code}\n' \
  -X POST http://127.0.0.1:5094/api/auth/nyxid/finalize \
  -H 'Accept: application/json' \
  -H 'Content-Type: application/json' \
  --data "$(jq -n --arg code "$CODE" --arg verifier "$VERIFIER" --arg redirect 'http://127.0.0.1:5094/api/oauth/nyxid-callback' '{code:$code, codeVerifier:$verifier, redirectUri:$redirect}')"
```

For service-access review, include:

```json
{"serviceAccessReview":true}
```

Sanitized success check:

```bash
jq '{user:{email:.user.email,name:.user.name}, authorizationCatalogReady, authorizationCatalogRefreshStatus, authorizationCatalogVisibilityStatus, bindingDispatchAccepted, tokenFields:(.tokens|keys)}' /tmp/aevatar-local-finalize.json
```

If finalize returns:

```json
{"error":"required_service_access_missing"}
```

check the OAuth client's default services and the user's connected services. The Mainnet path requires `aevatar`, `chrono-llm-public`, `ornn-api`, and `chrono-sandbox`.

If finalize returns `503 token_exchange_failed` and logs show:

```text
The SSL connection could not be established
Received an unexpected EOF or 0 bytes from the transport stream
```

retry once. NyxID CLI may show the same transient `tls handshake eof`; this is a network/TLS transient, not evidence that `/api/chat` needs code changes.

## Call /api/chat And Force The Real Schedule Tool

Use the returned local token. Do not print it.

First call may stop to ask for Team confirmation if the current scope has no Team. That is expected Studio behavior, not an auth failure.

```bash
TOKEN="$(jq -r '.tokens.accessToken' /tmp/aevatar-local-finalize.json)"
curl -sS -N -o /tmp/aevatar-local-chat-schedule.sse -w '%{http_code}\n' \
  -X POST http://127.0.0.1:5094/api/chat \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Accept: text/event-stream' \
  -H 'Content-Type: application/json' \
  --data "$(jq -n '{sessionId:"local-nyxid-schedule-smoke", workflow:"studio", prompt:"确认创建 Team 名称为 Local NyxID Smoke Team，然后使用 aevatar_provision_workflow_schedule 创建一个每分钟运行一次的简单工作流定时任务。工作流只需要输出 hello local nyxid smoke。时区使用 Asia/Shanghai。"}')"
```

Extract useful SSE evidence without dumping the full system prompt:

```bash
node - <<'NODE'
const fs = require('fs');
const file = '/tmp/aevatar-local-chat-schedule.sse';
for (const line of fs.readFileSync(file, 'utf8').split(/\n/)) {
  if (!line.startsWith('data: ')) continue;
  let obj;
  try { obj = JSON.parse(line.slice(6)); } catch { continue; }
  const hits = [];
  function walk(x) {
    if (!x || typeof x !== 'object') return;
    if (Array.isArray(x)) return x.forEach(walk);
    if (x.toolName || x.toolReceipts) hits.push(x);
    if (x.content && (x.toolCalls || x.contentEmitted)) hits.push({content: x.content, toolCalls: x.toolCalls, toolReceipts: x.toolReceipts});
    for (const value of Object.values(x)) walk(value);
  }
  walk(obj);
  for (const hit of hits) console.log(JSON.stringify(hit));
}
NODE
```

Expected evidence for the provision path:

```text
aevatar_create_team success receipt
aevatar_provision_workflow_schedule success receipt
sideEffectKind: studio.workflow.schedule.provision
subjectKind: studio_member_workflow_schedule
status: accepted
member_id: wf-...
schedule_id: provision-member-wf-...
binding_run_id: bind-...
studio_url: /scopes/.../teams/.../members/.../workflow
observatory_url: /workflow/observatory
```

These failures should be gone after the receipt fix and frontend-equivalent finalize:

```text
NYXID_CHAT_TOOL_RECEIPT_REQUIRED
caller_identity_unavailable
required_service_access_missing
```

## Check Scheduled Fire

Search host logs for the schedule id:

```bash
rg '<schedule-id>|<member-id>|<binding-run-id>|BindingServiceAccessMismatchException|required_service_access_missing|binding was revoked|caller_identity_unavailable|NYXID_CHAT_TOOL_RECEIPT_REQUIRED' <host-output-log>
```

Successful create/materialization evidence looks like:

```text
[Trace] Tool Start: aevatar_provision_workflow_schedule
Actor studio-member:... created
Actor studio-member-binding-run:... created
Actor scheduled-dispatch:... created
Scheduled dispatch configuration prepared ... hasServiceInvocationAuth=True ... hasSenderNyxId=True
ScheduledDispatchConfiguredEvent
ScheduledDispatchNextFireScheduledEvent
Projection read-model write completed ... ScheduledDispatchDocument ... result=Applied
```

Fire evidence looks like:

```text
ScheduledDispatchFireStartedEvent
Scheduled service invocation fire prepared from actor state ... hasSenderNyxId=True ... projectWorkflowCallerCredential=True
```

If fire then fails with:

```text
NyxID binding was revoked for the scheduled subject.
```

interpret it narrowly:

- `/api/chat` and the tool path worked;
- the schedule was created and armed;
- scheduled fire tried to exchange the NyxID broker binding;
- NyxID returned 400 for the broker token exchange;
- Aevatar classified that as revoked binding.

Next checks:

```bash
curl -sS http://127.0.0.1:5094/api/oauth/aevatar-client/status \
  | jq '{status, broker_capability_observed, broker_capability_observed_at, nyxid_authority}'

nyxid oauth bindings list --output json
```

If `broker_capability_observed` remains false after finalize and a broker binding exists in NyxID, the remaining gap is durable broker binding observation/reuse, not chat admission.

## Local Mainnet Observatory Smoke Through NyxID Catalog

Use this shorter path when the goal is to verify authenticated Mainnet Host APIs through online NyxID without exercising the full browser PKCE flow. It is useful for workflow observatory and Activity run list/search regressions.

Prerequisites:

- NyxID CLI is logged in against `https://nyx-api.chrono-ai.fun`.
- A local catalog service exists with slug `aevatar-local-diag-catalog` and points at `http://127.0.0.1:5107`.
- The local catalog service uses JWT identity propagation and forwards/delegates the caller token.
- Do not use the stale `aevatar-local-diag` service; it previously used `identity_propagation_mode: none` and produced misleading `401` results.

Check the service before relying on it:

```bash
nyxid service list --output json \
  | jq '.[] | select(.slug == "aevatar-local-diag-catalog") | {id, slug, endpoint_url, identity_propagation_mode, forward_access_token, inject_delegation_token}'
```

Start local Mainnet Host on `5107` with auth still enabled, but local-only startup dependencies disabled:

```bash
AEVATAR_ALLOW_PLAINTEXT_SECRETS=true \
ASPNETCORE_ENVIRONMENT=Development \
Aevatar__Authentication__Enabled=true \
Aevatar__Authentication__Authority=https://nyx-api.chrono-ai.fun \
Aevatar__Authentication__Audience= \
Aevatar__Authentication__RequireHttpsMetadata=true \
Aevatar__NyxId__InternalApiBaseUrl=https://nyx-api.chrono-ai.fun \
Aevatar__NyxId__AssistantActions__Enabled=false \
Audit__ActorIdentityHasher__ActiveKeyId=local-development-key \
Audit__ActorIdentityHasher__Keys__0__KeyId=local-development-key \
Audit__ActorIdentityHasher__Keys__0__Key=local-development-audit-identity-key \
ChannelIdentity__OAuthClient__Bootstrap__Enabled=false \
GAgentService__Demo__Enabled=false \
Projection__Document__Providers__Elasticsearch__Enabled=false \
Projection__Document__Providers__InMemory__Enabled=true \
Projection__Graph__Providers__Neo4j__Enabled=false \
Projection__Graph__Providers__InMemory__Enabled=true \
Projection__Policies__Environment=Development \
Projection__Policies__DenyInMemoryDocumentReadStore=false \
Projection__Policies__DenyInMemoryGraphFactStore=false \
ActorRuntime__Provider=InMemory \
ActorRuntime__SecretStoreBackend=InMemory \
ASPNETCORE_URLS=http://127.0.0.1:5107 \
dotnet run --project src/Aevatar.Mainnet.Host.Api/Aevatar.Mainnet.Host.Api.csproj --nologo
```

The cluster-internal NyxID URL in Mainnet appsettings is not reachable from local machines, so keep `Aevatar__NyxId__InternalApiBaseUrl=https://nyx-api.chrono-ai.fun` in this smoke. `Aevatar__NyxId__AssistantActions__Enabled=false` and `ChannelIdentity__OAuthClient__Bootstrap__Enabled=false` keep unrelated production bootstraps from blocking local observatory verification.

Verify direct local access is still protected:

```bash
curl -sS -i http://127.0.0.1:5107/api/workflow/observatory/me | head
```

Expected result is `401`. Then verify the same endpoint through NyxID succeeds:

```bash
nyxid proxy request aevatar-local-diag-catalog /api/workflow/observatory/me --output json
```

Create real local Activity run data before testing search. Empty result sets do not validate pre-pagination search behavior.

```bash
MARKER="mainnet-search-$(date +%Y%m%d%H%M%S)"
BODY=$(jq -n --arg marker "$MARKER" '{prompt:("Activity search smoke " + $marker), workflow:"auto", metadata:{smoke_marker:$marker}}')
nyxid proxy request aevatar-local-diag-catalog /api/chat \
  -m POST \
  -H 'content-type: application/json' \
  -d "$BODY" \
  --output json
```

Verify Activity run search hit and miss through the same authenticated catalog proxy:

```bash
nyxid proxy request aevatar-local-diag-catalog "/api/workflow/observatory/activity-runs?take=10&includeTotalCount=true&q=$MARKER" --output json
nyxid proxy request aevatar-local-diag-catalog "/api/workflow/observatory/activity-runs?take=10&includeTotalCount=true&q=${MARKER}-no-hit" --output json
```

For the hit query, expect at least one returned run or a positive total count containing the marker in a searchable field such as workflow name, run id, status, input summary, or activity initiator display value. For the miss query, expect no returned runs and `totalCount` zero when total count is requested.

If NyxID returns `tls handshake eof` or GitHub/NyxID CLI calls return transient `EOF`, retry once before treating it as a host or code failure.

## Cleanup

Remove token-bearing temp files after the smoke:

```bash
rm -f \
  /tmp/aevatar-local-finalize.json \
  /tmp/aevatar-local-pkce-verifier.txt \
  /tmp/aevatar-local-authorize-url.txt
```

Keep sanitized SSE and host logs only when needed for debugging. Stop the local host when finished, but avoid stopping unrelated worktree hosts on other ports.
