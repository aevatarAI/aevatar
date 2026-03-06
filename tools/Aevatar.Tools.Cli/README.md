# Aevatar.Tools.Cli

Unified `aevatar` global tool.

## Install (optional, as dotnet tool)

```bash
# from repo root
dotnet pack tools/Aevatar.Tools.Cli/Aevatar.Tools.Cli.csproj -c Release
dotnet tool install --global --add-source ./tools/Aevatar.Tools.Cli/bin/Release aevatar
```

One-command reinstall:

```bash
bash tools/Aevatar.Tools.Cli/reinstall-tool.sh
```

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
aevatar config config-json set Cli:App:ApiBaseUrl http://localhost:5000 --json
aevatar config config-json get Cli:App:ApiBaseUrl --json
```

```bash
# llm instance / default / probe
aevatar config llm instances upsert deepseek-main --provider-type deepseek --model deepseek-chat --api-key-stdin < api_key.txt
aevatar config llm default set deepseek-main --json
aevatar config llm probe test deepseek-main --json

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

# optional explicit SDK base url
aevatar app --api-base http://localhost:5000

# force restart app on port (kill listener process then relaunch)
aevatar app restart
aevatar app restart --port 6690
```

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

```bash
# open app UI and auto-send chat prompt
aevatar chat "summarize current workflow status"

# use non-default local app port
aevatar chat "hello" --port 6690

# one-shot override remote workflow API base url
aevatar chat "hello" --url http://localhost:5000
```

```bash
# generate workflow YAML from chat message (prints YAML in terminal)
aevatar chat workflow "build a customer-support triage workflow"

# read message from stdin; save directly without confirmation prompt
echo "design an incident response workflow with human approval" | aevatar chat workflow --stdin --yes

# custom API base and output filename
aevatar chat workflow "generate a rollout plan workflow with approval gate" --url http://localhost:5000 --filename rollout_plan
```

`aevatar chat workflow` writes files to `AEVATAR_HOME/workflows` (`~/.aevatar/workflows` by default).  
Without `--yes`, it prompts for confirmation before saving.

```bash
# persist chat/app remote workflow API base url
aevatar chat config set-url http://localhost:5000

# read persisted url
aevatar chat config get-url

# clear persisted url
aevatar chat config clear-url
```

URL precedence for both `aevatar chat` and `aevatar app`:

1. command line override (`chat --url` / `app --api-base`)
2. persisted config key `Cli:App:ApiBaseUrl` in `~/.aevatar/config.json`
3. local embedded host URL (`http://localhost:<port>`)

## Compatibility

Legacy `aevatar-config` remains available as a compatibility shim and prints a migration hint to:

```bash
aevatar config ui
```
