# Aevatar.Tools.Cli

Unified `aevatar` global tool.

## Install from NuGet

```bash
dotnet tool install --global aevatar
```

Update existing install:

```bash
dotnet tool update --global aevatar
```

## Install from local package (optional)

```bash
# from repo root
dotnet pack tools/Aevatar.Tools.Cli/Aevatar.Tools.Cli.csproj -c Release
dotnet tool install --global --add-source ./tools/Aevatar.Tools.Cli/bin/Release aevatar
```

One-command reinstall:

```bash
bash tools/Aevatar.Tools.Cli/reinstall-tool.sh
```

The reinstall script rebuilds the frontend, packs and reinstalls the global tool, then starts `aevatar app` in the background on port `6688` by default. Set `AEVATAR_REINSTALL_RESTART_APP=0` when you want reinstall without auto-start.

## Frontend workspace

The embedded app frontend lives under `tools/Aevatar.Tools.Cli/Frontend` and uses `npm`.

Install dependencies before running any frontend build or test command:

```bash
cd tools/Aevatar.Tools.Cli/Frontend
npm ci
```

Common frontend commands:

```bash
cd tools/Aevatar.Tools.Cli/Frontend
npm test
npm run build
```

Notes:

- Do not rely on globally installed `tsc` or `vite`; the workspace expects the local binaries from `node_modules/.bin`.
- If you see `tsc: command not found` or `vite: command not found`, run `npm ci` again in `tools/Aevatar.Tools.Cli/Frontend`.
- `bash tools/Aevatar.Tools.Cli/reinstall-tool.sh` already installs frontend dependencies and rebuilds the embedded assets before packing the tool.

## Commands

```bash
# open local config UI
aevatar config ui

# do not auto-open browser
aevatar config ui --no-browser

# custom port
aevatar config ui --port 8080

# ensure config ui is running (probe/start/wait)
aevatar config ui ensure --json
aevatar config ui ensure --port 8080 --json
```

```bash
# machine-readable config paths / doctor
aevatar config paths show --json
aevatar config doctor --json

# secrets.json key-value
aevatar config secrets set LLMProviders:Providers:deepseek:ApiKey --stdin < api_key.txt
aevatar config secrets get LLMProviders:Providers:deepseek:ApiKey --json
aevatar config secrets remove LLMProviders:Providers:deepseek:ApiKey --yes --json

# config.json key-value
aevatar config config-json set Cli:App:ApiBaseUrl http://localhost:5100 --json
aevatar config config-json get Cli:App:ApiBaseUrl --json
```

```bash
# llm instance / default / probe
aevatar config llm instances upsert deepseek-main --provider-type deepseek --model deepseek-chat --api-key-stdin < api_key.txt
aevatar config llm default set deepseek-main --json
aevatar config llm probe test deepseek-main --json

# NyxID gateway-backed provider
# if Cli:App:NyxId:Authority is configured, --endpoint can be omitted
aevatar config config-json set Cli:App:NyxId:Authority https://nyx.example.com --json
aevatar config llm instances upsert nyxid --provider-type nyxid --model claude-sonnet-4-5-20250929 --api-key-stdin < nyx_bearer_token.txt
aevatar config llm default set nyxid --json
aevatar config llm probe test nyxid --json

# workflows YAML
aevatar config workflows put demo.yaml --file ./workflows/demo.yaml --source home --json
aevatar config workflows list --source home --json

# connectors / mcp
aevatar config connectors put web-http --entry-json '{"type":"http","http":{"baseUrl":"https://example.com"}}' --json
aevatar config mcp put local-mcp --entry-json '{"command":"npx","args":["-y","demo-server"]}' --json
```

```bash
# launch embedded workflow playground app
aevatar app

# if app is already running on the port, open existing UI directly
# if port is occupied by another service, command exits with a warning

# run without browser and custom port
aevatar app --no-browser --port 6690

# optional explicit backend base url
aevatar app --url http://localhost:5100

# force restart app on port (kill listener process then relaunch)
aevatar app restart
aevatar app restart --port 6690
```

When `aevatar app` runs in proxy mode, NyxID browser login is enabled by default. The local app uses OIDC Authorization Code + PKCE against `https://nyx-api.chrono-ai.fun` and keeps a local cookie session for proxied API calls.

Optional config overrides:

```bash
# disable NyxID login explicitly
aevatar config config-json set Cli:App:NyxId:Enabled false --json

# switch authority / client id when needed
aevatar config config-json set Cli:App:NyxId:Authority https://nyx-api.chrono-ai.fun --json
aevatar config config-json set Cli:App:NyxId:ClientId 37a93189-2734-406e-bca1-7dbdf25c5a53 --json
aevatar config config-json set Cli:App:NyxId:Scope "openid profile email" --json
```

Relevant config keys:

- `Cli:App:NyxId:Enabled`
- `Cli:App:NyxId:Authority`
- `Cli:App:NyxId:ClientId`
- `Cli:App:NyxId:ClientSecret`
- `Cli:App:NyxId:Scope`
- `Cli:App:NyxId:CallbackPath`

Optional remote connector and role catalog storage:

```bash
# optional overrides when you do not want the built-in Nyx proxy defaults
aevatar config config-json set Cli:App:Connectors:ChronoStorage:UseNyxProxy false --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:BaseUrl https://chrono-storage.example.com --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:NyxProxyBaseUrl https://nyx-api.chrono-ai.fun --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:NyxProxyServiceSlug chrono-storage-service --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:Bucket studio-catalogs --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:Prefix aevatar/connectors/v1 --json
aevatar config config-json set Cli:App:Connectors:ChronoStorage:RolesPrefix aevatar/roles/v1 --json
aevatar config secrets set Cli:App:Connectors:ChronoStorage:StaticBearerToken <token>
```

By default, `aevatar app` now enables chrono-storage catalog backing with:

- `Cli:App:Connectors:ChronoStorage:UseNyxProxy = true`
- `Cli:App:Connectors:ChronoStorage:NyxProxyServiceSlug = chrono-storage-service`
- `Cli:App:Connectors:ChronoStorage:NyxProxyBaseUrl = <empty>` and falls back to `Cli:App:NyxId:Authority`
- `Cli:App:Connectors:ChronoStorage:Bucket = studio-catalogs`
- `Cli:App:Connectors:ChronoStorage:CreateBucketIfMissing = false`
- no required extra storage API key; the app reuses the current NyxID login access token when calling the Nyx proxy

`aevatar app` resolves the current NyxID-backed `scope_id/sub`, derives a stable scoped object key, encrypts the catalog payload locally, and stores it in `chrono-storage` through NyxID's authenticated proxy route (`/api/v1/proxy/s/chrono-storage-service/...`).

- Connector catalogs use `Cli:App:Connectors:ChronoStorage:Prefix`.
- Role catalogs use `Cli:App:Connectors:ChronoStorage:RolesPrefix`.
- Set `UseNyxProxy = false` only when you intentionally want to call a chrono-storage base URL directly.
- If `Cli:App:Connectors:ChronoStorage:MasterKey` is not configured, the app reuses `~/.aevatar/masterkey.bin`; if that file does not exist yet, it is created automatically on first chrono-storage write.
- Connector and role drafts stay local under the studio data directory and are separated per scope.
- Existing local `~/.aevatar/connectors.json` and `~/.aevatar/roles.json` files are not auto-migrated. Use the **Import local** button in the app when you want to copy the local file into `chrono-storage`.

`aevatar app` playground now includes a **Config** button:

- it calls an internal workflow to run `aevatar config ui ensure --no-browser`
- if config UI is already running, it jumps directly
- if not running, it auto-starts config UI and then jumps
- LLM status badge is refreshed continuously to reflect provider updates

Bundled Telegram/OpenClaw bridge demo workflow is available in app library:

- `telegram_openclaw_bridge_chat`
- uses `agent_type: TelegramUserBridgeGAgent` + `/sendMessage` and `/waitReply`
- treats Telegram group itself as the conversation stream (no OpenClaw callback required)
- supports `${telegram.chat_id}` / `${telegram.openclaw_bot_username}` from `WorkflowRuntimeDefaults` in `config.json`
- supports two-phase login: trigger code first, then collect verification code via `human_input`
- `telegram_openclaw_file_operator`
- uses multiple `human_input` checkpoints to capture small human decisions, then asks OpenClaw to write files and return file feedback JSON + file addresses

```bash
# open app UI and auto-send chat prompt
aevatar chat "summarize current workflow status"

# use non-default local app port
aevatar chat "hello" --port 6690

# one-shot override remote workflow API base url
aevatar chat "hello" --url http://localhost:5100
```

```bash
# generate workflow YAML from chat message (prints YAML in terminal)
aevatar chat workflow "build a customer-support triage workflow"

# read message from stdin; save directly without confirmation prompt
echo "design an incident response workflow with human approval" | aevatar chat workflow --stdin --yes

# custom API base and output filename
aevatar chat workflow "generate a rollout plan workflow with approval gate" --url http://localhost:5100 --filename rollout_plan
```

```bash
# open the Phase A browser voice UI for an actor
aevatar voice --agent workflow-agent-123

# use a non-default local app port and backend url
aevatar voice --agent workflow-agent-123 --port 6690 --url http://localhost:5100

# optional provider / voice hints for the browser UI
aevatar voice --agent workflow-agent-123 --provider minicpm --voice alloy
```

`aevatar voice` ensures the embedded web UI is running, updates its backend target, and opens:

```text
http://localhost:<port>/voice?agent=<actorId>
```

Optional config keys for the voice page:

- `Cli:Voice:Provider`
- `Cli:Voice:Voice`
- `Cli:Voice:SampleRateHz`

`aevatar chat workflow` writes files to `AEVATAR_HOME/workflows` (`~/.aevatar/workflows` by default).  
Without `--yes`, it prompts for confirmation before saving.

```bash
# persist chat/app remote workflow API base url
aevatar chat config set-url http://localhost:5100

# read persisted url
aevatar chat config get-url

# clear persisted url
aevatar chat config clear-url
```

URL precedence for `aevatar chat`, `aevatar voice`, and `aevatar app`:

1. command line override (`chat --url` / `voice --url` / `app --url`)
2. persisted config key `Cli:App:ApiBaseUrl` in `~/.aevatar/config.json`
3. local embedded host URL (`http://localhost:<port>`)

## Publish to NuGet (GitHub Actions)

Workflow file: `.github/workflows/publish-aevatar-cli.yml`.

Before first publish, configure repository secret:

- `NUGET_API_KEY`: API key generated from [nuget.org](https://www.nuget.org/)

Version behavior:

- manual run (`workflow_dispatch`) with no input version:
  - automatically queries NuGet for package `aevatar`
  - uses fixed base `0.0`
  - publishes next patch version (`0.0.1`, `0.0.2`, `0.0.3`, ...)
- manual run with input `version`:
  - publishes exactly that version (`x.y.z`)
- git tag trigger:
  - push tag `aevatar-vx.y.z` to publish exact version from tag
  - example: `git tag aevatar-v0.0.5 && git push origin aevatar-v0.0.5`

## Compatibility

Legacy `aevatar-config` remains available as a compatibility shim and prints a migration hint to:

```bash
aevatar config ui
```
