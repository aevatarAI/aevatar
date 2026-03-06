## Summary
- Introduce unified `aevatar` CLI command groups with richer subcommands around `config`, `chat`, and especially `app`.
- Add `aevatar app` runtime improvements (health probing, restart flow, API base resolution, embedded/proxy mode handling, and improved playground experience).
- Add Telegram bridge capabilities for workflow callback orchestration, including callback token issuance/ingress endpoints, bridge event contracts, and SDK support.
- Add in-process Telegram user connector (`telegram_user`) and bridge agents (`TelegramBridgeGAgent` / `TelegramUserBridgeGAgent`) to support Telegram group request/reply workflows.
- Add bundled workflow and docs for OpenClaw + Telegram integration (`telegram_openclaw_bridge_chat`) and related connector deployment guidance.

## Why
- Make local developer workflow and demo workflow execution easier with one entrypoint (`aevatar app`) and a better built-in playground.
- Unify CLI operations (configuration, workflow authoring, chat invocation, runtime app hosting) under one tool surface.
- Enable end-to-end Telegram-based bridge interactions without external callback relay services, and make workflow callback integrations explicit/typed in API + SDK.

## Main Changes
- **CLI / app**
  - Add `aevatar app` with `--port`, `--no-browser`, `--api-base`, and `aevatar app restart`.
  - Add app-side bridge proxy routes and playground endpoint enhancements.
  - Add persisted API base URL resolution behavior shared by `chat` and `app`.
- **Bridge / workflow API**
  - Add `/api/bridge/callback-token` and `/api/bridge/callbacks`.
  - Add bridge callback token contracts and HMAC token service.
  - Add bridge actor support (`BridgeGAgent`) and bridge options wiring.
- **Telegram integration**
  - Add `telegram_user` connector and builder in bootstrap connector registration.
  - Add Telegram bridge agents and agent-type alias extension points for `agent_type` workflow parameters.
  - Add Telegram/OpenClaw bridge workflow and update workflow library/display resources.
- **SDK / tests / docs**
  - Extend workflow SDK contracts/client with bridge callback APIs and parsing helpers.
  - Add/expand CLI, bridge, workflow, and host API tests for new paths.
  - Add docs for Telegram user channel setup and OpenClaw connector workflow usage.

## Test Plan
- [x] `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo`
- [x] `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo --filter "Bridge|TelegramBridge|ChatEndpointsInternal"`
- [ ] Manual smoke: `aevatar app` startup + `/api/app/health`
- [ ] Manual smoke: `aevatar app restart --port <port>`
- [ ] Manual smoke: run bundled `telegram_openclaw_bridge_chat` with `telegram_user` connector config

## Notes
- This PR includes command-surface changes and bridge protocol additions; downstream automation/scripts should verify expected CLI behavior and endpoint contracts.
