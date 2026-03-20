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

`pnpm dev` reads proxy targets from `.env.local`. If you want `pnpm dev:stack` to use custom ports from the same file, export it into your shell first:

```bash
cd apps/aevatar-console-web
set -a
source .env.local
set +a
```

If you change backend ports for `dev:stack`, also keep `AEVATAR_API_TARGET`, `AEVATAR_CONFIGURATION_API_TARGET`, and `AEVATAR_STUDIO_API_TARGET` aligned with those ports.

`pnpm dev:stack` also injects a default Studio app scope of `aevatar` through `Cli__App__ScopeId`, and keeps Studio NyxID login enabled. Chrono-storage backed connector and role catalogs require both the scope and a valid Studio NyxID session. Override the scope with `AEVATAR_CONSOLE_SCOPE_ID` if you need a different scope, or set `AEVATAR_CONSOLE_STUDIO_NYXID_ENABLED=false` only when you intentionally want to disable protected Studio APIs.

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
pnpm dev:stack
pnpm dev:stack:status
pnpm dev:stack:stop
pnpm build
pnpm test
pnpm tsc
```

## Local stack

`aevatar-console-web` depends on three local backend services during development:

- `Workflow Host API` on `http://127.0.0.1:5080`
- `Configuration API` on `http://127.0.0.1:6688`
- `Studio sidecar` on `http://127.0.0.1:6690`

Use the bundled stack script to start all required services from one place:

```bash
cd apps/aevatar-console-web
pnpm dev:stack
```

The script will:

- start `src/workflow/Aevatar.Workflow.Host.Api`
- start `tools/Aevatar.Tools.Config`
- start `tools/Aevatar.Tools.Cli -- app --api-base <Workflow Host API>` with `Cli__App__ScopeId=aevatar`
- keep Studio NyxID login enabled so remote chrono-storage catalog access can reuse the app session
- start `pnpm dev`
- write logs to `apps/aevatar-console-web/.temp/dev-stack/`

Current proxy split during local development:

- `/api/chat`, `/api/workflows/*`, `/api/actors/*`, `/api/runs/*`, `/api/primitives`, `/api/capabilities` -> `Workflow Host API`
- `/api/app/*`, `/api/auth/*`, `/api/workspace/*`, `/api/editor/*`, `/api/executions/*`, `/api/roles/*`, `/api/connectors/*`, `/api/settings/*` -> `Studio sidecar`
- `/api/configuration/*` -> `Configuration API`

Useful commands:

```bash
cd apps/aevatar-console-web
pnpm dev:stack:status
pnpm dev:stack:restart
pnpm dev:stack:stop
```

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
