---
title: "Backend Console Static Assets"
status: active
owner: eanzhao
---

# Backend Console Static Assets

本文定义 Aevatar backend console 的静态资产边界。这里的 console 是内部观测与运维页面，和 `apps/aevatar-console-web` 用户前端没有代码共享关系，也不使用 Node/SPA 构建链。

## 1. 资产策略

Backend console 只使用一种页面承载方式：

1. 页面文件是 checked-in `.html/.css/.js` 静态资产。
2. 资产通过 owning `.csproj` 的 `EmbeddedResource` 编进对应程序集。
3. Endpoint 通过 `IBackendConsoleAssetService` 返回资产。
4. 不使用 `wwwroot`、目录浏览、外部 CDN、npm/pnpm/yarn、webpack/vite/rollup 或其他前端构建步骤。
5. 不再使用 C# `const string Html` / `const string Page` 作为页面载体。

当前页面资产：

| Route | Asset | Owner |
|---|---|---|
| `/admin` | `src/Aevatar.Mainnet.Host.Api/BackendConsole/admin.html` | Mainnet Host |
| `/auto/callback` | `src/Aevatar.Mainnet.Host.Api/BackendConsole/auto-callback.html` | Mainnet Host |
| `/delivery` | `src/Aevatar.Mainnet.Host.Api/BackendConsole/delivery.html` | Mainnet Host |
| `/status` | `src/Aevatar.Mainnet.Host.Api/Status/status.html` | Mainnet Host |
| `/cqrs` | `src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html` | Mainnet Host |
| `/voice` | `src/Aevatar.Mainnet.Host.Api/Voice/voice-console.html` | Mainnet Host |
| `/workflow/skills` | `src/Aevatar.Mainnet.Host.Api/Skills/workflow-skills.html` | Mainnet Host |
| `/workflow/studio`, `/admin/studio` | `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/studio-assistant.html` plus `StudioAssistant/*` | Workflow Infrastructure |
| `/schedules` | `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html` | Workflow Infrastructure |
| `/channels` | `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html` | NyxIdRelay channel package |

## 2. Host Fact Injection

Nyx/OIDC deployment facts are host configuration, not page source:

| Config key | Meaning |
|---|---|
| `Aevatar:BackendConsole:OidcAuthority` | Browser OIDC authority; falls back to existing Nyx/Auth authority config when empty. |
| `Aevatar:BackendConsole:OidcClientId` | Canonical public OAuth client id used by embedded-console PKCE, Studio login/finalization, broker token-exchange, and binding revoke. Bootstrap materializes this value into the OAuth Client Actor; no runtime path may fall back to an older projected client id. |
| `Aevatar:BackendConsole:OidcScope` | Browser OIDC scope. The host adds `offline_access` to every non-empty configured scope to obtain the rotating refresh token required for a durable console session. |
| `Aevatar:BackendConsole:OidcResources` | Additional RFC 8707 resource indicators. The host always includes `{NyxApiBaseUrl}/api/v1/proxy/s/aevatar` and the Ornn proxy resource resolved from `Aevatar:Ornn:NyxIdSlug` (default `ornn-api`). |
| `Aevatar:BackendConsole:NyxApiBaseUrl` | Canonical public NyxID API/resource-server base. It falls back to `Aevatar:NyxId:ApiBaseUrl`; an authority alias is accepted only for a legacy single-endpoint deployment with no `InternalApiBaseUrl`. |
| `Aevatar:BackendConsole:NyxWebBaseUrl` | Optional public NyxID browser origin. It falls back to the resolved `NyxApiBaseUrl`, never to the internal transport address or an independent OAuth issuer. |
| `Aevatar:BackendConsole:StorageKey` | Shared browser localStorage/sessionStorage prefix. |
| `Aevatar:BackendConsole:DefaultReturnPath` | Safe default redirect path after `/auto/callback`. |

Each configurable HTML asset contains `__BACKEND_CONSOLE_CONFIG__`. The serving helper replaces that placeholder with JSON rendered from `BackendConsoleOptions`. The six `HOST_BACKEND_CONSOLE_*` environment variables are optional overrides for host deployment, but `.refactor-loop/host.env` is not a production configuration source.

The OIDC client id and resource indicators are public browser values, not secrets. Every configurable console page appends each injected resource to both `/oauth/authorize` and the authorization-code exchange at `/oauth/token`; the shared `/auto/callback` follows the same contract. The host normalizes the configured scope and adds `offline_access` exactly once, including after environment overrides, because NyxID access tokens expire after 15 minutes and broker-capable clients return a refresh token only when that scope is granted. `Aevatar:NyxId:Authority` owns the public OAuth issuer and discovery. `Aevatar:NyxId:ApiBaseUrl` owns the public control-plane REST origin, RFC 8707 resource identity, LLM gateway, webhook, catalog, and browser destinations. The chat capability broker's `/api/v1/user-services` read and other account/key control-plane calls always use this public API. Mainnet server-side proxy and SSH execution requests prefer `Aevatar:NyxId:InternalApiBaseUrl` only when `Aevatar:NyxId:EnableInternalApiTransport=true`; the default remains public so stale mounted configuration cannot silently select an internal endpoint. A proven pre-connect failure may retry the explicit public API once; a safe `GET/HEAD/OPTIONS` may also retry it once when the internal endpoint returns no response headers within `Aevatar:NyxId:InternalApiFallbackTimeoutSeconds` (default 5 seconds). Mutations never replay after a timeout, and no request replays after TLS failure, connection reset, redirect, caller cancellation, an HTTP response, or a body failure after response headers. Browser assets must never receive the internal transport address. Secrets still belong in the existing host secret/config mechanisms and must not be injected into page assets.

Mainnet production keeps the public identity and internal transport explicit:

```json
{
  "Aevatar": {
    "NyxId": {
      "Authority": "https://nyx-api.chrono-ai.fun",
      "ApiBaseUrl": "https://nyx-api.chrono-ai.fun",
      "EnableInternalApiTransport": true,
      "InternalApiBaseUrl": "http://nyxid-backend-production-api-svc.chronoai-platform.svc.cluster.local:3001",
      "InternalApiFallbackTimeoutSeconds": 5
    },
    "BackendConsole": {
      "OidcAuthority": "https://nyx-api.chrono-ai.fun",
      "NyxApiBaseUrl": "https://nyx-api.chrono-ai.fun"
    }
  }
}
```

Studio's `/api/auth/nyxid/config` still returns an actor-backed snapshot so authority, callback/scope contract, HMAC state, and broker observation remain cluster-coherent. Its `clientId` must match `Aevatar:BackendConsole:OidcClientId`; while Actor projection still carries another id, the provider fails closed instead of combining new configuration with stale runtime facts. Startup and the admin reconcile endpoint materialize the configured id into Actor state, but neither DCR output nor an API request body is an alternative client-id authority.

## 3. Endpoint Boundary

Backend console page endpoints are static shells. They may be anonymous because the browser page performs OIDC PKCE login and all data endpoints enforce authorization server-side.

Static shell endpoint files must not introduce mutating data APIs. Data surfaces stay in their existing API endpoint files, with their existing authorization and audit rules. Adding a new console page means adding the asset, declaring it as an embedded resource, mapping a GET shell route, and extending the guard inventory.

`/delivery` is the standalone Workflow Delivery Center shell, parallel to `/admin`. It shares the
console OIDC PKCE token and `/auto/callback` contract, while preserving a safe `/delivery#/...` return
route. The page determines administrator/customer capabilities only from `GET /api/delivery/session`.
Administrators can select only server-returned allowlisted workflow package versions and create a
delivery request for an explicit target scope. Customers can read only requests authorized for their
NyxID scope, select a server-returned Team, submit only `variableSchema` fields, and observe the durable
installation read model. The page never treats HTTP `202` as completion: only an installation whose
server status is `ready` is rendered as successfully installed. For a personal scope, a customer may
create a hosted NyxID connect link and explicitly re-check its server status. The transient
`connectUrl` stays only in the current page memory; connection references are resolved by the server
and are never submitted by the browser. External-service credentials, connection tokens, and secrets
never enter the page or browser storage; the console's own OIDC login session continues to use the
shared Backend Console storage contract. Organization-scope connection remains an explicit unsupported state until
the NyxID connect-link contract supports an organization target.

Workflow Observatory data endpoints are read-only. Normal run detail reads remain scope-bound under
`GET /api/workflow/observatory/runs/{runId}` and `GET /api/workflow/observatory/runs/{runId}/graph`.
The run rail reads `GET /api/workflow/observatory/activity-runs`: one activity row per `runId`, returned
as a bounded paged envelope with honest `hasMore` / `totalCount` coverage. That row is a selectable
request/run container, not a trajectory's atomic record. The rail offers cursor-based loading for
earlier pages instead of presenting the first window as complete.

The selected run keeps the original `Timeline` tab intact and adds a separate `Trajectory` tab.
`Timeline` remains the complete chronological workflow-event view, including its existing timestamp,
stage, message, actor/agent, step, event-type, and event-data detail; operation rows must not replace,
filter, or reduce that information. `Trajectory` owns the request operation view. It presents the
captured run input as one Input record and the selected detail's durable Model/Tool operations as an
ordered ledger in which every model response and every tool call has its own stable operation identity.
A three-lane `Input / Model / Tools` overview projects those same records onto one time domain, and
selecting a bar or ledger row opens that operation's inspector. `Timeline` remains the default detail
tab. The page polls the run rail and selected detail approximately every three seconds while visible;
the refresh interval is not a strong-consistency guarantee.

Operation timing is evidence, not decoration. A duration bar is rendered only from a recorded start and
completion pair owned by that operation. A recorded start without a completion may render a start
marker and running state, but never a fabricated live or final duration. Missing timing is displayed as
`unavailable`; `updatedAtUtc`, polling time, event order, or the parent run's duration must not be used as
a substitute. Selecting an operation opens its own inspector for the facts applicable to that kind:
input content; model output, reasoning, provider/model, usage, available tool names, and timing; or tool
payload, result, and timing. Missing facts remain visibly unavailable.

Model and Tool operations are durable typed facts, not classifications reconstructed from Timeline.
Committed role progress is copied into run-owned `WorkflowRuntimeOperationRecordedEvent` facts and
materialized into the run-report operation read model; the Observatory detail contract exposes stable
operation/session identity, progress sequence, kind, real start/completion timestamps, and the captured
Model- or Tool-specific fields. Workflow steps remain a separate workflow-oriented view and are never
reclassified as Model or Tool operations.

This contract is not yet fully DSH-equivalent. Input is sourced from the committed run input and does not
have its own typed start/completion lifecycle, so the Input row has no independent Duration bar. A
Model/Tool duration exists only when that operation's typed start and completion were both recorded.
Model operations expose the model-visible tool names captured at start, but the detail contract does not
carry a request-time tools catalog with per-tool schemas, nor separate TTFT/decoding timestamps. The
Admin page renders only the facts present in the operation read model and marks missing timing, model,
usage, catalog, or schema fields unavailable. It must not infer them from Timeline prose, step labels,
current-step summaries, `updatedAtUtc`, actor identity, or opportunistic generic-bag keys.
Aevatar admins resolved by `IPlatformAdminAuthorizer` may use
`GET /api/workflow/observatory/admin/runs/{runId}` and
`GET /api/workflow/observatory/admin/runs/{runId}/graph` to resolve a known run id across scopes. These admin
drilldown endpoints still read only workflow current-state/readmodel artifacts; they do not replay events, prime
projections, or dispatch actor commands.

`/admin#/observatory` is the only user-facing Workflow Observatory surface. The admin shell embeds the internal
`admin-workflow-observatory.html` renderer from `/admin/workflow-observatory`; that frame route is not a product
page and top-level navigation immediately returns to `/admin#/observatory`. The former `/workflow/observatory`
and `/workflow/observatory/callback` routes are not mapped. Studio, Skills, schedules, voice, CQRS, workflow
prompts, and API receipts must link to the admin hash route rather than recreating a standalone observatory.

The admin shell does not retain a second run cache, renderer, poller, or API path. It forwards only `scope`,
`status`, `origin`, `definition`, `schedule`, `from`, `to`, `run`, and `tab` to the internal same-origin frame.
Typed same-origin messages carry CQRS/audit navigation back to the shell without duplicating data reads. The
internal renderer owns the compact observation workspace bar, manual refresh, admin lookup tools, and immersive
observation. Immersive mode is session-local rather than URL state. A typed same-origin message asks the admin
shell to hide its navigation/header/account chrome; `Escape` exits without changing scope, filters, selected run,
or tab.

The canonical observatory stores run-list and detail-canvas scroll positions in `sessionStorage`, keyed by the
canonical observation route. Polling and same-route refresh preserve both positions; changing scope or server
filters resets the list, while changing run or detail tab resets the detail canvas. Browser reload restores only
the matching route's positions. The admin shell independently stores ordinary module scroll positions per hash
route. Same-route shell renders reuse an existing embedded suite iframe and update only shell chrome, avoiding an
unnecessary document reload; a different module or iframe source still creates the correct new view.

The authoritative page defaults every caller, including an administrator, to the caller's own scope.
`scope=all` is an explicit administrator viewing mode that maps to the backend-only `__all__` sentinel; exact
scope IDs remain exact and are never inferred from role.

Admin Studio exposes conversation and request-trajectory views over the same live `/api/chat` stream.
Each top-level text request creates a trace container keyed by its `clientRequestId`; a later `runId` or
`turnId` is attached as a server fact and never replaces that key. SSE frames incrementally create or
settle the container's ordered Input/Model/Tool operations. Each model response and each tool call is a
separate selectable ledger record rather than another top-level trace. One continuous ledger carries
every container in the conversation: a request is a numbered section boundary inside that ledger, not a
separate navigation rail. The fixed three-lane overview and the ledger project the same operation
identities onto one shared time domain across every container, so selecting a bar or row opens that
operation's own detail in the trajectory's resizable details pane. Overview drag selects a time
interval and dims records outside it, wheel zooms the domain, and right-button drag pans a zoomed
viewport; toolbar controls switch recorded-duration against equal-width projection, fold requests and
fold a model record's tool calls, and search the loaded ledger. Selecting an older container changes
only inspection; Actor controls remain scoped to the current task.

NyxID chat publishes typed `MODEL_CALL_START` / `MODEL_CALL_END` SSE frames for each provider
invocation. `MODEL_CALL_START.availableToolNames` is copied from the server-authorized request catalog
actually supplied to that model round; it is not inferred from tools the model later chose to call.
The Model row shows a compact loaded-tool summary, search includes those names, and the Input detail tab
lists the complete captured set. A round with an empty catalog remains an honest zero-tools invocation.
The durable `toolCatalogCaptured` fact distinguishes that exact empty catalog from a legacy operation
whose model-visible catalog was never recorded; the latter remains `未上报` after reload.

The trajectory survives a reload from two committed sources, and neither infers a record it did not
read. A terminal turn appends its Model/Tool operation ledger together with its chat history turn, so
`GET /api/chat/conversations/{id}` returns those operations beside the stored messages; the in-flight
turn is rebuilt from the conversation actor's current-state step ledger. Recovered containers are keyed
by the server's `turnId` because `clientRequestId` is a browser identity that does not survive the
reload, and a live container already owning that turn is never replaced. Persisted operation content is
a sanitized, size-bounded preview: a truncated preview is labelled as an archived fragment and never
presented as the complete payload. Tool result bodies are deliberately not archived, because untrusted
external text must not be retained by a conversation actor whose state is re-read when rebuilding model
input; a restored Tool record therefore carries identity, status and timing but reports its output as
uncaptured. The Input record has no independent start/completion pair. Missing operation timing remains
unavailable rather than being calculated from browser receipt time. The terminal operation ledger also
persists each model round's `availableToolNames`, so reopening the conversation restores the same loaded
tool set shown by the live SSE path, together with whether that catalog was captured.

Fleet links carry exact `scope + run`, and schedule links carry `schedule` while clearing an unrelated selected
run. The embedded page writes canonical route changes back to the parent hash without reloading its iframe.

Own-scope detail and graph reads use normal endpoints without `scope`; exact-scope reads use normal endpoints
with that scope; all-scope selections and unknown-owner manual run lookup use the administrator endpoints.
Administrator identity by itself does not select the administrator endpoint.

An active approval is recognized only from a typed, incomplete step. Human approval uses
`suspensionType=human_approval`. Tool approval uses `suspensionType=tool_approval` plus complete typed
`executionId`, `toolCallId`, and `approvalRequestId` identity; the UI does not infer these values from text or a
generic bag. Only the run owner (`detail.summary.scopeId == /api/workflow/observatory/me.scopeId`) may submit the
existing scope resume command; an administrator inspecting another scope remains read-only. HTTP `202` means
accepted for dispatch, not committed. The UI waits for a newer committed state version before treating the
approval as resolved. The Artifacts tab is deliberately labelled as a download derived from `finalOutput`; the
current detail contract does not claim a formal artifact collection.

The same owner boundary applies to stopping a running workflow. For an own-scope `running` selection, the page
uses the Activity row's independent `runId` and `actorId` fields to submit the existing
`POST /api/scopes/{scopeId}/runs/{runId}:stop` command with a stable `commandId`; it never parses or aliases one
identity into the other. If an exact deep link is not present in the current Activity window, the page may omit
`actorId` and let the canonical scope endpoint resolve the explicit `runId`. Cross-scope administrator views do
not expose this command. An accepted response remains a pending stop request until polling observes a committed
terminal status; the page must not optimistically rewrite the run as stopped.

Run detail responses expose `diagnostics` assembled from the committed workflow current-state snapshot and the
materialized run-report artifact. Diagnostics are query-time explanations for operators; they are not durable log
entries or deletion tombstones and must not be presented as either. Admin controls accept a run id through an
explicit run-id input. The browser must not infer run identity from actor-id prefixes or delimiters.

The CQRS observatory exposes three platform-admin-only projection-scope reads: the existing scope list,
`GET /api/cqrs/scopes/{scopeActorId}`, and
`GET /api/cqrs/scopes/{scopeActorId}/recent-envelopes?take=20`. All three read the materialized
`ProjectionScopeStatusDocument`; they do not activate a scope, read actor state, replay events, rebuild a view, or
prime a projection. The detail `StateVersion` is the authoritative projection-scope actor version copied into that
current-state read model. Each recent-envelope `stateVersion` is instead the version of its source committed state
event. Both surfaces are eventually consistent and expose the read-model refresh timestamp so operators can judge
freshness honestly.

Projection-scope actor state retains at most the newest 50 committed-envelope metadata records, and the endpoint
returns newest first with a default of 20 and a maximum of 50. Each record contains only `eventId`, `typeUrl`, source
`stateVersion`, and an optional source timestamp. Missing timestamps remain absent/null; neither the actor nor the
projector fabricates them from a processing clock. Payloads, outer-envelope data, projector-local counters, generic
state dumps, `lastError`, and query-time failure aggregation are not part of this contract. The CQRS page loads detail
and recent metadata only after an operator selects a scope, renders an explicit empty state when the materialized
window is empty, and offers no mutation controls.

## 4. Governance

`bash tools/ci/backend_console_static_asset_guard.sh` enforces:

1. all approved page assets exist;
2. configurable assets contain `__BACKEND_CONSOLE_CONFIG__`;
3. page assets do not hardcode Nyx/OIDC host facts;
4. old C# raw-string page carriers are absent;
5. pure static shell endpoint files stay GET-only;
6. owning projects declare the embedded resources;
7. no `wwwroot` or frontend build chain appears in page assets.

`bash tools/ci/workflow_observatory_readonly_guard.sh` keeps the observatory-specific read-query and query-port invariants while checking the embedded asset form instead of the old C# string form.
