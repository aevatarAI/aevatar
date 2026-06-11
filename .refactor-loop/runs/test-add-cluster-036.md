# test-add cluster-036

## Summary

Cluster intent: detached command monitoring publishes typed continuation signals; target-owned continuations own durable fallback and cleanup.

Changed test files:

| File | Added | Removed |
|---|---:|---:|
| `test/Aevatar.CQRS.Core.Tests/DefaultDetachedCommandDispatchServiceTests.cs` | 190 | 1 |
| `test/Aevatar.Workflow.Application.Tests/WorkflowRunCommandTargetAndPolicyTests.cs` | 51 | 0 |
| `test/Aevatar.Workflow.Application.Tests/WorkflowRunOrchestrationComponentTests.cs` | 70 | 0 |

## Coverage Mapping

| Uncovered / partial line | Test coverage |
|---|---|
| `DefaultDetachedCommandDispatchService.cs:58-64` | `DisposeAsync_ShouldWaitForInflightDrainUntilStreamPublishesTimeoutSignal`, `DisposeAsync_ShouldSwallowDrainTimeout` |
| `DefaultDetachedCommandDispatchService.cs:105-107` | `DispatchAsync_ShouldPublishTimeoutSignal_WhenLiveStreamEndsWithoutCompletion` |
| `DefaultDetachedCommandDispatchService.cs:121-132` | `DispatchAsync_ShouldPublishTimeoutSignal_WhenMonitorFailsBeforeCompletion`, `DispatchAsync_ShouldNotPublishTimeout_WhenMonitorFailsAfterCompletionWasObserved` |
| `DefaultDetachedCommandDispatchService.cs:151-159` | `DispatchAsync_ShouldSwallowBestEffortTimeoutPublishFailure_WhenMonitorFails` |
| `WorkflowRunCommandTarget.cs:41` | `Constructor_ShouldRejectMissingDurableCompletionResolver` |
| `WorkflowRunCommandTarget.cs:286-290` | `PublishDetachedCommandSignalAsync_WhenUnknownDetachedSignal_ShouldUseUnknownAndDurableFallback` |
| `WorkflowRunCommandTarget.cs:293-309` | `PublishDetachedCommandSignalAsync_WhenUnknownDetachedSignal_ShouldUseUnknownAndDurableFallback`, existing completed/timeout detached signal tests |
| `WorkflowRunCommandTargetResolver.cs:27` | `WorkflowRunCommandTargetResolver_ShouldRejectMissingDurableCompletionResolver` |
| `WorkflowRunCommandTargetResolver.cs:51-52` | `WorkflowRunCommandTargetResolver_ShouldWireDurableCompletionResolverIntoResolvedTarget` |

All cluster-036 refactor-introduced uncovered lines are covered locally. Remaining uncovered lines shown in full-project Cobertura for `WorkflowRunCommandTarget` are pre-existing non-cluster paths such as live-sink detach failure and sink complete failure.

## Verification

| Command | Result |
|---|---|
| `dotnet test test/Aevatar.CQRS.Core.Tests/Aevatar.CQRS.Core.Tests.csproj --nologo --filter "DefaultDetachedCommandDispatchServiceTests"` | Passed: 11 tests |
| `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --filter "WorkflowRunCommandTargetAndPolicyTests|WorkflowRunOrchestrationComponentTests"` | Passed: 25 tests |
| `dotnet test test/Aevatar.CQRS.Core.Tests/Aevatar.CQRS.Core.Tests.csproj --nologo --collect:"XPlat Code Coverage"` | Passed: 37 tests; `DefaultDetachedCommandDispatchService.cs` target methods line-rate 1 |
| `dotnet test test/Aevatar.Workflow.Application.Tests/Aevatar.Workflow.Application.Tests.csproj --nologo --collect:"XPlat Code Coverage"` | Passed: 170 tests; detached continuation method line-rate 1 |
| `bash /Users/auric/aevatar/tools/ci/test_stability_guards.sh` | Passed |

No `TEST_BLOCKED` items.
