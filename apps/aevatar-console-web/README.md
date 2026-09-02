# Aevatar Console Web

`aevatar-console-web` is the Ant Design Pro based admin shell for Aevatar.

## Stack

- `React 19`
- `@umijs/max`
- `antd`
- `@ant-design/pro-components`
- `pnpm`

## Setup

Run all frontend commands from `apps/aevatar-console-web`:

```bash
cd apps/aevatar-console-web
cp .env.example .env.local
pnpm install
```

`max` is provided by the local `@umijs/max` dependency in this workspace. Do not assume a global `max` CLI is installed.
Use `pnpm dev`, `pnpm build`, `pnpm preview`, or `pnpm exec max <command>` from `apps/aevatar-console-web`.
If you see `max: command not found`, install dependencies first with `pnpm install` in this directory.

`pnpm dev` reads proxy targets from `.env.local`, and `./boot.sh` loads the
same file before applying command-line overrides. If you also want your shell
to reuse the same values for manually starting backend processes, export the
file first:

```bash
cd apps/aevatar-console-web
set -a
source .env.local
set +a
```

If you change backend ports, also keep `AEVATAR_API_TARGET` and
`AEVATAR_STUDIO_API_TARGET` aligned with those ports. To point the whole local
console at the hosted backend, set both targets to the hosted API URL in
`.env.local` and set `AEVATAR_PROXY_PRESERVE_AUTH_HOST=false` so `/api/auth/*`
uses the hosted backend Host header.

For NyxID login, the console reads the authority, public OAuth client id, scope,
and callback URI from the frontend build environment. It finalizes callbacks
through `/api/auth/nyxid/finalize`, so keep `/api/auth/*` proxied to the Studio
backend. Configure all four browser OAuth values before building:

```bash
NYXID_BASE_URL=https://nyx.chrono-ai.fun
NYXID_CLIENT_ID=replace-with-public-client-id
NYXID_SCOPE="openid profile email offline_access urn:nyxid:scope:broker_binding proxy"
NYXID_REDIRECT_URI=http://127.0.0.1:5173/auth/callback
ORNN_BASE_URL=https://ornn.chrono-ai.fun
# Optional when deploying under a sub-path such as /console/
AEVATAR_CONSOLE_PUBLIC_PATH=/
```

`NYXID_BASE_URL`, `NYXID_CLIENT_ID`, and `NYXID_SCOPE` are injected into the
browser bundle at build time and are the single configuration source for
authorization, PKCE pending state, and token refresh. Keep them aligned with
the OAuth client configured for backend token finalization.
`NYXID_REDIRECT_URI` must exactly match the Studio login callback registered in
NyxID when you override it locally.
Default service preselection is owned by the NyxID OAuth Client
`default_service_catalog_slugs`; the browser does not send OAuth `resource` parameters.
`ORNN_BASE_URL` controls the Ornn skills endpoint used by Studio Settings. If you omit it, the frontend falls back to the public Ornn instance.
If you change `.env.local`, restart `pnpm dev` so Umi reloads the injected env values.

## Available scripts

```bash
cd apps/aevatar-console-web
pnpm dev
pnpm build
pnpm test
pnpm tsc
```

## Local stack

`aevatar-console-web` depends on the local Mainnet Host API during development.
Mainnet composes the runtime APIs and `Aevatar.Studio.Hosting`, including the
Team endpoints.

- `Mainnet Host API` on `http://127.0.0.1:5080`

Start the required services in separate terminals:

```bash
env ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Aevatar.Mainnet.Host.Api

cd apps/aevatar-console-web
AEVATAR_API_TARGET=http://127.0.0.1:5080 \
AEVATAR_STUDIO_API_TARGET=http://127.0.0.1:5080 \
ORNN_BASE_URL=https://ornn.chrono-ai.fun \
pnpm dev
```

Current proxy split during local development:

- `/api/chat`, `/api/workflows/*`, `/api/actors/*`, `/api/runs/*`, `/api/primitives`, `/api/capabilities`, most `/api/scopes/*` runtime routes -> `Mainnet Host API`
- `/api/app/*`, `/api/auth/*`, `/api/workspace/*`, `/api/editor/*`, `/api/executions/*`, `/api/roles/*`, `/api/connectors/*`, `/api/scopes/{scopeId}/teams*` -> `Studio Hosting API target`

## Current scope

- `Overview`
- `Studio`
- `Primitives`
- `Runs`
- `Actors`
- `Workflows`
- `Observability`
- `Settings`

If Studio shows `Failed to load Studio workflow` with an RFC 9110 `404 Not Found` payload, check that `AEVATAR_API_TARGET` points to `Aevatar.Mainnet.Host.Api` rather than `Aevatar.Workflow.Host.Api`; scope workflow detail requests are served by mainnet.
