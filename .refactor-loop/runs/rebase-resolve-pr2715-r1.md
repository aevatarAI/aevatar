## Rebase Resolve PR 2715

Resolved scheduled dispatch credential-source conflicts on top of the role-aware single-source auth model.

Changed files:
- src/platform/Aevatar.GAgentService.Core/Schedules/ScheduledDispatchGAgent.cs
- test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchApplicationServiceTests.cs
- test/Aevatar.GAgentService.Tests/Application/ScheduledDispatchServiceInvocationTests.cs
- test/Aevatar.GAgentService.Tests/Projection/ScheduledDispatchCurrentStateProjectorTests.cs
- test/Aevatar.Workflow.Core.Tests/ScheduledDispatchGAgentTests.cs
- test/Aevatar.GAgentService.Integration.Tests/ScheduledDispatchEndpointsTests.cs
- test/Aevatar.Studio.Tests/StudioMemberWorkflowSchedulePortTests.cs
- test/Aevatar.Studio.Tests/StudioWorkflowProvisioningServiceTests.cs

Verification:
- `dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ScheduledDispatch"`: passed, 85 tests.
- `dotnet test test/Aevatar.Workflow.Core.Tests/Aevatar.Workflow.Core.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ScheduledDispatch"`: passed, 55 tests.
- `dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~StudioWorkflowProvisioningService|FullyQualifiedName~StudioMemberWorkflowSchedulePort"`: passed, 28 tests.
- `dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo --no-restore --filter "FullyQualifiedName~ScheduledDispatchEndpointsTests"`: passed, 44 tests.
- `bash tools/ci/test_stability_guards.sh`: passed.

Unresolved risk:
- No unresolved conflict risk found. Verification emitted existing NuGet/analyzer warnings only.

⟦AI:AUTO-LOOP⟧
REBASE_RESOLVE_DONE:2715:resolved
