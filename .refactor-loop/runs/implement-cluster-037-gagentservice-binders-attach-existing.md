# implement-cluster-037-gagentservice-binders-attach-existing

## Modified files

- `src/platform/Aevatar.GAgentService.Abstractions/Ports/IScriptServiceAguiProjectionPort.cs` (40 lines)
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentDraftRunModels.cs` (97 lines)
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentDraftRunProjectionContracts.cs` (30 lines)
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentRunTerminalModels.cs` (73 lines)
- `src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentApprovalInteraction.cs` (439 lines)
- `src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentDraftRunInteraction.cs` (602 lines)
- `src/platform/Aevatar.GAgentService.Application/Scripts/ScriptServiceRunInteraction.cs` (435 lines)
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/GAgentDraftRunProjectionPort.cs` (85 lines)
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/GAgentRunTerminalProjectionPort.cs` (133 lines)
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/ScriptServiceAguiProjectionPort.cs` (90 lines)
- `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpointsStreamTests.cs` (1659 lines)
- `test/Aevatar.GAgentService.Tests/Application/GAgentApprovalInteractionTests.cs` (650 lines)
- `test/Aevatar.GAgentService.Tests/Application/GAgentDraftRunInteractionCoverageTests.cs` (797 lines)
- `test/Aevatar.GAgentService.Tests/Application/GAgentDraftRunInteractionTests.cs` (201 lines)
- `test/Aevatar.GAgentService.Tests/Application/ScriptServiceRunInteractionTests.cs` (563 lines)
- `test/Aevatar.GAgentService.Tests/Projection/GAgentDraftRunProjectionInfrastructureTests.cs` (170 lines)
- `test/Aevatar.GAgentService.Tests/Projection/ProjectionTestDoubles.cs` (196 lines)
- `test/Aevatar.GAgentService.Tests/Projection/ScriptServiceAguiProjectionPortTests.cs` (200 lines)
- `test/Aevatar.GAgentService.Tests/Projection/ServiceProjectionInfrastructureTests.cs` (562 lines)

## Summary

- Replaced GAgentService script-run, draft-run, and approval observation binders with attach-existing calls.
- Added capability-specific attach-existing methods on existing GAgentService projection ports; no new core abstraction was introduced.
- Implemented attach-existing by checking existing projection scope actor ids and constructing typed leases without invoking activation services.
- Cold live/terminal projection sessions now return typed `ProjectionUnavailable` before dispatch.

## Test results

- PASS: `dotnet build aevatar.slnx --nologo`
- PASS: `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "FullyQualifiedName~ScriptServiceRunInteractionTests|FullyQualifiedName~GAgentDraftRunInteraction|FullyQualifiedName~GAgentApprovalInteraction"`
- PASS: `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --filter FullyQualifiedName~ScopeServiceEndpointsStreamTests`
- PASS: `bash tools/ci/test_stability_guards.sh`
- PASS: `bash tools/ci/query_projection_priming_guard.sh`
- PASS: `bash tools/ci/architecture_guards.sh`
- PASS: `git diff --check`

## Deviations

- The repository has no `src/platform/Aevatar.GAgentService.*/Binders/*` directory. The actual GAgentService interaction binder implementations live in Application interaction lifecycle classes, matching the audit evidence.
- Added `ProjectionUnavailable` enum values for GAgent draft-run and approval start errors so cold attach-existing sessions can fail as typed results instead of exceptions.
- Did not add any top-level `CLAUDE.md` live-observation exception.
- Did not modify any external repositories.

## SCOPE_EXTEND records

- `src/platform/Aevatar.GAgentService.Abstractions/Ports/IScriptServiceAguiProjectionPort.cs` add capability-specific attach-existing method required by audit fix boundary; no new core abstraction
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentDraftRunProjectionContracts.cs` add capability-specific attach-existing method required by audit fix boundary; no new core abstraction
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentRunTerminalModels.cs` add capability-specific attach-existing materialization lease method required by audit fix boundary; no new core abstraction
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/ScriptServiceAguiProjectionPort.cs` implement existing-session attach by actor runtime existence check; no request-path activation
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/GAgentDraftRunProjectionPort.cs` implement existing-session attach by actor runtime existence check; no request-path activation
- `src/platform/Aevatar.GAgentService.Projection/Orchestration/GAgentRunTerminalProjectionPort.cs` implement existing-materialization lease by actor runtime existence check; no request-path activation
- `src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents/GAgentDraftRunModels.cs` add ProjectionUnavailable enum values so cold attach-existing sessions return typed start errors before dispatch
- `test/Aevatar.GAgentService.Tests/Projection/*` update projection port constructor tests for attach-existing runtime dependency and assertions
- `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpointsStreamTests.cs` update integration stubs for attach-existing ports

⟦AI:AUTO-LOOP⟧
