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
| `Aevatar:BackendConsole:OidcClientId` | Public OIDC client id used by browser PKCE. |
| `Aevatar:BackendConsole:OidcScope` | Browser OIDC scope. |
| `Aevatar:BackendConsole:OidcResources` | Additional RFC 8707 resource indicators. The host always includes `{NyxApiBaseUrl}/api/v1/proxy/s/aevatar`. |
| `Aevatar:BackendConsole:NyxApiBaseUrl` | Canonical NyxID API/resource-server base. It falls back only to `Aevatar:NyxId:ApiBaseUrl`, never to the browser OIDC authority. |
| `Aevatar:BackendConsole:StorageKey` | Shared browser localStorage/sessionStorage prefix. |
| `Aevatar:BackendConsole:DefaultReturnPath` | Safe default redirect path after `/auto/callback`. |

Each configurable HTML asset contains `__BACKEND_CONSOLE_CONFIG__`. The serving helper replaces that placeholder with JSON rendered from `BackendConsoleOptions`. The six `HOST_BACKEND_CONSOLE_*` environment variables are optional overrides for host deployment, but `.refactor-loop/host.env` is not a production configuration source.

The OIDC client id and resource indicators are public browser values, not secrets. Every configurable console page appends each injected resource to both `/oauth/authorize` and the authorization-code exchange at `/oauth/token`; the shared `/auto/callback` follows the same contract. The OIDC authority owns browser authorization, while `NyxApiBaseUrl` owns RFC 8707 resource identity and NyxID REST/admin routing; these hosts may differ and must not be substituted for each other. Secrets still belong in the existing host secret/config mechanisms and must not be injected into page assets.

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

Run detail responses expose `diagnostics` assembled from the committed workflow current-state snapshot and the
materialized run-report artifact. Diagnostics are query-time explanations for operators; they are not durable log
entries or deletion tombstones and must not be presented as either. Admin controls accept a run id through an
explicit run-id input. The browser must not infer run identity from actor-id prefixes or delimiters.

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
