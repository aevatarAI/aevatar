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

If you change backend ports, also keep `AEVATAR_API_TARGET`, `AEVATAR_CONFIGURATION_API_TARGET`, and `AEVATAR_STUDIO_API_TARGET` aligned with those ports.

When starting the Studio sidecar manually, set `Cli__App__ScopeId=aevatar` and keep `Cli__App__NyxId__Enabled=true` unless you intentionally want to disable protected Studio APIs. Chrono-storage backed connector and role catalogs require both the scope and a valid Studio NyxID session.

For NyxID login, also set these values in `.env.local`:

```bash
NYXID_BASE_URL=http://127.0.0.1:3001
NYXID_CLIENT_ID=your-public-client-id
NYXID_REDIRECT_URI=http://127.0.0.1:5173/auth/callback
NYXID_SCOPE="openid profile email"
# Optional when deploying under a sub-path such as /console/
AEVATAR_CONSOLE_PUBLIC_PATH=/
```

`NYXID_BASE_URL` and `NYXID_CLIENT_ID` are required. The console no longer ships a baked-in NyxID tenant or client id.
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

`aevatar-console-web` depends on three local backend services during development:

- `Workflow Host API` on `http://127.0.0.1:5080`
- `Configuration API` on `http://127.0.0.1:6688`
- `Studio sidecar` on `http://127.0.0.1:6690`

Start the required services in separate terminals:

```bash
env ASPNETCORE_URLS=http://127.0.0.1:5080 \
  dotnet run --project src/workflow/Aevatar.Workflow.Host.Api

dotnet run --project tools/Aevatar.Tools.Config -- --port 6688 --no-browser

env Cli__App__NyxId__Enabled=true Cli__App__ScopeId=aevatar \
  dotnet run --project tools/Aevatar.Tools.Cli -- app --no-browser --port 6690 --api-base http://127.0.0.1:5080

cd apps/aevatar-console-web
AEVATAR_API_TARGET=http://127.0.0.1:5080 \
AEVATAR_CONFIGURATION_API_TARGET=http://127.0.0.1:6688 \
AEVATAR_STUDIO_API_TARGET=http://127.0.0.1:6690 \
pnpm dev
```

Current proxy split during local development:

- `/api/chat`, `/api/workflows/*`, `/api/actors/*`, `/api/runs/*`, `/api/primitives`, `/api/capabilities` -> `Workflow Host API`
- `/api/app/*`, `/api/auth/*`, `/api/workspace/*`, `/api/editor/*`, `/api/executions/*`, `/api/roles/*`, `/api/connectors/*`, `/api/settings/*` -> `Studio sidecar`
- `/api/configuration/*` -> `Configuration API`

## Current scope

- `Overview`
- `Studio`
- `Primitives`
- `Runs`
- `Actors`
- `Workflows`
- `Observability`
- `Settings`

The shell currently combines direct workflow host APIs with the Studio sidecar APIs and keeps the default Ant Design Pro layout and theme.
