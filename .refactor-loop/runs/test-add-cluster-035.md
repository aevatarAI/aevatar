# test-add cluster-035

## Summary

Status: ok

Cluster intent: replace process-local live-sink subscription registries with explicit caller-owned `IAsyncDisposable` leases.

Changed test files:

| File | Lines |
|---|---:|
| `test/Aevatar.CQRS.Core.Tests/EventSinkProjectionLeaseOrchestratorTests.cs` | 250 |
| `test/Aevatar.GAgentService.Tests/Application/GAgentApprovalInteractionTests.cs` | 619 |
| `test/Aevatar.GAgentService.Tests/Application/GAgentDraftRunInteractionCoverageTests.cs` | 761 |
| `test/Aevatar.Scripting.Core.Tests/Runtime/RuntimeScriptInfrastructurePortsTests.cs` | 1486 |
| `test/Aevatar.Workflow.Application.Tests/WorkflowRunCommandTargetAndPolicyTests.cs` | 390 |

Existing test file used for endpoint partial coverage:

| File | Lines |
|---|---:|
| `test/Aevatar.GAgentService.Integration.Tests/ScopeServiceEndpointsTests.cs` | 5355 |

## Coverage Mapping

`{{uncovered_lines}}` was not expanded in the controller prompt, so line mapping below uses the Codecov file-level miss/partial list and the current source line ranges.

| Uncovered file / range | Test coverage |
|---|---|
| `src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSinkProjectionLeaseOrchestrator.cs:41-65` | Existing `EnsureAndAttachLeaseAsync_WhenAttachThrows_ShouldReleaseAndDisposeThenRethrow` covers release + sink disposal. `liveSinkLease != null` inside the catch is structurally unreachable for a normal throwing `await attachAsync(...)` assignment. |
| `src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSinkProjectionLeaseOrchestrator.cs:88-98` | `DetachReleaseAndDisposeAsync_WhenLeaseIsNull_ShouldSkipDetachAndReleaseButCloseSink`; `DetachReleaseAndDisposeAsync_WhenDetachThrows_ShouldStillReleaseCloseSinkAndRethrowDetachFailure`. |
| `src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSinkProjectionLeaseOrchestrator.cs:100-122` | `DetachReleaseAndDisposeAsync_ShouldRunCleanupSequence`; `DetachReleaseAndDisposeAsync_WhenDetachThrows_ShouldStillReleaseCloseSinkAndRethrowDetachFailure`. |
| `src/Aevatar.CQRS.Core.Abstractions/Streaming/EventSinkProjectionLeaseOrchestrator.cs:124-144` | `DetachReleaseAndDisposeAsync_WhenSinkCompleteThrows_ShouldStillDisposeAndRethrowCompleteFailure`. |
| `src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentApprovalInteraction.cs:100-129` | `CleanupAfterDispatchFailureAsync_WhenOnlySinkIsBound_ShouldCompleteDisposeAndSkipProjectionDetach`; `CleanupAfterDispatchFailureAsync_WhenOnlyProjectionLeaseIsBound_ShouldReleaseLeaseAndSkipProjectionDetach`. |
| `src/platform/Aevatar.GAgentService.Application/ScopeGAgents/GAgentDraftRunInteraction.cs:125-156` | `CommandTargetCleanup_WhenOnlySinkIsBound_ShouldCompleteDisposeAndClearInteractionSink`; `CommandTargetCleanup_WhenOnlyProjectionLeaseIsBound_ShouldReleaseLeaseWithoutDetach`. |
| `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTarget.cs:98-128` | `ScriptEvolutionCommandTarget_ReleaseAsync_WhenOnlyProjectionLeaseIsBound_ShouldReleaseWithoutDetach`. |
| `src/Aevatar.Scripting.Infrastructure/Ports/ScriptEvolutionCommandTargetBinder.cs:35-54` | `ScriptEvolutionCommandTargetBinder_ShouldReturnProjectionDisabled_WhenActivationFails`. |
| `src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs:113-121,156-164` | Existing `ScopeDraftRunEndpoint_ShouldReturnBadRequest_WhenEventFormatIsInvalid` and `ScopeDraftRunEndpoint_ShouldReturnBadRequest_WhenWorkflowYamlsAreMissing`. |
| `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs:76-80` | `DetachLiveObservationAsync_WhenNoLiveSinkIsBound_ShouldNoopWithoutProjectionCalls`. |
| `src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunCommandTarget.cs:83-99` | `DetachLiveObservationAsync_WhenLiveSinkLeaseIsNull_ShouldDetachWithExplicitNullLease`. |

All listed uncovered file areas have direct behavioral coverage or existing endpoint coverage. No `TEST_BLOCKED` items.

## Verification

Passed:

```bash
dotnet test test/Aevatar.CQRS.Core.Tests/Aevatar.CQRS.Core.Tests.csproj --nologo --filter "EventSinkProjectionLeaseOrchestratorTests"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "GAgentApprovalInteractionTests|GAgentDraftRunInteractionCoverageTests"
dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter "RuntimeScriptInfrastructurePortsTests"
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "WorkflowRunCommandTargetAndPolicyTests"
```

Passed with coverage collection:

```bash
dotnet test test/Aevatar.CQRS.Core.Tests/Aevatar.CQRS.Core.Tests.csproj --nologo --filter "EventSinkProjectionLeaseOrchestratorTests" --collect:"XPlat Code Coverage"
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --filter "GAgentApprovalInteractionTests|GAgentDraftRunInteractionCoverageTests" --collect:"XPlat Code Coverage"
dotnet test test/Aevatar.Scripting.Core.Tests/Aevatar.Scripting.Core.Tests.csproj --nologo --filter "RuntimeScriptInfrastructurePortsTests" --collect:"XPlat Code Coverage"
dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "WorkflowRunCommandTargetAndPolicyTests" --collect:"XPlat Code Coverage"
```

Coverage artifacts:

| Project | Cobertura XML |
|---|---|
| CQRS Core Tests | `test/Aevatar.CQRS.Core.Tests/TestResults/1fbe643c-f14d-42c0-b496-328fce01bd0e/coverage.cobertura.xml` |
| GAgentService Tests | `test/Aevatar.GAgentService.Tests/TestResults/bbad77a7-1ebc-461a-a8ed-f610c72dfe7c/coverage.cobertura.xml` |
| Scripting Core Tests | `test/Aevatar.Scripting.Core.Tests/TestResults/614f8d59-78b6-4496-b31e-8ebeedd5043f/coverage.cobertura.xml` |
| Workflow Application Tests | `test/Aevatar.Workflow.Application.Tests/TestResults/e27d7db3-9869-4a93-9546-ea1ea5bce382/coverage.cobertura.xml` |

Passed:

```bash
bash /Users/auric/aevatar/tools/ci/test_stability_guards.sh
```
