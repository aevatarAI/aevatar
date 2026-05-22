# Implement cluster-002-command-path-projection-activation

## Modified Files

- `agents/Aevatar.GAgents.Scheduled/SkillRunnerCommandPort.cs` — 82 lines
- `agents/Aevatar.GAgents.Scheduled/UserAgentCatalogCommandPort.cs` — 95 lines
- `agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs` — 153 lines
- `test/Aevatar.GAgents.ChannelRuntime.Tests/SkillRunnerCommandPortTests.cs` — 228 lines
- `test/Aevatar.GAgents.ChannelRuntime.Tests/UserAgentCatalogCommandPortTests.cs` — 217 lines
- `test/Aevatar.AI.Tests/StreamingProxyRoomCommandServiceTests.cs` — 384 lines
- `tools/ci/query_projection_priming_guard.sh` — 53 lines

## Summary

- Removed scheduled runner command-port projection activation before initialize/trigger/disable/enable dispatch.
- Removed catalog command-port projection activation before upsert/tombstone dispatch.
- Removed streaming room subscription projection lease setup from room creation admission and rollback semantics.
- Updated focused tests to assert command acceptance is based on actor lifecycle/dispatch/registry semantics, not projection activation.
- Extended the query projection priming guard to block the three command-path regression files from reintroducing projection activation.

## Tests

- `dotnet build aevatar.slnx --nologo` — passed, existing warnings only.
- `dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo` — passed, 819 tests.
- `dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo` — passed, 569 tests.
- `bash tools/ci/test_stability_guards.sh` — passed.
- `bash tools/ci/query_projection_priming_guard.sh` — passed.
- `bash tools/ci/architecture_guards.sh` — passed.

## Deviations

- The prompt placeholders were not expanded in the user message. I recovered the actual cluster context from `/Users/auric/aevatar/.refactor-loop/runs/audit-iter-23.md` and `/Users/auric/aevatar/.refactor-loop/runs/cluster-iter23-002-spec.md`.
- `/CLAUDE.md` was absent; I used this worktree's `CLAUDE.md` plus injected `AGENTS.md` instructions.
- Did not run full `dotnet test aevatar.slnx --nologo`; ran the affected test projects plus required build and guards.

## SCOPE_EXTEND

- `tools/ci/query_projection_priming_guard.sh` — add static guard required by cluster spec to block command ports from reintroducing projection activation before dispatch.

IMPLEMENT_DONE:cluster-002:ok

⟦AI:AUTO-LOOP⟧
