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

`pnpm dev` reads proxy targets from `.env.local`. If you also want your shell to reuse the same values for manually starting backend processes, export the file first:

```bash
cd apps/aevatar-console-web
set -a
source .env.local
set +a
```

If you change backend ports, also keep `AEVATAR_API_TARGET` and `AEVATAR_STUDIO_API_TARGET` aligned with those ports.

When starting the Studio sidecar manually, set `Cli__App__ScopeId=aevatar` and keep `Cli__App__NyxId__Enabled=true` unless you intentionally want to disable protected Studio APIs. Chrono-storage backed connector and role catalogs require both the scope and a valid Studio NyxID session.

For NyxID login, also set these values in `.env.local`:

```bash
NYXID_BASE_URL=http://127.0.0.1:3001
NYXID_CLIENT_ID=your-public-client-id
NYXID_REDIRECT_URI=http://127.0.0.1:5173/auth/callback
NYXID_SCOPE="openid profile email"
```

`NYXID_REDIRECT_URI` must exactly match the public client registration in NyxID.
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

`aevatar-console-web` depends on two local backend services during development:

- `Mainnet Host API` on `http://127.0.0.1:5080`
- `Studio sidecar` on `http://127.0.0.1:6690`

The dedicated local configuration tool is still available when you need to edit
secrets, workflows, providers, MCP servers, or raw config files, but it is no
longer proxied through the console:

- `aevatar config ui --no-browser`

Start the required services in separate terminals:

```bash
env ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/Aevatar.Mainnet.Host.Api

env Cli__App__NyxId__Enabled=true Cli__App__ScopeId=aevatar \
  dotnet run --project tools/Aevatar.Tools.Cli -- app --no-browser --port 6690 --api-base http://127.0.0.1:5080

cd apps/aevatar-console-web
AEVATAR_API_TARGET=http://127.0.0.1:5080 \
AEVATAR_STUDIO_API_TARGET=http://127.0.0.1:6690 \
pnpm dev
```

Current proxy split during local development:

- `/api/chat`, `/api/workflows/*`, `/api/actors/*`, `/api/runs/*`, `/api/primitives`, `/api/capabilities`, `/api/scopes/*` -> `Mainnet Host API`
- `/api/app/*`, `/api/auth/*`, `/api/workspace/*`, `/api/editor/*`, `/api/executions/*`, `/api/roles/*`, `/api/connectors/*`, `/api/settings/*` -> `Studio sidecar`

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
