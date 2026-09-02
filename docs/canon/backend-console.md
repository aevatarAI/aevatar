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
| `/status` | `src/Aevatar.Mainnet.Host.Api/Status/status.html` | Mainnet Host |
| `/cqrs` | `src/Aevatar.Mainnet.Host.Api/Cqrs/cqrs-observatory.html` | Mainnet Host |
| `/voice` | `src/Aevatar.Mainnet.Host.Api/Voice/voice-console.html` | Mainnet Host |
| `/workflow/skills` | `src/Aevatar.Mainnet.Host.Api/Skills/workflow-skills.html` | Mainnet Host |
| `/workflow/observatory` | `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html` | Workflow Infrastructure |
| `/workflow/studio`, `/schedules` | `src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-studio.html` | Workflow Infrastructure |
| `/channels` | `agents/channels/Aevatar.GAgents.Channel.NyxIdRelay/channels.html` | NyxIdRelay channel package |

## 2. Host Fact Injection

Nyx/OIDC deployment facts are host configuration, not page source:

| Config key | Meaning |
|---|---|
| `Aevatar:BackendConsole:OidcAuthority` | Browser OIDC authority; falls back to existing Nyx/Auth authority config when empty. |
| `Aevatar:BackendConsole:OidcClientId` | Canonical public OAuth client id used by embedded-console PKCE, Studio login/finalization, broker token-exchange, and binding revoke. Bootstrap materializes this value into the OAuth Client Actor; no runtime path may fall back to an older projected client id. |
| `Aevatar:BackendConsole:OidcScope` | Browser OIDC scope. |
| `Aevatar:BackendConsole:OidcResources` | Additional RFC 8707 resource indicators. The host always includes `{NyxApiBaseUrl}/api/v1/proxy/s/aevatar` and the Ornn proxy resource resolved from `Aevatar:Ornn:NyxIdSlug` (default `ornn-api`). |
| `Aevatar:BackendConsole:NyxApiBaseUrl` | Canonical NyxID API/resource-server base. It falls back only to `Aevatar:NyxId:ApiBaseUrl`, never to the browser OIDC authority. |
| `Aevatar:BackendConsole:StorageKey` | Shared browser localStorage/sessionStorage prefix. |
| `Aevatar:BackendConsole:DefaultReturnPath` | Safe default redirect path after `/auto/callback`. |

Each configurable HTML asset contains `__BACKEND_CONSOLE_CONFIG__`. The serving helper replaces that placeholder with JSON rendered from `BackendConsoleOptions`. The six `HOST_BACKEND_CONSOLE_*` environment variables are optional overrides for host deployment, but `.refactor-loop/host.env` is not a production configuration source.

The OIDC client id and resource indicators are public browser values, not secrets. Every configurable console page appends each injected resource to both `/oauth/authorize` and the authorization-code exchange at `/oauth/token`; the shared `/auto/callback` follows the same contract. The OIDC authority owns browser authorization, while `NyxApiBaseUrl` owns RFC 8707 resource identity and NyxID REST/admin routing; these hosts may differ and must not be substituted for each other. Secrets still belong in the existing host secret/config mechanisms and must not be injected into page assets.

Studio's `/api/auth/nyxid/config` still returns an actor-backed snapshot so authority, callback/scope contract, HMAC state, and broker observation remain cluster-coherent. Its `clientId` must match `Aevatar:BackendConsole:OidcClientId`; while Actor projection still carries another id, the provider fails closed instead of combining new configuration with stale runtime facts. Startup and the admin reconcile endpoint materialize the configured id into Actor state, but neither DCR output nor an API request body is an alternative client-id authority.

## 3. Endpoint Boundary

Backend console page endpoints are static shells. They may be anonymous because the browser page performs OIDC PKCE login and all data endpoints enforce authorization server-side.

Static shell endpoint files must not introduce mutating data APIs. Data surfaces stay in their existing API endpoint files, with their existing authorization and audit rules. Adding a new console page means adding the asset, declaring it as an embedded resource, mapping a GET shell route, and extending the guard inventory.

Workflow Observatory data endpoints are read-only. Normal run detail reads remain scope-bound under
`GET /api/workflow/observatory/runs/{runId}` and `GET /api/workflow/observatory/runs/{runId}/graph`.
Aevatar admins resolved by `IPlatformAdminAuthorizer` may use
`GET /api/workflow/observatory/admin/runs/{runId}` and
`GET /api/workflow/observatory/admin/runs/{runId}/graph` to resolve a known run id across scopes. These admin
drilldown endpoints still read only workflow current-state/readmodel artifacts; they do not replay events, prime
projections, or dispatch actor commands.

`/workflow/observatory` is the only Workflow Observatory renderer and data client. `/admin#/observatory` is a
shell route that embeds that page in a same-origin iframe; it does not retain a second run cache, renderer,
poller, or API path. The shell forwards only `scope`, `status`, `origin`, `definition`, `schedule`, `from`, `to`,
`run`, and `tab`, preserving exact values so standalone and embedded deep links express the same observation
intent. Typed same-origin messages carry CQRS/audit navigation back to the shell without duplicating data reads.
The canonical page also owns the compact observation workspace bar, manual refresh, admin lookup tools, and
immersive observation. Immersive mode is session-local rather than URL state. When embedded, a typed same-origin
message asks the admin shell to hide its navigation/header/account chrome; `Escape` exits without changing scope,
filters, selected run, or tab.

The canonical observatory stores run-list and detail-canvas scroll positions in `sessionStorage`, keyed by the
canonical observation route. Polling and same-route refresh preserve both positions; changing scope or server
filters resets the list, while changing run or detail tab resets the detail canvas. Browser reload restores only
the matching route's positions. The admin shell independently stores ordinary module scroll positions per hash
route. Same-route shell renders reuse an existing embedded suite iframe and update only shell chrome, avoiding an
unnecessary document reload; a different module or iframe source still creates the correct new view.

The authoritative page defaults every caller, including an administrator, to the caller's own scope.
`scope=all` is an explicit administrator viewing mode that maps to the backend-only `__all__` sentinel; exact
scope IDs remain exact and are never inferred from role.

Fleet links carry exact `scope + run`, and schedule links carry `schedule` while clearing an unrelated selected
run. The embedded page writes canonical route changes back to the parent hash without reloading its iframe.

Own-scope detail and graph reads use normal endpoints without `scope`; exact-scope reads use normal endpoints
with that scope; all-scope selections and unknown-owner manual run lookup use the administrator endpoints.
Administrator identity by itself does not select the administrator endpoint.

An active human approval is recognized only from a typed step with `suspensionType=human_approval` and no
completion timestamp. Only the run owner (`detail.summary.scopeId == /api/workflow/observatory/me.scopeId`) may
submit the existing scope resume command; an administrator inspecting another scope remains read-only. HTTP
`202` means accepted for dispatch, not committed. The UI waits for a newer committed state version before
treating the approval as resolved. The Artifacts tab is deliberately labelled as a download derived from
`finalOutput`; the current detail contract does not claim a formal artifact collection.

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

`bash tools/ci/workflow_observatory_readonly_guard.sh` keeps the observatory-specific read-only and query-port invariants while checking the embedded asset form instead of the old C# string form.
