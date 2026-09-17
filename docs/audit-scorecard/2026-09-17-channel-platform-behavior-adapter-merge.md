# Channel platform behavior adapter merge verification

## Merge inputs and approved contract

- Target: `feat/2026-09-16_channel-platform-behavior-adapter`, parent `a1291b8d533a8cfeb58210d7e8c6596ba0afb417`.
- Source: local `feature/integrate`, parent `c72a8165913d3cfab719ac0b9856c762d7b55f69`. No fetch, push, or production operation was performed.
- Resolved 13 content conflicts and one modify/delete conflict. The modified authorization-planner tests were retained.
- Preserve the deployed source registration request and response fields, including registration/command/correlation IDs, detail/update APIs, service selection and skill configuration.
- Retire `register_channel_via_nyx`; the tool returns `retired_action` and provides list/delete only.
- Infer platform from verified, caller-authorized NyxID Bot detail. Resolve webhook base URL from configured options or the request origin.
- Keep registration-owned routes: reject a caller-specified existing route or an active foreign default route before writes. Do not reuse, update or restore foreign routes.
- Keep one adoption service, strict identity/owner validation, exact selected-service grants, typed Agent Key updates, honest cleanup results and uncertain-outcome protections.
- Delete the obsolete registration facade, duplicate planner/runtime-config types, and unreachable route takeover/restore code.

## Verification on final source

All commands ran in the isolated target worktree. Documentation-only changes followed the final source build and tests.

| Command or suite | Result |
|---|---|
| `dotnet build aevatar.slnx --nologo --no-restore` | Passed; 478 warnings, 0 errors |
| `Aevatar.GAgents.ChannelRuntime.Tests` | 2,565 passed |
| `Aevatar.GAgents.Channel.Protocol.Tests` | 163 passed |
| `Aevatar.Foundation.Abstractions.Tests` | 96 passed |
| `Aevatar.GAgents.Platform.Lark.Tests` | 48 passed |
| `Aevatar.GAgents.Platform.Telegram.Tests` | 25 passed |
| `Aevatar.AI.Tests`, filter `FullyQualifiedName~NyxIdRelay\|FullyQualifiedName~NyxRelay` | 55 passed |
| `Aevatar.GAgentService.Tests`, filter `FullyQualifiedName~ServiceDeploymentManagerGAgentTests` | 71 passed |
| `bash tools/ci/test_stability_guards.sh` | Passed |
| `bash tools/ci/channel_platform_behavior_guard.sh` | Passed |
| `bash tools/ci/workflow_binding_boundary_guard.sh` | Passed |
| `bash tools/ci/query_projection_priming_guard.sh` | Passed |
| `bash tools/ci/projection_state_version_guard.sh` | Passed |
| `bash tools/ci/projection_state_mirror_current_state_guard.sh` | Passed |
| `bash tools/docs/lint.sh` | Passed |

The seven test invocations used `dotnet test <project> --nologo --no-build --no-restore`, with filters only where listed. Total: 3,023 passed, zero failed, zero skipped. The full solution test suite and live production validation were not run. Build warnings were not classified or repaired outside the merge scope.

The first focused run exposed three source test calls that omitted the existing list endpoint's nullable `scope` argument. Adding explicit `null` at those call sites restored the intended caller-scope checks without changing the production endpoint. The final full ChannelRuntime invocation includes those tests.

Independent static reviews checked the approved API contract first and code quality second. They found no remaining blockers after obsolete route orchestration was removed. Tests cover the exact deployed registration response field set, platform inference, configured/origin webhook URLs, retired tool behavior, foreign-route rejection, strict Bot detail validation, typed Key updates and cleanup retry/uncertainty.

## Existing architecture-gate failure

`bash tools/ci/architecture_guards.sh` did not pass. It stopped at `nyxid_conformance_guard.sh` with:

```text
Aevatar HEAD source differs from declared revision: src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs
```

The checked-in conformance source pin declares revision `78e2490bc1ad548ed72f75061d8927f8cf599ac1`. For that file:

| State | SHA-256 |
|---|---|
| Declared revision and source merge parent | `a6faf5a532b43dd1e1cf56cfdf5a05f473d755073e84d86ea459e4acec7828bd` |
| Target parent before this merge and final working file | `030451d96b83849a3dbd0f2c8159f5c4dd72ad79629be4d2183cd9d9fa88d98e` |

This mismatch already exists in the target parent. The merge does not modify that Host file or the conformance source declaration. The gate compares committed HEAD content, so editing unrelated merge files cannot resolve it. Refreshing that separate declaration is recorded for follow-up and was not bundled into this merge. Later checks in the aggregate architecture script were not reached; the relevant standalone checks listed above were run separately.

## Preservation

The three pre-existing local design/plan/verification documents from 2026-09-16 were not included in the merge changes. Their SHA-256 values remain identical to the pre-merge backup. No frontend files, external repositories or production resources were changed.
