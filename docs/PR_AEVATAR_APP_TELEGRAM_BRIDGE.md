# PR Title
feat(cli): add `aevatar app` command suite and Telegram bridge integration

## Summary
- Introduce a unified `aevatar` CLI entry with subcommands focused on app hosting and workflow interaction.
- Add `aevatar app` as the primary local playground host, including embedded/proxy runtime mode selection, health endpoint, and static playground experience.
- Add Telegram bridge capability (`TelegramUserConnector` + bridge gAgents) so workflows can send/wait through Telegram group streams.
- Remove callback-token/callback ingress vertical slice and keep bridge behavior strictly group-stream polling.
- Add/refresh docs, sample workflows, and targeted tests for CLI app + bridge scenarios.

## Why
- Unify local developer workflow under one command surface instead of scattered startup paths.
- Make `aevatar app` the default, low-friction way to run playground + workflow capability locally.
- Provide a practical Telegram channel bridge for real-world send/wait-signal workflows without external callback relay.

## What Changed
### CLI and `aevatar app`
- Added tool project and root command wiring for `aevatar`.
- Added `app`, `chat`, `config` command groups; `app` focuses on run/startup lifecycle and local UX.
- Added app host endpoints and aliases for playground/bridge interactions.
- Updated playground assets and CLI docs.

### Telegram bridge
- Added `TelegramUserConnector` and builder registration in bootstrap/config.
- Added bridge extensions with `TelegramBridgeGAgent` and `TelegramUserBridgeGAgent`.
- Added Telegram/OpenClaw workflow example for send/wait-reply bridge flow.

### Workflow/Bridge plumbing
- Removed bridge callback token contracts, callback ingress endpoints, and corresponding SDK/AGUI custom-event surfaces.
- Decoupled Telegram bridge extension from workflow core bridge base implementation.
- Removed app-only `aevatar_call` workflow primitive from workflow core.
- Added workflow core wiring needed by app/bridge runtime path (including secure-input related runtime path support).

## Impact Scope
- CLI host and command surface: `tools/Aevatar.Tools.Cli/*`
- Bootstrap/config connector registration: `src/Aevatar.Bootstrap/*`, `src/Aevatar.Configuration/*`
- Workflow bridge/core/infrastructure wiring:
  - `src/workflow/Aevatar.Workflow.Core/*`
  - `src/workflow/Aevatar.Workflow.Infrastructure/*`
  - `src/workflow/extensions/Aevatar.Workflow.Extensions.Bridge/*`
  - `src/workflow/Aevatar.Workflow.Sdk/*`
- Docs and workflow examples:
  - `docs/AEVATAR_APP_GUIDE_CN.md`
  - `docs/AEVATAR_TELEGRAM_USER_CHANNEL_CN.md`
  - `tools/Aevatar.Tools.Cli/workflows/telegram_openclaw_bridge_chat.yaml`
  - `workflows/openclaw_group_reply*.yaml`

## Test Plan
- [x] `dotnet test test/Aevatar.Tools.Cli.Tests/Aevatar.Tools.Cli.Tests.csproj --nologo`
- [x] `dotnet test test/Aevatar.Workflow.Host.Api.Tests/Aevatar.Workflow.Host.Api.Tests.csproj --nologo`
- [ ] `dotnet restore aevatar.slnx --nologo`
- [ ] `dotnet build aevatar.slnx --nologo`
- [ ] `dotnet test aevatar.slnx --nologo`
- [ ] `bash tools/ci/architecture_guards.sh`
- [ ] `bash tools/ci/test_stability_guards.sh`
- [ ] Manual E2E: run `aevatar app`, execute telegram bridge workflow, verify send -> wait -> receive path.

## Known Risks / Follow-ups
- `waitReply` without a strong correlation marker may still match unrelated chat traffic in busy groups; recommend enforcing marker/user constraints in workflow defaults.
- Sensitive steps (e.g. Telegram 2FA password) should use `secure_input` end-to-end to avoid accidental exposure in logs/projections.